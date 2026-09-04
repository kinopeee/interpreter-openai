using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>原文 1 本 + 翻訳 2 本を束ね、音声を検出言語の逆側 target だけへ流す。</summary>
public interface IDualRealtimeTranslationClient
{
    ChannelReader<RealtimeTranslationStreamEvent> Events { get; }

    int ConnectionEpoch { get; }

    Task StartAsync(string apiKey, RealtimeSessionTuning tuning, CancellationToken cancellationToken = default);

    Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair,
        CancellationToken cancellationToken = default);

    Task AppendAudioFrameAsync(ReadOnlyMemory<byte> pcm16LittleEndian, CancellationToken cancellationToken = default);

    Task SelectTranslationTargetAsync(
        RealtimeTranslationOutputLanguage? target,
        CancellationToken cancellationToken = default);

    Task UpdateTranscriptionTuningAsync(RealtimeSessionTuning tuning, CancellationToken cancellationToken = default);

    Task ResetAudioRoutingAsync();

    Task CloseGracefullyAsync(CancellationToken cancellationToken = default);

    Task ForceCloseAsync();
}

public sealed class DualRealtimeTranslationClient : IDualRealtimeTranslationClient, IDisposable
{
    /// <summary>100 ms frame × 40 = 直近 4 秒。言語判定の遅れがあっても発話冒頭を翻訳へ届ける。</summary>
    public const int TranslationPrerollFrameLimit = 40;

    public const int TranslationPendingFrameLimit = 80;

    public const int ConsecutiveTranslationFailureLimit = 3;

    public static string TransportErrorMessage => UserCopy.Current.Text("error.audioSendFailed");

    public static string TranslationBacklogErrorMessage => UserCopy.Current.Text("error.translationBacklog");

    public const string TransportErrorCode = "transport";

    /// <summary>停止時 drain で未送信 frame 1 枚あたりに足す予算。preroll flush 後の短い停滞で訳文を落とさない。</summary>
    public const int TranslationDrainTimeoutMillisecondsPerPendingFrame = 250;

    /// <summary>停止時 drain の上限。Send 停滞でも Stop が無期限待ちしない。</summary>
    public static readonly TimeSpan TranslationDrainTimeoutCap = TimeSpan.FromSeconds(30);

    private readonly RealtimeSourceTranscriptionConnection _sourceConnection;
    private readonly Dictionary<RealtimeTranslationOutputLanguage, RealtimeTranslationConnection> _connections;
    private readonly TimeSpan _translationDrainTimeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Queue<PendingTranslationFrame> _pendingTranslationFrames = new();
    private readonly Queue<ReadOnlyMemory<byte>> _translationPrerollFrames = new();

    private Channel<RealtimeTranslationStreamEvent> _events = RealtimeEventChannel.Create();
    private Task? _mergeTask;
    private Task? _translationPumpTask;
    private CancellationTokenSource? _mergeCts;
    private CancellationTokenSource _translationPumpCts = new();
    private int _connectionEpoch;
    private bool _isRunning;
    private int _consecutiveTranslationFailures;

    /// <summary>transport failure 後は再接続まで翻訳ポンプを再開しない。</summary>
    private bool _translationPumpHaltedForTransportFailure;

    private RealtimeTranslationOutputLanguage? _selectedTranslationTarget;

    /// <summary>この世代で handshake した翻訳 lane。未使用 leftover 接続は merge しない。</summary>
    private RealtimeTranslationOutputLanguage[] _startedTranslationTargets = [];

    /// <summary>テスト用。StartEventMerge 直前に差し込む。</summary>
    internal Action? BeforeStartEventMergeForTests { get; set; }

    public DualRealtimeTranslationClient(
        RealtimeSourceTranscriptionConnection sourceConnection,
        RealtimeTranslationConnection englishConnection,
        RealtimeTranslationConnection japaneseConnection,
        TimeSpan? translationDrainTimeout = null,
        RealtimeTranslationConnection? spanishConnection = null)
    {
        ArgumentNullException.ThrowIfNull(sourceConnection);
        ArgumentNullException.ThrowIfNull(englishConnection);
        ArgumentNullException.ThrowIfNull(japaneseConnection);

        _sourceConnection = sourceConnection;
        _connections = new()
        {
            [RealtimeTranslationOutputLanguage.English] = englishConnection,
            [RealtimeTranslationOutputLanguage.Japanese] = japaneseConnection,
        };
        if (spanishConnection is not null)
        {
            _connections[RealtimeTranslationOutputLanguage.Spanish] = spanishConnection;
        }
        // 既定 5 秒。送信停滞でも CloseGracefully が session.close へ進める上限。
        _translationDrainTimeout = translationDrainTimeout ?? TimeSpan.FromSeconds(5);
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

    public int ConnectionEpoch
    {
        get
        {
            lock (_sync)
            {
                return _connectionEpoch;
            }
        }
    }

    internal int PendingTranslationFrameCountForTests
    {
        get
        {
            lock (_sync)
            {
                return _pendingTranslationFrames.Count;
            }
        }
    }

    internal bool IsTranslationPumpHaltedForTests
    {
        get
        {
            lock (_sync)
            {
                return _translationPumpHaltedForTransportFailure;
            }
        }
    }

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        CancellationToken cancellationToken = default) =>
        await StartAsync(apiKey, tuning, LanguagePair.JaEn, cancellationToken).ConfigureAwait(false);

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        await ForceCloseAsync().ConfigureAwait(false);

        // pair に必要な接続が無いときは running に入らない。
        // Select / Append は未開始 Dual と同じ NotConnected になり、Events も完了したまま。
        EnsureConnectionsForPair(pair);

        int epoch;
        lock (_sync)
        {
            _events = RealtimeEventChannel.Create();
            _connectionEpoch += 1;
            epoch = _connectionEpoch;
            _isRunning = true;
            _consecutiveTranslationFailures = 0;
            _translationPumpHaltedForTransportFailure = false;
            _selectedTranslationTarget = null;
            _translationPrerollFrames.Clear();
            _pendingTranslationFrames.Clear();
            _translationPumpCts.Dispose();
            _translationPumpCts = new CancellationTokenSource();
        }

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await _lifecycleGate.WaitAsync(handshakeCts.Token).ConfigureAwait(false);
            try
            {
                var starts = new List<Task>
                {
                    _sourceConnection.StartAsync(apiKey, tuning, pair, handshakeCts.Token),
                };
                starts.AddRange(pair.Languages().Select(language =>
                    {
                        var target = language.ToOutputLanguage();
                        return _connections[target].StartAsync(
                            apiKey,
                            new RealtimeTranslationSessionConfig(
                                target,
                                null,
                                tuning.NoiseReduction),
                            handshakeCts.Token);
                    }));

                // Swift の throwing TaskGroup と同じく、1 本が失敗したら残り handshake を
                // timeout まで待たずキャンセルし、ready leftover をすぐ ForceClose する。
                Exception? handshakeFault = null;
                var pending = new List<Task>(starts);
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);
                    if (done.IsCompletedSuccessfully)
                    {
                        continue;
                    }

                    try
                    {
                        await done.ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        handshakeFault = error;
                    }

                    await handshakeCts.CancelAsync().ConfigureAwait(false);
                    break;
                }

                try
                {
                    await Task.WhenAll(starts).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (handshakeFault is not null)
                    {
                        ExceptionDispatchInfo.Capture(handshakeFault).Throw();
                    }

                    throw;
                }

                if (handshakeFault is not null)
                {
                    ExceptionDispatchInfo.Capture(handshakeFault).Throw();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch
        {
            try
            {
                await ForceCloseAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // leftover close 失敗で handshake / cancel の例外を消さない。
            catch (Exception)
#pragma warning restore CA1031
            {
                // Swift の forceClose は throw しない。cleanup 失敗で元例外を置換しない。
            }

            throw;
        }

        lock (_sync)
        {
            if (epoch != _connectionEpoch || !_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
            }

            _startedTranslationTargets = pair.Languages()
                .Select(language => language.ToOutputLanguage())
                .ToArray();
        }

        BeforeStartEventMergeForTests?.Invoke();
        StartEventMerge(epoch);
    }

    /// <summary>
    /// テスト用。未使用 lane の Events に完了済み leftover を埋め、pair 切替後の merge 混線を再現する。
    /// </summary>
    internal void SeedCompletedTranslationEventForTests(
        RealtimeTranslationOutputLanguage target,
        RealtimeTranslationServerEvent serverEvent)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);

        if (!_connections.TryGetValue(target, out var connection))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "unknown translation target");
        }

        connection.SeedCompletedEventForTests(serverEvent);
    }

    public async Task AppendAudioFrameAsync(
        ReadOnlyMemory<byte> pcm16LittleEndian,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        // 原文送信は単独で完了させ、翻訳側の停滞に巻き込まない。
        await _sourceConnection.AppendAudioFrameAsync(pcm16LittleEndian, cancellationToken).ConfigureAwait(false);

        ChannelWriter<RealtimeTranslationStreamEvent>? overflowWriter = null;
        RealtimeTranslationOutputLanguage overflowTarget = default;
        int overflowEpoch = 0;
        lock (_sync)
        {
            // stop/force-close が await 中に走った場合、停止済みへは enqueue しない。
            if (!_isRunning)
            {
                return;
            }

            // 呼び出し側バッファ再利用に備え、保持前に所有コピーを取る。
            ReadOnlyMemory<byte> retained = pcm16LittleEndian.ToArray();

            // 言語切替検出の遅延を吸収するため、選択後も直近 4 秒を rolling 保持する。
            _translationPrerollFrames.Enqueue(retained);
            while (_translationPrerollFrames.Count > TranslationPrerollFrameLimit)
            {
                _translationPrerollFrames.Dequeue();
            }

            if (_selectedTranslationTarget is { } target)
            {
                if (!TryEnqueueTranslationFrameLocked(retained, target))
                {
                    overflowWriter = _events.Writer;
                    overflowTarget = target;
                    overflowEpoch = _connectionEpoch;
                }
            }
        }

        if (overflowWriter is not null)
        {
            PublishTransportError(
                overflowWriter,
                overflowTarget,
                overflowEpoch,
                TranslationBacklogErrorMessage);
        }
    }

    public Task SelectTranslationTargetAsync(
        RealtimeTranslationOutputLanguage? target,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        ChannelWriter<RealtimeTranslationStreamEvent>? overflowWriter = null;
        RealtimeTranslationOutputLanguage overflowTarget = default;
        int overflowEpoch = 0;
        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }

            if (_selectedTranslationTarget == target)
            {
                return Task.CompletedTask;
            }

            // 旧 target 向けの未送信 frame は破棄し、rolling preroll を新 target へ flush する。
            _pendingTranslationFrames.Clear();
            if (target is not { } selected)
            {
                _selectedTranslationTarget = null;
                return Task.CompletedTask;
            }

            if (!_connections.ContainsKey(selected))
            {
                throw new ArgumentException(
                    $"Translation connection for '{selected.ToWireValue()}' is not configured.",
                    nameof(target));
            }

            _selectedTranslationTarget = selected;
            foreach (var frame in _translationPrerollFrames)
            {
                if (!TryEnqueueTranslationFrameLocked(frame, selected))
                {
                    overflowWriter = _events.Writer;
                    overflowTarget = selected;
                    overflowEpoch = _connectionEpoch;
                    break;
                }
            }
        }

        if (overflowWriter is not null)
        {
            PublishTransportError(
                overflowWriter,
                overflowTarget,
                overflowEpoch,
                TranslationBacklogErrorMessage);
        }

        return Task.CompletedTask;
    }

    public Task UpdateTranscriptionTuningAsync(
        RealtimeSessionTuning tuning,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        return _sourceConnection.UpdateTuningAsync(tuning, cancellationToken);
    }

    public Task ResetAudioRoutingAsync()
    {
        lock (_sync)
        {
            // rolling preroll は維持し、次の target 選択で flush できるようにする。
            _selectedTranslationTarget = null;
            _pendingTranslationFrames.Clear();
            _consecutiveTranslationFailures = 0;
        }

        return Task.CompletedTask;
    }

    public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
    {
        ChannelWriter<RealtimeTranslationStreamEvent>? idleWriter = null;
        lock (_sync)
        {
            if (!_isRunning)
            {
                // Start 前や ForceClose 後でも Events を完了させる。
                // 未完了のままだと InterpretationSession の stop drain が
                // WaitToReadAsync で Closing に固まり、次の録音を開始できない。
                idleWriter = _events.Writer;
            }
        }

        if (idleWriter is not null)
        {
            idleWriter.TryComplete();
            return;
        }

        // 未送信の翻訳フレームを先に送り、停止時の訳文欠落を防ぐ。
        // preroll flush 直後は待ち行列が長いので pending 数に応じて予算を伸ばす。
        // 送信が長時間停滞しても cap で close 自体は進める。
        try
        {
            await WaitForTranslationDrainAsync(ResolveCloseDrainTimeout(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // drain できなくても session.close へ進む。
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        Task? pump;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _pendingTranslationFrames.Clear();
            pump = _translationPumpTask;
            pumpCts = _translationPumpCts;
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        try
        {
            await Task.WhenAll(
                new[] { _sourceConnection.CloseGracefullyAsync(cancellationToken) }
                    .Concat(_connections.Values.Select(connection => connection.CloseGracefullyAsync(cancellationToken))))
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 最初の close 失敗だけを呼び出し元へ返し、残りの解放は必ず行う。
        catch (Exception error)
#pragma warning restore CA1031
        {
            firstError = error;
        }

        await StopEventMergeAsync().ConfigureAwait(false);

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    public async Task ForceCloseAsync()
    {
        Task? pump;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            _isRunning = false;
            _selectedTranslationTarget = null;
            _startedTranslationTargets = [];
            _translationPrerollFrames.Clear();
            _pendingTranslationFrames.Clear();
            _consecutiveTranslationFailures = 0;
            _translationPumpHaltedForTransportFailure = false;
            _connectionEpoch += 1;
            pump = _translationPumpTask;
            pumpCts = _translationPumpCts;
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);
        await AwaitPumpAsync(pump).ConfigureAwait(false);

        Exception? firstError = null;
        foreach (var close in new Func<Task>[] { _sourceConnection.ForceCloseAsync }
                     .Concat(_connections.Values.Select(connection => (Func<Task>)connection.ForceCloseAsync)))
        {
            try
            {
                await close().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 最初の失敗だけを返し、残りの接続と merge は必ず解放する。
            catch (Exception error)
#pragma warning restore CA1031
            {
                firstError ??= error;
            }
        }

        await StopEventMergeAsync().ConfigureAwait(false);

        if (firstError is not null)
        {
            throw firstError;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? mergeCts;
        CancellationTokenSource pumpCts;
        lock (_sync)
        {
            _isRunning = false;
            mergeCts = _mergeCts;
            _mergeCts = null;
            pumpCts = _translationPumpCts;
        }

        // Dispose 経路でも背景タスクを止める。Cancel せず Dispose だけだと loop が残る。
        try
        {
            mergeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 二重 Dispose は無視する。
        }

        mergeCts?.Dispose();

        try
        {
            pumpCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 二重 Dispose は無視する。
        }

        pumpCts.Dispose();

        _lifecycleGate.Dispose();
        _sourceConnection.Dispose();
        foreach (var connection in _connections.Values.Distinct())
        {
            connection.Dispose();
        }
    }

    private static async Task AwaitPumpAsync(Task? pump)
    {
        if (pump is null)
        {
            return;
        }

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止時のキャンセルは正常終了として扱う。
        }
    }

    /// <summary>
    /// 停止時 drain 予算。base（既定5秒）に未送信 frame 分を足し、cap（30秒）で打ち切る。
    /// テストが短い base を注入しているときはその base を下限・基準にする。
    /// </summary>
    internal static TimeSpan ResolveTranslationDrainTimeout(TimeSpan baseTimeout, int pendingFrameCount)
    {
        var baseMs = Math.Max(0, baseTimeout.TotalMilliseconds);
        var pending = Math.Max(0, pendingFrameCount);
        var scaledMs = baseMs + (pending * (double)TranslationDrainTimeoutMillisecondsPerPendingFrame);
        var capMs = Math.Max(baseMs, TranslationDrainTimeoutCap.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Clamp(scaledMs, baseMs, capMs));
    }

    private TimeSpan ResolveCloseDrainTimeout()
    {
        int pending;
        lock (_sync)
        {
            pending = _pendingTranslationFrames.Count;
            // 送信中の 1 frame も予算に含め、preroll 直後の Stop で足りなくならないようにする。
            if (_translationPumpTask is not null)
            {
                pending += 1;
            }
        }

        return ResolveTranslationDrainTimeout(_translationDrainTimeout, pending);
    }

    /// <summary>テスト用。停止時 drain 予算（送信中 frame の +1 を含む）。</summary>
    internal TimeSpan CloseDrainTimeoutForTests => ResolveCloseDrainTimeout();

    /// <summary>翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。決定的なテストのために使う。</summary>
    /// <remarks>送信が停滞しても timeout（既定5秒、Close時はpending比例）で打ち切る（ポンプTaskを無期限待ちしない）。</remarks>
    internal async Task WaitForTranslationDrainAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64
            + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? pump;
            lock (_sync)
            {
                if (_translationPumpTask is null && _pendingTranslationFrames.Count == 0)
                {
                    return;
                }

                pump = _translationPumpTask;
            }

            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0)
            {
                lock (_sync)
                {
                    if (_translationPumpTask is null && _pendingTranslationFrames.Count == 0)
                    {
                        return;
                    }
                }

                throw new TimeoutException("translation pump did not drain");
            }

            if (pump is null)
            {
                await Task.Yield();
                continue;
            }

            // ポンプ完了とdeadlineを競わせ、停滞したsendで無期限待ちにしない。
            var delay = Task.Delay((int)Math.Min(remainingMs, int.MaxValue), cancellationToken);
            var completed = await Task.WhenAny(pump, delay).ConfigureAwait(false);
            if (completed != pump)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // timeout と完了が競合したとき、すでに空なら成功扱いにする。
                lock (_sync)
                {
                    if (_translationPumpTask is null && _pendingTranslationFrames.Count == 0)
                    {
                        return;
                    }
                }

                throw new TimeoutException("translation pump did not drain");
            }

            await pump.ConfigureAwait(false);
        }
    }

    private bool TryEnqueueTranslationFrameLocked(
        ReadOnlyMemory<byte> frame,
        RealtimeTranslationOutputLanguage target)
    {
        // transport failure 後は enqueue 自体を止め、ポンプ再起動の隙を残さない。
        if (_translationPumpHaltedForTransportFailure)
        {
            return true;
        }

        if (_pendingTranslationFrames.Count >= TranslationPendingFrameLimit)
        {
            _translationPumpHaltedForTransportFailure = true;
            _pendingTranslationFrames.Clear();
            return false;
        }

        _pendingTranslationFrames.Enqueue(new PendingTranslationFrame(frame, target));
        _translationPumpTask ??= Task.Run(PumpTranslationFramesAsync, CancellationToken.None);
        return true;
    }

    private async Task PumpTranslationFramesAsync()
    {
        CancellationToken pumpToken;
        int pumpEpoch;
        lock (_sync)
        {
            pumpToken = _translationPumpCts.Token;
            pumpEpoch = _connectionEpoch;
        }

        while (true)
        {
            PendingTranslationFrame pending;
            lock (_sync)
            {
                if (!_isRunning
                    || _translationPumpHaltedForTransportFailure
                    || _pendingTranslationFrames.Count == 0)
                {
                    _translationPumpTask = null;
                    return;
                }

                pending = _pendingTranslationFrames.Dequeue();
            }

            try
            {
                var connection = _connections[pending.Target];
                await connection.AppendAudioFrameAsync(pending.Frame, pumpToken).ConfigureAwait(false);

                lock (_sync)
                {
                    if (!_translationPumpHaltedForTransportFailure
                        && _connectionEpoch == pumpEpoch)
                    {
                        _consecutiveTranslationFailures = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    _translationPumpTask = null;
                }

                return;
            }
#pragma warning disable CA1031 // 送信失敗の種類に関わらず連続失敗として数え、上限で transport error を出す。
            catch (Exception)
#pragma warning restore CA1031
            {
                bool halted;
                int epoch;
                lock (_sync)
                {
                    if (_translationPumpHaltedForTransportFailure || _connectionEpoch != pumpEpoch)
                    {
                        _translationPumpTask = null;
                        return;
                    }

                    _consecutiveTranslationFailures += 1;
                    halted = _consecutiveTranslationFailures >= ConsecutiveTranslationFailureLimit;
                    epoch = _connectionEpoch;
                    if (halted)
                    {
                        // 再接続待ちの間、死にかけの socket へ送り続けない。
                        _translationPumpHaltedForTransportFailure = true;
                        _pendingTranslationFrames.Clear();
                    }
                }

                if (halted)
                {
                    // drain 待ちが復帰する前に transport error を確実に発行する。
                    PublishTransportError(pending.Target, epoch, TransportErrorMessage);
                    lock (_sync)
                    {
                        _translationPumpTask = null;
                    }

                    return;
                }
            }
        }
    }

    private void PublishTransportError(
        RealtimeTranslationOutputLanguage target,
        int epoch,
        string message)
    {
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            writer = _events.Writer;
        }

        PublishTransportError(writer, target, epoch, message);
    }

    private static void PublishTransportError(
        ChannelWriter<RealtimeTranslationStreamEvent> writer,
        RealtimeTranslationOutputLanguage target,
        int epoch,
        string message)
    {
        writer.TryWrite(new RealtimeTranslationStreamEvent(
            target,
            new RealtimeTranslationServerEvent.ServerError(message, TransportErrorCode),
            epoch));
    }

    private void StartEventMerge(int epoch)
    {
        var cts = new CancellationTokenSource();

        // Dispose 済み CTS へ触れないよう、Task 開始前に token を確定させる。
        var token = cts.Token;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        RealtimeTranslationOutputLanguage[] startedTargets;
        lock (_sync)
        {
            _mergeCts = cts;
            writer = _events.Writer;
            startedTargets = _startedTranslationTargets;
        }

        _mergeTask = Task.Run(
            async () =>
            {
                // 原文 connection だけ input transcript を通し、翻訳側は接続フィルタと二重化する。
                var pumps = new List<Task>
                {
                    MergeOneAsync(
                        _sourceConnection.Events,
                        writer,
                        epoch,
                        acceptInputTranscript: true,
                        token),
                };
                // コンストラクタで用意した未使用 leftover lane は merge しない。
                // ForceClose が epoch を先に進めると merge が残りを読まず、
                // 完了済み Channel に残った訳文 / transport error が次世代へ混線する。
                pumps.AddRange(startedTargets.Select(target => MergeOneAsync(
                        _connections[target].Events,
                        writer,
                        epoch,
                        acceptInputTranscript: false,
                        token)));

                await Task.WhenAll(pumps).ConfigureAwait(false);

                // 全接続のイベント流が終わったら購読側を解放する。
                if (ConnectionEpoch == epoch)
                {
                    writer.TryComplete();
                }
            },
            CancellationToken.None);
    }

    private async Task MergeOneAsync(
        ChannelReader<RealtimeTranslationStreamEvent> reader,
        ChannelWriter<RealtimeTranslationStreamEvent> writer,
        int epoch,
        bool acceptInputTranscript,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var streamEvent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ConnectionEpoch != epoch)
                {
                    return;
                }

                // MVP は翻訳音声を再生しない。念のため merge でも落とす（接続側のフィルタと二重化）。
                if (streamEvent.Event is RealtimeTranslationServerEvent.OutputAudioDelta)
                {
                    continue;
                }

                // 翻訳接続の input_transcript は原文 authority にしない。
                if (!acceptInputTranscript
                    && streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptDelta)
                {
                    continue;
                }

                // Dual 側の epoch で貼り直し、接続内部の epoch と揃える。
                writer.TryWrite(streamEvent with { Epoch = epoch });
            }
        }
        catch (OperationCanceledException)
        {
            // 停止時のキャンセルは正常終了として扱う。
        }
    }

    private void EnsureConnectionsForPair(LanguagePair pair)
    {
        foreach (var language in pair.Languages())
        {
            var target = language.ToOutputLanguage();
            if (!_connections.ContainsKey(target))
            {
                throw new ArgumentException(
                    $"Translation connection for '{target.ToWireValue()}' is required for pair '{pair.ToWireValue()}'.",
                    nameof(pair));
            }
        }
    }

    private async Task StopEventMergeAsync()
    {
        CancellationTokenSource? cts;
        Task? mergeTask;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
            cts = _mergeCts;
            _mergeCts = null;
            mergeTask = _mergeTask;
            _mergeTask = null;
            writer = _events.Writer;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (mergeTask is not null)
        {
            try
            {
                await mergeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancel 済みの merge は正常終了として扱う。
            }
        }

        writer.TryComplete();
    }

    private readonly record struct PendingTranslationFrame(
        ReadOnlyMemory<byte> Frame,
        RealtimeTranslationOutputLanguage Target);
}
