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
    /// 録音停止後、最後の字幕ペアを読み取れるよう残す時間。
    static let defaultPostStopSubtitleRetentionNanoseconds: UInt64 = 5_000_000_000
    /// ルーティング判定用に保持する原文の上限 (UTF-16)。
    /// ja-* は末尾の非空白 scalar ウィンドウへ切り詰め、ウィンドウ内の空白が異常に長い場合の
    /// 安全弁として空白 run を圧縮してこの長さへ収める。en-es は語窓へ切り詰める。
    static let routingSourceTextMaxLength = 16 * SpokenLanguageDetector.recentEvidenceWindow

    weak var delegate: InterpretationSessionDelegate?

    private let apiKeyStore: any APIKeyStore
    private let audioCapture: any RealtimeAudioCaptureServicing
    private let dualClient: any DualRealtimeTranslationClienting
    private let aggregator: SubtitleAggregator
    private let activeTickerIntervalNanoseconds: UInt64
    private let postStopSubtitleRetentionNanoseconds: UInt64
    private let tuningProvider: @MainActor () -> RealtimeSessionTuning
    private let languagePairProvider: @MainActor () -> LanguagePair

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
    private var postStopClearTask: Task<Void, Never>?
    private var pendingUpdate: RealtimeSubtitleUpdate?
    private var lastRenderedAt = Date.distantPast
    private var lifecycleGeneration = 0
    private var assembler = RealtimeSubtitleAssembler()
    private var reconnectAttempt = 0
    private var routingSourceText = ""
    /// 現在の録音世代で使う言語ペア。Start 時に固定し、再接続でも settings の変更を取り込まない。
    private var sessionLanguagePair: LanguagePair?
    private var activeLanguagePair: LanguagePair?
    private var selectedTranslationTarget: RealtimeTranslationOutputLanguage?
    private var reverseEvidenceCount = 0

    /// テスト用。generation 確認後・assembler 更新前に差し込む。
    var beforeAssemblerIngestForTests: (() -> Void)?

    /// テスト用。ルーティング判定バッファの保持長 (UTF-16)。
    var routingSourceTextLengthForTests: Int {
        routingSourceText.utf16.count
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
        languagePairProvider: @escaping @MainActor () -> LanguagePair = { .jaEn }
    ) {
        self.apiKeyStore = apiKeyStore
        self.audioCapture = audioCapture
        self.dualClient = dualClient
        self.aggregator = aggregator
        self.activeTickerIntervalNanoseconds = activeTickerIntervalNanoseconds
        self.postStopSubtitleRetentionNanoseconds = postStopSubtitleRetentionNanoseconds
        self.tuningProvider = tuningProvider
        self.languagePairProvider = languagePairProvider
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
        reconnectAttempt = 0
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
        let tuning = tuningProvider().forPair(activeLanguagePair ?? .jaEn)
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
                    await tearDownStreaming()
                    flushPendingFinalizeIfNeeded()
                    enterError(error)
                    return
                }
            } catch {
                // 未知のアプリエラーは再接続せず即 error（予測可能性を優先）。
                guard generation == lifecycleGeneration else { return }
                await tearDownStreaming()
                flushPendingFinalizeIfNeeded()
                enterError(error)
                return
            }

            guard generation == lifecycleGeneration else { return }
            guard reconnectAttempt < Self.maxReconnectAttempts else {
                await tearDownStreaming()
                flushPendingFinalizeIfNeeded()
                enterErrorMessage(UiCopy.text("error.reconnectLimit"))
                return
            }

            reconnectAttempt += 1
            state = .reconnecting
            let micMessage = RealtimeAudioCaptureError.inputDeviceChanged.errorDescription
            if let reconnectDetail, let micMessage, reconnectDetail == micMessage {
                aggregator.setStatusBanner(reconnectingBanner(detail: reconnectDetail))
            } else {
                aggregator.setStatusBanner(reconnectingBanner(detail: nil))
            }
            publishSubtitles()
            await tearDownStreaming(keepSubtitles: true)

            let delay = Self.initialReconnectDelayNanoseconds
                << UInt64(min(reconnectAttempt - 1, 4))
            let jitter = UInt64.random(in: 0...250_000_000)
            try? await Task.sleep(nanoseconds: delay + jitter)
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

        let epoch = await dualClient.connectionEpoch
        // 再接続時 beginNewEpoch は buffer を捨てる。idle finalize 前の完全ペアを
        // 先に確定しないと、オプトイン字幕記録へ .finalized が届かない。
        flushPendingFinalizeIfNeeded()
        assembler.beginNewEpoch(epoch)
        routingSourceText = ""
        activeLanguagePair = pair
        selectedTranslationTarget = nil
        reverseEvidenceCount = 0
        assembler.setLanguagePair(pair)
        await dualClient.resetAudioRouting()

        try await audioCapture.start()
        guard generation == lifecycleGeneration else {
            await audioCapture.stop()
            await dualClient.forceClose()
            return
        }

        state = .listening
        reconnectAttempt = 0
        aggregator.setStatusBanner(UiCopy.text("banner.listening"))
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
            let value = await group.next() ?? .failure(CancellationError())
            first.cancel()
            second.cancel()
            group.cancelAll()
            while await group.next() != nil {}
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
        if let terminationError = audioCapture.terminationError {
            throw terminationError
        }
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
                if RealtimeTranslationError.isAuthenticationFailure(code: code, message: message) {
                    throw RealtimeTranslationError.authenticationFailed
                }
                // サーバー文言にキー断片が含まれる場合があるため、ユーザー向け文言はサニタイズする。
                throw RealtimeTranslationError.fatalServerError(
                    RealtimeTranslationError.sanitizedServerMessage(message)
                )
            }

            // 原文 routing は専用 transcription の source lane だけを使う。
            if case .inputTranscriptDelta(let delta, _, _) = streamEvent.event,
               streamEvent.lane.isSource {
                try await updateAudioRouting(withSourceDelta: delta)
            }

            beforeAssemblerIngestForTests?()
            if let update = assembler.ingest(streamEvent) {
                #if DEBUG
                AppLogger.session.notice(
                    "DBG_ASSEMBLER_UPDATE epoch=\(streamEvent.epoch, privacy: .public) generation=\(update.segmentGeneration, privacy: .public) sourceEmpty=\(update.sourceText.isEmpty, privacy: .public) translationEmpty=\(update.translatedText.isEmpty, privacy: .public)"
                )
                #endif
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
        aggregator.setStatusBanner(UiCopy.text("banner.closing"))
        publishSubtitles()

        let runningSessionTask = sessionTask
        runningSessionTask?.cancel()
        sessionTask = nil
        renderTask?.cancel()
        renderTask = nil
        let pending = pendingUpdate
        pendingUpdate = nil

        // 先に音声と session consumer を止め、close drain を破棄されないようにする。
        // generation を上げたまま consumer が生きていると、commit/session.close の
        // 最終 delta を読んで捨ててしまい、オプトイン字幕記録が欠ける。
        await audioCapture.stop()
        if let runningSessionTask {
            await runningSessionTask.value
        }

        // consumer 終了直後から drain を蓄え、translation pump drain / session.close の
        // 窓で届く最終 delta を AsyncStream の読み捨てにしない。
        await dualClient.beginStopDrainCapture()

        // スロットル中の旧 snapshot を先に適用し、その後の close drain で上書きする。
        if let pending {
            apply(pending)
        }

        let drainedEvents = await dualClient.closeGracefully()
        ingestStopDrainEvents(drainedEvents)
        if let tickUpdate = assembler.tick(now: Date()) {
            apply(tickUpdate)
        }

        let snapshot = aggregator.forceFinalize()
        delegate?.interpretationSession(self, didUpdateSubtitles: snapshot)
        aggregator.setStatusBanner(nil)
        sessionLanguagePair = nil
        activeLanguagePair = nil
        state = .idle
        publishSubtitles()
        stopTicker()
        schedulePostStopSubtitleClearIfNeeded()
    }

    /// 正常停止の close drain で届いた字幕イベントを assembler へ取り込む。
    private func ingestStopDrainEvents(_ events: [RealtimeTranslationStreamEvent]) {
        for streamEvent in events {
            if case .error = streamEvent.event {
                continue
            }
            if let update = assembler.ingest(streamEvent) {
                apply(update)
            }
        }
    }

    private func schedulePostStopSubtitleClearIfNeeded() {
        cancelPostStopSubtitleClear()
        guard !aggregator.snapshot().current.isEmpty else { return }
        let generation = lifecycleGeneration
        let retention = postStopSubtitleRetentionNanoseconds
        postStopClearTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: retention)
            guard let self, !Task.isCancelled else { return }
            guard self.lifecycleGeneration == generation else { return }
            guard self.state == .idle else { return }
            self.aggregator.reset()
            self.publishSubtitles()
            self.postStopClearTask = nil
        }
    }

    private func cancelPostStopSubtitleClear() {
        postStopClearTask?.cancel()
        postStopClearTask = nil
    }

    private func tearDownStreaming(keepSubtitles: Bool = false) async {
        await audioCapture.stop()
        await dualClient.forceClose()
        activeLanguagePair = nil
        stopTicker()
        if !keepSubtitles {
            renderTask?.cancel()
            renderTask = nil
            pendingUpdate = nil
        }
    }

    /// 完全な原文+訳文ペアが assembler / aggregator に残っていれば idle 待ちを飛ばして確定する。
    /// 停止・再接続・致命エラーで epoch/buffer を捨てる直前に呼び、字幕記録の欠落を防ぐ。
    private func flushPendingFinalizeIfNeeded() {
        renderTask?.cancel()
        renderTask = nil
        // スロットル中の live snapshot より assembler を正とする。
        pendingUpdate = nil

        let flushAt = Date().addingTimeInterval(RealtimeSubtitleAssembler.idleFinalizeInterval)
        if let update = assembler.tick(now: flushAt) {
            apply(update)
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
        guard let pair = activeLanguagePair else { return }
        routingSourceText = Self.trimRoutingSourceText(routingSourceText + delta, pair: pair)
        let evidence = SpokenLanguageDetector.recentEvidence(
            in: routingSourceText,
            pair: pair
        )
        let selection = TranslationTargetSelector.select(
            pair: pair,
            currentTarget: selectedTranslationTarget,
            reverseEvidenceCount: reverseEvidenceCount,
            evidence: evidence
        )
        reverseEvidenceCount = selection.reverseEvidenceCount
        guard selection.target != selectedTranslationTarget else { return }

        if selectedTranslationTarget != nil {
            if let finalized = assembler.finalizeForLanguageSwitch() {
                enqueueRender(finalized)
            }
            await resetAudioRoutingForNextSegment()
            routingSourceText = Self.trimRoutingSourceText(delta, pair: pair)
        }
        selectedTranslationTarget = selection.target
        assembler.expectLane(selection.target)
        try await dualClient.selectTranslationTarget(selection.target)
    }

    /// `SpokenLanguageDetector.recentEvidence` と同じ判定窓を残す。
    /// `en-es` は語窓、それ以外は末尾非空白 scalar 窓。空白 run が異常に長い場合だけ圧縮する。
    static func trimRoutingSourceText(_ text: String, pair: LanguagePair) -> String {
        guard !text.isEmpty else { return text }
        if pair == .enEs {
            let start = SpokenLanguageDetector.recentWordWindowStart(in: text)
            return String(text.unicodeScalars[start...])
        }
        let window = recentEvidenceWindowSubstring(
            text,
            window: SpokenLanguageDetector.recentEvidenceWindow
        )
        if window.utf16.count <= routingSourceTextMaxLength {
            return window
        }
        return collapseWhitespaceRuns(window)
    }

    /// 末尾から空白以外の Unicode scalar を `window` 個含む範囲の部分文字列。
    private static func recentEvidenceWindowSubstring(_ text: String, window: Int) -> String {
        guard window > 0, !text.isEmpty else { return text }
        let scalars = text.unicodeScalars
        var nonWhitespaceCount = 0
        var start = scalars.endIndex
        while start > scalars.startIndex, nonWhitespaceCount < window {
            start = scalars.index(before: start)
            if !CharacterSet.whitespacesAndNewlines.contains(scalars[start]) {
                nonWhitespaceCount += 1
            }
        }
        guard nonWhitespaceCount > 0 else { return "" }
        return String(scalars[start...])
    }

    /// 連続する空白 scalar を U+0020 1 個へ潰す。ラテン語境界を残しつつ保持長を抑える。
    private static func collapseWhitespaceRuns(_ text: String) -> String {
        var collapsed = ""
        collapsed.reserveCapacity(min(text.utf16.count, routingSourceTextMaxLength))
        var previousWasWhitespace = false
        for scalar in text.unicodeScalars {
            if CharacterSet.whitespacesAndNewlines.contains(scalar) {
                if previousWasWhitespace {
                    continue
                }
                previousWasWhitespace = true
                collapsed.unicodeScalars.append(" ")
                continue
            }
            previousWasWhitespace = false
            collapsed.unicodeScalars.append(scalar)
        }
        return collapsed
    }

    private func resetAudioRoutingForNextSegment() async {
        routingSourceText = ""
        selectedTranslationTarget = nil
        reverseEvidenceCount = 0
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

    private func reconnectingBanner(detail: String?) -> String {
        let substitutions = [
            "detail": detail ?? "",
            "attempt": String(reconnectAttempt),
            "max": String(Self.maxReconnectAttempts),
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
