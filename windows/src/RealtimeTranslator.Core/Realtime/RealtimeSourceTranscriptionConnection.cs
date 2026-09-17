using System;
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

    private readonly IRealtimeWebSocketTransport _transport;
    private readonly string _safetyIdentifier;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _closeTimeout;
    private readonly RealtimeConnectionLifecycle _lifecycle;

    private bool _isReady;
    private bool _didReceiveCommitOutcome;

    /// <summary>commit 送信完了後だけ立てる。送信待ち中の録音時 outcome を commit 結果にしない。</summary>
    private bool _isAwaitingCommitOutcome;
    private LanguagePair _pair = LanguagePair.JaEn;

    /// <summary>接続開始時の noise_reduction。live update では変更しない。</summary>
    private RealtimeTranslationNoiseReduction _connectedNoiseReduction = RealtimeTranslationNoiseReduction.FarField;

    public RealtimeSourceTranscriptionConnection(
        IRealtimeWebSocketTransport transport,
        string safetyIdentifier,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? closeTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(safetyIdentifier);

        _transport = transport;
        _safetyIdentifier = safetyIdentifier;
        _handshakeTimeout = handshakeTimeout ?? RealtimeTranslationConnection.DefaultHandshakeTimeout;
        _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(5);
        _lifecycle = new RealtimeConnectionLifecycle(transport);
    }

    public ChannelReader<RealtimeTranslationStreamEvent> Events => _lifecycle.Events;

    public async Task StartAsync(
        string apiKey,
        RealtimeSessionTuning tuning,
        LanguagePair pair = LanguagePair.JaEn,
        EventDeliveryState? deliveryState = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tuning);
        apiKey = RealtimeApiKey.Require(apiKey);

        await _lifecycle.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycle.TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);

            int currentEpoch;
            lock (_lifecycle.Sync)
            {
                currentEpoch = _lifecycle.ResetForReconnect();
                _isReady = false;
                _didReceiveCommitOutcome = false;
                _isAwaitingCommitOutcome = false;
                _connectedNoiseReduction = tuning.NoiseReduction;
                _pair = pair;
            }

            var state = deliveryState ?? new EventDeliveryState(currentEpoch);

            try
            {
                await _transport
                    .ConnectAsync(EndpointUrl, RealtimeRequestHeaders.For(apiKey, _safetyIdentifier), cancellationToken)
                    .ConfigureAwait(false);

                var created = await ReceiveHandshakeEventAsync(state, cancellationToken).ConfigureAwait(false);
                RealtimeConnectionLifecycle.RequireHandshakeEvent<RealtimeSourceTranscriptionServerEvent.SessionCreated>(
                    created
                );
                state.RecordSessionExpiry(
                    RealtimeTranslationLane.Source,
                    ((RealtimeSourceTranscriptionServerEvent.SessionCreated)created).ExpiresAtUnixSeconds
                );

                await SendAsync(
                        new RealtimeSourceTranscriptionClientEvent.SessionUpdate(tuning, pair),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                var updated = await ReceiveHandshakeEventAsync(state, cancellationToken).ConfigureAwait(false);
                RealtimeConnectionLifecycle.RequireHandshakeEvent<RealtimeSourceTranscriptionServerEvent.SessionUpdated>(
                    updated
                );

                lock (_lifecycle.Sync)
                {
                    if (!_lifecycle.IsCurrentEpoch(currentEpoch))
                    {
                        throw new RealtimeTranslationException(RealtimeTranslationErrorKind.Cancelled);
                    }

                    _isReady = true;
                }

                _lifecycle.StartReceiveLoop(currentEpoch, state, EventDeliveryStage.Source, ReceiveLoopAsync);
            }
            catch
            {
                await _lifecycle.TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.ReleaseGate();
        }
    }

    /// <summary>録音中に prompt/keywords/delay を更新する。noise_reduction は接続時の値を維持する。</summary>
    public Task UpdateTuningAsync(RealtimeSessionTuning tuning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        RealtimeTranslationNoiseReduction connectedNoiseReduction;
        LanguagePair pair;
        lock (_lifecycle.Sync)
        {
            if (!_isReady)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }

            connectedNoiseReduction = _connectedNoiseReduction;
            pair = _pair;
        }

        var liveTuning = tuning with { NoiseReduction = connectedNoiseReduction };
        return SendAsync(new RealtimeSourceTranscriptionClientEvent.SessionUpdate(liveTuning, pair), cancellationToken);
    }

    public Task AppendAudioFrameAsync(
        ReadOnlyMemory<byte> pcm16LittleEndian,
        CancellationToken cancellationToken = default
    )
    {
        lock (_lifecycle.Sync)
        {
            if (!_isReady)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.NotConnected);
            }
        }

        var base64 = Convert.ToBase64String(pcm16LittleEndian.Span);
        return SendAsync(new RealtimeSourceTranscriptionClientEvent.InputAudioBufferAppend(base64), cancellationToken);
    }

    public async Task CloseGracefullyAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasReady;
            lock (_lifecycle.Sync)
            {
                wasReady = _isReady;
                _isReady = false;
                // 録音中の failed / completed を、この commit の結果として使わない。
                _didReceiveCommitOutcome = false;
                _isAwaitingCommitOutcome = true;
            }

            if (!wasReady)
            {
                await _lifecycle.TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
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

            var completed = await _lifecycle
                .WaitForCloseSignalAsync(
                    () => _didReceiveCommitOutcome,
                    _closeTimeout,
                    bumpEpochOnCancel: true,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await _lifecycle.TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
            if (!completed)
            {
                throw new RealtimeTranslationException(RealtimeTranslationErrorKind.CloseTimeout);
            }
        }
        finally
        {
            _lifecycle.ReleaseGate();
        }
    }

    public async Task ForceCloseAsync()
    {
        await _lifecycle.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lifecycle.Sync)
            {
                _isReady = false;
            }

            await _lifecycle.TearDownTransportAsync(bumpEpoch: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.ReleaseGate();
        }
    }

    public void Dispose() => _lifecycle.Dispose(() => _isReady = false);

    private Task SendAsync(RealtimeSourceTranscriptionClientEvent clientEvent, CancellationToken cancellationToken) =>
        _transport.SendAsync(RealtimeSourceTranscriptionCodec.Encode(clientEvent), cancellationToken);

    private Task<RealtimeSourceTranscriptionServerEvent> ReceiveHandshakeEventAsync(
        EventDeliveryState state,
        CancellationToken cancellationToken
    ) =>
        _lifecycle.ReceiveHandshakeEventAsync(
            RealtimeSourceTranscriptionCodec.DecodeServerEvent,
            TryClassifyError,
            _handshakeTimeout,
            cancellationToken,
            _ => state.RecordReceive(RealtimeTranslationLane.Source)
        );

    private static RealtimeServerErrorClassification? TryClassifyError(
        RealtimeSourceTranscriptionServerEvent serverEvent
    ) => serverEvent is RealtimeSourceTranscriptionServerEvent.ServerError error ? error.Classification : null;

    private async Task ReceiveLoopAsync(
        int currentEpoch,
        EventDeliveryWriter writer,
        EventDeliveryState deliveryState,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RealtimeSourceTranscriptionServerEvent serverEvent;
            try
            {
                var data = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!_lifecycle.IsCurrentEpoch(currentEpoch))
                {
                    return;
                }

                serverEvent = RealtimeSourceTranscriptionCodec.DecodeServerEvent(data);
                deliveryState.RecordReceive(RealtimeTranslationLane.Source);
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
                if (
                    !writer.TryDeliver(
                        new RealtimeTranslationStreamEvent(
                            RealtimeTranslationLane.Source,
                            new RealtimeTranslationServerEvent.ServerError(
                                UserCopy.Current.Text("error.sourceDisconnected"),
                                "transport"
                            ),
                            currentEpoch
                        )
                    )
                )
                {
                    return;
                }

                writer.Complete();
                return;
            }

            switch (serverEvent)
            {
                case RealtimeSourceTranscriptionServerEvent.InputTranscriptDelta delta:
                    if (
                        !writer.TryDeliver(
                            new RealtimeTranslationStreamEvent(
                                RealtimeTranslationLane.Source,
                                new RealtimeTranslationServerEvent.InputTranscriptDelta(
                                    delta.Delta,
                                    delta.EventId,
                                    null
                                ),
                                currentEpoch
                            )
                        )
                    )
                    {
                        return;
                    }

                    break;

                case RealtimeSourceTranscriptionServerEvent.TranscriptionCompleted:
                    lock (_lifecycle.Sync)
                    {
                        if (_isAwaitingCommitOutcome)
                        {
                            _didReceiveCommitOutcome = true;
                        }
                    }

                    break;

                case RealtimeSourceTranscriptionServerEvent.TranscriptionFailed failed:
                    lock (_lifecycle.Sync)
                    {
                        if (_isAwaitingCommitOutcome)
                        {
                            _didReceiveCommitOutcome = true;
                        }
                    }

                    deliveryState.NoteSourceFailureQueued();
                    if (failed.Classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                    {
                        if (
                            !writer.TryDeliver(
                                new RealtimeTranslationStreamEvent(
                                    RealtimeTranslationLane.Source,
                                    new RealtimeTranslationServerEvent.InputTranscriptFailed(
                                        failed.ItemId,
                                        failed.EventId,
                                        failed.Code,
                                        failed.ErrorType
                                    ),
                                    currentEpoch
                                )
                            )
                        )
                        {
                            return;
                        }

                        break;
                    }

                    deliveryState.TryRecordTermination(failed.Classification);
                    if (
                        !writer.TryDeliver(
                            new RealtimeTranslationStreamEvent(
                                RealtimeTranslationLane.Source,
                                new RealtimeTranslationServerEvent.InputTranscriptFailed(
                                    failed.ItemId,
                                    failed.EventId,
                                    failed.Code,
                                    failed.ErrorType
                                ),
                                currentEpoch
                            )
                        )
                    )
                    {
                        return;
                    }

                    break;

                case RealtimeSourceTranscriptionServerEvent.ServerError error:
                    var streamError = error.ToStreamError();
                    var classification = error.Classification;
                    if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                    {
                        break;
                    }

                    deliveryState.TryRecordTermination(classification);
                    if (
                        !writer.TryDeliver(
                            new RealtimeTranslationStreamEvent(
                                RealtimeTranslationLane.Source,
                                streamError,
                                currentEpoch
                            )
                        )
                    )
                    {
                        return;
                    }

                    break;

                default:
                    break;
            }
        }
    }
}
