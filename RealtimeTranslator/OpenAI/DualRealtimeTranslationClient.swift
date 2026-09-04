import Foundation

protocol DualRealtimeTranslationClienting: AnyObject, Sendable {
    var events: AsyncStream<RealtimeTranslationStreamEvent> { get async }
    func start(apiKey: String, tuning: RealtimeSessionTuning, pair: LanguagePair) async throws
    func appendAudioFrame(_ pcm16LE: Data) async throws
    func selectTranslationTarget(_ target: RealtimeTranslationOutputLanguage?) async throws
    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws
    func resetAudioRouting() async
    /// 停止開始時に呼ぶ。未読の merge イベントとこれ以降の close 窓を stop drain へ蓄える。
    func beginStopDrainCapture() async
    /// session consumer が stream から読んで適用／破棄したあとに呼ぶ。
    /// stop drain は未消費の recentYields だけをコピーし、既読の nil event_id delta を再適用しない。
    func acknowledgeConsumedStreamEvent() async
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
    static let translationPendingFrameLimit = 80
    static let consecutiveTranslationFailureLimit = 3
    /// 停止時 drain で未送信 frame 1 枚あたりに足す予算。preroll flush 後の短い停滞で訳文を落とさない。
    static let translationDrainTimeoutNanosecondsPerPendingFrame: UInt64 = 250_000_000
    /// 停止時 drain の上限。Send 停滞でも Stop が無期限待ちしない。
    static let translationDrainTimeoutCapNanoseconds: UInt64 = 30_000_000_000
    static let defaultTranslationDrainTimeoutNanoseconds: UInt64 = 5_000_000_000

    private let sourceConnection: RealtimeSourceTranscriptionConnection
    private let connections: [RealtimeTranslationOutputLanguage: RealtimeTranslationConnection]
    private let translationDrainTimeoutNanoseconds: UInt64
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
    /// この世代で handshake した翻訳 lane。未使用 leftover 接続は merge しない。
    private var startedTranslationTargets: [RealtimeTranslationOutputLanguage] = []
    private var translationPrerollFrames: [Data] = []
    private var pendingTranslationFrames: [(Data, RealtimeTranslationOutputLanguage)] = []
    /// closeGracefully 中だけ詰め、停止時の最終 delta 欠落を防ぐ。
    private var stopDrainBuffer: [RealtimeTranslationStreamEvent]?
    /// `events` AsyncStream と同じ容量。finish() が未読を捨ててもここから close drain へ移せる。
    private static let mergedEventBufferLimit = 512
    /// yield 済み字幕イベントの最新側。stream の bufferingNewest と同じ窓。
    private var recentYields: [RealtimeTranslationStreamEvent] = []
    /// `forwardMergedEvent` が yield した回数。recentYields と同じ窓で数える。
    private var yieldedEventCount = 0
    /// session consumer が stream から読んで acknowledge した回数。
    private var consumedEventCount = 0

    var events: AsyncStream<RealtimeTranslationStreamEvent> {
        eventStream
    }

    var pendingTranslationFrameCount: Int {
        pendingTranslationFrames.count
    }

    var isTranslationPumpHalted: Bool {
        translationPumpHaltedForTransportFailure
    }

    init(
        sourceConnection: RealtimeSourceTranscriptionConnection? = nil,
        englishConnection: RealtimeTranslationConnection? = nil,
        japaneseConnection: RealtimeTranslationConnection? = nil,
        spanishConnection: RealtimeTranslationConnection? = nil,
        translationDrainTimeoutNanoseconds: UInt64 = DualRealtimeTranslationClient
            .defaultTranslationDrainTimeoutNanoseconds
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
        self.translationDrainTimeoutNanoseconds = translationDrainTimeoutNanoseconds
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
        // 新しい録音が、停止途中に残した drain 窓を closeGracefully で返さない。
        stopDrainBuffer = nil
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
        startedTranslationTargets = pair.languages.map(Self.outputLanguage(for:))
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

    /// 停止時 drain 予算。base（既定5秒）に未送信 frame 分を足し、cap（30秒）で打ち切る。
    static func resolveTranslationDrainTimeoutNanoseconds(
        baseNanoseconds: UInt64,
        pendingFrameCount: Int
    ) -> UInt64 {
        let pending = UInt64(max(0, pendingFrameCount))
        let scaled = baseNanoseconds
            &+ (pending &* translationDrainTimeoutNanosecondsPerPendingFrame)
        let cap = max(baseNanoseconds, translationDrainTimeoutCapNanoseconds)
        return min(max(scaled, baseNanoseconds), cap)
    }

    private func resolveCloseDrainTimeoutNanoseconds() -> UInt64 {
        var pending = pendingTranslationFrames.count
        if translationPumpTask != nil {
            pending += 1
        }
        return Self.resolveTranslationDrainTimeoutNanoseconds(
            baseNanoseconds: translationDrainTimeoutNanoseconds,
            pendingFrameCount: pending
        )
    }

    /// 翻訳ポンプが現在の待ち行列を処理し終えるまで待つ。決定的なテストのために使う。
    /// 送信が停滞しても `timeoutNanoseconds` で待機だけを打ち切り、送信ポンプ自体は停止しない。
    func waitForTranslationDrain(
        timeoutNanoseconds: UInt64 = DualRealtimeTranslationClient.defaultTranslationDrainTimeoutNanoseconds
    ) async throws {
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
            // consumer が generation bump で ingest を止めたあと、
            // AsyncStream.finish() は未読要素を捨てる。Windows Channel と違い再読できないので、
            // 未消費の最新窓だけを移す。既に ingest した nil event_id delta は再適用しない。
            let unreadCount = min(recentYields.count, max(0, yieldedEventCount - consumedEventCount))
            stopDrainBuffer = Array(recentYields.suffix(unreadCount))
        }
    }

    func acknowledgeConsumedStreamEvent() {
        consumedEventCount += 1
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
        // preroll flush 直後は待ち行列が長いので pending 数に応じて予算を伸ばす。
        // drain 待ち中に届く最終 delta も stopDrainBuffer へ蓄える。
        try? await waitForTranslationDrain(timeoutNanoseconds: resolveCloseDrainTimeoutNanoseconds())

        isRunning = false
        translationPumpTask?.cancel()
        translationPumpTask = nil
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        // 原文 close が先に失敗しても翻訳 close を捨てない。Windows の WhenAll と同じく
        // 全 lane を待ち、未 await の close が次セッションのソケットを閉じるのを防ぐ。
        var closeFailed = false
        let closes = connections.values.map { connection in
            Task { try await connection.closeGracefully() }
        }
        do {
            try await sourceConnection.closeGracefully()
        } catch {
            closeFailed = true
            AppLogger.realtime.error(
                "Graceful close failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
        }
        for close in closes {
            do {
                try await close.value
            } catch {
                if !closeFailed {
                    closeFailed = true
                    AppLogger.realtime.error(
                        "Graceful close failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
                    )
                }
            }
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
        startedTranslationTargets = []
        translationPrerollFrames.removeAll(keepingCapacity: true)
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        consecutiveTranslationFailures = 0
        translationPumpHaltedForTransportFailure = false
        // beginStopDrainCapture 済みの窓は残す。reconnect の tearDown / generation
        // mismatch の forceClose が、stop が close drain へ渡す未読 delta を消さない。
        recentYields.removeAll(keepingCapacity: true)
        yieldedEventCount = 0
        consumedEventCount = 0
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
        if pendingTranslationFrames.count >= Self.translationPendingFrameLimit {
            haltTranslationPump(target: target, messageKey: "error.translationBacklog")
            return
        }
        pendingTranslationFrames.append((pcm16LE, target))
        guard translationPumpTask == nil else { return }
        translationPumpTask = Task {
            await self.pumpTranslationFrames()
        }
    }

    private func pumpTranslationFrames() async {
        let pumpEpoch = connectionEpoch
        while isRunning, !Task.isCancelled, !translationPumpHaltedForTransportFailure {
            guard !pendingTranslationFrames.isEmpty else { break }
            let (frame, target) = pendingTranslationFrames.removeFirst()
            do {
                guard let connection = connections[target] else {
                    throw RealtimeTranslationError.notConnected
                }
                try await connection.appendAudioFrame(frame)
                if !translationPumpHaltedForTransportFailure, connectionEpoch == pumpEpoch {
                    consecutiveTranslationFailures = 0
                }
            } catch is CancellationError {
                break
            } catch {
                if translationPumpHaltedForTransportFailure || connectionEpoch != pumpEpoch {
                    break
                }
                consecutiveTranslationFailures += 1
                AppLogger.realtime.error(
                    "Translation append failed count=\(self.consecutiveTranslationFailures, privacy: .public) target=\(target.rawValue, privacy: .public)"
                )
                if consecutiveTranslationFailures >= Self.consecutiveTranslationFailureLimit {
                    haltTranslationPump(target: target, messageKey: "error.audioSendFailed")
                    // 再接続待ち中にdying socketへ送り続けない。
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

    private func haltTranslationPump(
        target: RealtimeTranslationOutputLanguage,
        messageKey: String
    ) {
        let pendingCount = pendingTranslationFrames.count
        let reason = messageKey == "error.translationBacklog" ? "backlog" : "sendFailure"
        translationPumpHaltedForTransportFailure = true
        pendingTranslationFrames.removeAll(keepingCapacity: true)
        AppLogger.realtime.error(
            "Translation pump halted reason=\(reason, privacy: .public) count=\(pendingCount, privacy: .public) limit=\(Self.translationPendingFrameLimit, privacy: .public) target=\(target.rawValue, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
        )
        eventContinuation?.yield(
            RealtimeTranslationStreamEvent(
                lane: .translation(target),
                event: .error(
                    message: UiCopy.text(messageKey),
                    code: "transport"
                ),
                epoch: connectionEpoch
            )
        )
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
                // コンストラクタで用意した未使用 leftover lane は merge しない。
                // Windows Channel と同様、完了済み stream に残った訳文 / transport error が
                // 次世代へ混線しないように、handshake した target だけを購読する。
                for target in self.startedTranslationTargets {
                    guard let connection = self.connections[target] else {
                        continue
                    }
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
        recentYields.append(event)
        yieldedEventCount += 1
        if recentYields.count > Self.mergedEventBufferLimit {
            let overflow = recentYields.count - Self.mergedEventBufferLimit
            recentYields.removeFirst(overflow)
            yieldedEventCount -= overflow
            consumedEventCount = max(0, consumedEventCount - overflow)
        }
        eventContinuation?.yield(event)
        if stopDrainBuffer != nil {
            stopDrainBuffer?.append(event)
        }
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
        recentYields.removeAll(keepingCapacity: true)
        yieldedEventCount = 0
        consumedEventCount = 0
        let pair = Self.makeEventStream()
        eventStream = pair.stream
        eventContinuation = pair.continuation
    }

    private static func makeEventStream() -> (
        stream: AsyncStream<RealtimeTranslationStreamEvent>,
        continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation
    ) {
        var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
        let stream = AsyncStream(bufferingPolicy: .bufferingNewest(mergedEventBufferLimit)) {
            continuation = $0
        }
        return (stream, continuation)
    }

    deinit {
        eventContinuation?.finish()
    }
}
