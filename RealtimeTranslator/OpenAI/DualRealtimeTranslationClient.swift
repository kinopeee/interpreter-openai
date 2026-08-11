import Foundation

protocol DualRealtimeTranslationClienting: AnyObject, Sendable {
    var events: AsyncStream<RealtimeTranslationStreamEvent> { get async }
    func start(apiKey: String, tuning: RealtimeSessionTuning, pair: LanguagePair) async throws
    func appendAudioFrame(_ pcm16LE: Data) async throws
    func selectTranslationTarget(_ target: RealtimeTranslationOutputLanguage?) async throws
    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws
    func resetAudioRouting() async
    /// session consumer 停止後に呼び、以降の merge イベントを stop drain へ蓄える。
    func beginStopDrainCapture() async
    /// 正常停止。commit/session.close 中の字幕イベントを返し、呼び出し側が assembler へ取り込む。
    /// 接続 close が失敗しても drain 済みイベントは返し、残接続は内部で forceClose する。
    @discardableResult
    func closeGracefully() async -> [RealtimeTranslationStreamEvent]
    func forceClose() async
    var connectionEpoch: Int { get async }
}

actor DualRealtimeTranslationClient: DualRealtimeTranslationClienting {
    /// 100 ms frame × 40 = 直近4秒。言語判定遅延でも発話冒頭を翻訳へ届ける。
    static let translationPrerollFrameLimit = 40
    static let consecutiveTranslationFailureLimit = 3

    private let sourceConnection: RealtimeSourceTranscriptionConnection
    private let connections: [RealtimeTranslationOutputLanguage: RealtimeTranslationConnection]
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
    /// transport failure後、再接続まで翻訳ポンプを再開しない。
    private var translationPumpHaltedForTransportFailure = false
    private var selectedTranslationTarget: RealtimeTranslationOutputLanguage?
    private var translationPrerollFrames: [Data] = []
    private var pendingTranslationFrames: [(Data, RealtimeTranslationOutputLanguage)] = []
    /// closeGracefully 中だけ詰め、停止時の最終 delta 欠落を防ぐ。
    private var stopDrainBuffer: [RealtimeTranslationStreamEvent]?

    var events: AsyncStream<RealtimeTranslationStreamEvent> {
        eventStream
    }

    init(
        sourceConnection: RealtimeSourceTranscriptionConnection? = nil,
        englishConnection: RealtimeTranslationConnection? = nil,
        japaneseConnection: RealtimeTranslationConnection? = nil,
        spanishConnection: RealtimeTranslationConnection? = nil
    ) {
        if let sourceConnection, let englishConnection, let japaneseConnection {
            // 明示注入時は渡された接続だけを使い、欠けた Spanish を実ソケットで補完しない。
            self.sourceConnection = sourceConnection
            var injected: [RealtimeTranslationOutputLanguage: RealtimeTranslationConnection] = [
                .english: englishConnection,
                .japanese: japaneseConnection,
            ]
            if let spanishConnection {
                injected[.spanish] = spanishConnection
            }
            self.connections = injected
        } else {
            let safetyIdentifier = OpenAISafetyIdentifier.hashedValue()
            self.sourceConnection = sourceConnection
                ?? RealtimeSourceTranscriptionConnection(safetyIdentifier: safetyIdentifier)
            let english = englishConnection
                ?? RealtimeTranslationConnection(
                    target: .english,
                    safetyIdentifier: safetyIdentifier
                )
            let japanese = japaneseConnection
                ?? RealtimeTranslationConnection(
                    target: .japanese,
                    safetyIdentifier: safetyIdentifier
                )
            let spanish = spanishConnection
                ?? RealtimeTranslationConnection(
                    target: .spanish,
                    safetyIdentifier: safetyIdentifier
                )
            self.connections = [.english: english, .japanese: japanese, .spanish: spanish]
        }
        let pair = Self.makeEventStream()
        eventStream = pair.stream
        eventContinuation = pair.continuation
    }

    func start(
        apiKey: String,
        tuning: RealtimeSessionTuning = .default,
        pair: LanguagePair
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
        translationPumpHaltedForTransportFailure = false
        selectedTranslationTarget = nil
        translationPrerollFrames.removeAll(keepingCapacity: true)
        pendingTranslationFrames.removeAll(keepingCapacity: true)

        do {
            let translationConnections = connections
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask {
                    try await self.sourceConnection.start(
                        apiKey: apiKey,
                        tuning: tuning,
                        pair: pair
                    )
                }
                for language in pair.languages {
                    let target = Self.outputLanguage(for: language)
                    guard let connection = translationConnections[target] else {
                        throw RealtimeTranslationError.notConnected
                    }
                    group.addTask {
                        try await connection.start(
                            apiKey: apiKey,
                            config: .withoutSourceTranscription(
                                target: target,
                                noiseReduction: tuning.noiseReduction
                            )
                        )
                    }
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
        #if DEBUG
        if sourceSentFrameCount == 1 || sourceSentFrameCount.isMultiple(of: 25) {
            AppLogger.realtime.notice(
                "DBG_SOURCE_STATS sent=\(self.sourceSentFrameCount, privacy: .public) deltas=\(self.sourceDeltaCount, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
            )
            AppLogger.realtime.notice(
                "DBG_SOCKET_FRAME count=\(self.appendedFrameCount, privacy: .public) bytes=\(pcm16LE.count, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
            )
        }
        #endif

        // 言語切替検出の遅延を吸収するため、選択後も直近4秒をrolling保持する。
        appendRollingPreroll(pcm16LE)
        if let selectedTranslationTarget {
            enqueueTranslationFrame(pcm16LE, target: selectedTranslationTarget)
        }
    }

    func selectTranslationTarget(_ target: RealtimeTranslationOutputLanguage?) async throws {
        guard isRunning else {
            throw RealtimeTranslationError.notConnected
        }
        guard selectedTranslationTarget != target else { return }
        selectedTranslationTarget = target
        guard let target else { return }
        // 旧target向けの未送信frameは破棄し、rolling prerollを新targetへflushする。
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        let preroll = translationPrerollFrames
        #if DEBUG
        AppLogger.realtime.notice(
            "DBG_AUDIO_ROUTE target=\(target.rawValue, privacy: .public) frame=\(self.appendedFrameCount, privacy: .public) preroll=\(preroll.count, privacy: .public)"
        )
        #endif
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
        // rolling prerollは維持し、次のtarget選択でflushできるようにする。
        selectedTranslationTarget = nil
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        consecutiveTranslationFailures = 0
    }

    /// 翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。決定的なテストのために使う。
    /// 送信が停滞しても `timeoutNanoseconds` で待機だけを打ち切り、送信ポンプ自体は停止しない。
    func waitForTranslationDrain(timeoutNanoseconds: UInt64 = 5_000_000_000) async throws {
        let deadline = ContinuousClock.now + .nanoseconds(Int64(timeoutNanoseconds))
        let pollInterval = Duration.milliseconds(5)
        while true {
            if translationPumpTask == nil, pendingTranslationFrames.isEmpty {
                return
            }

            let remaining = deadline - ContinuousClock.now
            guard remaining > .zero else {
                if translationPumpTask == nil, pendingTranslationFrames.isEmpty {
                    return
                }
                throw RealtimeTranslationError.recoverableTransportFailure("translation pump drain timeout")
            }

            // TaskGroupはスコープ終了時にキャンセル済み子タスクの完了も待つ。
            // pump.value待ちはキャンセルで解けないため、状態を短周期で再確認する。
            try await Task.sleep(for: min(remaining, pollInterval))
        }
    }

    private func appendRollingPreroll(_ pcm16LE: Data) {
        translationPrerollFrames.append(pcm16LE)
        if translationPrerollFrames.count > Self.translationPrerollFrameLimit {
            translationPrerollFrames.removeFirst(
                translationPrerollFrames.count - Self.translationPrerollFrameLimit
            )
        }
    }

    func beginStopDrainCapture() {
        if stopDrainBuffer == nil {
            stopDrainBuffer = []
        }
    }

    @discardableResult
    func closeGracefully() async -> [RealtimeTranslationStreamEvent] {
        guard isRunning else {
            let drained = stopDrainBuffer ?? []
            stopDrainBuffer = nil
            return drained
        }

        // consumer 停止後〜ここまでのイベントも落とさない。未武装ならここで武装する。
        beginStopDrainCapture()

        // 未送信の翻訳フレームを先に送り、停止時の訳文欠落を防ぐ。
        // drain 待ち中に届く最終 delta も stopDrainBuffer へ蓄える。
        try? await waitForTranslationDrain()

        isRunning = false
        translationPumpTask?.cancel()
        translationPumpTask = nil
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        var closeFailed = false
        do {
            let closes = connections.values.map { connection in
                Task { try await connection.closeGracefully() }
            }
            try await sourceConnection.closeGracefully()
            for close in closes {
                try await close.value
            }
        } catch {
            closeFailed = true
            AppLogger.realtime.error(
                "Graceful close failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
        }
        mergeTask?.cancel()
        mergeTask = nil
        // close 失敗でも drain を先に確定し、forceClose で消えないようにする。
        let drained = stopDrainBuffer ?? []
        stopDrainBuffer = nil
        eventContinuation?.finish()
        eventContinuation = nil
        if closeFailed {
            await forceClose()
        }
        return drained
    }

    func forceClose() async {
        isRunning = false
        selectedTranslationTarget = nil
        translationPrerollFrames.removeAll(keepingCapacity: true)
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        consecutiveTranslationFailures = 0
        translationPumpHaltedForTransportFailure = false
        stopDrainBuffer = nil
        connectionEpoch += 1
        translationPumpTask?.cancel()
        translationPumpTask = nil
        mergeTask?.cancel()
        mergeTask = nil
        await sourceConnection.forceClose()
        for connection in connections.values {
            await connection.forceClose()
        }
        eventContinuation?.finish()
        eventContinuation = nil
    }

    private func enqueueTranslationFrame(
        _ pcm16LE: Data,
        target: RealtimeTranslationOutputLanguage
    ) {
        // transport failure後はenqueue自体を止め、ポンプ再起動の隙を残さない。
        guard !translationPumpHaltedForTransportFailure else { return }
        pendingTranslationFrames.append((pcm16LE, target))
        guard translationPumpTask == nil else { return }
        translationPumpTask = Task {
            await self.pumpTranslationFrames()
        }
    }

    private func pumpTranslationFrames() async {
        while isRunning, !Task.isCancelled, !translationPumpHaltedForTransportFailure {
            guard !pendingTranslationFrames.isEmpty else { break }
            let (frame, target) = pendingTranslationFrames.removeFirst()
            do {
                guard let connection = connections[target] else {
                    throw RealtimeTranslationError.notConnected
                }
                try await connection.appendAudioFrame(frame)
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
                            lane: .translation(target),
                            event: .error(
                                message: "翻訳サーバーへの音声送信が失敗しました",
                                code: "transport"
                            ),
                            epoch: epoch
                        )
                    )
                    // 再接続待ち中にdying socketへ送り続けない。
                    translationPumpHaltedForTransportFailure = true
                    pendingTranslationFrames.removeAll(keepingCapacity: true)
                    break
                }
            }
        }
        translationPumpTask = nil
        // ポンプ停止中に積まれたframeがあれば再開する。
        // transport failure後はInterpretationSession側の再接続に任せ、ここでは再開しない。
        if !translationPumpHaltedForTransportFailure, isRunning, !pendingTranslationFrames.isEmpty {
            translationPumpTask = Task {
                await self.pumpTranslationFrames()
            }
        }
    }

    private func startEventMerge(epoch: Int) {
        mergeTask?.cancel()
        mergeTask = Task {
            await withTaskGroup(of: Void.self) { group in
                group.addTask { [sourceConnection] in
                    let stream = await sourceConnection.events
                    for await event in stream {
                        guard await self.connectionEpoch == epoch else { return }
                        if case .inputTranscriptDelta = event.event {
                            await self.noteSourceDelta()
                        }
                            await self.forwardMergedEvent(
                                RealtimeTranslationStreamEvent(
                                lane: event.lane,
                                event: event.event,
                                epoch: epoch
                            )
                        )
                    }
                }
                for connection in self.connections.values {
                    group.addTask {
                        let stream = await connection.events
                        for await event in stream {
                            guard await self.connectionEpoch == epoch else { return }
                            if case .inputTranscriptDelta = event.event {
                                continue
                            }
                            await self.forwardMergedEvent(
                                RealtimeTranslationStreamEvent(
                                    lane: .translation(event.target),
                                    event: event.event,
                                    epoch: epoch
                                )
                            )
                        }
                    }
                }
            }
            // 全接続のイベント流が終わったら購読側を解放する。
            if await self.connectionEpoch == epoch {
                await self.finishMergedEventStream()
            }
        }
    }

    private static func outputLanguage(for language: SpokenLanguage) -> RealtimeTranslationOutputLanguage {
        switch language {
        case .japanese: return .japanese
        case .english: return .english
        case .spanish: return .spanish
        case .unknown: return .english
        }
    }

    private func forwardMergedEvent(_ event: RealtimeTranslationStreamEvent) {
        // 接続側で落とすのが正攻法だが、stopDrainBuffer のメモリ肥大も防ぐ。
        if case .outputAudioDelta = event.event {
            return
        }
        if stopDrainBuffer != nil {
            stopDrainBuffer?.append(event)
        }
        eventContinuation?.yield(event)
    }

    private func finishMergedEventStream() {
        eventContinuation?.finish()
    }

    private func noteSourceDelta() {
        sourceDeltaCount += 1
        #if DEBUG
        if sourceDeltaCount == 1 || sourceDeltaCount.isMultiple(of: 25) {
            AppLogger.realtime.notice(
                "DBG_SOURCE_STATS sent=\(self.sourceSentFrameCount, privacy: .public) deltas=\(self.sourceDeltaCount, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
            )
        }
        #endif
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
