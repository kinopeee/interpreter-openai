using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>翻訳 target 1 つ分の Realtime 接続。1 target = 1 接続で混線させない。</summary>
public sealed class RealtimeTranslationConnection : IDisposable
{
    public static readonly Uri EndpointUrl =
        new("wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate");

    private static readonly TimeSpan ClosePollInterval = TimeSpan.FromMilliseconds(50);

    private readonly RealtimeTranslationOutputLanguage _target;
    private readonly IRealtimeWebSocketTransport _transport;
    private readonly string _safetyIdentifier;
    private readonly TimeSpan _sessionUpdateTimeout;
    private readonly TimeSpan _closeTimeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Channel<RealtimeTranslationStreamEvent> _events = RealtimeEventChannel.Create();
    private int _epoch;
    private bool _isReady;
    private bool _isClosing;
    private bool _didReceiveClosed;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public RealtimeTranslationConnection(
        RealtimeTranslationOutputLanguage target,
        IRealtimeWebSocketTransport transport,
        string safetyIdentifier,
        TimeSpan? sessionUpdateTimeout = null,
        TimeSpan? closeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(safetyIdentifier);

        _target = target;
        _transport = transport;
        _safetyIdentifier = safetyIdentifier;
        _sessionUpdateTimeout = sessionUpdateTimeout ?? TimeSpan.FromSeconds(15);
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(15);
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

    public async Task StartAsync(
        string apiKey,
        RealtimeTranslationSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        apiKey = RealtimeApiKey.Require(apiKey);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownTransportAsync().ConfigureAwait(false);

            int currentEpoch;
            lock (_sync)
            {
                _events = RealtimeEventChannel.Create();
                _epoch += 1;
                currentEpoch = _epoch;
                _isReady = false;
                _isClosing = false;
                _didReceiveClosed = false;
            }

            try
            {
                await _transport.ConnectAsync(
                    EndpointUrl,
                    RealtimeRequestHeaders.For(apiKey, _safetyIdentifier),
                    cancellationToken).ConfigureAwait(false);

                // handshake は共有 channel を消費せず transport から直接読む。
                var created = await ReceiveDirectEventAsync(cancellationToken).ConfigureAwait(false);
                RequireHandshakeEvent<RealtimeTranslationServerEvent.SessionCreated>(created);

                await SendAsync(
                    new RealtimeTranslationClientEvent.SessionUpdate(config),
                    cancellationToken).ConfigureAwait(false);

                var updated = await ReceiveDirectEventAsync(cancellationToken).ConfigureAwait(false);
                RequireHandshakeEvent<RealtimeTranslationServerEvent.SessionUpdated>(updated);

                lock (_sync)
                {
                    if (currentEpoch != _epoch)
                    {
                        throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
                    }

                    _isReady = true;
                }

                StartReceiveLoop(currentEpoch);
            }
            catch
            {
                await TearDownTransportAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task AppendAudioFrameAsync(ReadOnlyMemory<byte> pcm16LittleEndian, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isReady || _isClosing)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        var base64 = Convert.ToBase64String(pcm16LittleEndian.Span);
        return SendAsync(new RealtimeTranslationClientEvent.InputAudioBufferAppend(base64), cancellationToken);
    }

    public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasReady;
            lock (_sync)
            {
                if (_isClosing)
                {
                    return;
                }

                wasReady = _isReady;
                _isClosing = true;
                _isReady = false;
                if (!wasReady)
                {
                    // handshake 未完了では receive loop が無いため session.closed を待てない。
                    // 原文接続と同様に即 teardown し、停止が closeTimeout まで固まらないようにする。
                    _epoch += 1;
                }
            }

            if (!wasReady)
            {
                await TearDownTransportAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                await SendAsync(new RealtimeTranslationClientEvent.SessionClose(), cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // close 送信の失敗は握り潰し、close 待ちへ進む。
            catch (Exception)
#pragma warning restore CA1031
            {
                // 相手が既に落ちている場合も close 待ちへ進む。
            }

            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < _closeTimeout)
            {
                lock (_sync)
                {
                    if (_didReceiveClosed)
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
                    await TearDownTransportAsync().ConfigureAwait(false);
                    throw;
                }
            }

            bool closed;
            lock (_sync)
            {
                closed = _didReceiveClosed;
            }

            await TearDownTransportAsync().ConfigureAwait(false);
            if (!closed)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.CloseTimeout);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ForceCloseAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _isClosing = true;
                _isReady = false;
                _epoch += 1;
            }

            await TearDownTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _isClosing = true;
            _isReady = false;
            _epoch += 1;
            cts = _receiveCts;
            _receiveCts = null;
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

        _lifecycleGate.Dispose();
    }

    private Task SendAsync(RealtimeTranslationClientEvent clientEvent, CancellationToken cancellationToken) =>
        _transport.SendAsync(RealtimeTranslationMessageCodec.Encode(clientEvent), cancellationToken);

    private async Task<RealtimeTranslationServerEvent> ReceiveDirectEventAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_sessionUpdateTimeout);
        byte[] data;
        try
        {
            data = await _transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.SessionUpdateTimeout);
        }

        return RealtimeTranslationMessageCodec.DecodeServerEvent(data);
    }

    private static void RequireHandshakeEvent<T>(RealtimeTranslationServerEvent serverEvent)
        where T : RealtimeTranslationServerEvent
    {
        if (serverEvent is RealtimeTranslationServerEvent.ServerError error)
        {
            throw ClassifyServerError(error);
        }

        if (serverEvent is not T)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }
    }

    private static RealtimeTranslationException ClassifyServerError(RealtimeTranslationServerEvent.ServerError error)
    {
        Trace.WriteLine("Realtime translation error code=" + (error.Code ?? "none"));
        return RealtimeTranslationException.IsAuthenticationFailure(error.Code, error.Message)
            ? new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed)
            : new RealtimeTranslationException(
                RealtimeTranslationErrorKind.FatalServerError,
                RealtimeTranslationException.SanitizeServerMessage(error.Message));
    }

    private void StartReceiveLoop(int currentEpoch)
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

        _receiveTask = Task.Run(() => ReceiveLoopAsync(currentEpoch, writer, token), CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(
        int currentEpoch,
        ChannelWriter<RealtimeTranslationStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RealtimeTranslationServerEvent serverEvent;
            try
            {
                var data = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrentEpoch(currentEpoch))
                {
                    return;
                }

                serverEvent = RealtimeTranslationMessageCodec.DecodeServerEvent(data);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // transport / decode の失敗はすべて transport error として下流へ通知する。
            catch (Exception)
#pragma warning restore CA1031
            {
                if (!IsCurrentEpoch(currentEpoch))
                {
                    return;
                }

                writer.TryWrite(new RealtimeTranslationStreamEvent(
                    _target,
                    new RealtimeTranslationServerEvent.ServerError(
                        UserCopy.Current.Text("error.transportDisconnected"),
                        "transport"),
                    currentEpoch));
                writer.TryComplete();
                return;
            }

            if (serverEvent is RealtimeTranslationServerEvent.SessionClosed)
            {
                lock (_sync)
                {
                    _didReceiveClosed = true;
                }
            }

            // MVP は翻訳音声を再生しない。output_audio.delta を bounded channel へ入れると
            // Stop の close-drain 待ち（購読停止中）に DropOldest で字幕 delta を押し出す。
            // 翻訳接続の input_transcript は原文 authority にしない（専用 transcription のみ）。
            // target=en 翻訳セッションの delta を通すと assembler が原文として取り込む。
            if (serverEvent is RealtimeTranslationServerEvent.OutputAudioDelta
                or RealtimeTranslationServerEvent.InputTranscriptDelta)
            {
                continue;
            }

            if (serverEvent is RealtimeTranslationServerEvent.ServerError errorEvent)
            {
                Trace.WriteLine("Realtime translation error code=" + (errorEvent.Code ?? "none"));
            }

            writer.TryWrite(new RealtimeTranslationStreamEvent(_target, serverEvent, currentEpoch));

            if (serverEvent is RealtimeTranslationServerEvent.SessionClosed)
            {
                writer.TryComplete();
                return;
            }
        }
    }

    private bool IsCurrentEpoch(int currentEpoch)
    {
        lock (_sync)
        {
            return currentEpoch == _epoch;
        }
    }

    private async Task TearDownTransportAsync()
    {
        CancellationTokenSource? cts;
        Task? receiveTask;
        ChannelWriter<RealtimeTranslationStreamEvent> writer;
        lock (_sync)
        {
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

        await _transport.CloseAsync().ConfigureAwait(false);

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
        }

        writer.TryComplete();
    }
}

/// <summary>接続が下流へ流すイベント channel。取りこぼしより最新優先で詰まらせない。</summary>
internal static class RealtimeEventChannel
{
    public static Channel<RealtimeTranslationStreamEvent> Create() =>
        Channel.CreateBounded<RealtimeTranslationStreamEvent>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
}

/// <summary>Realtime エンドポイントへ送るヘッダー。<c>OpenAI-Beta</c> は送らない。</summary>
internal static class RealtimeRequestHeaders
{
    public static IReadOnlyDictionary<string, string> For(string apiKey, string safetyIdentifier) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + apiKey,
            ["OpenAI-Safety-Identifier"] = safetyIdentifier,
        };
}
