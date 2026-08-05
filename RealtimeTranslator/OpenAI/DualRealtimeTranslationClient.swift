import Foundation

protocol DualRealtimeTranslationClienting: AnyObject, Sendable {
    var events: AsyncStream<RealtimeTranslationStreamEvent> { get async }
    func start(apiKey: String, tuning: RealtimeSessionTuning) async throws
    func appendAudioFrame(_ pcm16LE: Data) async throws
    func setSpokenLanguage(_ language: SpokenLanguage) async throws
    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws
    func resetAudioRouting() async
    func closeGracefully() async throws
    func forceClose() async
    var connectionEpoch: Int { get async }
}

actor DualRealtimeTranslationClient: DualRealtimeTranslationClienting {
    /// 100 ms frame × 40 = 直近4秒。言語判定遅延でも発話冒頭を翻訳へ届ける。
    private static let translationPrerollFrameLimit = 40
    private static let consecutiveTranslationFailureLimit = 3

    private let sourceConnection: RealtimeSourceTranscriptionConnection
    private let englishConnection: RealtimeTranslationConnection
    private let japaneseConnection: RealtimeTranslationConnection
    private var mergeTask: Task<Void, Never>?
    private var translationPumpTask: Task<Void, Never>?
    private var eventContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation?
    private var eventStream: AsyncStream<RealtimeTranslationStreamEvent>
    private(set) var connectionEpoch = 0
    private var isRunning = false
    private var appendedFrameCount = 0
    private var sourceSentFrameCount = 0
    private var sourceDeltaCount = 0
    private var consecutiveTranslationFailures = 0
    private var selectedTranslationTarget: RealtimeTranslationOutputLanguage?
    private var translationPrerollFrames: [Data] = []
    private var pendingTranslationFrames: [(Data, RealtimeTranslationOutputLanguage)] = []

    var events: AsyncStream<RealtimeTranslationStreamEvent> {
        eventStream
    }

    init(
        sourceConnection: RealtimeSourceTranscriptionConnection =
            RealtimeSourceTranscriptionConnection(),
        englishConnection: RealtimeTranslationConnection = RealtimeTranslationConnection(
            target: .english
        ),
        japaneseConnection: RealtimeTranslationConnection = RealtimeTranslationConnection(
            target: .japanese
        )
    ) {
        self.sourceConnection = sourceConnection
        self.englishConnection = englishConnection
        self.japaneseConnection = japaneseConnection
        let pair = Self.makeEventStream()
        eventStream = pair.stream
        eventContinuation = pair.continuation
    }

    func start(
        apiKey: String,
        tuning: RealtimeSessionTuning = .default
    ) async throws {
        await forceClose()
        recreateEventStream()
        connectionEpoch += 1
        let epoch = connectionEpoch
        isRunning = true
        appendedFrameCount = 0
        sourceSentFrameCount = 0
        sourceDeltaCount = 0
        consecutiveTranslationFailures = 0
        selectedTranslationTarget = nil
        translationPrerollFrames.removeAll(keepingCapacity: true)
        pendingTranslationFrames.removeAll(keepingCapacity: true)

        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask {
                    try await self.sourceConnection.start(apiKey: apiKey, tuning: tuning)
                }
                group.addTask {
                    try await self.englishConnection.start(
                        apiKey: apiKey,
                        config: .englishTargetWithoutSourceTranscription(
                            noiseReduction: tuning.noiseReduction
                        )
                    )
                }
                group.addTask {
                    try await self.japaneseConnection.start(
                        apiKey: apiKey,
                        config: .japaneseTargetWithoutSourceTranscription(
                            noiseReduction: tuning.noiseReduction
                        )
                    )
                }
                try await group.waitForAll()
            }
        } catch {
            await forceClose()
            throw error
        }

        guard epoch == connectionEpoch, isRunning else {
            throw RealtimeTranslationError.cancelled
        }
        startEventMerge(epoch: epoch)
    }

    func appendAudioFrame(_ pcm16LE: Data) async throws {
        guard isRunning else {
            throw RealtimeTranslationError.notConnected
        }
        appendedFrameCount += 1

        // 原文送信は単独で完了させ、翻訳側の停滞に巻き込まない。
        try await sourceConnection.appendAudioFrame(pcm16LE)
        sourceSentFrameCount += 1
        if sourceSentFrameCount == 1 || sourceSentFrameCount.isMultiple(of: 25) {
            AppLogger.realtime.notice(
                "DBG_SOURCE_STATS sent=\(self.sourceSentFrameCount, privacy: .public) deltas=\(self.sourceDeltaCount, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
            )
            AppLogger.realtime.notice(
                "DBG_SOCKET_FRAME count=\(self.appendedFrameCount, privacy: .public) bytes=\(pcm16LE.count, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
            )
        }

        // 言語切替検出の遅延を吸収するため、選択後も直近4秒をrolling保持する。
        appendRollingPreroll(pcm16LE)
        if let selectedTranslationTarget {
            enqueueTranslationFrame(pcm16LE, target: selectedTranslationTarget)
        }
    }

    func setSpokenLanguage(_ language: SpokenLanguage) async throws {
        guard isRunning else {
            throw RealtimeTranslationError.notConnected
        }
        let target: RealtimeTranslationOutputLanguage
        switch language {
        case .japanese:
            target = .english
        case .english:
            target = .japanese
        case .unknown:
            return
        }
        guard selectedTranslationTarget != target else { return }
        selectedTranslationTarget = target
        // 旧target向けの未送信frameは破棄し、rolling prerollを新targetへflushする。
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        let preroll = translationPrerollFrames
        AppLogger.realtime.notice(
            "DBG_AUDIO_ROUTE target=\(target.rawValue, privacy: .public) frame=\(self.appendedFrameCount, privacy: .public) preroll=\(preroll.count, privacy: .public)"
        )
        for frame in preroll {
            enqueueTranslationFrame(frame, target: target)
        }
    }

    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws {
        guard isRunning else {
            throw RealtimeTranslationError.notConnected
        }
        try await sourceConnection.updateTuning(tuning)
    }

    func resetAudioRouting() {
        // rolling prerollは維持し、次のsetSpokenLanguageでflushできるようにする。
        selectedTranslationTarget = nil
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        consecutiveTranslationFailures = 0
    }

    private func appendRollingPreroll(_ pcm16LE: Data) {
        translationPrerollFrames.append(pcm16LE)
        if translationPrerollFrames.count > Self.translationPrerollFrameLimit {
            translationPrerollFrames.removeFirst(
                translationPrerollFrames.count - Self.translationPrerollFrameLimit
            )
        }
    }

    func closeGracefully() async throws {
        guard isRunning else { return }
        isRunning = false
        translationPumpTask?.cancel()
        translationPumpTask = nil
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        var firstError: Error?
        do {
            async let sourceClose: Void = sourceConnection.closeGracefully()
            async let englishClose: Void = englishConnection.closeGracefully()
            async let japaneseClose: Void = japaneseConnection.closeGracefully()
            _ = try await (sourceClose, englishClose, japaneseClose)
        } catch {
            firstError = error
        }
        mergeTask?.cancel()
        mergeTask = nil
        eventContinuation?.finish()
        eventContinuation = nil
        if let firstError {
            throw firstError
        }
    }

    func forceClose() async {
        isRunning = false
        selectedTranslationTarget = nil
        translationPrerollFrames.removeAll(keepingCapacity: true)
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        consecutiveTranslationFailures = 0
        connectionEpoch += 1
        translationPumpTask?.cancel()
        translationPumpTask = nil
        mergeTask?.cancel()
        mergeTask = nil
        await sourceConnection.forceClose()
        await englishConnection.forceClose()
        await japaneseConnection.forceClose()
        eventContinuation?.finish()
        eventContinuation = nil
    }

    private func enqueueTranslationFrame(
        _ pcm16LE: Data,
        target: RealtimeTranslationOutputLanguage
    ) {
        pendingTranslationFrames.append((pcm16LE, target))
        guard translationPumpTask == nil else { return }
        translationPumpTask = Task {
            await self.pumpTranslationFrames()
        }
    }

    private func pumpTranslationFrames() async {
        while isRunning, !Task.isCancelled {
            guard !pendingTranslationFrames.isEmpty else { break }
            let (frame, target) = pendingTranslationFrames.removeFirst()
            do {
                switch target {
                case .english:
                    try await englishConnection.appendAudioFrame(frame)
                case .japanese:
                    try await japaneseConnection.appendAudioFrame(frame)
                }
                consecutiveTranslationFailures = 0
            } catch is CancellationError {
                break
            } catch {
                consecutiveTranslationFailures += 1
                AppLogger.realtime.error(
                    "Translation append failed count=\(self.consecutiveTranslationFailures, privacy: .public) target=\(target.rawValue, privacy: .public)"
                )
                if consecutiveTranslationFailures >= Self.consecutiveTranslationFailureLimit {
                    let epoch = connectionEpoch
                    eventContinuation?.yield(
                        RealtimeTranslationStreamEvent(
                            target: target,
                            event: .error(
                                message: "翻訳サーバーへの音声送信が失敗しました",
                                code: "transport"
                            ),
                            epoch: epoch
                        )
                    )
                    break
                }
            }
        }
        translationPumpTask = nil
        // ポンプ停止中に積まれたframeがあれば再開する。
        if isRunning, !pendingTranslationFrames.isEmpty {
            translationPumpTask = Task {
                await self.pumpTranslationFrames()
            }
        }
    }

    private func startEventMerge(epoch: Int) {
        mergeTask?.cancel()
        let continuation = eventContinuation
        mergeTask = Task {
            await withTaskGroup(of: Void.self) { group in
                group.addTask { [sourceConnection] in
                    let stream = await sourceConnection.events
                    for await event in stream {
                        guard await self.connectionEpoch == epoch else { return }
                        if case .inputTranscriptDelta = event.event {
                            await self.noteSourceDelta()
                        }
                        continuation?.yield(
                            RealtimeTranslationStreamEvent(
                                target: event.target,
                                event: event.event,
                                epoch: epoch
                            )
                        )
                    }
                }
                group.addTask { [englishConnection] in
                    let stream = await englishConnection.events
                    for await event in stream {
                        guard await self.connectionEpoch == epoch else { return }
                        // Dual側のepochで再ラベルし、接続内部epochと揃える。
                        continuation?.yield(
                            RealtimeTranslationStreamEvent(
                                target: event.target,
                                event: event.event,
                                epoch: epoch
                            )
                        )
                    }
                }
                group.addTask { [japaneseConnection] in
                    let stream = await japaneseConnection.events
                    for await event in stream {
                        guard await self.connectionEpoch == epoch else { return }
                        continuation?.yield(
                            RealtimeTranslationStreamEvent(
                                target: event.target,
                                event: event.event,
                                epoch: epoch
                            )
                        )
                    }
                }
            }
            // 全接続のイベント流が終わったら購読側を解放する。
            if await self.connectionEpoch == epoch {
                continuation?.finish()
            }
        }
    }

    private func noteSourceDelta() {
        sourceDeltaCount += 1
        if sourceDeltaCount == 1 || sourceDeltaCount.isMultiple(of: 25) {
            AppLogger.realtime.notice(
                "DBG_SOURCE_STATS sent=\(self.sourceSentFrameCount, privacy: .public) deltas=\(self.sourceDeltaCount, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
            )
        }
    }

    private func recreateEventStream() {
        eventContinuation?.finish()
        let pair = Self.makeEventStream()
        eventStream = pair.stream
        eventContinuation = pair.continuation
    }

    private static func makeEventStream() -> (
        stream: AsyncStream<RealtimeTranslationStreamEvent>,
        continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    ) {
        var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let stream = AsyncStream(bufferingPolicy: .bufferingNewest(512)) {
            continuation = $0
        }
        return (stream, continuation)
    }

    deinit {
        eventContinuation?.finish()
    }
}
