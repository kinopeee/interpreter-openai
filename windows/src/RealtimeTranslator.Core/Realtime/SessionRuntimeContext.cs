using System;
using System.Threading.Tasks;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// <see cref="InterpretationSession"/> の実行時状態とコラボレータの共有コンテキスト。
/// <c>_sync</c> ロック契約を引き継ぎ、独自ロックは持たない。
/// SessionLoopRunner / SessionStreamRunner / SessionAudioFeeder /
/// SessionEventConsumer / SessionTicker がここ経由で状態と依存を参照する。
/// プロパティはすべて内部で <c>_sync</c> を取るため、呼び出し側が <see cref="Sync"/> を
/// 保持したまま複数プロパティを触っても再入で atomic 性は保たれる。
/// </summary>
internal sealed class SessionRuntimeContext
{
    internal static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _sync = new();
    private int _lifecycleGeneration;
    private TranslationState _state = TranslationState.Idle;
    private LanguagePair? _sessionLanguagePair;
    private RealtimeEventFeed? _activeFeed;

    internal SessionRuntimeContext(
        IApiKeyStore apiKeyStore,
        IRealtimeAudioCapture audioCapture,
        IDualRealtimeTranslationClient dualClient,
        Func<RealtimeSessionTuning> tuningProvider,
        TimeProvider timeProvider,
        TimeSpan? initialReconnectDelay,
        TimeSpan? tickInterval,
        Func<LanguagePair> languagePairProvider,
        ReconnectPolicy? reconnectPolicy,
        Func<TimeSpan, TimeSpan>? reconnectJitter,
        SessionHealthThresholds? healthThresholds
    )
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(audioCapture);
        ArgumentNullException.ThrowIfNull(dualClient);
        ArgumentNullException.ThrowIfNull(tuningProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(languagePairProvider);

        ApiKeyStore = apiKeyStore;
        AudioCapture = audioCapture;
        DualClient = dualClient;
        TuningProvider = tuningProvider;
        TimeProvider = timeProvider;
        TickInterval = tickInterval ?? DefaultTickInterval;
        LanguagePairProvider = languagePairProvider;

        var policy = reconnectPolicy ?? ReconnectPolicy.Default;
        if (initialReconnectDelay is { } initialBackoff)
        {
            policy = policy with { InitialBackoff = initialBackoff };
        }

        ReconnectBudget = new ReconnectBudget(policy, TimeProvider, reconnectJitter);
        StartTimestamp = TimeProvider.GetTimestamp();
        HealthBookkeeper = new SessionHealthBookkeeper(
            healthThresholds,
            () => TimeProvider.GetElapsedTime(0L),
            () => TimeProvider.GetUtcNow().ToUnixTimeSeconds()
        );
        SubtitlePipeline = new SessionSubtitlePipeline(
            _sync,
            TimeProvider,
            GetActiveFeed,
            () => DualClient.Feed,
            EmitSubtitleUpdate,
            () => AfterFailedSourceFallbackForTests
        );
        AudioRouting = new AudioRoutingCoordinator(_sync, DualClient, SubtitlePipeline, HealthBookkeeper);
    }

    /// <summary>セッション横断のロック。pipeline / routing と同一オブジェクトを共有する。</summary>
    internal object Sync => _sync;

    internal IApiKeyStore ApiKeyStore { get; }

    internal IRealtimeAudioCapture AudioCapture { get; }

    internal IDualRealtimeTranslationClient DualClient { get; }

    internal Func<RealtimeSessionTuning> TuningProvider { get; }

    internal TimeProvider TimeProvider { get; }

    internal ReconnectBudget ReconnectBudget { get; }

    internal TimeSpan TickInterval { get; }

    internal Func<LanguagePair> LanguagePairProvider { get; }

    internal long StartTimestamp { get; }

    internal SessionHealthBookkeeper HealthBookkeeper { get; }

    internal SessionSubtitlePipeline SubtitlePipeline { get; }

    internal AudioRoutingCoordinator AudioRouting { get; }

    internal int LifecycleGeneration
    {
        get
        {
            lock (_sync)
            {
                return _lifecycleGeneration;
            }
        }
    }

    internal void AdvanceLifecycleGeneration()
    {
        lock (_sync)
        {
            _lifecycleGeneration += 1;
        }
    }

    internal bool IsCurrentGeneration(int generation)
    {
        lock (_sync)
        {
            return _lifecycleGeneration == generation;
        }
    }

    internal TranslationState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>状態遷移。重複遷移は通知しない。イベントはロック解放後に発火する。</summary>
    internal void SetState(TranslationState state)
    {
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        OnStateChanged?.Invoke(state);
    }

    /// <summary>
    /// Idle / Error のときだけ Connecting へ進め、ロック解放後に 1 回通知する。
    /// <paramref name="onAccepted"/> は受理と同じロック内で実行し、受理時点の状態を取り出す。
    /// </summary>
    internal bool TryBeginConnecting(Action onAccepted)
    {
        lock (_sync)
        {
            if (_state is not (TranslationState.Idle or TranslationState.Error))
            {
                return false;
            }

            _state = TranslationState.Connecting;
            onAccepted();
        }

        OnStateChanged?.Invoke(TranslationState.Connecting);
        return true;
    }

    /// <summary>現在の録音世代で使う言語ペア。Start 時に固定し、再接続でも settings の変更を取り込まない。</summary>
    internal LanguagePair? SessionLanguagePair
    {
        get
        {
            lock (_sync)
            {
                return _sessionLanguagePair;
            }
        }
        set
        {
            lock (_sync)
            {
                _sessionLanguagePair = value;
            }
        }
    }

    internal RealtimeEventFeed? ActiveFeed
    {
        get
        {
            lock (_sync)
            {
                return _activeFeed;
            }
        }
        set
        {
            lock (_sync)
            {
                _activeFeed = value;
            }
        }
    }

    internal RealtimeEventFeed? GetActiveFeed() => ActiveFeed;

    /// <summary>状態遷移の転送先。ファサードが公開イベントへ中継する。</summary>
    internal Action<TranslationState>? OnStateChanged { get; set; }

    /// <summary>字幕更新の転送先。ファサードが公開イベントへ中継する。</summary>
    internal Action<RealtimeSubtitleUpdate>? OnSubtitleUpdated { get; set; }

    /// <summary>ユーザー向け文言の転送先。サーバー文言は必ずサニタイズ済みのものを渡す。</summary>
    internal Action<string>? OnMessageEncountered { get; set; }

    /// <summary>受信停止監視の検知（診断のみ）の転送先。content は含まない。</summary>
    internal Action<SessionHealthDetection>? OnHealthDetected { get; set; }

    /// <summary>テスト用。generation 確認後・assembler 更新前に差し込む。</summary>
    internal Action? BeforeAssemblerIngestForTests { get; set; }

    internal Action? BeforeRoutingResetForTests { get; set; }

    internal Func<Task>? BeforeAudioLossRoutingResetForTests { get; set; }

    internal Action? AfterFailedSourceFallbackForTests { get; set; }

    /// <summary>テスト用。Listening 直前の monitor 世代開始 lock 直前に差し込む。</summary>
    internal Action? BeforeHealthGenerationBeginForTests { get; set; }

    /// <summary>
    /// セッションループ終了・停止時の診断。kind は自前 enum のみ（生 message は渡さない）。
    /// 各世代で最初の終了経路だけを記録する。
    /// </summary>
    internal void RecordHealthTermination(SessionTerminationKind kind)
    {
        // handshake 中の stop では Dual 側の予約 epoch が attempt の意図 epoch より
        // 先に進むため、_sync 保持前に予約 epoch を読んでおく。
        var reservedEpoch = DualClient.ReservedEpoch;
        lock (_sync)
        {
            HealthBookkeeper.RecordTermination(kind, reservedEpoch);
        }
    }

    internal void EnterError(string message)
    {
        SetState(TranslationState.Error);
        OnMessageEncountered?.Invoke(message);
    }

    private void EmitSubtitleUpdate(RealtimeSubtitleUpdate update) => OnSubtitleUpdated?.Invoke(update);
}
