using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// Realtime 接続の世代(epoch)・events channel・receive task・teardown を束ねる共有ライフサイクル。
/// 接続ごとのフラグも <see cref="Sync"/> の同じロックで守る。
/// </summary>
internal sealed class RealtimeConnectionLifecycle : IDisposable
{
    private static readonly TimeSpan ClosePollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IRealtimeWebSocketTransport _transport;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Channel<RealtimeTranslationStreamEvent> _events = RealtimeEventChannel.Create();
    private int _epoch;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public RealtimeConnectionLifecycle(IRealtimeWebSocketTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    public object Sync => _sync;

    public SemaphoreSlim Gate => _lifecycleGate;

    /// <summary>Dispose と競合した解放は握り潰す。finally から呼ぶ。</summary>
    public void ReleaseGate()
    {
        try
        {
            _lifecycleGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Dispose 済みなら解放は不要。
        }
    }

    public int Epoch
    {
        get
        {
            lock (_sync)
            {
                return _epoch;
            }
        }
    }

    public ChannelReader<RealtimeTranslationStreamEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.Reader;
            }
        }
    }

    /// <summary>開始時の channel 差し替えと epoch 進行。接続ごとのフラグ初期化と一緒に lock (Sync) 内で呼ぶ。</summary>
    public int ResetForReconnect()
    {
        lock (_sync)
        {
            _events = RealtimeEventChannel.Create();
            _epoch += 1;
            return _epoch;
        }
    }

    /// <summary>テスト用。完了済み Channel へ差し替える。</summary>
    public void SwapEventsChannel(Channel<RealtimeTranslationStreamEvent> channel)
    {
        lock (_sync)
        {
            _events = channel;
        }
    }

    public bool IsCurrentEpoch(int currentEpoch)
    {
        lock (_sync)
        {
            return currentEpoch == _epoch;
        }
    }

    /// <summary>lock (Sync) 内からも呼べる。</summary>
    public void BumpEpoch()
    {
        lock (_sync)
        {
            _epoch += 1;
        }
    }

    public void StartReceiveLoop(
        int currentEpoch,
        EventDeliveryState deliveryState,
        EventDeliveryStage stage,
        Func<int, EventDeliveryWriter, EventDeliveryState, CancellationToken, Task> receiveLoop)
    {
        var cts = new CancellationTokenSource();

        // Dispose 済み CTS へ触れないよう、Task 開始前に token を確定させる。
        var token = cts.Token;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            _receiveCts = cts;
            writer = _events.Writer;
        }

        _receiveTask = Task.Run(
            () => receiveLoop(
                currentEpoch,
                new EventDeliveryWriter(
                    writer,
                    deliveryState,
                    stage,
                    RealtimeEventChannel.Capacity),
                deliveryState,
                token),
            CancellationToken.None);
    }

    /// <summary>handshake / close 直読み用の 1 イベント受信。期限切れは SessionUpdateTimeout として扱う。</summary>
    public async Task<TServerEvent> ReceiveDirectEventAsync<TServerEvent>(
        ServerEventDecoder<TServerEvent> decode,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? remaining = null)
    {
        var budget = remaining ?? timeout;
        if (budget <= TimeSpan.Zero)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.SessionUpdateTimeout);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(budget);
        byte[] data;
        try
        {
            data = await _transport.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.SessionUpdateTimeout);
        }

        return decode(data);
    }

    /// <summary>
    /// handshake 中の接続維持エラーは読み飛ばして次のイベントを待つ。
    /// 期限は handshake 1 段あたり 1 つ（keep-alive で延長しない）。
    /// </summary>
    public async Task<TServerEvent> ReceiveHandshakeEventAsync<TServerEvent>(
        ServerEventDecoder<TServerEvent> decode,
        Func<TServerEvent, RealtimeServerErrorClassification?> tryClassifyError,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            var serverEvent = await ReceiveDirectEventAsync(decode, timeout, cancellationToken, remaining)
                .ConfigureAwait(false);
            if (tryClassifyError(serverEvent) is { } classification)
            {
                if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                {
                    continue;
                }

                throw classification.ToException();
            }

            return serverEvent;
        }
    }

    public static void RequireHandshakeEvent<TExpected>(object serverEvent)
    {
        if (serverEvent is not TExpected)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }
    }

    /// <summary>close 完了イベントを closeTimeout までポーリングする。cancel 時は teardown して再送出する。</summary>
    public async Task<bool> WaitForCloseSignalAsync(
        Func<bool> isSignaled,
        TimeSpan closeTimeout,
        bool bumpEpochOnCancel,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < closeTimeout)
        {
            lock (_sync)
            {
                if (isSignaled())
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(ClosePollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 並行 close の片方が失敗したときは期限まで待たない。
                await TearDownTransportAsync(bumpEpochOnCancel).ConfigureAwait(false);
                throw;
            }
        }

        lock (_sync)
        {
            return isSignaled();
        }
    }

    public async Task TearDownTransportAsync(bool bumpEpoch = false)
    {
        CancellationTokenSource? cts;
        Task? receiveTask;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            if (bumpEpoch)
            {
                _epoch += 1;
            }

            cts = _receiveCts;
            _receiveCts = null;
            receiveTask = _receiveTask;
            _receiveTask = null;
            writer = _events.Writer;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        Exception? closeError = null;
        try
        {
            await _transport.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            closeError = error;
        }

        // transport の close に失敗しても受信ループの観測と channel 完了は必ず行う。
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancel 済みの受信ループは正常終了として扱う。
            }
            catch (Exception error) when (closeError is null)
            {
                closeError = error;
            }
            catch (Exception)
            {
                // transport 側の例外を優先する。
            }
        }

        writer.TryComplete();

        if (closeError is not null)
        {
            ExceptionDispatchInfo.Throw(closeError);
        }
    }

    public void Dispose() => Dispose(null);

    /// <summary>受信ループを止めて epoch を進め、events を完了させてから lifecycle gate を破棄する。接続ごとのフラグ更新は同じロック内で行う。</summary>
    public void Dispose(Action? lockedCleanup)
    {
        CancellationTokenSource? cts;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            lockedCleanup?.Invoke();
            _epoch += 1;
            cts = _receiveCts;
            _receiveCts = null;
            writer = _events.Writer;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 二重 Dispose は無視する。
            }

            cts.Dispose();
        }

        // Events を待つ consumer を解放してから gate を破棄する。
        writer.TryComplete();
        _lifecycleGate.Dispose();
    }
}

internal delegate TServerEvent ServerEventDecoder<TServerEvent>(ReadOnlySpan<byte> data);
