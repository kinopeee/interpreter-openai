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
}

@MainActor
final class InterpretationSession {
    private static let transcriptionRenderInterval: TimeInterval = 0.16
    private static let maxReconnectAttempts = 5
    private static let initialReconnectDelayNanoseconds: UInt64 = 500_000_000

    weak var delegate: InterpretationSessionDelegate?

    private let apiKeyStore: any APIKeyStore
    private let audioCapture: any RealtimeAudioCaptureServicing
    private let dualClient: any DualRealtimeTranslationClienting
    private let aggregator: SubtitleAggregator
    private let activeTickerIntervalNanoseconds: UInt64
    private let tuningProvider: @MainActor () -> RealtimeSessionTuning

    private(set) var state: TranslationState = .idle {
        didSet {
            guard oldValue != state else { return }
            delegate?.interpretationSession(self, didUpdateState: state)
        }
    }

    var isTickerRunning: Bool {
        tickerTask != nil
    }

    private var tickerTask: Task<Void, Never>?
    private var stopTask: Task<Void, Never>?
    private var sessionTask: Task<Void, Never>?
    private var renderTask: Task<Void, Never>?
    private var pendingUpdate: RealtimeSubtitleUpdate?
    private var lastRenderedAt = Date.distantPast
    private var lifecycleGeneration = 0
    private var assembler = RealtimeSubtitleAssembler()
    private var reconnectAttempt = 0
    private var routingSourceText = ""
    private var routedSpokenLanguage = SpokenLanguage.unknown

    init(
        apiKeyStore: any APIKeyStore,
        audioCapture: any RealtimeAudioCaptureServicing = RealtimeAudioCaptureService(),
        dualClient: any DualRealtimeTranslationClienting = DualRealtimeTranslationClient(),
        aggregator: SubtitleAggregator = SubtitleAggregator(),
        activeTickerIntervalNanoseconds: UInt64 = 200_000_000,
        tuningProvider: @escaping @MainActor () -> RealtimeSessionTuning = { .default }
    ) {
        self.apiKeyStore = apiKeyStore
        self.audioCapture = audioCapture
        self.dualClient = dualClient
        self.aggregator = aggregator
        self.activeTickerIntervalNanoseconds = activeTickerIntervalNanoseconds
        self.tuningProvider = tuningProvider
    }

    func start() async {
        guard state == .idle || state == .error else { return }
        lifecycleGeneration += 1
        let generation = lifecycleGeneration
        reconnectAttempt = 0
        state = .connecting
        aggregator.reset()
        aggregator.setStatusBanner("OpenAI Realtimeへ接続中…")
        publishSubtitles()

        sessionTask?.cancel()
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
        let tuning = tuningProvider()
        do {
            try await dualClient.updateTranscriptionTuning(tuning)
        } catch {
            AppLogger.session.error(
                "Failed to update transcription tuning: \(error.localizedDescription, privacy: .public)"
            )
        }
    }

    private func runSessionLoop(generation: Int) async {
        while generation == lifecycleGeneration {
            do {
                try await connectAndStream(generation: generation)
                return
            } catch is CancellationError {
                return
            } catch let error as RealtimeTranslationError where !error.isRecoverable {
                guard generation == lifecycleGeneration else { return }
                await tearDownStreaming()
                enterError(error)
                return
            } catch let error as RealtimeAudioCaptureError {
                guard generation == lifecycleGeneration else { return }
                await tearDownStreaming()
                enterError(error)
                return
            } catch {
                // recoverable transport / stream end
            }

            guard generation == lifecycleGeneration else { return }
            guard reconnectAttempt < Self.maxReconnectAttempts else {
                await tearDownStreaming()
                enterError(RealtimeTranslationError.recoverableTransportFailure("再接続上限"))
                return
            }

            reconnectAttempt += 1
            state = .reconnecting
            aggregator.setStatusBanner("再接続中… (\(reconnectAttempt)/\(Self.maxReconnectAttempts))")
            publishSubtitles()
            await tearDownStreaming(keepSubtitles: true)

            let delay = Self.initialReconnectDelayNanoseconds
                << UInt64(min(reconnectAttempt - 1, 4))
            let jitter = UInt64.random(in: 0...250_000_000)
            try? await Task.sleep(nanoseconds: delay + jitter)
        }
    }

    private func connectAndStream(generation: Int) async throws {
        let apiKey = try requireAPIKey()
        state = .connecting
        aggregator.setStatusBanner("OpenAI Realtimeへ接続中…")
        publishSubtitles()

        let tuning = tuningProvider()
        try await dualClient.start(apiKey: apiKey, tuning: tuning)
        guard generation == lifecycleGeneration else {
            await dualClient.forceClose()
            return
        }

        let epoch = await dualClient.connectionEpoch
        assembler.beginNewEpoch(epoch)
        routingSourceText = ""
        routedSpokenLanguage = .unknown
        await dualClient.resetAudioRouting()

        try await audioCapture.start()
        guard generation == lifecycleGeneration else {
            await audioCapture.stop()
            await dualClient.forceClose()
            return
        }

        state = .listening
        reconnectAttempt = 0
        aggregator.setStatusBanner("録音中… 話してください")
        startTicker(intervalNanoseconds: activeTickerIntervalNanoseconds)
        publishSubtitles()

        let feedTask = Task { @MainActor in
            try await self.feedAudio(generation: generation)
        }
        let eventTask = Task { @MainActor in
            try await self.consumeEvents(generation: generation, epoch: epoch)
        }
        let firstResult = await raceFirstResult(feedTask, eventTask)
        feedTask.cancel()
        eventTask.cancel()
        _ = await feedTask.result
        _ = await eventTask.result
        try firstResult.get()
    }

    private func raceFirstResult(
        _ first: Task<Void, Error>,
        _ second: Task<Void, Error>
    ) async -> Result<Void, Error> {
        await withTaskGroup(of: Result<Void, Error>.self) { group in
            group.addTask { await first.result }
            group.addTask { await second.result }
            let value = await group.next() ?? .failure(CancellationError())
            group.cancelAll()
            return value
        }
    }

    private func feedAudio(generation: Int) async throws {
        for await frame in audioCapture.frames {
            guard generation == lifecycleGeneration else { return }
            guard state == .listening else { return }
            try await dualClient.appendAudioFrame(frame)
        }
        guard generation == lifecycleGeneration, state == .listening else { return }
        throw RealtimeTranslationError.recoverableTransportFailure("audio stream ended")
    }

    private func consumeEvents(generation: Int, epoch: Int) async throws {
        let stream = await dualClient.events
        for await streamEvent in stream {
            guard generation == lifecycleGeneration else { return }
            guard streamEvent.epoch == epoch else { continue }

            if case .error(let message, let code) = streamEvent.event {
                if code == "transport" {
                    throw RealtimeTranslationError.recoverableTransportFailure(message)
                }
                let lowered = (code ?? "").lowercased()
                if lowered.contains("auth") || lowered.contains("401") || lowered.contains("403") {
                    throw RealtimeTranslationError.authenticationFailed
                }
                throw RealtimeTranslationError.fatalServerError(message)
            }

            if case .inputTranscriptDelta(let delta, _, _) = streamEvent.event {
                try await updateAudioRouting(withSourceDelta: delta)
            }

            if let update = assembler.ingest(streamEvent) {
                AppLogger.session.notice(
                    "DBG_ASSEMBLER_UPDATE epoch=\(streamEvent.epoch, privacy: .public) generation=\(update.segmentGeneration, privacy: .public) sourceEmpty=\(update.sourceText.isEmpty, privacy: .public) translationEmpty=\(update.translatedText.isEmpty, privacy: .public)"
                )
                enqueueRender(update)
                if update.shouldFinalize {
                    await resetAudioRoutingForNextSegment()
                }
            }
        }
        guard generation == lifecycleGeneration else { return }
        throw RealtimeTranslationError.recoverableTransportFailure("event stream ended")
    }

    private func performStop() async {
        lifecycleGeneration += 1
        state = .closing
        aggregator.setStatusBanner("録音を終了中…")
        publishSubtitles()

        sessionTask?.cancel()
        sessionTask = nil
        renderTask?.cancel()
        renderTask = nil
        let pending = pendingUpdate
        pendingUpdate = nil

        await audioCapture.stop()
        do {
            try await dualClient.closeGracefully()
        } catch {
            AppLogger.realtime.error(
                "Graceful close failed: \(error.localizedDescription, privacy: .public)"
            )
            await dualClient.forceClose()
        }

        if let pending {
            apply(pending)
        } else if let tickUpdate = assembler.tick(now: Date()) {
            apply(tickUpdate)
        }

        let snapshot = aggregator.forceFinalize()
        delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
        aggregator.setStatusBanner(nil)
        state = .idle
        publishSubtitles()
        stopTicker()
    }

    private func tearDownStreaming(keepSubtitles: Bool = false) async {
        await audioCapture.stop()
        await dualClient.forceClose()
        stopTicker()
        if !keepSubtitles {
            renderTask?.cancel()
            renderTask = nil
            pendingUpdate = nil
        }
    }

    private func requireAPIKey() throws -> String {
        guard let key = try apiKeyStore.load(),
              !key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        else {
            throw RealtimeTranslationError.missingAPIKey
        }
        return key
    }

    private func enqueueRender(_ update: RealtimeSubtitleUpdate) {
        if update.shouldFinalize {
            renderTask?.cancel()
            renderTask = nil
            pendingUpdate = nil
            apply(update)
            return
        }

        pendingUpdate = update
        guard renderTask == nil else { return }

        let elapsed = Date().timeIntervalSince(lastRenderedAt)
        let delay = max(0, Self.transcriptionRenderInterval - elapsed)
        renderTask = Task { @MainActor [weak self] in
            if delay > 0 {
                try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
            guard let self, !Task.isCancelled else { return }
            self.renderTask = nil
            guard let pending = self.pendingUpdate else { return }
            self.pendingUpdate = nil
            self.apply(pending)
        }
    }

    private func updateAudioRouting(withSourceDelta delta: String) async throws {
        routingSourceText += delta
        let evidence = SpokenLanguageDetector.recentEvidence(in: routingSourceText)

        if routedSpokenLanguage == .unknown {
            let detected: SpokenLanguage
            switch evidence {
            case .japanese:
                detected = .japanese
            case .english, .ambiguousLatin:
                detected = .english
            case .none:
                return
            }
            routedSpokenLanguage = detected
            assembler.expectLane(Self.expectedTranslationLane(for: detected))
            try await dualClient.setSpokenLanguage(detected)
            return
        }

        // 確定的な文字種反転のみをセグメント境界とする（ambiguousLatinは除外）。
        let flipped: SpokenLanguage?
        switch evidence {
        case .japanese where routedSpokenLanguage == .english:
            flipped = .japanese
        case .english where routedSpokenLanguage == .japanese:
            flipped = .english
        default:
            flipped = nil
        }
        guard let flipped else { return }

        if let finalized = assembler.finalizeForLanguageSwitch() {
            enqueueRender(finalized)
        }
        await resetAudioRoutingForNextSegment()
        routingSourceText = delta
        routedSpokenLanguage = flipped
        assembler.expectLane(Self.expectedTranslationLane(for: flipped))
        try await dualClient.setSpokenLanguage(flipped)
    }

    private static func expectedTranslationLane(
        for spoken: SpokenLanguage
    ) -> RealtimeTranslationOutputLanguage? {
        switch spoken {
        case .japanese:
            return .english
        case .english:
            return .japanese
        case .unknown:
            return nil
        }
    }

    private func resetAudioRoutingForNextSegment() async {
        routingSourceText = ""
        routedSpokenLanguage = .unknown
        assembler.expectLane(nil)
        await dualClient.resetAudioRouting()
    }

    private func apply(_ update: RealtimeSubtitleUpdate) {
        lastRenderedAt = Date()
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
                if let update = self.assembler.tick() {
                    self.enqueueRender(update)
                    if update.shouldFinalize {
                        await self.resetAudioRoutingForNextSegment()
                    }
                }
                let snapshot = self.aggregator.tick()
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
        delegate?.interpretationSession(self, didUpdateSubtitles: aggregator.snapshot())
    }

    private func enterError(_ error: Error) {
        state = .error
        aggregator.setStatusBanner(error.localizedDescription)
        publishSubtitles()
        delegate?.interpretationSession(self, didEncounterMessage: error.localizedDescription)
    }
}
