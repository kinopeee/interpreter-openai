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
    private let activeTickerIntervalNanoseconds: UInt64
    private let tuningProvider: @MainActor () -> RealtimeSessionTuning
    private let languagePairProvider: @MainActor () -> LanguagePair
    private var reconnectBudget: ReconnectBudget
    private let healthBookkeeper: SessionHealthBookkeeper
    private let subtitlePipeline: SessionSubtitlePipeline

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
    /// 現在の録音世代で使う言語ペア。Start 時に固定し、再接続でも settings の変更を取り込まない。
    private var sessionLanguagePair: LanguagePair?
    private var activeFeed: EventFeed?
    internal var afterFailedSourceTerminationForTests: (() -> Void)?

    /// テスト・診断用の最新 snapshot（検知には使わない）。
    var latestHealthSnapshot: SessionHealthSnapshot? {
        healthBookkeeper.latestHealthSnapshot
    }
    /// テスト・診断用の最新 termination diagnostic（各試行で最大 1 件）。
    var latestHealthTermination: SessionTerminationDiagnostic? {
        healthBookkeeper.latestHealthTermination
    }

    var audioLossMetrics: AudioLossMetrics {
        subtitlePipeline.audioLossMetrics
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
        self.subtitlePipeline.bind(
            activeFeed: { [weak self] in self?.activeFeed },
            lifecycleGeneration: { [weak self] in self?.lifecycleGeneration ?? 0 },
            state: { [weak self] in self?.state ?? .idle },
            publishUpdate: { [weak self] snapshot in
                guard let self else { return }
                self.delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
            }
        )
    }

    func start() async {
        guard state == .idle || state == .error else { return }
        subtitlePipeline.cancelPostStopClear()

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
        healthBookkeeper.beginRecordingGeneration()
        reconnectBudget.reset()
        subtitlePipeline.resetAudioLoss()
        // 録音開始時点のペアを世代全体で固定する。録音中の設定変更は再接続でも反映しない
        // （VALIDATION: 停止→次の録音開始後にだけ新しいペアが反映される）。
        sessionLanguagePair = languagePairProvider()
        state = .connecting
        subtitlePipeline.aggregator.reset()
        subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.connecting"))
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
        let tuning = tuningProvider().forPair(subtitlePipeline.activeLanguagePair ?? .jaEn)
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
            var recoverableError: Error?
            do {
                try await connectAndStream(generation: generation)
                return
            } catch is CancellationError {
                return
            } catch let error as RealtimeTranslationError where error.isRecoverable {
                // recoverable: 終了診断を記録して再接続へ進む
                recoverableError = error
                if case .recoverableTransportFailure(let detail) = error {
                    reconnectDetail = detail
                }
            } catch let error as URLError where Self.isTransientURLError(error) {
                // 一時的な URLSession 切断のみ再接続
                recoverableError = error
            } catch let error as URLSessionWebSocketTransportError {
                // transport 境界の未接続など: fall through to reconnect
                recoverableError = error
            } catch let error as NSError where Self.isTransientPOSIXError(error) {
                // URLError に bridge されない POSIX 切断
                recoverableError = error
            } catch let error as RealtimeTranslationError {
                guard generation == lifecycleGeneration else { return }
                await healthBookkeeper.recordTermination(error)
                await tearDownStreaming()
                // epoch/buffer を捨てる前に完全ペアを確定し、オプトイン字幕記録へ渡す。
                subtitlePipeline.flushPendingFinalizeIfNeeded()
                enterError(error)
                return
            } catch let error as RealtimeAudioCaptureError {
                switch error {
                case .inputDeviceChanged:
                    // マイク切断/切替は再接続。バナーに理由を残す。
                    reconnectDetail = error.localizedDescription
                    recoverableError = error
                case .pipelineOverloaded:
                    // フレーム経路の背圧は再接続で立て直す
                    recoverableError = error
                default:
                    guard generation == lifecycleGeneration else { return }
                    await healthBookkeeper.recordTermination(kind: .other)
                    await tearDownStreaming()
                    subtitlePipeline.flushPendingFinalizeIfNeeded()
                    enterError(error)
                    return
                }
            } catch {
                // 未知のアプリエラーは再接続せず即 error（予測可能性を優先）。
                guard generation == lifecycleGeneration else { return }
                await healthBookkeeper.recordTermination(kind: .other)
                await tearDownStreaming()
                subtitlePipeline.flushPendingFinalizeIfNeeded()
                enterError(error)
                return
            }

            guard generation == lifecycleGeneration else { return }
            let decision = reconnectBudget.recordFailure()
            guard decision.kind == .wait else {
                await healthBookkeeper.recordTermination(
                    kind: decision.kind == .budgetExhausted
                        ? .reconnectBudgetExhausted
                        : .reconnectAttemptLimit
                )
                await tearDownStreaming()
                subtitlePipeline.flushPendingFinalizeIfNeeded()
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
                subtitlePipeline.aggregator.setStatusBanner(reconnectingBanner(detail: reconnectDetail))
            } else {
                subtitlePipeline.aggregator.setStatusBanner(reconnectingBanner(detail: nil))
            }
            publishSubtitles()
            // recoverable 失敗も終了診断として記録する（診断のみ、挙動は変えない）。
            if let recoverableError {
                await healthBookkeeper.recordTermination(recoverableError)
            }
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
        healthBookkeeper.beginAttempt(
            lifecycleGeneration: lifecycleGeneration,
            reservedEpoch: await dualClient.reservedEpoch
        )
        let apiKey = try requireAPIKey()
        state = .connecting
        subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.connecting"))
        publishSubtitles()

        let pair = sessionLanguagePair ?? languagePairProvider()
        do {
            try await dualClient.start(
                apiKey: apiKey,
                tuning: tuningProvider().forPair(pair),
                pair: pair
            )
        } catch {
            // start は network 処理の前に connectionEpoch を予約済み。失敗した handshake の
            // epoch で診断するため読み直す。
            healthBookkeeper.updateAttemptEpoch(await dualClient.reservedEpoch)
            throw error
        }
        healthBookkeeper.updateAttemptEpoch(await dualClient.reservedEpoch)
        guard generation == lifecycleGeneration else {
            await dualClient.forceClose()
            return
        }

        let feed = await dualClient.feed
        activeFeed = feed
        let epoch = feed.runToken
        // 再接続時 beginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ .finalized が届かない。
        subtitlePipeline.flushPendingFinalizeIfNeeded()
        subtitlePipeline.beginEpoch(epoch, pair: pair)
        await dualClient.resetAudioRouting()

        try await audioCapture.start()
        guard generation == lifecycleGeneration else {
            await audioCapture.stop()
            await dualClient.forceClose()
            return
        }

        state = .listening
        reconnectBudget.recordListening()
        healthBookkeeper.beginGeneration(
            generation: generation,
            epoch: epoch,
            deliveryState: feed.deliveryState
        )
        subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.listening"))
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
                self.subtitlePipeline.handleEventLoss(feed)
                throw feed.deliveryState.makeError()
            }
            if feed.deliveryState.termination != .none {
                self.afterFailedSourceTerminationForTests?()
            }
            // 欠落なしの終了理由は stream 上の error event が消費側へ届くので、そちらに任せる。
            // ただし tryRecordTermination の直後に error 投入が満杯で recordLoss すると
            // completed 済みのため waitForCompletion は再起床しない。欠落を再確認する。
            // 正常完了も consumeEvents に任せる。success で戻ると session loop が終わる。
            while !Task.isCancelled {
                if feed.deliveryState.didLoseEvents {
                    self.subtitlePipeline.handleEventLoss(feed)
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
            subtitlePipeline.handleEventLoss(feed)
        }
        subtitlePipeline.discardFailedSourceIfNeeded(feed)
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

    private func feedAudio(generation: Int) async throws {
        for await frame in audioCapture.frames {
            guard generation == lifecycleGeneration else { return }
            guard state == .listening else { return }
            healthBookkeeper.recordCapture(
                hasAudioActivity: PCM16AudioActivity.normalizedPeakAmplitude(of: frame.pcm16)
                    > healthBookkeeper.thresholds.audioActivityPeakFloor
            )
            let queueWaitMilliseconds = max(
                0,
                Int(
                    ReconnectBudget.nanoseconds(
                        frame.capturedAt.duration(to: .now)
                    ) / 1_000_000
                )
            )
            let atMilliseconds = max(
                0,
                Int(ReconnectBudget.nanoseconds(ReconnectBudget.continuousNow()) / 1_000_000)
            )
            let observation = subtitlePipeline.observeAudio(
                generation: frame.generation,
                sequence: frame.sequence,
                discardedMilliseconds: frame.discardedMilliseconds,
                queueWaitMilliseconds: queueWaitMilliseconds,
                atMilliseconds: atMilliseconds
            )
            if observation.didLose {
                await subtitlePipeline.handleAudioLoss()
                #if DEBUG
                AppLogger.session.notice(
                    "DBG_AUDIO_LOSS droppedFrames=\(observation.droppedFrames, privacy: .public) lostMs=\(observation.lostMilliseconds, privacy: .public) reconnect=\(observation.shouldReconnect, privacy: .public)"
                )
                #endif
            }
            if observation.shouldReconnect {
                throw RealtimeAudioCaptureError.pipelineOverloaded
            }
            healthBookkeeper.recordSendStart()
            try await dualClient.appendAudioFrame(frame.pcm16)
            healthBookkeeper.recordSendSuccess()
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
            if subtitlePipeline.checkEventLoss(feed, generation: generation) {
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
                healthBookkeeper.recordSourceProgress()
            case .outputTranscriptDelta(let delta, _, _) where !delta.isEmpty:
                healthBookkeeper.recordTranslationProgress(lane: streamEvent.lane)
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

            if case .inputTranscriptFailed(let itemID, let eventID, let code, let errorType) = streamEvent.event {
                let classification = EventDeliveryState.classifyTranscriptionFailure(
                    errorType: errorType,
                    code: code
                )
                let invalidation = subtitlePipeline.discardFailedSource(itemID: itemID, eventID: eventID)
                feed.deliveryState.noteSourceFailureConsumed()
                if let invalidation {
                    subtitlePipeline.discardPending()
                    // halt/recover の flushPendingFinalizeIfNeeded が discardPending するため、
                    // 間引きせず即時適用し、aggregator の未確定ペアを先に消す。
                    subtitlePipeline.renderNow(invalidation)
                    await subtitlePipeline.resetAudioRoutingForNextSegment()
                }
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
            if let result = subtitlePipeline.processSubtitleEvent(streamEvent, now: Date()) {
                #if DEBUG
                AppLogger.session.notice(
                    "DBG_ASSEMBLER_UPDATE epoch=\(streamEvent.epoch, privacy: .public) generation=\(result.ingestedUpdate.segmentGeneration, privacy: .public) sourceEmpty=\(result.ingestedUpdate.sourceText.isEmpty, privacy: .public) translationEmpty=\(result.ingestedUpdate.translatedText.isEmpty, privacy: .public)"
                )
                #endif
                for update in result.updates {
                    subtitlePipeline.enqueueRender(update)
                }
                switch result.routingAction {
                case .none:
                    if !result.isSourceUpdate && result.ingestedUpdate.shouldFinalize {
                        await subtitlePipeline.resetAudioRoutingForNextSegment()
                    }
                case .select(let target):
                    healthBookkeeper.setSelectedLane(target.map { .translation($0) })
                    try await dualClient.selectTranslationTarget(target)
                case .switch(let target):
                    healthBookkeeper.setSelectedLane(target.map { .translation($0) })
                    await dualClient.resetAudioRouting()
                    try await dualClient.selectTranslationTarget(target)
                }
            }
            await dualClient.acknowledgeConsumedStreamEvent(runToken: feed.runToken)
        }
        guard generation == lifecycleGeneration else { return }
        if subtitlePipeline.checkEventLoss(feed, generation: generation) {
            if generation == lifecycleGeneration {
                throw feed.deliveryState.makeError()
            }
            return
        }
        if feed.deliveryState.termination != .none {
            subtitlePipeline.discardFailedSourceIfNeeded(feed)
            throw feed.deliveryState.makeError()
        }
        throw RealtimeTranslationError.recoverableTransportFailure("event stream ended")
    }

    private func performStop() async {
        await healthBookkeeper.recordTermination(kind: .userStopped)
        // ingest を先に止め、既読を acknowledge させてから未読窓だけを武装する。
        // AsyncStream.finish() は未読を捨てるため、未消費の最新窓とこれ以降の close 窓を Dual 側で保持する。
        lifecycleGeneration += 1
        await dualClient.beginStopDrainCapture()
        state = .closing
        subtitlePipeline.aggregator.setStatusBanner(UiCopy.text("banner.closing"))
        publishSubtitles()

        let runningSessionTask = sessionTask
        runningSessionTask?.cancel()
        sessionTask = nil
        let pending = subtitlePipeline.takePendingUpdate()

        // 先に音声と session consumer を止め、close drain を破棄されないようにする。
        // generation を上げたまま consumer が生きていると、commit/session.close の
        // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
        await audioCapture.stop()
        if let runningSessionTask {
            await runningSessionTask.value
        }

        // スロットル中の旧 snapshot を先に適用し、その後の close drain で上書きする。
        if let pending {
            subtitlePipeline.renderNow(pending)
        }

        let drainedEvents = await dualClient.closeGracefully()
        if let feed = activeFeed {
            if feed.deliveryState.didLoseEvents {
                subtitlePipeline.handleEventLoss(feed)
            } else {
                subtitlePipeline.ingestStopDrainEvents(drainedEvents, feed: feed)
            }
        }
        subtitlePipeline.clearBoundaryCandidate()
        if let feed = activeFeed, feed.deliveryState.hasPendingSourceFailure {
            subtitlePipeline.discardFailedSourceIfNeeded(feed)
        } else if subtitlePipeline.isCurrentSegmentTainted {
            let invalidation = subtitlePipeline.discardUnconfirmed()
            subtitlePipeline.renderNow(invalidation)
        } else {
            if let tickUpdate = subtitlePipeline.tickProcessor(now: Date()) {
                subtitlePipeline.renderNow(tickUpdate)
            }
            let snapshot = subtitlePipeline.aggregator.forceFinalize()
            delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
        }
        subtitlePipeline.aggregator.setStatusBanner(nil)
        sessionLanguagePair = nil
        subtitlePipeline.deactivateLanguagePair()
        state = .idle
        publishSubtitles()
        stopTicker()
        subtitlePipeline.schedulePostStopSubtitleClearIfNeeded()
    }

    private func tearDownStreaming(keepSubtitles: Bool = false) async {
        // recoverable・正常終了を問わず接続 teardown で健康世代を閉じる。
        healthBookkeeper.endGeneration()
        await audioCapture.stop()
        await dualClient.forceClose()
        if let feed = activeFeed {
            subtitlePipeline.discardFailedSourceIfNeeded(feed)
        }
        activeFeed = nil
        subtitlePipeline.resetHandledLossRunToken()
        subtitlePipeline.deactivateLanguagePair()
        subtitlePipeline.clearBoundaryCandidate()
        stopTicker()
        if !keepSubtitles {
            subtitlePipeline.discardPending()
        }
    }

    private func requireAPIKey() throws -> String {
        guard let key = try apiKeyStore.load() else {
            throw RealtimeTranslationError.missingAPIKey
        }
        return try RealtimeTranslationError.requireNormalizedAPIKey(key)
    }

    private func startTicker(intervalNanoseconds: UInt64) {
        stopTicker()
        tickerTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                try? await Task.sleep(nanoseconds: intervalNanoseconds)
                guard !Task.isCancelled else { return }
                if let feed = self.activeFeed,
                   self.subtitlePipeline.checkEventLoss(feed, generation: self.lifecycleGeneration)
                {
                    continue
                }
                if let update = self.subtitlePipeline.tickProcessor(now: Date()) {
                    self.subtitlePipeline.enqueueRender(update)
                    if update.shouldFinalize || update.isInvalidation {
                        await self.subtitlePipeline.resetAudioRoutingForNextSegment()
                    }
                }
                for detection in self.healthBookkeeper.tick(feed: self.activeFeed) {
                    self.delegate?.interpretationSession(self, didEmitHealthDetection: detection)
                }
                let snapshot = self.subtitlePipeline.aggregator.tick()
                self.delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
                if self.state == .idle || self.state == .error {
                    self.tickerTask = nil
                    return
                }
            }
        }
    }

    private func stopTicker() {
        tickerTask?.cancel()
        tickerTask = nil
    }

    private func publishSubtitles() {
        delegate?.interpretationSession(self, didUpdateSubtitles: subtitlePipeline.aggregator.snapshot())
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
        subtitlePipeline.aggregator.setStatusBanner(message)
        publishSubtitles()
        delegate?.interpretationSession(self, didEncounterMessage: message)
    }

}
