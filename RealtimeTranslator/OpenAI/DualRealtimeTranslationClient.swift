import Foundation

protocol DualRealtimeTranslationClienting: AnyObject, Sendable {
    var events: AsyncStream<RealtimeTranslationStreamEvent> { get async }
    var feed: EventFeed { get async }
    func start(apiKey: String, tuning: RealtimeSessionTuning, pair: LanguagePair) async throws
    func appendAudioFrame(_ pcm16LE: Data) async throws
    func selectTranslationTarget(_ target: RealtimeTranslationOutputLanguage?) async throws
    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws
    func resetAudioRouting() async
    /// 停止開始時に呼ぶ。未読の merge イベントとこれ以降の close 窓を stop drain へ蓄える。
    func beginStopDrainCapture() async
    /// session consumer が stream から読んで適用／破棄したあとに呼ぶ。
    /// stop drain は未消費の recentYields だけをコピーし、既読の nil event_id delta を再適用しない。
    func acknowledgeConsumedStreamEvent(runToken: Int?) async
    /// 正常停止。commit/session.close 中の字幕イベントを返し、呼び出し側が assembler へ取り込む。
    /// 接続 close が失敗しても drain 済みイベントは返し、残接続は内部で forceClose する。
    @discardableResult
    func closeGracefully() async -> [RealtimeTranslationStreamEvent]
    func forceClose() async
    var connectionEpoch: Int { get async }
}

/// 原文 transcription 接続と翻訳 lane 群を束ねるオーケストレーター。
/// 帳簿は `MergedEventBuffer`（merge 配送）、`TranslationFrameQueues`（preroll/pending）、
/// `TranslationPumpSupervisor`（ポンプ世代）へ分割し、ここは接続調停とルーティングだけを持つ。
actor DualRealtimeTranslationClient: DualRealtimeTranslationClienting {
    static let mergedEventBufferLimit =
        DualRealtimeTranslationClientTuning.default.mergedEventBufferLimit
    static let unacknowledgedRetentionLimit =
        DualRealtimeTranslationClientTuning.default.unacknowledgedRetentionLimit
    static let stopDrainRetentionLimit =
        DualRealtimeTranslationClientTuning.default.stopDrainRetentionLimit
    static let translationPrerollFrameLimit =
        DualRealtimeTranslationClientTuning.default.translationPrerollFrameLimit
    static let translationPendingFrameLimit =
        DualRealtimeTranslationClientTuning.default.translationPendingFrameLimit
    static let consecutiveTranslationFailureLimit =
        DualRealtimeTranslationClientTuning.default.consecutiveTranslationFailureLimit
    static let translationDrainTimeoutNanosecondsPerPendingFrame =
        DualRealtimeTranslationClientTuning.default.translationDrainTimeoutNanosecondsPerPendingFrame
    static let translationDrainTimeoutCapNanoseconds =
        DualRealtimeTranslationClientTuning.default.translationDrainTimeoutCapNanoseconds
    static let defaultTranslationDrainTimeoutNanoseconds =
        DualRealtimeTranslationClientTuning.default.defaultTranslationDrainTimeoutNanoseconds

    private let sourceConnection: RealtimeSourceTranscriptionConnection
    private let connections: [RealtimeTranslationOutputLanguage: RealtimeTranslationConnection]
    private let tuning: DualRealtimeTranslationClientTuning
    private let translationDrainTimeoutNanoseconds: UInt64
    private var mergeTask: Task<Void, Never>?
    private var mergedEvents: MergedEventBuffer
    private var frameQueues: TranslationFrameQueues
    private var pump: TranslationPumpSupervisor
    private(set) var connectionEpoch = 0
    private var isRunning = false
    private var appendedFrameCount = 0
    private var sourceSentFrameCount = 0
    private var sourceDeltaCount = 0
    private var selectedTranslationTarget: RealtimeTranslationOutputLanguage?
    /// この世代で handshake した翻訳 lane。未使用 leftover 接続は merge しない。
    private var startedTranslationTargets: [RealtimeTranslationOutputLanguage] = []

    var events: AsyncStream<RealtimeTranslationStreamEvent> {
        mergedEvents.stream
    }

    var feed: EventFeed {
        EventFeed(
            events: mergedEvents.stream,
            runToken: connectionEpoch,
            deliveryState: mergedEvents.deliveryState
        )
    }

    var pendingTranslationFrameCount: Int {
        frameQueues.pendingCount
    }

    var isTranslationPumpHalted: Bool {
        pump.haltedForTransportFailure
    }

    var isTranslationPumpTracked: Bool {
        pump.isTracked
    }

    init(
        sourceConnection: RealtimeSourceTranscriptionConnection? = nil,
        englishConnection: RealtimeTranslationConnection? = nil,
        japaneseConnection: RealtimeTranslationConnection? = nil,
        spanishConnection: RealtimeTranslationConnection? = nil,
        translationDrainTimeoutNanoseconds: UInt64? = nil,
        tuning: DualRealtimeTranslationClientTuning = .default
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
        self.tuning = tuning
        // drain の base は明示引数が優先。省略時は注入 tuning の既定値を使い、
        // per-frame 予算・cap と同じ設定源から計算されるようにする。
        self.translationDrainTimeoutNanoseconds =
            translationDrainTimeoutNanoseconds ?? tuning.defaultTranslationDrainTimeoutNanoseconds
        mergedEvents = MergedEventBuffer(tuning: tuning)
        frameQueues = TranslationFrameQueues(
            prerollLimit: tuning.translationPrerollFrameLimit,
            pendingLimit: tuning.translationPendingFrameLimit
        )
        pump = TranslationPumpSupervisor(failureLimit: tuning.consecutiveTranslationFailureLimit)
    }

    func start(
        apiKey: String,
        tuning: RealtimeSessionTuning = .default,
        pair: LanguagePair
    ) async throws {
        await forceClose()
        // 新しい録音が、停止途中に残した drain 窓を closeGracefully で返さない。
        mergedEvents.clearStopDrain()
        mergedEvents.recreate()
        connectionEpoch += 1
        let epoch = connectionEpoch
        mergedEvents.arm(epoch: epoch)
        isRunning = true
        appendedFrameCount = 0
        sourceSentFrameCount = 0
        sourceDeltaCount = 0
        pump.reset()
        selectedTranslationTarget = nil
        frameQueues.clearAll()

        do {
            let translationConnections = connections
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask {
                    try await self.sourceConnection.start(
                        apiKey: apiKey,
                        tuning: tuning,
                        pair: pair,
                        deliveryState: self.mergedEvents.deliveryState
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
                            ),
                            deliveryState: self.mergedEvents.deliveryState
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
        frameQueues.appendPreroll(pcm16LE)
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
        frameQueues.clearPending()
        let preroll = frameQueues.prerollFrames
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
        frameQueues.clearPending()
        pump.resetFailures()
    }

    static func resolveTranslationDrainTimeoutNanoseconds(
        baseNanoseconds: UInt64,
        pendingFrameCount: Int
    ) -> UInt64 {
        DualRealtimeTranslationClientTuning.default
            .resolveTranslationDrainTimeoutNanoseconds(
                baseNanoseconds: baseNanoseconds,
                pendingFrameCount: pendingFrameCount
            )
    }

    private func resolveCloseDrainTimeoutNanoseconds() -> UInt64 {
        var pending = frameQueues.pendingCount
        if pump.isTracked {
            pending += 1
        }
        return tuning.resolveTranslationDrainTimeoutNanoseconds(
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
            if !pump.isTracked, frameQueues.pendingCount == 0 {
                return
            }

            let remaining = deadline - ContinuousClock.now
            guard remaining > .zero else {
                if !pump.isTracked, frameQueues.pendingCount == 0 {
                    return
                }
                throw RealtimeTranslationError.recoverableTransportFailure("translation pump drain timeout")
            }

            // TaskGroupはスコープ終了時にキャンセル済み子タスクの完了も待つ。
            // pump.value待ちはキャンセルで解けないため、状態を短周期で再確認する。
            try await Task.sleep(for: min(remaining, pollInterval))
        }
    }

    func beginStopDrainCapture() {
        mergedEvents.beginStopDrainCapture()
    }

    func acknowledgeConsumedStreamEvent(runToken: Int? = nil) {
        mergedEvents.acknowledge(runToken: runToken, currentEpoch: connectionEpoch)
    }

    @discardableResult
    func closeGracefully() async -> [RealtimeTranslationStreamEvent] {
        guard isRunning else {
            return mergedEvents.takeStopDrainEvents()
        }

        // consumer 停止後〜ここまでのイベントも落とさない。未武装ならここで武装する。
        mergedEvents.beginStopDrainCapture()

        // 未送信の翻訳フレームを先に送り、停止時の訳文欠落を防ぐ。
        // preroll flush 直後は待ち行列が長いので pending 数に応じて予算を伸ばす。
        // drain 待ち中に届く最終 delta も stopDrainBuffer へ蓄える。
        try? await waitForTranslationDrain(timeoutNanoseconds: resolveCloseDrainTimeoutNanoseconds())

        isRunning = false
        pump.invalidate()
        frameQueues.clearPending()
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
        let drained = mergedEvents.takeStopDrainEvents()
        mergedEvents.finishStream()
        if closeFailed {
            await forceClose()
        }
        return drained
    }

    func forceClose() async {
        isRunning = false
        selectedTranslationTarget = nil
        startedTranslationTargets = []
        frameQueues.clearAll()
        pump.reset()
        // beginStopDrainCapture 済みの窓は残す。reconnect の tearDown / generation
        // mismatch の forceClose が、stop が close drain へ渡す未読 delta を消さない。
        mergedEvents.clearYieldWindow()
        connectionEpoch += 1
        pump.invalidate()
        mergeTask?.cancel()
        mergeTask = nil
        await sourceConnection.forceClose()
        for connection in connections.values {
            await connection.forceClose()
        }
        mergedEvents.finishStream()
    }

    private func enqueueTranslationFrame(
        _ pcm16LE: Data,
        target: RealtimeTranslationOutputLanguage
    ) {
        // transport failure後はenqueue自体を止め、ポンプ再起動の隙を残さない。
        guard !pump.haltedForTransportFailure else { return }
        if !frameQueues.hasCapacityForPending {
            haltTranslationPump(target: target, messageKey: "error.translationBacklog")
            return
        }
        frameQueues.enqueuePending(pcm16LE, target: target)
        startTranslationPumpIfNeeded()
    }

    private func startTranslationPumpIfNeeded() {
        guard !pump.isTracked else { return }
        pump.start { generation in
            await self.pumpTranslationFrames(generation: generation)
        }
    }

    private func finishTranslationPumpIfCurrent(generation: Int, pumpEpoch: Int) {
        guard pump.finishIfCurrent(generation: generation) else { return }
        // ポンプ停止中に積まれたframeがあれば再開する。
        // transport failure後はInterpretationSession側の再接続に任せ、ここでは再開しない。
        if !pump.haltedForTransportFailure,
            isRunning,
            connectionEpoch == pumpEpoch,
            frameQueues.pendingCount > 0
        {
            startTranslationPumpIfNeeded()
        }
    }

    private func pumpTranslationFrames(generation: Int) async {
        let pumpEpoch = connectionEpoch
        while isRunning, !Task.isCancelled, !pump.haltedForTransportFailure {
            guard let (frame, target) = frameQueues.popPending() else { break }
            do {
                guard let connection = connections[target] else {
                    throw RealtimeTranslationError.notConnected
                }
                try await connection.appendAudioFrame(frame)
                if !pump.haltedForTransportFailure, connectionEpoch == pumpEpoch {
                    pump.resetFailures()
                }
            } catch is CancellationError {
                break
            } catch {
                if pump.haltedForTransportFailure || connectionEpoch != pumpEpoch {
                    break
                }
                let failureCount = pump.recordFailure()
                AppLogger.realtime.error(
                    "Translation append failed count=\(failureCount, privacy: .public) target=\(target.rawValue, privacy: .public)"
                )
                if pump.reachedFailureLimit {
                    haltTranslationPump(target: target, messageKey: "error.audioSendFailed")
                    // 再接続待ち中にdying socketへ送り続けない。
                    break
                }
            }
        }
        finishTranslationPumpIfCurrent(generation: generation, pumpEpoch: pumpEpoch)
    }

    private func haltTranslationPump(
        target: RealtimeTranslationOutputLanguage,
        messageKey: String
    ) {
        let pendingCount = frameQueues.pendingCount
        let reason = messageKey == "error.translationBacklog" ? "backlog" : "sendFailure"
        pump.haltForTransportFailure()
        frameQueues.clearPending()
        AppLogger.realtime.error(
            "Translation pump halted reason=\(reason, privacy: .public) count=\(pendingCount, privacy: .public) limit=\(self.tuning.translationPendingFrameLimit, privacy: .public) target=\(target.rawValue, privacy: .public) epoch=\(self.connectionEpoch, privacy: .public)"
        )
        mergedEvents.deliveryState.tryRecordTermination(.transportFailure)
        mergedEvents.deliver(
            RealtimeTranslationStreamEvent(
                lane: .translation(target),
                event: .error(
                    message: UiCopy.text(messageKey),
                    code: RealtimeServerErrorClassification.transportCode,
                    errorType: nil
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
        mergedEvents.forward(event)
    }

    private func finishMergedEventStream() {
        mergedEvents.finishDelivery()
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

    deinit {
        mergedEvents.finishContinuation()
    }
}
