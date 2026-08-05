import os
import XCTest
@testable import RealtimeTranslator

@MainActor
final class InterpretationSessionTests: XCTestCase {
    func testStartDoesNotCaptureBeforeBothSessionsReady() async {
        // Given: Dual clientのstartが完了するまで待機できるfake
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startGate = CheckedContinuationBox()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )

        // When: startを呼び、Dual start完了前を観測する
        let startTask = Task { await session.start() }
        await waitUntil { dual.startCallCount == 1 }
        XCTAssertEqual(audio.startCallCount, 0)

        // Then: Dual start解放後にcaptureが始まる
        dual.startGate?.resume()
        dual.startGate = nil
        await waitUntil { audio.startCallCount == 1 }
        XCTAssertEqual(session.state, .listening)
        await session.stop()
        startTask.cancel()
    }

    func testStopDuringStartDoesNotLeaveListening() async {
        // Given: 接続中に止められるセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startGate = CheckedContinuationBox()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )

        // When: start直後にstopする（stopはsessionTask排水を待つのでgateを先に解放）
        let startTask = Task { await session.start() }
        await waitUntil { dual.startCallCount == 1 }
        let stopTask = Task { await session.stop() }
        await waitUntil { session.state == .closing || session.state == .idle }
        dual.startGate?.resume()
        dual.startGate = nil
        await stopTask.value
        await startTask.value

        // Then: idleに戻りcaptureは開始されないか、開始後でも停止済み
        XCTAssertEqual(session.state, .idle)
        XCTAssertFalse(audio.isRunning)
    }

    func testStopDrainsSessionTaskBeforeReturningSoRestartIsStable() async {
        // Given: dual.start待ちで止まっているセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let gate = CheckedContinuationBox()
        dual.startGate = gate
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        let firstStart = Task { await session.start() }
        await waitUntil { dual.startCallCount == 1 }

        // When: stop中に旧startを進め、排水後に再startする
        let stopTask = Task { await session.stop() }
        await waitUntil { session.state == .closing || session.state == .idle }
        dual.startGate = nil
        gate.resume()
        await stopTask.value
        await firstStart.value
        XCTAssertEqual(session.state, .idle)

        let forceCloseAfterStop = dual.forceCloseCallCount
        await session.start()
        await waitUntil { session.state == .listening }

        // Then: 旧sessionTaskの世代不一致forceCloseが新セッションへ飛ばない
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(session.state, .listening)
        XCTAssertEqual(dual.forceCloseCallCount, forceCloseAfterStop)
    }

    func testDoubleStopIsIdempotent() async {
        // Given: 録音中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )
        await session.start()
        await waitUntil { session.state == .listening }

        // When: stopを二重に呼ぶ
        async let first: Void = session.stop()
        async let second: Void = session.stop()
        _ = await (first, second)

        // Then: 最終状態はidleで、graceful closeは1回
        XCTAssertEqual(session.state, .idle)
        XCTAssertEqual(dual.closeGracefullyCallCount, 1)
    }

    func testMissingAPIKeyEntersErrorWithoutConnecting() async {
        // Given: APIキー未設定
        let apiKeyStore = InMemoryAPIKeyStore()
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )
        session.delegate = delegate

        // When: startする
        await session.start()
        await waitUntil { session.state == .error }

        // Then: 接続せずエラーになり、秘密情報はdelegateへ出ない
        XCTAssertEqual(dual.startCallCount, 0)
        XCTAssertEqual(audio.startCallCount, 0)
        XCTAssertFalse(delegate.messages.contains(where: { $0.contains("sk-") }))
        XCTAssertEqual(delegate.messages.first, "APIキーが設定されていません")
    }

    func testRecoverableFailureTriggersReconnectThenListening() async {
        // Given: 1回目のstartだけ失敗するdual
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        dual.startFailuresRemaining = 1
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )

        // When: startする
        await session.start()

        // Then: 再接続してlisteningへ戻る
        await waitUntil(timeout: 3) { session.state == .listening && dual.startCallCount >= 2 }
        XCTAssertGreaterThanOrEqual(dual.startCallCount, 2)
        await session.stop()
    }

    func testRuntimeTransportErrorCancelsAudioFeedAndReconnects() async {
        // Given: listening中で、audio feedは次frame待ちのまま
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 20_000_000
        )
        await session.start()
        await waitUntil { session.state == .listening }
        let startCountAtListening = dual.startCallCount
        let forceCloseAtListening = dual.forceCloseCallCount

        // When: ストリーミング中にtransport errorが届く
        dual.emit(
            target: .english,
            event: .error(message: "socket closed", code: "transport")
        )

        // Then: feed側のframe待ちでraceが固まらず、再接続してlisteningへ戻る
        await waitUntil(timeout: 3) {
            dual.forceCloseCallCount > forceCloseAtListening
        }
        await waitUntil(timeout: 3) {
            session.state == .listening && dual.startCallCount > startCountAtListening
        }
        XCTAssertGreaterThan(dual.startCallCount, startCountAtListening)
        await session.stop()
    }

    func testStopDrainsPendingSubtitleUpdate() async throws {
        // Given: 原文と訳文が揃ったlisteningセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: deltaを流してからstopする
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("こんにちは") == true
        }
        await session.stop()

        // Then: 停止直後は完全ペアがcurrentに残り、すぐには消えない
        let finalSnapshot = try XCTUnwrap(delegate.latestSnapshot)
        XCTAssertFalse(finalSnapshot.current.sourceText.isEmpty)
        XCTAssertFalse(finalSnapshot.current.translatedText.isEmpty)
        XCTAssertEqual(session.state, .idle)
    }

    func testPostStopSubtitleClearsAfterRetention() async throws {
        // Given: 短い保持時間と、原文・訳文が揃ったlisteningセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            postStopSubtitleRetentionNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("こんにちは") == true
        }

        // When: 録音を止め、保持時間を超えるまで待つ
        await session.stop()
        XCTAssertEqual(session.state, .idle)
        XCTAssertFalse(delegate.latestSnapshot?.current.isEmpty == true)
        await waitUntil(timeout: 2) {
            delegate.latestSnapshot?.current.isEmpty == true
        }

        // Then: 一定時間後に字幕ブロックが空になる
        let cleared = try XCTUnwrap(delegate.latestSnapshot)
        XCTAssertTrue(cleared.current.isEmpty)
        XCTAssertNil(cleared.statusBanner)
    }

    func testRestartCancelsPendingPostStopSubtitleClear() async throws {
        // Given: 停止直後に字幕が残っているセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            postStopSubtitleRetentionNanoseconds: 120_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "残す文", eventID: "s1", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Keep this", eventID: "t1", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("残す文") == true
        }
        await session.stop()
        XCTAssertFalse(delegate.latestSnapshot?.current.isEmpty == true)

        // When: 保持時間前に再録音し、新しい字幕を出す
        await session.start()
        await waitUntil { session.state == .listening }
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "新しい文", eventID: "s2", elapsedMs: 10)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "New sentence", eventID: "t2", elapsedMs: 20)
        )
        await waitUntil {
            delegate.latestSnapshot?.current.sourceText.contains("新しい文") == true
        }
        try? await Task.sleep(nanoseconds: 200_000_000)

        // Then: 前回停止の遅延消去が新セッションの字幕を消さない
        XCTAssertEqual(session.state, .listening)
        XCTAssertTrue(delegate.latestSnapshot?.current.sourceText.contains("新しい文") == true)
        await session.stop()
    }

    func testLanguageFlipFinalizesAndReroutes() async throws {
        // Given: 日本語でルーティング済みのlisteningセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(delta: "今日は会議です", eventID: "s1", elapsedMs: 1)
        )
        dual.emit(
            target: .english,
            event: .outputTranscriptDelta(delta: "Today is a meeting", eventID: "t1", elapsedMs: 2)
        )
        await waitUntil { dual.spokenLanguages == [.japanese] }
        await waitUntil {
            delegate.latestSnapshot?.current.translatedText.contains("Today") == true
        }
        let resetsAfterJapanese = dual.resetAudioRoutingCallCount

        // When: 間を空けず英語原文が続く
        dual.emit(
            target: .english,
            event: .inputTranscriptDelta(
                delta: " Hello how are you today",
                eventID: "s2",
                elapsedMs: 3
            )
        )

        // Then: 言語切替で再ルーティングし、前セグメントが確定する
        await waitUntil { dual.spokenLanguages == [.japanese, .english] }
        XCTAssertGreaterThan(dual.resetAudioRoutingCallCount, resetsAfterJapanese)
        await waitUntil {
            delegate.latestSnapshot?.current.state == .finalized
                || delegate.latestSnapshot?.current.sourceText.contains("Hello") == true
        }
        await session.stop()
    }

    func testApplyTuningChangeForwardsWhileListening() async throws {
        // Given: listening中のセッションとカスタムtuningProvider
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        var currentTuning = RealtimeSessionTuning.default
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000,
            tuningProvider: { currentTuning }
        )
        await session.start()
        await waitUntil { session.state == .listening }
        XCTAssertEqual(dual.updateTranscriptionTuningCallCount, 0)

        // When: tuningを変えてapplyTuningChangeする
        currentTuning = RealtimeSessionTuning(
            noiseReduction: .nearField,
            transcriptionDelay: .high,
            transcriptionPrompt: "Updated glossary",
            transcriptionKeywords: ["Acme"]
        )
        await session.applyTuningChange()

        // Then: dualへ最新tuningが転送される
        XCTAssertEqual(dual.updateTranscriptionTuningCallCount, 1)
        XCTAssertEqual(dual.lastTuning?.transcriptionPrompt, "Updated glossary")
        XCTAssertEqual(dual.lastTuning?.transcriptionKeywords, ["Acme"])
        await session.stop()
    }

    func testApplyTuningChangeIsNoOpWhenIdle() async throws {
        // Given: idleのセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual
        )

        // When: idleでapplyTuningChangeする
        await session.applyTuningChange()

        // Then: 転送されない
        XCTAssertEqual(dual.updateTranscriptionTuningCallCount, 0)
    }

    func testInvalidAPIKeyRuntimeErrorDoesNotLeakKeyMaterial() async throws {
        // Given: listening中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: キー断片を含むinvalid_api_keyエラーが届く
        dual.emit(
            target: .english,
            event: .error(
                message: "Incorrect API key provided: sk-leak-example",
                code: "invalid_api_key"
            )
        )
        await waitUntil { session.state == .error }

        // Then: 認証エラーになり、sk-や原文メッセージはdelegateへ出ない
        XCTAssertEqual(delegate.messages.first, "OpenAI APIキーが無効です")
        XCTAssertFalse(delegate.messages.contains(where: { $0.contains("sk-") }))
        XCTAssertFalse(
            delegate.latestSnapshot?.statusBanner?.contains("sk-") == true
        )
        await session.stop()
    }

    func testNonAuthServerErrorRedactsAPIKeyLikePayload() async throws {
        // Given: listening中のセッション
        let apiKeyStore = InMemoryAPIKeyStore(initialKey: "sk-test")
        let audio = FakeRealtimeAudioCaptureService()
        let dual = FakeDualRealtimeTranslationClient()
        let delegate = InterpretationSessionDelegateSpy()
        let session = InterpretationSession(
            apiKeyStore: apiKeyStore,
            audioCapture: audio,
            dualClient: dual,
            activeTickerIntervalNanoseconds: 50_000_000
        )
        session.delegate = delegate
        await session.start()
        await waitUntil { session.state == .listening }

        // When: codeは非認証だが文言にAPIキー断片が含まれる
        dual.emit(
            target: .english,
            event: .error(
                message: "Provider echo included sk-should-not-appear",
                code: "server_error"
            )
        )
        await waitUntil { session.state == .error }

        // Then: 汎用エラー文言に置換され秘密情報は出ない
        XCTAssertEqual(delegate.messages.first, "翻訳サーバーでエラーが発生しました")
        XCTAssertFalse(delegate.messages.contains(where: { $0.contains("sk-") }))
        await session.stop()
    }
}

// MARK: - Fakes

@MainActor
final class FakeRealtimeAudioCaptureService: RealtimeAudioCaptureServicing {
    private(set) var frames: AsyncStream<Data>
    private var continuation: AsyncStream<Data>.Continuation?
    private(set) var startCallCount = 0
    private(set) var stopCallCount = 0
    private(set) var isRunning = false
    var startError: Error?

    init() {
        var continuation: AsyncStream<Data>.Continuation!
        frames = AsyncStream { continuation = $0 }
        self.continuation = continuation
    }

    func start() async throws {
        startCallCount += 1
        if let startError {
            throw startError
        }
        continuation?.finish()
        var next: AsyncStream<Data>.Continuation!
        frames = AsyncStream { next = $0 }
        continuation = next
        isRunning = true
    }

    func stop() async {
        stopCallCount += 1
        isRunning = false
        continuation?.finish()
        continuation = nil
    }

    func emit(_ frame: Data) {
        _ = continuation?.yield(frame)
    }
}

final class FakeDualRealtimeTranslationClient: DualRealtimeTranslationClienting, @unchecked Sendable {
    private let state = OSAllocatedUnfairLock(initialState: ClientState())

    private struct ClientState {
        var eventStream: AsyncStream<RealtimeTranslationStreamEvent>
        var eventContinuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation?
        var connectionEpoch = 0
        var appendedFrames: [Data] = []

        init() {
            var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
            eventStream = AsyncStream { continuation = $0 }
            eventContinuation = continuation
        }
    }

    private(set) var startCallCount = 0
    private(set) var closeGracefullyCallCount = 0
    private(set) var forceCloseCallCount = 0
    private(set) var spokenLanguages: [SpokenLanguage] = []
    private(set) var resetAudioRoutingCallCount = 0
    private(set) var updateTranscriptionTuningCallCount = 0
    private(set) var lastTuning: RealtimeSessionTuning?
    var startGate: CheckedContinuationBox?
    var startFailuresRemaining = 0
    var startError: Error?

    var connectionEpoch: Int {
        get async {
            state.withLock(\.connectionEpoch)
        }
    }

    var events: AsyncStream<RealtimeTranslationStreamEvent> {
        get async {
            state.withLock(\.eventStream)
        }
    }

    func start(apiKey: String, tuning: RealtimeSessionTuning) async throws {
        startCallCount += 1
        lastTuning = tuning
        if let startGate {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation {
                    (continuation: CheckedContinuation<Void, Error>) in
                    if Task.isCancelled {
                        continuation.resume(throwing: CancellationError())
                        return
                    }
                    startGate.throwingContinuation = continuation
                }
            } onCancel: {
                startGate.resumeThrowing(CancellationError())
            }
        }
        try Task.checkCancellation()
        if let startError {
            throw startError
        }
        if startFailuresRemaining > 0 {
            startFailuresRemaining -= 1
            throw RealtimeTranslationError.recoverableTransportFailure("forced start failure")
        }

        state.withLock { state in
            state.connectionEpoch += 1
            state.eventContinuation?.finish()
            var continuation: AsyncStream<RealtimeTranslationStreamEvent>.Continuation!
            state.eventStream = AsyncStream { continuation = $0 }
            state.eventContinuation = continuation
        }
    }

    func appendAudioFrame(_ pcm16LE: Data) async throws {
        state.withLock { state in
            state.appendedFrames.append(pcm16LE)
        }
    }

    func setSpokenLanguage(_ language: SpokenLanguage) async throws {
        spokenLanguages.append(language)
    }

    func updateTranscriptionTuning(_ tuning: RealtimeSessionTuning) async throws {
        updateTranscriptionTuningCallCount += 1
        lastTuning = tuning
    }

    func resetAudioRouting() async {
        resetAudioRoutingCallCount += 1
    }

    func closeGracefully() async throws {
        closeGracefullyCallCount += 1
        finishEvents()
    }

    func forceClose() async {
        forceCloseCallCount += 1
        state.withLock { state in
            state.connectionEpoch += 1
        }
        finishEvents()
    }

    func emit(
        target: RealtimeTranslationOutputLanguage,
        event: RealtimeTranslationServerEvent,
        epoch: Int? = nil
    ) {
        let payload = state.withLock { state -> (AsyncStream<RealtimeTranslationStreamEvent>.Continuation?, Int) in
            (state.eventContinuation, epoch ?? state.connectionEpoch)
        }
        payload.0?.yield(
            RealtimeTranslationStreamEvent(
                target: target,
                event: event,
                epoch: payload.1
            )
        )
    }

    private func finishEvents() {
        state.withLock { state in
            state.eventContinuation?.finish()
            state.eventContinuation = nil
        }
    }
}

final class CheckedContinuationBox: @unchecked Sendable {
    var continuation: CheckedContinuation<Void, Never>?
    var throwingContinuation: CheckedContinuation<Void, Error>?

    func resume() {
        if let throwingContinuation {
            self.throwingContinuation = nil
            throwingContinuation.resume()
            return
        }
        continuation?.resume()
        continuation = nil
    }

    func resumeThrowing(_ error: Error) {
        if let throwingContinuation {
            self.throwingContinuation = nil
            throwingContinuation.resume(throwing: error)
            return
        }
        continuation?.resume()
        continuation = nil
    }
}

@MainActor
final class InterpretationSessionDelegateSpy: InterpretationSessionDelegate {
    private(set) var states: [TranslationState] = []
    private(set) var messages: [String] = []
    private(set) var latestSnapshot: SubtitleSnapshot?

    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateState state: TranslationState
    ) {
        states.append(state)
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateSubtitles snapshot: SubtitleSnapshot
    ) {
        latestSnapshot = snapshot
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didEncounterMessage message: String
    ) {
        messages.append(message)
    }
}

@MainActor
func waitUntil(
    timeout: TimeInterval = 1.5,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ condition: @escaping () -> Bool
) async {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if condition() { return }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    XCTFail("Condition not met before timeout", file: file, line: line)
}
