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

@MainActor
final class InterpretationSession {
    /// 録音停止後、最後の字幕ペアを読み取れるよう残す時間。
    static let defaultPostStopSubtitleRetentionNanoseconds: UInt64 = 5_000_000_000

    weak var delegate: InterpretationSessionDelegate?

    private let apiKeyStore: any APIKeyStore
    private let audioCapture: any RealtimeAudioCaptureServicing
    private let dualClient: any DualRealtimeTranslationClienting
    private let aggregator: SubtitleAggregator
    private let displayScheduler: SubtitleDisplayScheduler
    private let activeTickerIntervalNanoseconds: UInt64
    private let postStopSubtitleRetentionNanoseconds: UInt64
    private let tuningProvider: @MainActor () -> RealtimeSessionTuning
    private let languagePairProvider: @MainActor () -> LanguagePair
    private var reconnectBudget: ReconnectBudget
    /// 受信停止監視用の単調時計と壁時計（remaining は受信時に一度だけ壁時計で算出）。
    private let healthNow: ReconnectBudget.Now
    private let wallClockNow: @Sendable () -> TimeInterval

    private(set) var state: TranslationState = .idle {
        didSet {
            guard oldValue != state else { return }
            delegate?.interpretationSession(self, didUpdateState: state)
        }
    }

    private var tickerTask: Task<Void, Never>?
    private var stopTask: Task<Void, Never>?
    private var sessionTask: Task<Void, Never>?
    private var lifecycleGeneration = 0
    private var processor = RealtimeSubtitleProcessor()
    /// 現在の録音世代で使う言語ペア。Start 時に固定し、再接続でも settings の変更を取り込まない。
    private var sessionLanguagePair: LanguagePair?
    private var activeFeed: EventFeed?
    private var handledLossRunToken: Int?
    private var healthMonitor = SessionHealthMonitor()
    private var healthReceiveCounts: [RealtimeTranslationLane: Int] = [:]
    private var healthGenerationEnded = true
    private var lastHealthSnapshotLogAt: Duration?
    private var connectionCountInGeneration = 0
    /// テスト・診断用の最新 snapshot（検知には使わない）。
    private(set) var latestHealthSnapshot: SessionHealthSnapshot?

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
        self.apiKeyStore = apiKeyStore
        self.audioCapture = audioCapture
        self.dualClient = dualClient
        self.aggregator = aggregator
        self.displayScheduler = displayScheduler
        self.activeTickerIntervalNanoseconds = activeTickerIntervalNanoseconds
        self.postStopSubtitleRetentionNanoseconds = postStopSubtitleRetentionNanoseconds
        self.tuningProvider = tuningProvider
        self.languagePairProvider = languagePairProvider
        self.reconnectBudget = reconnectBudget
        self.healthNow = healthNow
        self.wallClockNow = wallClockNow
        self.healthMonitor.thresholds = healthThresholds
        self.displayScheduler.delegate = self
    }

    func start() async {
        guard state == .idle || state == .error else { return }
        cancelPostStopSubtitleClear()

        // 旧sessionTaskが世代不一致のforceClose/stopを後から走らせ、
        // 新しい接続やマイクを落とさないよう先に排水する。
        let previousSessionTask = sessionTask
        previousSessionTask?.cancel()
        sessionTask = nil
        if let previousSessionTask {
            await previousSessionTask.value
        }

        lifecycleGeneration += 1
        let generation = lifecycleGeneration
        connectionCountInGeneration = 0
        reconnectBudget.reset()
        // 録音開始時点のペアを世代全体で固定する。録音中の設定変更は再接続でも反映しない
        // （VALIDATION: 停止→次の録音開始後にだけ新しいペアが反映される）。
        sessionLanguagePair = languagePairProvider()
        state = .connecting
        aggregator.reset()
        aggregator.setStatusBanner(UiCopy.text("banner.connecting"))
        publishSubtitles()

        sessionTask = Task { @MainActor [weak self] in
            await self?.runSessionLoop(generation: generation)
        }
    }

    func stop() async {
        if let stopTask {
            await stopTask.value
            return
        }
        guard state != .idle else { return }

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
        guard state == .listening else { return }
        let tuning = tuningProvider().forPair(processor.activeLanguagePair ?? .jaEn)
        do {
            try await dualClient.updateTranscriptionTuning(tuning)
        } catch {
            AppLogger.session.error(
                "Failed to update transcription tuning: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
        }
    }

    private func runSessionLoop(generation: Int) async {
        while generation == lifecycleGeneration {
            var reconnectDetail: String?
            do {
                try await connectAndStream(generation: generation)
                return
            } catch is CancellationError {
                return
            } catch let error as RealtimeTranslationError where error.isRecoverable {
                // recoverable: fall through to reconnect
                if case .recoverableTransportFailure(let detail) = error {
                    reconnectDetail = detail
                }
            } catch let error as URLError where Self.isTransientURLError(error) {
                // 一時的な URLSession 切断のみ再接続
            } catch is URLSessionWebSocketTransportError {
                // transport 境界の未接続など: fall through to reconnect
            } catch let error as NSError where Self.isTransientPOSIXError(error) {
                // URLError に bridge されない POSIX 切断
                _ = error
            } catch let error as RealtimeTranslationError {
                guard generation == lifecycleGeneration else { return }
                recordHealthTermination(error)
                await tearDownStreaming()
                // epoch/buffer を捨てる前に完全ペアを確定し、オプトイン字幕記録へ渡す。
                flushPendingFinalizeIfNeeded()
                enterError(error)
                return
            } catch let error as RealtimeAudioCaptureError {
                switch error {
                case .inputDeviceChanged:
                    // マイク切断/切替は再接続。バナーに理由を残す。
                    reconnectDetail = error.localizedDescription
                case .pipelineOverloaded:
                    // フレーム経路の背圧は再接続で立て直す
                    break
                default:
                    guard generation == lifecycleGeneration else { return }
                    recordHealthTermination(kind: .other)
                    await tearDownStreaming()
                    flushPendingFinalizeIfNeeded()
                    enterError(error)
                    return
                }
            } catch {
                // 未知のアプリエラーは再接続せず即 error（予測可能性を優先）。
                guard generation == lifecycleGeneration else { return }
                recordHealthTermination(kind: .other)
                await tearDownStreaming()
                flushPendingFinalizeIfNeeded()
                enterError(error)
                return
            }

            guard generation == lifecycleGeneration else { return }
            let decision = reconnectBudget.recordFailure()
            guard decision.kind == .wait else {
                recordHealthTermination(kind: .reconnectBudgetExhausted)
                await tearDownStreaming()
                flushPendingFinalizeIfNeeded()
                enterErrorMessage(
                    UiCopy.text(
                        decision.kind == .budgetExhausted
                            ? "error.reconnectBudgetExhausted"
                            : "error.reconnectLimit"
                    )
                )
                return
            }

            state = .reconnecting
            let micMessage = RealtimeAudioCaptureError.inputDeviceChanged.errorDescription
            if let reconnectDetail, let micMessage, reconnectDetail == micMessage {
                aggregator.setStatusBanner(reconnectingBanner(detail: reconnectDetail))
            } else {
                aggregator.setStatusBanner(reconnectingBanner(detail: nil))
            }
            publishSubtitles()
            await tearDownStreaming(keepSubtitles: true)

            // 停止（cancel）で待ちを即座に打ち切る。ループ先頭の世代確認で再接続を始めない。
            try? await Task.sleep(for: decision.delay)
        }
    }

    /// 再接続対象の一時的な URLError のみ許可する（証明書/ATS/不正 URL は即 error）。
    private static func isTransientURLError(_ error: URLError) -> Bool {
        switch error.code {
        case .timedOut,
            .cannotFindHost,
            .cannotConnectToHost,
            .networkConnectionLost,
            .dnsLookupFailed,
            .notConnectedToInternet,
            .cannotLoadFromNetwork,
            .internationalRoamingOff,
            .callIsActive,
            .dataNotAllowed:
            return true
        default:
            return false
        }
    }

    /// URLError に bridge されない POSIX 切断コード（Darwin 値）。
    private static let transientPOSIXCodes: Set<Int> = [
        32, // EPIPE
        50, // ENETDOWN
        51, // ENETUNREACH
        53, // ECONNABORTED
        54, // ECONNRESET
        57, // ENOTCONN
        60, // ETIMEDOUT
        65, // EHOSTUNREACH
    ]

    private static func isTransientPOSIXError(_ error: NSError) -> Bool {
        error.domain == NSPOSIXErrorDomain && transientPOSIXCodes.contains(error.code)
    }

    private func connectAndStream(generation: Int) async throws {
        connectionCountInGeneration += 1
        let apiKey = try requireAPIKey()
        state = .connecting
        aggregator.setStatusBanner(UiCopy.text("banner.connecting"))
        publishSubtitles()

        let pair = sessionLanguagePair ?? languagePairProvider()
        try await dualClient.start(
            apiKey: apiKey,
            tuning: tuningProvider().forPair(pair),
            pair: pair
        )
        guard generation == lifecycleGeneration else {
            await dualClient.forceClose()
            return
        }

        let feed = await dualClient.feed
        activeFeed = feed
        let epoch = feed.runToken
        // 再接続時 beginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ .finalized が届かない。
        flushPendingFinalizeIfNeeded()
        processor.beginEpoch(epoch, pair: pair)
        await dualClient.resetAudioRouting()

        try await audioCapture.start()
        guard generation == lifecycleGeneration else {
            await audioCapture.stop()
            await dualClient.forceClose()
            return
        }

        state = .listening
        reconnectBudget.recordListening()

        let monitorNow = healthNow()
        healthMonitor.beginGeneration(
            generation: generation,
            epoch: epoch,
            isRecovery: connectionCountInGeneration > 1,
            now: monitorNow
        )
        healthGenerationEnded = false
        healthReceiveCounts = [:]
        // 期限の remaining は受信時に一度だけ壁時計で算出し、以後は単調時計で追う。
        let wallNow = wallClockNow()
        for lane in Self.healthLanes {
            let remaining = feed.deliveryState.sessionExpiry(lane).flatMap {
                RealtimeSessionExpiry.remaining(
                    expiresAtUnixSeconds: $0,
                    wallNowUnixSeconds: Int64(wallNow)
                )
            }
            healthMonitor.recordSessionExpiry(lane: lane, remaining: remaining, now: monitorNow)
        }
        aggregator.setStatusBanner(UiCopy.text("banner.listening"))
        startTicker(intervalNanoseconds: activeTickerIntervalNanoseconds)
        publishSubtitles()

        let feedTask = Task { @MainActor in
            try await self.feedAudio(generation: generation)
        }
        let eventTask = Task { @MainActor in
            try await self.consumeEvents(generation: generation, feed: feed)
        }
        let completionTask = Task<Void, Error> { @MainActor in
            await feed.deliveryState.waitForCompletion()
            if Task.isCancelled {
                throw CancellationError()
            }
            if feed.deliveryState.didLoseEvents {
                self.handleEventLoss(feed)
                throw feed.deliveryState.makeError()
            }
            // 欠落なしの終了理由は stream 上の error event が消費側へ届くので、そちらに任せる。
            // ただし tryRecordTermination の直後に error 投入が満杯で recordLoss すると
            // completed 済みのため waitForCompletion は再起床しない。欠落を再確認する。
            // 正常完了も consumeEvents に任せる。success で戻ると session loop が終わる。
            while !Task.isCancelled {
                if feed.deliveryState.didLoseEvents {
                    self.handleEventLoss(feed)
                    throw feed.deliveryState.makeError()
                }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            throw CancellationError()
        }
        let firstResult = await raceFirstResult(feedTask, eventTask, completionTask)
        feedTask.cancel()
        eventTask.cancel()
        completionTask.cancel()
        // feedAudio の for-await は cancel だけでは解けないことがある。
        await audioCapture.stop()
        _ = await feedTask.result
        _ = await eventTask.result
        if feed.deliveryState.didLoseEvents {
            handleEventLoss(feed)
        }
        try firstResult.get()
    }

    private func raceFirstResult(
        _ first: Task<Void, Error>,
        _ second: Task<Void, Error>,
        _ third: Task<Void, Error>
    ) async -> Result<Void, Error> {
        // 先に完了した側の結果で戻る。負け側の非構造化Taskも必ずcancelしないと、
        // withTaskGroupが `.result` 待ちの子タスクで戻りを阻み、再接続不能になる。
        await withTaskGroup(of: Result<Void, Error>.self) { group in
            group.addTask {
                await withTaskCancellationHandler {
                    await first.result
                } onCancel: {
                    first.cancel()
                }
            }
            group.addTask {
                await withTaskCancellationHandler {
                    await second.result
                } onCancel: {
                    second.cancel()
                }
            }
            group.addTask {
                await withTaskCancellationHandler {
                    await third.result
                } onCancel: {
                    third.cancel()
                }
            }
            let value = await group.next() ?? .failure(CancellationError())
            first.cancel()
            second.cancel()
            third.cancel()
            group.cancelAll()
            while await group.next() != nil {}
            return value
        }
    }

    /// 受信監視で数える対象 lane（source + 全 target）。
    private static let healthLanes: [RealtimeTranslationLane] = [
        .source,
        .translation(.english),
        .translation(.japanese),
        .translation(.spanish),
    ]

    private func feedAudio(generation: Int) async throws {
        for await frame in audioCapture.frames {
            guard generation == lifecycleGeneration else { return }
            guard state == .listening else { return }
            healthMonitor.recordCapture(
                now: healthNow(),
                hasAudioActivity: PCM16AudioActivity.normalizedPeakAmplitude(of: frame)
                    > healthMonitor.thresholds.audioActivityPeakFloor
            )
            try await dualClient.appendAudioFrame(frame)
            healthMonitor.recordSendSuccess(now: healthNow())
        }
        guard generation == lifecycleGeneration, state == .listening else { return }
        if let terminationError = audioCapture.terminationError {
            throw terminationError
        }
        throw RealtimeTranslationError.recoverableTransportFailure("audio stream ended")
    }

    private func consumeEvents(generation: Int, feed: EventFeed) async throws {
        let stream = feed.events
        for await streamEvent in stream {
            // stop 後の未読 delta は Dual recentYields → close drain が assembler へ同期適用する。
            // ここで ingest/enqueueRender すると、performStop が消した renderTask が再生成され、
            // forceFinalize 後に replaceCurrent で確定ペアを live へ戻す。
            guard generation == lifecycleGeneration else { return }
            if checkEventLoss(feed, generation: generation) {
                if generation == lifecycleGeneration {
                    throw feed.deliveryState.makeError()
                }
                return
            }
            guard streamEvent.epoch == feed.runToken else {
                await dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
                continue
            }

            // delta 文字列は monitor へ渡さない（検知は到着・進捗の事実だけを見る）。
            switch streamEvent.event {
            case .inputTranscriptDelta(let delta, _, _) where streamEvent.lane == .source
                && !delta.isEmpty:
                healthMonitor.recordSourceProgress(now: healthNow())
            case .outputTranscriptDelta(let delta, _, _) where !delta.isEmpty:
                healthMonitor.recordTranslationProgress(lane: streamEvent.lane, now: healthNow())
            default:
                break
            }

            if case .error(let message, let code, let errorType) = streamEvent.event {
                let classification = EventDeliveryState.classify(
                    errorType: errorType,
                    code: code,
                    message: message
                )
                if classification.disposition == .keepAlive {
                    await dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
                    continue
                }
                feed.deliveryState.tryRecordTermination(classification)
                throw feed.deliveryState.makeError()
            }

            // 原文 routing は専用 transcription の source lane だけを使う。
            // 適用または明示破棄のあとで acknowledge する。ack を先にすると、
            // この await 中に performStop が走ったとき未適用イベントが stop drain から外れる。
            guard generation == lifecycleGeneration else { return }
            if let result = processSubtitleEvent(streamEvent, now: Date()) {
                #if DEBUG
                AppLogger.session.notice(
                    "DBG_ASSEMBLER_UPDATE epoch=\(streamEvent.epoch, privacy: .public) generation=\(result.ingestedUpdate.segmentGeneration, privacy: .public) sourceEmpty=\(result.ingestedUpdate.sourceText.isEmpty, privacy: .public) translationEmpty=\(result.ingestedUpdate.translatedText.isEmpty, privacy: .public)"
                )
                #endif
                for update in result.updates {
                    enqueueRender(update)
                }
                switch result.routingAction {
                case .none:
                    if !result.isSourceUpdate && result.ingestedUpdate.shouldFinalize {
                        await resetAudioRoutingForNextSegment()
                    }
                case .select(let target):
                    healthMonitor.setSelectedLane(target.map { .translation($0) }, now: healthNow())
                    try await dualClient.selectTranslationTarget(target)
                case .switch(let target):
                    healthMonitor.setSelectedLane(target.map { .translation($0) }, now: healthNow())
                    await dualClient.resetAudioRouting()
                    try await dualClient.selectTranslationTarget(target)
                }
            }
            await dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
        }
        guard generation == lifecycleGeneration else { return }
        if checkEventLoss(feed, generation: generation) {
            if generation == lifecycleGeneration {
                throw feed.deliveryState.makeError()
            }
            return
        }
        if feed.deliveryState.termination != .none {
            throw feed.deliveryState.makeError()
        }
        throw RealtimeTranslationError.recoverableTransportFailure("event stream ended")
    }

    private func performStop() async {
        recordHealthTermination(kind: .userStopped)
        // ingest を先に止め、既読を acknowledge させてから未読窓だけを武装する。
        // AsyncStream.finish() は未読を捨てるため、未消費の最新窓とこれ以降の close 窓を Dual 側で保持する。
        lifecycleGeneration += 1
        await dualClient.beginStopDrainCapture()
        state = .closing
        aggregator.setStatusBanner(UiCopy.text("banner.closing"))
        publishSubtitles()

        let runningSessionTask = sessionTask
        runningSessionTask?.cancel()
        sessionTask = nil
        let pending = displayScheduler.takePendingUpdate()

        // 先に音声と session consumer を止め、close drain を破棄されないようにする。
        // generation を上げたまま consumer が生きていると、commit/session.close の
        // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
        await audioCapture.stop()
        if let runningSessionTask {
            await runningSessionTask.value
        }

        // スロットル中の旧 snapshot を先に適用し、その後の close drain で上書きする。
        if let pending {
            displayScheduler.renderNow(pending)
        }

        let drainedEvents = await dualClient.closeGracefully()
        if let feed = activeFeed {
            if feed.deliveryState.didLoseEvents {
                handleEventLoss(feed)
            } else {
                ingestStopDrainEvents(drainedEvents, feed: feed)
            }
        }
        processor.clearBoundaryCandidate()
        if let tickUpdate = processor.tick(now: Date()) {
            displayScheduler.renderNow(tickUpdate)
        }

        let snapshot = aggregator.forceFinalize()
        delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
        aggregator.setStatusBanner(nil)
        sessionLanguagePair = nil
        processor.deactivateLanguagePair()
        state = .idle
        publishSubtitles()
        stopTicker()
        schedulePostStopSubtitleClearIfNeeded()
    }

    /// 正常停止の close drain で届いた字幕イベントを assembler へ取り込む。
    private func ingestStopDrainEvents(_ events: [RealtimeTranslationStreamEvent], feed: EventFeed) {
        guard !feed.deliveryState.didLoseEvents else { return }
        for streamEvent in events {
            if case .error = streamEvent.event {
                continue
            }
            guard let result = processSubtitleEvent(streamEvent, now: Date(), isReplay: true) else {
                continue
            }
            for update in result.updates {
                displayScheduler.renderNow(update)
            }
        }
    }

    private func schedulePostStopSubtitleClearIfNeeded() {
        cancelPostStopSubtitleClear()
        guard !aggregator.snapshot().current.isEmpty else { return }
        let generation = lifecycleGeneration
        displayScheduler.schedulePostStopClear(
            afterNanoseconds: postStopSubtitleRetentionNanoseconds
        ) { [weak self] in
            guard let self else { return false }
            return self.lifecycleGeneration == generation && self.state == .idle
        } onClear: { [weak self] in
            guard let self else { return }
            self.aggregator.reset()
            self.publishSubtitles()
        }
    }

    private func cancelPostStopSubtitleClear() {
        displayScheduler.cancelPostStopClear()
    }

    private func tearDownStreaming(keepSubtitles: Bool = false) async {
        await audioCapture.stop()
        await dualClient.forceClose()
        activeFeed = nil
        handledLossRunToken = nil
        processor.deactivateLanguagePair()
        processor.clearBoundaryCandidate()
        stopTicker()
        if !keepSubtitles {
            displayScheduler.discardPending()
        }
    }

    /// 完全な原文+訳文ペアが assembler / aggregator に残っていれば idle 待ちを飛ばして確定する。
    /// 停止・再接続・致命エラーで epoch/buffer を捨てる直前に呼び、字幕記録の欠落を防ぐ。
    private func flushPendingFinalizeIfNeeded() {
        if let feed = activeFeed, checkEventLoss(feed, generation: lifecycleGeneration) {
            return
        }
        // スロットル中の live snapshot より assembler を正とする。
        displayScheduler.discardPending()

        let flushAt = Date().addingTimeInterval(RealtimeSubtitleAssembler.idleFinalizeInterval)
        if let update = processor.tick(now: flushAt) {
            displayScheduler.renderNow(update)
            return
        }

        // assembler が空でも、live 経路 (canFinalize: false) の完全ペアが
        // aggregator に残っている場合がある — 字幕記録のため確定する。
        let before = aggregator.snapshot().current
        guard before.state != .finalized else { return }
        let snapshot = aggregator.forceFinalize()
        guard snapshot.current.state == .finalized else { return }
        delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
    }

    private func requireAPIKey() throws -> String {
        guard let key = try apiKeyStore.load() else {
            throw RealtimeTranslationError.missingAPIKey
        }
        return try RealtimeTranslationError.requireNormalizedAPIKey(key)
    }

    private func enqueueRender(_ update: RealtimeSubtitleUpdate) {
        if let feed = activeFeed, checkEventLoss(feed, generation: lifecycleGeneration) {
            return
        }
        displayScheduler.enqueue(update)
    }

    private func processSubtitleEvent(
        _ streamEvent: RealtimeTranslationStreamEvent,
        now: Date,
        isReplay: Bool = false
    ) -> RealtimeSubtitleProcessingResult? {
        if let feed = activeFeed, checkEventLoss(feed, generation: lifecycleGeneration) {
            return nil
        }
        return processor.process(streamEvent, now: now, isReplay: isReplay)
    }

    private func resetAudioRoutingForNextSegment() async {
        processor.resetRoutingForNextSegment()
        healthMonitor.setSelectedLane(nil, now: healthNow())
        await dualClient.resetAudioRouting()
    }

    /// scheduler からの描画要求を aggregator・delegate へ反映する。
    private func apply(_ update: RealtimeSubtitleUpdate) {
        if update.isInvalidation {
            let snapshot = aggregator.invalidateCurrent()
            delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
            return
        }

        if state == .listening || state == .reconnecting {
            aggregator.setStatusBanner(nil)
        }

        if update.shouldFinalize {
            let snapshot = aggregator.finalizePair(
                sourceText: update.sourceText,
                translatedText: update.translatedText,
                clearCurrent: true
            )
            delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
            return
        }

        let snapshot = aggregator.replaceCurrent(
            sourceText: update.sourceText,
            translatedText: update.translatedText,
            isTranslationCurrent: update.isTranslationCurrent,
            canFinalize: false
        )
        delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
    }

    private func startTicker(intervalNanoseconds: UInt64) {
        stopTicker()
        tickerTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                try? await Task.sleep(nanoseconds: intervalNanoseconds)
                guard !Task.isCancelled else { return }
                if let feed = self.activeFeed,
                   self.checkEventLoss(feed, generation: self.lifecycleGeneration)
                {
                    continue
                }
                if let update = self.processor.tick(now: Date()) {
                    self.enqueueRender(update)
                    if update.shouldFinalize {
                        await self.resetAudioRoutingForNextSegment()
                    }
                }
                self.healthTick()
                let snapshot = self.aggregator.tick()
                self.delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
                if self.state == .idle || self.state == .error {
                    self.tickerTask = nil
                    return
                }
            }
        }
    }

    /// 各 lane の decode 受信数の差分で recordReceive し、evaluate を回す。
    /// 検知に対して再接続や lane 変更は行わない（診断のみ）。
    private func healthTick() {
        guard let feed = activeFeed else { return }
        let now = healthNow()
        for lane in Self.healthLanes {
            let count = feed.deliveryState.receiveCount(lane)
            if count > (healthReceiveCounts[lane] ?? 0) {
                healthMonitor.recordReceive(lane: lane, now: now)
            }
            healthReceiveCounts[lane] = count
        }

        let (snapshot, detections) = healthMonitor.evaluate(now: now)
        latestHealthSnapshot = snapshot
        for detection in detections {
            #if DEBUG
            AppLogger.session.notice(
                "DBG_HEALTH \(detection.description, privacy: .public)"
            )
            #endif
            delegate?.interpretationSession(self, didEmitHealthDetection: detection)
        }
        #if DEBUG
        if lastHealthSnapshotLogAt == nil || now - lastHealthSnapshotLogAt! >= .seconds(5) {
            lastHealthSnapshotLogAt = now
            AppLogger.session.notice(
                "DBG_HEALTH_SNAPSHOT \(snapshot.description, privacy: .public)"
            )
        }
        #endif
    }

    /// セッションループ終了・停止時の診断。kind は自前 enum のみ（生 message は渡さない）。
    /// 各世代で最初の終了経路だけを記録する。
    private func recordHealthTermination(_ error: Error) {
        let kind: SessionTerminationKind
        if let error = error as? RealtimeTranslationError {
            kind = SessionTerminationKind(error)
        } else {
            kind = .other
        }
        recordHealthTermination(kind: kind)
    }

    private func recordHealthTermination(kind: SessionTerminationKind) {
        guard !healthGenerationEnded else { return }
        let diagnostic = healthMonitor.recordTermination(kind: kind, now: healthNow())
        #if DEBUG
        AppLogger.session.notice(
            "DBG_HEALTH_TERMINATION \(diagnostic.description, privacy: .public)"
        )
        #endif
        endHealthGeneration()
    }

    private func endHealthGeneration() {
        guard !healthGenerationEnded else { return }
        healthGenerationEnded = true
        healthMonitor.endGeneration(now: healthNow())
        latestHealthSnapshot = healthMonitor.evaluate(now: healthNow()).snapshot
    }

    private func stopTicker() {
        tickerTask?.cancel()
        tickerTask = nil
    }

    private func publishSubtitles() {
        delegate?.interpretationSession(self, didUpdateSubtitles: aggregator.snapshot())
    }

    private func checkEventLoss(_ feed: EventFeed, generation _: Int) -> Bool {
        guard feed.deliveryState.didLoseEvents else { return false }
        if handledLossRunToken != feed.runToken {
            handledLossRunToken = feed.runToken
            let invalidation = processor.discardUnconfirmed()
            displayScheduler.discardPending()
            displayScheduler.renderNow(invalidation)
        }
        return true
    }

    private func handleEventLoss(_ feed: EventFeed) {
        _ = checkEventLoss(feed, generation: lifecycleGeneration)
    }

    private func reconnectingBanner(detail: String?) -> String {
        let substitutions = [
            "detail": detail ?? "",
            "attempt": String(reconnectBudget.currentAttempt),
            "max": String(reconnectBudget.policy.maxAttempts),
        ]
        return UiCopy.text("banner.reconnectingProgress", substitutions)
            .trimmingCharacters(in: .whitespaces)
    }

    private func enterError(_ error: Error) {
        enterErrorMessage(error.localizedDescription)
    }

    private func enterErrorMessage(_ message: String) {
        state = .error
        aggregator.setStatusBanner(message)
        publishSubtitles()
        delegate?.interpretationSession(self, didEncounterMessage: message)
    }

}

extension InterpretationSession: SubtitleDisplaySchedulerDelegate {
    func subtitleDisplayScheduler(
        _ scheduler: SubtitleDisplayScheduler,
        requestsRenderOf update: RealtimeSubtitleUpdate
    ) {
        apply(update)
    }
}
