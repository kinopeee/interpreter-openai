import Foundation

/// `InterpretationSession` の実行時状態とコラボレータの共有コンテキスト。
/// @MainActor 閉じ込め。SessionLoopRunner / SessionStreamRunner /
/// SessionAudioFeeder / SessionEventConsumer / SessionTicker がここ経由で
/// 状態と依存を参照する。独自の同期機構は持たない。
@MainActor
final class SessionRuntimeContext {
    /// 現在の録音世代で使う言語ペア。Start 時に固定し、再接続でも settings の変更を取り込まない。
    var sessionLanguagePair: LanguagePair?

    var lifecycleGeneration = 0

    var state: TranslationState = .idle {
        didSet {
            guard oldValue != state else { return }
            onStateChanged?(state)
        }
    }

    var activeFeed: EventFeed?

    let apiKeyStore: any APIKeyStore
    let audioCapture: any RealtimeAudioCaptureServicing
    let dualClient: any DualRealtimeTranslationClienting
    let activeTickerIntervalNanoseconds: UInt64
    let tuningProvider: @MainActor () -> RealtimeSessionTuning
    let languagePairProvider: @MainActor () -> LanguagePair
    var reconnectBudget: ReconnectBudget
    let healthBookkeeper: SessionHealthBookkeeper
    let subtitlePipeline: SessionSubtitlePipeline
    let ticker: SessionTicker

    var onStateChanged: ((TranslationState) -> Void)?
    var onSubtitleUpdate: ((SubtitleSnapshot) -> Void)?
    var onMessageEncountered: ((String) -> Void)?
    var onHealthDetection: ((SessionHealthDetection) -> Void)?

    var afterFailedSourceTerminationForTests: (() -> Void)?

    init(
        apiKeyStore: any APIKeyStore,
        audioCapture: any RealtimeAudioCaptureServicing,
        dualClient: any DualRealtimeTranslationClienting,
        aggregator: SubtitleAggregator,
        activeTickerIntervalNanoseconds: UInt64,
        postStopSubtitleRetentionNanoseconds: UInt64,
        tuningProvider: @escaping @MainActor () -> RealtimeSessionTuning,
        languagePairProvider: @escaping @MainActor () -> LanguagePair,
        reconnectBudget: ReconnectBudget,
        displayScheduler: SubtitleDisplayScheduler,
        healthNow: @escaping ReconnectBudget.Now,
        wallClockNow: @escaping @Sendable () -> TimeInterval,
        healthThresholds: SessionHealthThresholds
    ) {
        self.apiKeyStore = apiKeyStore
        self.audioCapture = audioCapture
        self.dualClient = dualClient
        self.activeTickerIntervalNanoseconds = activeTickerIntervalNanoseconds
        self.tuningProvider = tuningProvider
        self.languagePairProvider = languagePairProvider
        self.reconnectBudget = reconnectBudget
        self.healthBookkeeper = SessionHealthBookkeeper(
            thresholds: healthThresholds,
            healthNow: healthNow,
            wallClockNow: wallClockNow,
            reservedEpochProvider: { await dualClient.reservedEpoch }
        )
        self.subtitlePipeline = SessionSubtitlePipeline(
            aggregator: aggregator,
            displayScheduler: displayScheduler,
            postStopSubtitleRetentionNanoseconds: postStopSubtitleRetentionNanoseconds,
            healthBookkeeper: self.healthBookkeeper,
            dualClient: dualClient
        )
        self.ticker = SessionTicker()
        self.subtitlePipeline.bind(
            activeFeed: { [weak self] in self?.activeFeed },
            lifecycleGeneration: { [weak self] in self?.lifecycleGeneration ?? 0 },
            state: { [weak self] in self?.state ?? .idle },
            publishUpdate: { [weak self] snapshot in
                self?.onSubtitleUpdate?(snapshot)
            }
        )
    }

    func isCurrentGeneration(_ generation: Int) -> Bool {
        generation == lifecycleGeneration
    }

    func publishSubtitles() {
        onSubtitleUpdate?(subtitlePipeline.aggregator.snapshot())
    }

    func enterError(_ error: Error) {
        enterErrorMessage(error.localizedDescription)
    }

    func enterErrorMessage(_ message: String) {
        state = .error
        subtitlePipeline.aggregator.setStatusBanner(message)
        publishSubtitles()
        onMessageEncountered?(message)
    }
}
