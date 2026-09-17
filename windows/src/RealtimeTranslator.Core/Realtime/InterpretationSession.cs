using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

public enum TranslationState
{
    Idle,
    Connecting,
    Listening,
    Reconnecting,
    Closing,
    Error,
}

/// <summary>BYOK の API キー取得。保管先 (Credential Manager 等) は Windows 層が実装する。</summary>
public interface IApiKeyStore
{
    string? Load();
}

/// <summary>24kHz / PCM16 / mono / 100ms frame を供給する録音源。</summary>
public interface IRealtimeAudioCapture
{
    ChannelReader<CapturedAudioFrame> Frames { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>録音・3 接続・字幕組み立てを束ねるセッション。UI 非依存。</summary>
public sealed class InterpretationSession : IDisposable
{
    /// <summary><see cref="ReconnectPolicy.Default"/> と同じ値。fixture との整合はテストで確認する。</summary>
    public const int MaxReconnectAttempts = 5;

    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(200);

    private readonly IApiKeyStore _apiKeyStore;
    private readonly IRealtimeAudioCapture _audioCapture;
    private readonly IDualRealtimeTranslationClient _dualClient;
    private readonly Func<RealtimeSessionTuning> _tuningProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ReconnectBudget _reconnectBudget;
    private readonly TimeSpan _tickInterval;
    private readonly Func<LanguagePair> _languagePairProvider;
    private readonly long _startTimestamp;
    private readonly object _sync = new();
    private readonly SessionHealthBookkeeper _healthBookkeeper;
    private readonly SessionSubtitlePipeline _subtitlePipeline;
    private readonly AudioRoutingCoordinator _audioRouting;

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    /// <summary>進行中の Stop。二重 Stop を macOS の stopTask と同様に合流させる。</summary>
    private Task? _stopTask;
    private int _lifecycleGeneration;
    private TranslationState _state = TranslationState.Idle;
    /// <summary>現在の録音世代で使う言語ペア。Start 時に固定し、再接続でも settings の変更を取り込まない。</summary>
    private LanguagePair? _sessionLanguagePair;
    private RealtimeEventFeed? _activeFeed;

    /// <summary>テスト用。generation 確認後・assembler 更新前に差し込む。</summary>
    internal Action? BeforeAssemblerIngestForTests { get; set; }

    internal Action? BeforeRoutingResetForTests { get; set; }

    internal Func<Task>? BeforeAudioLossRoutingResetForTests { get; set; }

    internal Action? AfterFailedSourceFallbackForTests { get; set; }

    /// <summary>テスト用。Listening 直前の monitor 世代開始 lock 直前に差し込む。</summary>
    internal Action? BeforeHealthGenerationBeginForTests { get; set; }

    public InterpretationSession(
        IApiKeyStore apiKeyStore,
        IRealtimeAudioCapture audioCapture,
        IDualRealtimeTranslationClient dualClient,
        Func<RealtimeSessionTuning>? tuningProvider = null,
        TimeProvider? timeProvider = null,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? tickInterval = null,
        Func<LanguagePair>? languagePairProvider = null,
        ReconnectPolicy? reconnectPolicy = null,
        Func<TimeSpan, TimeSpan>? reconnectJitter = null,
        SessionHealthThresholds? healthThresholds = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(audioCapture);
        ArgumentNullException.ThrowIfNull(dualClient);

        _apiKeyStore = apiKeyStore;
        _audioCapture = audioCapture;
        _dualClient = dualClient;
        _tuningProvider = tuningProvider ?? (() => RealtimeSessionTuning.Default);
        _timeProvider = timeProvider ?? TimeProvider.System;
        var policy = reconnectPolicy ?? ReconnectPolicy.Default;
        if (initialReconnectDelay is { } initialBackoff)
        {
            policy = policy with { InitialBackoff = initialBackoff };
        }

        _reconnectBudget = new ReconnectBudget(policy, _timeProvider, reconnectJitter);
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _languagePairProvider = languagePairProvider ?? (() => LanguagePair.JaEn);
        _healthBookkeeper = new SessionHealthBookkeeper(
            healthThresholds,
            () => _timeProvider.GetElapsedTime(0L),
            () => _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        _subtitlePipeline = new SessionSubtitlePipeline(
            _sync,
            _timeProvider,
            GetActiveFeed,
            () => _dualClient.Feed,
            update => SubtitleUpdated?.Invoke(this, update),
            () => AfterFailedSourceFallbackForTests);
        _audioRouting = new AudioRoutingCoordinator(
            _sync,
            _dualClient,
            _subtitlePipeline,
            _healthBookkeeper);
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    public event EventHandler<TranslationState>? StateChanged;

    public event EventHandler<RealtimeSubtitleUpdate>? SubtitleUpdated;

    /// <summary>ユーザー向け文言。サーバー文言は必ずサニタイズ済みのものを渡す。</summary>
    public event EventHandler<string>? MessageEncountered;

    /// <summary>受信停止監視の検知（診断のみ）。content は含まない。</summary>
    public event EventHandler<SessionHealthDetection>? HealthDetected;

    /// <summary>テスト・診断用の最新 snapshot（検知には使わない）。</summary>
    public SessionHealthSnapshot? LatestHealthSnapshot => _healthBookkeeper.LatestSnapshot;

    /// <summary>テスト・診断用の最新 termination diagnostic（各試行で最大 1 件）。</summary>
    public SessionTerminationDiagnostic? LatestTerminationDiagnostic => _healthBookkeeper.LatestTermination;

    public TranslationState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public AudioLossMetrics AudioLossMetrics
    {
        get
        {
            lock (_sync)
            {
                return _subtitlePipeline.AudioLossMetrics;
            }
        }
    }

    public async Task StartAsync()
    {
        Task? previous;
        CancellationTokenSource? previousCts;
        lock (_sync)
        {
            if (_state is not (TranslationState.Idle or TranslationState.Error))
            {
                return;
            }

            // await をまたぐ再入を防ぐため、受理直後に Connecting へ進める。
            _state = TranslationState.Connecting;
            previous = _sessionTask;
            previousCts = _sessionCts;
        }

        StateChanged?.Invoke(this, TranslationState.Connecting);

        // 旧世代の teardown が新しい接続や録音を落とさないよう、先に排水する。
        if (previousCts is not null)
        {
            await previousCts.CancelAsync().ConfigureAwait(false);
        }

        if (previous is not null)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // 旧世代の失敗があっても新しいセッション開始を妨げない。
            catch (Exception)
#pragma warning restore CA1031
            {
                // 旧 session task の例外はここで吸収する。
            }
        }

        int generation;
        CancellationTokenSource cts = new();
        var token = cts.Token;
        lock (_sync)
        {
            previousCts?.Dispose();
            _lifecycleGeneration += 1;
            generation = _lifecycleGeneration;
            _healthBookkeeper.BeginRecordingGeneration();
            _reconnectBudget.Reset();
            _subtitlePipeline.ResetAudioLoss();
            // 録音開始時点のペアを世代全体で固定する。録音中の設定変更は再接続でも反映しない
            // （VALIDATION: 停止→次の録音開始後にだけ新しいペアが反映される）。
            _sessionLanguagePair = _languagePairProvider();
            _sessionCts = cts;
        }

        SetState(TranslationState.Connecting);
        var sessionTask = Task.Run(() => RunSessionLoopAsync(generation, token), CancellationToken.None);
        lock (_sync)
        {
            _sessionTask = sessionTask;
        }
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_state == TranslationState.Idle)
            {
                return Task.CompletedTask;
            }

            // Closing 中の再入は進行中 Stop へ合流する。
            // Idle 以外で都度 CloseGracefully すると、先に Idle へ戻ったあと
            // 後続の ForceClose が次の録音の WebSocket を落とす。
            if (_stopTask is { } inFlight)
            {
                return inFlight;
            }

            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        // StopAsync が _sync を握ったまま同期実行しないよう、一度外へ出す。
        await Task.Yield();

        try
        {
            Task? sessionTask;
            CancellationTokenSource? cts;
            lock (_sync)
            {
                if (_state == TranslationState.Idle)
                {
                    return;
                }

                _lifecycleGeneration += 1;
                sessionTask = _sessionTask;
                _sessionTask = null;
                cts = _sessionCts;
                _sessionCts = null;
            }

            RecordHealthTermination(SessionTerminationKind.UserStopped);
            SetState(TranslationState.Closing);

            // 先に音声と session consumer を止め、close drain イベントを破棄されないようにする。
            // generation を上げたまま consumer が生きていると、commit/session.close の
            // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
            await _audioCapture.StopAsync().ConfigureAwait(false);
            if (cts is not null)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }

            if (sessionTask is not null)
            {
                try
                {
                    await sessionTask.ConfigureAwait(false);
                }
#pragma warning disable CA1031 // stop 中の旧世代失敗は Idle 遷移を妨げない。
                catch (Exception)
#pragma warning restore CA1031
                {
                    // session loop の例外は停止完了を阻まない。
                }
            }

            cts?.Dispose();

            try
            {
                await _dualClient.CloseGracefullyAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // graceful close が失敗しても force close で必ず解放する。
            catch (Exception)
#pragma warning restore CA1031
            {
                await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            }

            // commit / session.close 中に届いた最終 delta を assembler へ取り込む。
            await _subtitlePipeline.IngestStopDrainEventsAsync().ConfigureAwait(false);

            // 停止時点で完全ペアが残っていれば確定して見せる（オプトイン字幕記録も含む）。
            _subtitlePipeline.FlushPendingFinalizeIfNeeded();

            lock (_sync)
            {
                _sessionLanguagePair = null;
                _subtitlePipeline.DeactivateLanguagePair();
            }

            SetState(TranslationState.Idle);
        }
        finally
        {
            lock (_sync)
            {
                _stopTask = null;
            }
        }
    }

    /// <summary>録音中の prompt/keywords/delay 変更を原文接続へ反映する。</summary>
    public async Task ApplyTuningChangeAsync()
    {
        if (State != TranslationState.Listening)
        {
            return;
        }

        try
        {
            LanguagePair? activePair;
            lock (_sync)
            {
                activePair = _subtitlePipeline.ActiveLanguagePair;
            }

            await _dualClient.UpdateTranscriptionTuningAsync(
                _tuningProvider().ForPair(activePair ?? LanguagePair.JaEn)).ConfigureAwait(false);
        }
        catch (RealtimeTranslationException)
        {
            // 反映失敗は録音を止めるほどではない。次の再接続で新しい tuning が乗る。
        }
    }

    public void Dispose()
    {
        // OnExit / プロセス終了は StopAsync を経由しないことがある。
        // flush より先に generation を進め CTS を切って取り込みをフェンスし、
        // その時点の assembler 状態だけを ShouldFinalize する。
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _lifecycleGeneration += 1;
            cts = _sessionCts;
            _sessionCts = null;
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

        try
        {
            _subtitlePipeline.FlushPendingFinalizeIfNeeded();
        }
#pragma warning disable CA1031 // Dispose 経路では例外を外へ出さない。
        catch (Exception)
#pragma warning restore CA1031
        {
            // flush 失敗でも破棄完了は継続する。
        }

        _audioRouting.Dispose();
    }

    private async Task RunSessionLoopAsync(int generation, CancellationToken cancellationToken)
    {
        while (IsCurrentGeneration(generation) && !cancellationToken.IsCancellationRequested)
        {
            Exception? recoverableError = null;
            try
            {
                await ConnectAndStreamAsync(generation, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RealtimeTranslationException error) when (!error.IsRecoverable)
            {
                if (!IsCurrentGeneration(generation))
                {
                    // stop 中の teardown が起こした失敗を、ユーザー向けエラーに昇格させない。
                    return;
                }

                RecordHealthTermination(SessionTerminationKindMapping.FromException(error));
                await TearDownStreamingAsync().ConfigureAwait(false);
                // epoch を捨てる前に完全ペアを確定し、オプトイン字幕記録へ渡す。
                _subtitlePipeline.FlushPendingFinalizeIfNeeded();
                EnterError(error.Message);
                return;
            }
#pragma warning disable CA1031 // 想定外の失敗でも session task を落とさず再接続へ倒す。
            catch (Exception error)
#pragma warning restore CA1031
            {
                // recoverable transport failure / 音声デバイス失敗。予算判定が wait を
                // 返す場合だけ終了診断として記録する（予算超過ならそちらの kind で記録）。
                recoverableError = error;
            }

            if (!IsCurrentGeneration(generation) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ReconnectDecision decision;
            lock (_sync)
            {
                decision = _reconnectBudget.RecordFailure();
            }

            if (decision.Kind != ReconnectDecisionKind.Wait)
            {
                RecordHealthTermination(
                    decision.Kind == ReconnectDecisionKind.BudgetExhausted
                        ? SessionTerminationKind.ReconnectBudgetExhausted
                        : SessionTerminationKind.ReconnectAttemptLimit);
                await TearDownStreamingAsync().ConfigureAwait(false);
                _subtitlePipeline.FlushPendingFinalizeIfNeeded();
                EnterError(UserCopy.Current.Text(
                    decision.Kind == ReconnectDecisionKind.BudgetExhausted
                        ? "error.reconnectBudgetExhausted"
                        : "error.reconnectLimit"));
                return;
            }

            // recoverable 失敗も終了診断として記録する（診断のみ、挙動は変えない）。
            if (recoverableError is { } recoverable)
            {
                RecordHealthTermination(SessionTerminationKindMapping.FromException(recoverable));
            }

            SetState(TranslationState.Reconnecting);
            await TearDownStreamingAsync().ConfigureAwait(false);

            try
            {
                await Task.Delay(decision.Delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAndStreamAsync(int generation, CancellationToken cancellationToken)
    {
        // _dualClient.ReservedEpoch は Dual 側の lock を取るため _sync 保持中には読まない。
        var entryAttemptEpoch = _dualClient.ReservedEpoch;
        lock (_sync)
        {
            // RequireApiKey 失敗（missing key）も試行の終了診断へ乗せるため先に記録する。
            _healthBookkeeper.BeginAttempt(_lifecycleGeneration, entryAttemptEpoch);
        }

        var apiKey = RequireApiKey();
        SetState(TranslationState.Connecting);

        LanguagePair languagePair;
        lock (_sync)
        {
            languagePair = _sessionLanguagePair ?? _languagePairProvider();
        }

        try
        {
            await _dualClient.StartAsync(
                apiKey,
                _tuningProvider().ForPair(languagePair),
                languagePair,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // StartAsync は network 処理の前に connectionEpoch を予約済み。失敗した
            // handshake の epoch で診断するため読み直す。
            var reservedEpoch = _dualClient.ReservedEpoch;
            lock (_sync)
            {
                _healthBookkeeper.UpdateAttemptEpoch(reservedEpoch);
            }
            throw;
        }
        var reservedAfterStart = _dualClient.ReservedEpoch;
        lock (_sync)
        {
            _healthBookkeeper.UpdateAttemptEpoch(reservedAfterStart);
        }
        if (!IsCurrentGeneration(generation))
        {
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        var feed = _dualClient.Feed;
        var epoch = feed.Epoch;
        // 再接続時 BeginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ ShouldFinalize が届かない。
        _subtitlePipeline.FlushPendingFinalizeIfNeeded();
        lock (_sync)
        {
            _healthBookkeeper.UpdateAttemptEpoch(epoch);
            _subtitlePipeline.BeginEpoch(epoch, languagePair);
            _activeFeed = feed;
        }

        await _dualClient.ResetAudioRoutingAsync().ConfigureAwait(false);
        await _audioCapture.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrentGeneration(generation))
        {
            await _audioCapture.StopAsync().ConfigureAwait(false);
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        SetState(TranslationState.Listening);
        BeforeHealthGenerationBeginForTests?.Invoke();
        var staleGeneration = false;
        lock (_sync)
        {
            if (generation != _lifecycleGeneration)
            {
                // SetState とこの lock の間に StopAsync が世代を進めた場合、
                // 止められた世代の monitor を再活性化しない。
                staleGeneration = true;
            }
            else
            {
                _reconnectBudget.RecordListening();
                _healthBookkeeper.BeginGeneration(generation, epoch, feed.DeliveryState);
            }
        }
        if (staleGeneration)
        {
            await _audioCapture.StopAsync().ConfigureAwait(false);
            await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            return;
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var audioTask = FeedAudioAsync(generation, streamCts.Token);
        var consume = ConsumeEventsAsync(generation, feed, streamCts.Token);
        var tick = RunTickerAsync(streamCts.Token);

        var first = await Task.WhenAny(audioTask, consume).ConfigureAwait(false);
        await streamCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(SuppressCancellation(audioTask), SuppressCancellation(consume), SuppressCancellation(tick))
            .ConfigureAwait(false);
        await first.ConfigureAwait(false);
    }

    private static async Task SuppressCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 敗者側の失敗は勝者側の結果で報告するため握り潰す。
        catch (Exception)
#pragma warning restore CA1031
        {
            // 勝者の例外だけを再接続判定に使う。
        }
    }

    private async Task FeedAudioAsync(int generation, CancellationToken cancellationToken)
    {
        await foreach (var frame in _audioCapture.Frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!IsCurrentGeneration(generation) || State != TranslationState.Listening)
            {
                return;
            }

            lock (_sync)
            {
                _healthBookkeeper.RecordCapture(
                    Pcm16AudioActivity.NormalizedPeakAmplitude(frame.Pcm16.Span)
                        > _healthBookkeeper.Thresholds.AudioActivityPeakFloor);
            }

            var queueWaitMilliseconds = Math.Max(
                0,
                (int)_timeProvider.GetElapsedTime(frame.CapturedAtTimestamp).TotalMilliseconds);
            var atMilliseconds = Math.Max(
                0,
                (long)_timeProvider.GetElapsedTime(_startTimestamp).TotalMilliseconds);
            AudioLossObservation observation;
            RealtimeSubtitleUpdate? invalidation = null;
            lock (_sync)
            {
                observation = _subtitlePipeline.ObserveAudio(
                    frame.Generation,
                    frame.Sequence,
                    frame.DiscardedMilliseconds,
                    queueWaitMilliseconds,
                    atMilliseconds);
                if (observation.DidLose)
                {
                    invalidation = _subtitlePipeline.MarkAudioLoss();
                }
            }

            if (invalidation is { } lossUpdate)
            {
                _subtitlePipeline.EmitSubtitleUpdate(lossUpdate);
                await _audioRouting.ResetAfterAudioLossAsync(BeforeAudioLossRoutingResetForTests)
                    .ConfigureAwait(false);
            }

            if (observation.ShouldReconnect)
            {
                throw new RealtimeTranslationException(
                    RealtimeTranslationErrorKind.RecoverableTransportFailure,
                    UserCopy.Current.Text("error.audioInputStopped"));
            }

            lock (_sync)
            {
                _healthBookkeeper.RecordSendStart();
            }

            await _dualClient.AppendAudioFrameAsync(frame.Pcm16, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _healthBookkeeper.RecordSendSuccess();
            }
        }

        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        throw new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            UserCopy.Current.Text("error.audioInputStopped"));
    }

    private async Task ConsumeEventsAsync(
        int generation,
        RealtimeEventFeed feed,
        CancellationToken cancellationToken)
    {
        var events = feed.Events;
        while (true)
        {
            var waitToRead = events.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(waitToRead, feed.DeliveryState.Completion).ConfigureAwait(false);

            if (feed.DeliveryState.DidLoseEvents)
            {
                _subtitlePipeline.HandleEventLoss(feed);
                if (IsCurrentGeneration(generation))
                {
                    throw feed.DeliveryState.ToException();
                }

                return;
            }

            while (events.TryRead(out var streamEvent))
            {
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            if (streamEvent.Epoch != feed.Epoch)
            {
                continue;
            }

            if (feed.DeliveryState.DidLoseEvents)
            {
                _subtitlePipeline.HandleEventLoss(feed);
                if (IsCurrentGeneration(generation))
                {
                    throw feed.DeliveryState.ToException();
                }

                return;
            }

            if (streamEvent.Event is RealtimeTranslationServerEvent.ServerError error)
            {
                var classification = EventDeliveryState.Classify(error);
                if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                {
                    continue;
                }

                feed.DeliveryState.TryRecordTermination(classification);
                throw feed.DeliveryState.ToException();
            }

            // delta 文字列は monitor へ渡さない（検知は到着・進捗の事実だけを見る）。
            lock (_sync)
            {
                switch (streamEvent.Event)
                {
                    case RealtimeTranslationServerEvent.InputTranscriptDelta input
                        when streamEvent.Lane.IsSource && input.Delta.Length > 0:
                        _healthBookkeeper.RecordSourceProgress();
                        break;
                    case RealtimeTranslationServerEvent.OutputTranscriptDelta output
                        when output.Delta.Length > 0:
                        _healthBookkeeper.RecordTranslationProgress(streamEvent.Lane);
                        break;
                }
            }

            if (streamEvent.Event is RealtimeTranslationServerEvent.InputTranscriptFailed failed)
            {
                RealtimeServerErrorClassification classification =
                    RealtimeServerErrorClassification.ClassifyTranscriptionFailure(
                        failed.ErrorType,
                        failed.Code);
                RealtimeSubtitleUpdate? invalidation;
                lock (_sync)
                {
                    invalidation = _subtitlePipeline.DiscardFailedSource(failed.ItemId, failed.EventId);
                }
                feed.DeliveryState.NoteSourceFailureConsumed();

                if (invalidation is { } failedUpdate)
                {
                    _subtitlePipeline.EmitSubtitleUpdate(failedUpdate);
                    await _audioRouting.ResetForNextSegmentAsync().ConfigureAwait(false);
                }

                if (classification.Disposition == RealtimeServerErrorDisposition.KeepAlive)
                {
                    continue;
                }

                feed.DeliveryState.TryRecordTermination(classification);
                throw feed.DeliveryState.ToException();
            }

            BeforeAssemblerIngestForTests?.Invoke();

            RealtimeSubtitleProcessingResult? result = null;
            var lostAfterRouting = false;
            lock (_sync)
            {
                // Dispose/Stop が generation を進めたあとに、取り出し済みイベントで
                // assembler を更新しない（flush 後の完全ペア欠落を防ぐ）。
                if (_lifecycleGeneration != generation)
                {
                    return;
                }

                if (feed.DeliveryState.DidLoseEvents)
                {
                    lostAfterRouting = true;
                }
                else
                {
                    result = _subtitlePipeline.Process(streamEvent);
                }
            }

            if (lostAfterRouting)
            {
                _subtitlePipeline.HandleEventLoss(feed);
                if (IsCurrentGeneration(generation))
                {
                    throw feed.DeliveryState.ToException();
                }

                return;
            }

            if (result is { } processed)
            {
                foreach (var update in processed.Updates)
                {
                    _subtitlePipeline.EmitSubtitleUpdate(update);
                }

                switch (processed.RoutingAction)
                {
                    case RealtimeSubtitleRoutingAction.None:
                        if (processed.IngestedUpdate.ShouldFinalize)
                        {
                            await _audioRouting.ResetForNextSegmentAsync().ConfigureAwait(false);
                        }

                        break;
                    case RealtimeSubtitleRoutingAction.Select select:
                        await _audioRouting.ApplyAsync(
                            select,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case RealtimeSubtitleRoutingAction.Switch @switch:
                        await _audioRouting.ApplyAsync(
                            @switch,
                            cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            }

            if (feed.DeliveryState.Completion.IsCompleted
                || !await waitToRead.ConfigureAwait(false))
            {
                break;
            }
        }

        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        _subtitlePipeline.DiscardFailedSourceIfNeeded(feed);
        if (feed.DeliveryState.Termination != EventDeliveryTermination.None)
        {
            throw feed.DeliveryState.ToException();
        }

        throw new RealtimeTranslationException(
            RealtimeTranslationErrorKind.RecoverableTransportFailure,
            UserCopy.Current.Text("error.eventStreamStopped"));
    }

    private async Task RunTickerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_tickInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RealtimeSubtitleUpdate? update;
            var feed = GetActiveFeed();
            IReadOnlyList<SessionHealthDetection> healthDetections;
            lock (_sync)
            {
                healthDetections = _healthBookkeeper.Tick(feed);
                if (feed is { DeliveryState.DidLoseEvents: true })
                {
                    update = null;
                }
                else
                {
                    update = _subtitlePipeline.Tick();
                }
            }

            foreach (var detection in healthDetections)
            {
                HealthDetected?.Invoke(this, detection);
            }

            if (feed is { DeliveryState.DidLoseEvents: true })
            {
                _subtitlePipeline.HandleEventLoss(feed);
                continue;
            }

            if (update is { } value)
            {
                _subtitlePipeline.EmitSubtitleUpdate(value);
                BeforeRoutingResetForTests?.Invoke();
                await _audioRouting.ResetForNextSegmentAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task TearDownStreamingAsync()
    {
        // consumer が ServerError で抜けたあとの未読 delta を先に取り込む。
        // Complete 後の Stop drain だけに頼ると、再接続 Start が channel を
        // 張り替えたときに訳文が消える。
        var feed = GetActiveFeed();
        if (feed is { DeliveryState.DidLoseEvents: true })
        {
            _subtitlePipeline.HandleEventLoss(feed);
        }

        _subtitlePipeline.IngestAlreadyQueuedEvents();

        lock (_sync)
        {
            // recoverable・正常終了を問わず接続 teardown で健康世代を閉じる。
            _healthBookkeeper.EndGeneration();
        }

        try
        {
            await _audioCapture.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _dualClient.ForceCloseAsync().ConfigureAwait(false);
            }
            finally
            {
                // merge pump 停止後に connection から遅れて乗った delta も回収する。
                // StartAsync が channel を差し替える前に読まないと消える。
                // ForceClose 中の遅延 source でも言語境界を分割できるよう、
                // ペアと tracker は二度目の回収が終わるまで残す。通信先は変えない。
                _subtitlePipeline.IngestAlreadyQueuedEvents();
                if (feed is { DeliveryState.HasPendingSourceFailure: true })
                {
                    _subtitlePipeline.DiscardFailedSourceIfNeeded(feed);
                }
                lock (_sync)
                {
                    _subtitlePipeline.DeactivateLanguagePair();
                    _subtitlePipeline.ClearBoundaryCandidate();
                }
            }
        }
    }

    /// <summary>
    /// セッションループ終了・停止時の診断。kind は自前 enum のみ（生 message は渡さない）。
    /// 各世代で最初の終了経路だけを記録する。
    /// </summary>
    private void RecordHealthTermination(SessionTerminationKind kind)
    {
        // handshake 中の stop では Dual 側の予約 epoch が attempt の意図 epoch より
        // 先に進むため、_sync 保持前に予約 epoch を読んでおく。
        var reservedEpoch = _dualClient.ReservedEpoch;
        lock (_sync)
        {
            _healthBookkeeper.RecordTermination(kind, reservedEpoch);
        }
    }

    private string RequireApiKey() => RealtimeApiKey.Require(_apiKeyStore.Load());

    private RealtimeEventFeed? GetActiveFeed()
    {
        lock (_sync)
        {
            return _activeFeed;
        }
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_sync)
        {
            return _lifecycleGeneration == generation;
        }
    }

    private void EnterError(string message)
    {
        SetState(TranslationState.Error);
        MessageEncountered?.Invoke(this, message);
    }

    private void SetState(TranslationState state)
    {
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
