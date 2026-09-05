using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>字幕の原文を得る専用接続。翻訳側の input transcript は原文 authority にしない。</summary>
public sealed class RealtimeSourceTranscriptionConnection : IDisposable
{
    public static readonly Uri EndpointUrl = new("wss://api.openai.com/v1/realtime?intent=transcription");

    private static readonly TimeSpan ClosePollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IRealtimeWebSocketTransport _transport;
    private readonly string _safetyIdentifier;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _closeTimeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Channel<RealtimeTranslationStreamEvent> _events = RealtimeEventChannel.Create();
    private int _epoch;
    private bool _isReady;
    private bool _didReceiveCompleted;
    private LanguagePair _pair = LanguagePair.JaEn;

    /// <summary>接続開始時の noise_reduction。live update では変更しない。</summary>
    private RealtimeTranslationNoiseReduction _connectedNoiseReduction = RealtimeTranslationNoiseReduction.FarField;

    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public RealtimeSourceTranscriptionConnection(
        IRealtimeWebSocketTransport transport,
        string safetyIdentifier,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? closeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(safetyIdentifier);

        _transport = transport;
        _safetyIdentifier = safetyIdentifier;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15);
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(5);
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

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        CancellationToken cancellationToken = default) =>
        await StartAsync(apiKey, tuning, LanguagePair.JaEn, null, cancellationToken)
            .ConfigureAwait(false);

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair,
        CancellationToken cancellationToken = default) =>
        await StartAsync(apiKey, tuning, pair, null, cancellationToken).ConfigureAwait(false);

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair,
        EventDeliveryState? deliveryState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        apiKey = RealtimeApiKey.Require(apiKey);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);

            int currentEpoch;
            lock (_sync)
            {
                _events = RealtimeEventChannel.Create();
                _epoch += 1;
                currentEpoch = _epoch;
                _isReady = false;
                _didReceiveCompleted = false;
                _connectedNoiseReduction = tuning.NoiseReduction;
                _pair = pair;
            }

            try
            {
                await _transport.ConnectAsync(
                    EndpointUrl,
                    RealtimeRequestHeaders.For(apiKey, _safetyIdentifier),
                    cancellationToken).ConfigureAwait(false);

                var created = await ReceiveDirectEventAsync(cancellationToken).ConfigureAwait(false);
                RequireHandshakeEvent<RealtimeSourceTranscriptionServerEvent.SessionCreated>(created);

                await SendAsync(
                    new RealtimeSourceTranscriptionClientEvent.SessionUpdate(tuning, pair),
                    cancellationToken).ConfigureAwait(false);

                var updated = await ReceiveDirectEventAsync(cancellationToken).ConfigureAwait(false);
                RequireHandshakeEvent<RealtimeSourceTranscriptionServerEvent.SessionUpdated>(updated);

                lock (_sync)
                {
                    if (currentEpoch != _epoch)
                    {
                        throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
                    }

                    _isReady = true;
                }

                StartReceiveLoop(currentEpoch, deliveryState ?? new EventDeliveryState(currentEpoch));
            }
            catch
            {
                await TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>録音中に prompt/keywords/delay を更新する。noise_reduction は接続時の値を維持する。</summary>
    public Task UpdateTuningAsync(RealtimeSessionTuning tuning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        RealtimeTranslationNoiseReduction connectedNoiseReduction;
        LanguagePair pair;
        lock (_sync)
        {
            if (!_isReady)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }

            connectedNoiseReduction = _connectedNoiseReduction;
            pair = _pair;
        }

        var liveTuning = tuning with { NoiseReduction = connectedNoiseReduction };
        return SendAsync(
            new RealtimeSourceTranscriptionClientEvent.SessionUpdate(liveTuning, pair),
            cancellationToken);
    }

    public Task AppendAudioFrameAsync(
        ReadOnlyMemory<byte> pcm16LittleEndian,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_isReady)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        var base64 = Convert.ToBase64String(pcm16LittleEndian.Span);
        return SendAsync(
            new RealtimeSourceTranscriptionClientEvent.InputAudioBufferAppend(base64),
            cancellationToken);
    }

    public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasReady;
            lock (_sync)
            {
                wasReady = _isReady;
                _isReady = false;
            }

            if (!wasReady)
            {
                await TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
                return;
            }

            try
            {
                await SendAsync(new RealtimeSourceTranscriptionClientEvent.Commit(), cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // commit 送信の失敗は握り潰し、completed 待ちと teardown へ進む。
            catch (Exception)
#pragma warning restore CA1031
            {
                // 相手が既に落ちている場合も completed 待ちへ進む。
            }

            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < _closeTimeout)
            {
                lock (_sync)
                {
                    if (_didReceiveCompleted)
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
                    await TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
                    throw;
                }
            }

            bool completed;
            lock (_sync)
            {
                completed = _didReceiveCompleted;
            }

            await TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
            if (!completed)
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
                _isReady = false;
            }

            await TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
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

    private Task SendAsync(RealtimeSourceTranscriptionClientEvent clientEvent, CancellationToken cancellationToken) =>
        _transport.SendAsync(RealtimeSourceTranscriptionCodec.Encode(clientEvent), cancellationToken);

    private async Task<RealtimeSourceTranscriptionServerEvent> ReceiveDirectEventAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_handshakeTimeout);
        byte[] data;
        try
        {
            data = await _transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.SessionUpdateTimeout);
        }

        return RealtimeSourceTranscriptionCodec.DecodeServerEvent(data);
    }

    private static void RequireHandshakeEvent<T>(RealtimeSourceTranscriptionServerEvent serverEvent)
        where T : RealtimeSourceTranscriptionServerEvent
    {
        if (serverEvent is RealtimeSourceTranscriptionServerEvent.ServerError error)
        {
            throw RealtimeTranslationException.IsAuthenticationFailure(error.Code, error.Message)
                ? new RealtimeTranslationException(RealtimeTranslationErrorKind.AuthenticationFailed)
                : new RealtimeTranslationException(
                    RealtimeTranslationErrorKind.FatalServerError,
                    RealtimeTranslationException.SanitizeServerMessage(error.Message));
        }

        if (serverEvent is not T)
        {
            throw new RealtimeTranslationException(RealtimeTranslationErrorKind.InvalidMessage);
        }
    }

    private void StartReceiveLoop(int currentEpoch, EventDeliveryState deliveryState)
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
            () => ReceiveLoopAsync(
                currentEpoch,
                new EventDeliveryWriter(
                    writer,
                    deliveryState,
                    EventDeliveryStage.Source,
                    RealtimeEventChannel.Capacity),
                deliveryState,
                token),
            CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(
        int currentEpoch,
        EventDeliveryWriter writer,
        EventDeliveryState deliveryState,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RealtimeSourceTranscriptionServerEvent serverEvent;
            try
            {
                var data = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrentEpoch(currentEpoch))
                {
                    return;
                }

                serverEvent = RealtimeSourceTranscriptionCodec.DecodeServerEvent(data);
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

                deliveryState.TryRecordTermination(EventDeliveryTermination.TransportFailure);
                if (!writer.TryDeliver(new RealtimeTranslationStreamEvent(
                    RealtimeTranslationLane.Source,
                    new RealtimeTranslationServerEvent.ServerError(
                        UserCopy.Current.Text("error.sourceDisconnected"),
                        "transport"),
                    currentEpoch)))
                {
                    return;
                }

                writer.Complete();
                return;
            }

            switch (serverEvent)
            {
                case RealtimeSourceTranscriptionServerEvent.InputTranscriptDelta delta:
                    if (!writer.TryDeliver(new RealtimeTranslationStreamEvent(
                        RealtimeTranslationLane.Source,
                        new RealtimeTranslationServerEvent.InputTranscriptDelta(delta.Delta, delta.EventId, null),
                        currentEpoch)))
                    {
                        return;
                    }

                    break;

                case RealtimeSourceTranscriptionServerEvent.TranscriptionCompleted:
                    lock (_sync)
                    {
                        _didReceiveCompleted = true;
                    }

                    break;

                case RealtimeSourceTranscriptionServerEvent.ServerError error:
                    var streamError = new RealtimeTranslationServerEvent.ServerError(error.Message, error.Code);
                    var termination = EventDeliveryState.Classify(streamError);
                    deliveryState.TryRecordTermination(termination.Termination, termination.SanitizedMessage);
                    if (!writer.TryDeliver(new RealtimeTranslationStreamEvent(
                        RealtimeTranslationLane.Source,
                        streamError,
                        currentEpoch)))
                    {
                        return;
                    }

                    break;

                default:
                    break;
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

    private async Task TearDownTransportAsync(bool bumpEpoch)
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
