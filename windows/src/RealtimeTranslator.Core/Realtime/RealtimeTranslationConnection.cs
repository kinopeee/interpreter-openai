using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>翻訳 target 1 つ分の Realtime 接続。1 target = 1 接続で混線させない。</summary>
public sealed class RealtimeTranslationConnection : IDisposable
{
    /// <summary>1 回の接続試行（handshake）の上限。再接続予算とは独立に数える。</summary>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(15);

    public static readonly Uri EndpointUrl =
        new("wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate");

    private readonly RealtimeTranslationOutputLanguage _target;
    private readonly IRealtimeWebSocketTransport _transport;
    private readonly string _safetyIdentifier;
    private readonly TimeSpan _sessionUpdateTimeout;
    private readonly TimeSpan _closeTimeout;
    private readonly RealtimeConnectionLifecycle _lifecycle;

    private bool _isReady;
    private bool _isClosing;
    private bool _didReceiveClosed;

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
        _sessionUpdateTimeout = sessionUpdateTimeout ?? DefaultHandshakeTimeout;
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(15);
        _lifecycle = new RealtimeConnectionLifecycle(transport);
    }

    public ChannelReader<RealtimeTranslationStreamEvent> Events => _lifecycle.Events;

    /// <summary>
    /// テスト用。完了済み Channel に leftover イベントを 1 件入れて差し替える。
    /// pair 切替後の Dual merge が未使用 lane を読むと次世代へ混線する。
    /// </summary>
    internal void SeedCompletedEventForTests(RealtimeTranslationServerEvent serverEvent)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);

        var channel = RealtimeEventChannel.Create();
        channel.Writer.TryWrite(new RealtimeTranslationStreamEvent(_target, serverEvent, _lifecycle.Epoch));
        channel.Writer.TryComplete();
        _lifecycle.SwapEventsChannel(channel);
    }

    public int Epoch => _lifecycle.Epoch;

    public Task StartAsync(
        string apiKey,
        RealtimeTranslationSessionConfig config,
        CancellationToken cancellationToken = default) =>
        StartAsync(apiKey, config, null, cancellationToken);

    public async Task StartAsync(
        string apiKey,
        RealtimeTranslationSessionConfig config,
        EventDeliveryState? deliveryState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        apiKey = RealtimeApiKey.Require(apiKey);

        await _lifecycle.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycle.TearDownTransportAsync().ConfigureAwait(false);

            int currentEpoch;
            lock (_lifecycle.Sync)
            {
                currentEpoch = _lifecycle.ResetForReconnect();
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
                var created = await ReceiveHandshakeEventAsync(cancellationToken).ConfigureAwait(false);
                RealtimeConnectionLifecycle
                    .RequireHandshakeEvent<RealtimeTranslationServerEvent.SessionCreated>(created);

                await SendAsync(
                    new RealtimeTranslationClientEvent.SessionUpdate(config),
                    cancellationToken).ConfigureAwait(false);

                var updated = await ReceiveHandshakeEventAsync(cancellationToken).ConfigureAwait(false);
                RealtimeConnectionLifecycle
                    .RequireHandshakeEvent<RealtimeTranslationServerEvent.SessionUpdated>(updated);

                lock (_lifecycle.Sync)
                {
                    if (!_lifecycle.IsCurrentEpoch(currentEpoch))
                    {
                        throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
                    }

                    _isReady = true;
                }

                _lifecycle.StartReceiveLoop(
                    currentEpoch,
                    deliveryState ?? new EventDeliveryState(currentEpoch),
                    EventDeliveryStage.Translation,
                    ReceiveLoopAsync);
            }
            catch
            {
                await _lifecycle.TearDownTransportAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Gate.Release();
        }
    }

    public Task AppendAudioFrameAsync(ReadOnlyMemory<byte> pcm16LittleEndian, CancellationToken cancellationToken = default)
    {
        lock (_lifecycle.Sync)
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
        await _lifecycle.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasReady;
            lock (_lifecycle.Sync)
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
                    _lifecycle.BumpEpoch();
                }
            }

            if (!wasReady)
            {
                await _lifecycle.TearDownTransportAsync().ConfigureAwait(false);
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

            var closed = await _lifecycle.WaitForCloseSignalAsync(
                () => _didReceiveClosed,
                _closeTimeout,
                bumpEpochOnCancel: false,
                cancellationToken).ConfigureAwait(false);

            await _lifecycle.TearDownTransportAsync().ConfigureAwait(false);
            if (!closed)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.CloseTimeout);
            }
        }
        finally
        {
            _lifecycle.Gate.Release();
        }
    }

    public async Task ForceCloseAsync()
    {
        await _lifecycle.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lifecycle.Sync)
            {
                _isClosing = true;
                _isReady = false;
                _lifecycle.BumpEpoch();
            }

            await _lifecycle.TearDownTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Gate.Release();
        }
    }

    public void Dispose() => _lifecycle.Dispose(() =>
    {
        _isClosing = true;
        _isReady = false;
    });

    private Task SendAsync(RealtimeTranslationClientEvent clientEvent, CancellationToken cancellationToken) =>
        _transport.SendAsync(RealtimeTranslationMessageCodec.Encode(clientEvent), cancellationToken);

    private Task<RealtimeTranslationServerEvent> ReceiveHandshakeEventAsync(CancellationToken cancellationToken) =>
        _lifecycle.ReceiveHandshakeEventAsync(
            RealtimeTranslationMessageCodec.DecodeServerEvent,
            TryClassifyError,
            _sessionUpdateTimeout,
            cancellationToken);

    private static RealtimeServerErrorClassification? TryClassifyError(
        RealtimeTranslationServerEvent serverEvent) =>
        serverEvent is RealtimeTranslationServerEvent.ServerError error
            ? EventDeliveryState.Classify(error)
            : null;

    private async Task ReceiveLoopAsync(
        int currentEpoch,
        EventDeliveryWriter writer,
        EventDeliveryState deliveryState,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RealtimeTranslationServerEvent serverEvent;
            try
            {
                var data = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!_lifecycle.IsCurrentEpoch(currentEpoch))
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
                if (!_lifecycle.IsCurrentEpoch(currentEpoch))
                {
                    return;
                }

                deliveryState.TryRecordTermination(EventDeliveryTermination.TransportFailure);
                if (!writer.TryDeliver(new RealtimeTranslationStreamEvent(
                    _target,
                    new RealtimeTranslationServerEvent.ServerError(
                        UserCopy.Current.Text("error.transportDisconnected"),
                        "transport"),
                    currentEpoch)))
                {
                    return;
                }

                writer.Complete();
                return;
            }

            if (serverEvent is RealtimeTranslationServerEvent.SessionClosed)
            {
                lock (_lifecycle.Sync)
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

            if (serverEvent is RealtimeTranslationServerEvent.ServerError error)
            {
                // 接続維持エラーは termination も下流イベントも出さない。
                var classification = EventDeliveryState.Classify(error);
                if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                {
                    continue;
                }

                deliveryState.TryRecordTermination(classification);
            }

            if (!writer.TryDeliver(new RealtimeTranslationStreamEvent(_target, serverEvent, currentEpoch)))
            {
                return;
            }

            if (serverEvent is RealtimeTranslationServerEvent.SessionClosed)
            {
                writer.Complete();
                return;
            }
        }
    }
}

/// <summary>接続が下流へ流すイベント channel。取りこぼしより最新優先で詰まらせない。</summary>
internal static class RealtimeEventChannel
{
    public const int Capacity = 512;

    public static Channel<RealtimeTranslationStreamEvent> Create() =>
        Channel.CreateBounded<RealtimeTranslationStreamEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
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
