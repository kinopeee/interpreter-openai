import Foundation

@MainActor
protocol InterpretationSessionDelegate: AnyObject {
    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateState state: TranslationState
    )
    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateSubtitles snapshot: SubtitleSnapshot
    )
    func interpretationSession(
        _ session: InterpretationSession,
        didEncounterMessage message: String
    )
    /// 受信停止監視の検知（診断のみ）。
    func interpretationSession(
        _ session: InterpretationSession,
        didEmitHealthDetection detection: SessionHealthDetection
    )
}

extension InterpretationSessionDelegate {
    func interpretationSession(
        _: InterpretationSession,
        didEmitHealthDetection _: SessionHealthDetection
    ) {}
}

/// 録音セッションの公開ファサード。ライフサイクル（start/stop）と
/// delegate 転送を担い、接続試行・ストリーミング・再接続・teardown は
/// SessionLoopRunner 以下の内部型へ委譲する。
@MainActor
final class InterpretationSession {
    /// 録音停止後、最後の字幕ペアを読み取れるよう残す時間。
    static let defaultPostStopSubtitleRetentionNanoseconds: UInt64 = 5_000_000_000

    weak var delegate: InterpretationSessionDelegate?

    private let context: SessionRuntimeContext
    private let loopRunner: SessionLoopRunner
    private var stopTask: Task<Void, Never>?
    private var sessionTask: Task<Void, Never>?

    var state: TranslationState { context.state }

    /// テスト・診断用の最新 snapshot（検知には使わない）。
    var latestHealthSnapshot: SessionHealthSnapshot? {
        context.healthBookkeeper.latestHealthSnapshot
    }
    /// テスト・診断用の最新 termination diagnostic（各試行で最大 1 件）。
    var latestHealthTermination: SessionTerminationDiagnostic? {
        context.healthBookkeeper.latestHealthTermination
    }

    var audioLossMetrics: AudioLossMetrics {
        context.subtitlePipeline.audioLossMetrics
    }

    internal var afterFailedSourceTerminationForTests: (() -> Void)? {
        get { context.afterFailedSourceTerminationForTests }
        set { context.afterFailedSourceTerminationForTests = newValue }
    }

    init(
        apiKeyStore: any APIKeyStore,
        audioCapture: any RealtimeAudioCaptureServicing = RealtimeAudioCaptureService(),
        dualClient: any DualRealtimeTranslationClienting = DualRealtimeTranslationClient(),
        aggregator: SubtitleAggregator = SubtitleAggregator(),
        activeTickerIntervalNanoseconds: UInt64 = 200_000_000,
        postStopSubtitleRetentionNanoseconds: UInt64 = InterpretationSession
            .defaultPostStopSubtitleRetentionNanoseconds,
        tuningProvider: @escaping @MainActor () -> RealtimeSessionTuning = { .default },
        languagePairProvider: @escaping @MainActor () -> LanguagePair = { .jaEn },
        reconnectBudget: ReconnectBudget = ReconnectBudget(),
        displayScheduler: SubtitleDisplayScheduler = SubtitleDisplayScheduler(),
        healthNow: @escaping ReconnectBudget.Now = ReconnectBudget.continuousNow,
        wallClockNow: @escaping @Sendable () -> TimeInterval = { Date().timeIntervalSince1970 },
        healthThresholds: SessionHealthThresholds = SessionHealthThresholds()
    ) {
        self.context = SessionRuntimeContext(
            apiKeyStore: apiKeyStore,
            audioCapture: audioCapture,
            dualClient: dualClient,
            aggregator: aggregator,
            activeTickerIntervalNanoseconds: activeTickerIntervalNanoseconds,
            postStopSubtitleRetentionNanoseconds: postStopSubtitleRetentionNanoseconds,
            tuningProvider: tuningProvider,
            languagePairProvider: languagePairProvider,
            reconnectBudget: reconnectBudget,
            displayScheduler: displayScheduler,
            healthNow: healthNow,
            wallClockNow: wallClockNow,
            healthThresholds: healthThresholds
        )
        self.loopRunner = SessionLoopRunner(context: context)
        context.onStateChanged = { [weak self] state in
            guard let self else { return }
            self.delegate?.interpretationSession(self, didUpdateState: state)
        }
        context.onSubtitleUpdate = { [weak self] snapshot in
            guard let self else { return }
            self.delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
        }
        context.onMessageEncountered = { [weak self] message in
            guard let self else { return }
            self.delegate?.interpretationSession(self, didEncounterMessage: message)
        }
        context.onHealthDetection = { [weak self] detection in
            guard let self else { return }
            self.delegate?.interpretationSession(self, didEmitHealthDetection: detection)
        }
    }

    func start() async {
        guard context.state == .idle || context.state == .error else { return }
        context.subtitlePipeline.cancelPostStopClear()

        // 旧sessionTaskが世代不一致のforceClose/stopを後から走らせ、
        // 新しい接続やマイクを落とさないよう先に排水する。
        let previousSessionTask = sessionTask
        previousSessionTask?.cancel()
        sessionTask = nil
        if let previousSessionTask {
            await previousSessionTask.value
        }

        context.lifecycleGeneration += 1
        let generation = context.lifecycleGeneration
        context.healthBookkeeper.beginRecordingGeneration()
        context.reconnectBudget.reset()
        context.subtitlePipeline.resetAudioLoss()
        // 録音開始時点のペアを世代全体で固定する。録音中の設定変更は再接続でも反映しない
        // （VALIDATION: 停止→次の録音開始後にだけ新しいペアが反映される）。
        context.sessionLanguagePair = context.languagePairProvider()
        context.state = .connecting
        context.subtitlePipeline.aggregator.reset()
        context.subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.connecting"))
        context.publishSubtitles()

        sessionTask = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.loopRunner.run(generation: generation)
        }
    }

    func stop() async {
        if let stopTask {
            await stopTask.value
            return
        }
        guard context.state != .idle else { return }

        let task = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.performStop()
        }
        stopTask = task
        await task.value
        stopTask = nil
    }

    /// 録音中に設定画面から変更されたprompt/keywordsを原文セッションへ反映する。
    func applyTuningChange() async {
        guard context.state == .listening else { return }
        let tuning = context.tuningProvider().forPair(
            context.subtitlePipeline.activeLanguagePair ?? .jaEn
        )
        do {
            try await context.dualClient.updateTranscriptionTuning(tuning)
        } catch {
            AppLogger.session.error(
                "Failed to update transcription tuning: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
        }
    }

    private func performStop() async {
        await context.healthBookkeeper.recordTermination(kind: .userStopped)
        // ingest を先に止め、既読を acknowledge させてから未読窓だけを武装する。
        // AsyncStream.finish() は未読を捨てるため、未消費の最新窓とこれ以降の close 窓を Dual 側で保持する。
        context.lifecycleGeneration += 1
        await context.dualClient.beginStopDrainCapture()
        context.state = .closing
        context.subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.closing"))
        context.publishSubtitles()

        let runningSessionTask = sessionTask
        runningSessionTask?.cancel()
        sessionTask = nil
        let pending = context.subtitlePipeline.takePendingUpdate()

        // 先に音声と session consumer を止め、close drain を破棄されないようにする。
        // generation を上げたまま consumer が生きていると、commit/session.close の
        // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
        await context.audioCapture.stop()
        if let runningSessionTask {
            await runningSessionTask.value
        }

        // スロットル中の旧 snapshot を先に適用し、その後の close drain で上書きする。
        if let pending {
            context.subtitlePipeline.renderNow(pending)
        }

        let drainedEvents = await context.dualClient.closeGracefully()
        if let feed = context.activeFeed {
            if feed.deliveryState.didLoseEvents {
                context.subtitlePipeline.handleEventLossIfNeeded(feed)
            } else {
                context.subtitlePipeline.ingestStopDrainEvents(drainedEvents, feed: feed)
            }
        }
        context.subtitlePipeline.clearBoundaryCandidate()
        if let feed = context.activeFeed, feed.deliveryState.hasPendingSourceFailure {
            context.subtitlePipeline.discardFailedSourceIfNeeded(feed)
        } else if context.subtitlePipeline.isCurrentSegmentTainted {
            let invalidation = context.subtitlePipeline.discardUnconfirmed()
            context.subtitlePipeline.renderNow(invalidation)
        } else {
            if let tickUpdate = context.subtitlePipeline.tickProcessor(now: Date()) {
                context.subtitlePipeline.renderNow(tickUpdate)
            }
            let snapshot = context.subtitlePipeline.aggregator.forceFinalize()
            delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
        }
        context.subtitlePipeline.aggregator.setStatusBanner(nil)
        context.sessionLanguagePair = nil
        context.subtitlePipeline.deactivateLanguagePair()
        context.state = .idle
        context.publishSubtitles()
        context.ticker.stop()
        context.subtitlePipeline.schedulePostStopSubtitleClearIfNeeded()
    }
}
