import XCTest
@testable import RealtimeTranslator

actor FakeRealtimeWebSocketTransport: RealtimeWebSocketTransport {
    private var inbound: [Data] = []
    private var waiters: [CheckedContinuation<Data, Error>] = []
    private(set) var sent: [Data] = []
    private(set) var connectCount = 0
    private(set) var lastHeaders: [String: String] = [:]
    private(set) var closeCount = 0
    private(set) var sendAttemptCount = 0
    var connectError: Error?
    var sendError: Error?
    private var failNextSend = false
    private var heldAudioAppends: [CheckedContinuation<Void, Error>] = []
    /// セットするとsendがこの時間だけ待機してから通常処理へ進む。
    var sendHangNanoseconds: UInt64 = 0
    /// audio append だけ遅延させ、session.close / commit の停止経路を巻き込まない。
    var audioAppendHangNanoseconds: UInt64 = 0
    var holdAudioAppends = false

    func setHoldAudioAppends(_ value: Bool) {
        holdAudioAppends = value
    }
    /// graceful close 用の完了イベントを自動応答する。
    var autoCloseResponses = false

    var heldAudioAppendCount: Int {
        heldAudioAppends.count
    }

    @discardableResult
    func releaseOneAudioAppend() -> Bool {
        guard !heldAudioAppends.isEmpty else { return false }
        heldAudioAppends.removeFirst().resume()
        return true
    }

    @discardableResult
    func failOneHeldAudioAppend() -> Bool {
        guard !heldAudioAppends.isEmpty else { return false }
        heldAudioAppends.removeFirst().resume(
            throwing: RealtimeTranslationError.recoverableTransportFailure(
                "injected send failure"
            )
        )
        return true
    }

    func releaseAllAudioAppends() {
        while releaseOneAudioAppend() {}
    }

    func enqueueInbound(_ data: Data) {
        if let waiter = waiters.first {
            waiters.removeFirst()
            waiter.resume(returning: data)
        } else {
            inbound.append(data)
        }
    }

    func enqueueJSON(_ object: [String: Any]) throws {
        let data = try JSONSerialization.data(withJSONObject: object)
        enqueueInbound(data)
    }

    func connect(url: URL, headers: [String: String]) async throws {
        connectCount += 1
        lastHeaders = headers
        if let connectError {
            throw connectError
        }
        _ = url
    }

    func failNextSendOnce() {
        failNextSend = true
    }

    func send(_ data: Data) async throws {
        sendAttemptCount += 1
        let type = Self.messageType(of: data)
        let isAudioAppend = type == "session.input_audio_buffer.append"
            || type == "input_audio_buffer.append"
        if isAudioAppend, holdAudioAppends {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { continuation in
                    if Task.isCancelled {
                        continuation.resume(throwing: CancellationError())
                    } else {
                        heldAudioAppends.append(continuation)
                    }
                }
            } onCancel: {
                Task { await self.cancelHeldAudioAppends() }
            }
        }
        if isAudioAppend, audioAppendHangNanoseconds > 0 {
            try await Task.sleep(nanoseconds: audioAppendHangNanoseconds)
        } else if sendHangNanoseconds > 0 {
            try await Task.sleep(nanoseconds: sendHangNanoseconds)
        }
        if failNextSend {
            failNextSend = false
            throw RealtimeTranslationError.recoverableTransportFailure("one-shot send failure")
        }
        if let sendError {
            throw sendError
        }
        sent.append(data)
        if autoCloseResponses {
            if type == "session.close" {
                try enqueueJSON(["type": "session.closed"])
            } else if type == "input_audio_buffer.commit" {
                try enqueueJSON([
                    "type": "conversation.item.input_audio_transcription.completed",
                ])
            }
        }
    }

    private func cancelHeldAudioAppends() {
        let pending = heldAudioAppends
        heldAudioAppends.removeAll()
        for continuation in pending {
            continuation.resume(throwing: CancellationError())
        }
    }

    private static func messageType(of data: Data) -> String? {
        guard
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            return nil
        }
        return object["type"] as? String
    }

    func receive() async throws -> Data {
        if !inbound.isEmpty {
            return inbound.removeFirst()
        }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                waiters.append(continuation)
            }
        } onCancel: {
            Task { await self.cancelWaiters() }
        }
    }

    func close() async {
        closeCount += 1
        await cancelWaiters()
    }

    private func cancelWaiters() {
        let pending = waiters
        waiters.removeAll()
        for waiter in pending {
            waiter.resume(throwing: CancellationError())
        }
    }
}

/// `URLSessionWebSocketTask` と同様、Swift Task キャンセルでは解けず `close()` だけが待ちを解く。
actor HangUntilCloseWebSocketTransport: RealtimeWebSocketTransport {
    private var receiveWaiters: [CheckedContinuation<Data, Error>] = []
    private var sendWaiters: [CheckedContinuation<Void, Error>] = []
    private var isClosed = false
    private(set) var closeCount = 0

    func connect(url: URL, headers: [String: String]) async throws {
        _ = url
        _ = headers
        isClosed = false
    }

    func send(_ data: Data) async throws {
        _ = data
        if isClosed {
            throw URLSessionWebSocketTransportError.notConnected
        }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            sendWaiters.append(continuation)
        }
    }

    func receive() async throws -> Data {
        if isClosed {
            throw URLSessionWebSocketTransportError.notConnected
        }
        return try await withCheckedThrowingContinuation { continuation in
            receiveWaiters.append(continuation)
        }
    }

    func close() async {
        closeCount += 1
        isClosed = true
        let receive = receiveWaiters
        receiveWaiters.removeAll()
        let send = sendWaiters
        sendWaiters.removeAll()
        for waiter in receive {
            waiter.resume(throwing: CancellationError())
        }
        for waiter in send {
            waiter.resume(throwing: CancellationError())
        }
    }
}

/// AsyncTimeout.onTimeout から continuation を再開する。
private final class TimeoutResumeBox: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?
    private var resumePending = false

    func store(_ continuation: CheckedContinuation<Void, Error>) {
        lock.lock()
        if resumePending {
            resumePending = false
            lock.unlock()
            continuation.resume(returning: ())
            return
        }
        self.continuation = continuation
        lock.unlock()
    }

    func resume() {
        lock.lock()
        if let pending = continuation {
            continuation = nil
            lock.unlock()
            pending.resume(returning: ())
            return
        }
        resumePending = true
        lock.unlock()
    }
}

final class RealtimeTranslationConnectionTests: XCTestCase {
    func testHandshakeCreatedUpdateUpdated() async throws {
        // Given: created -> updated を返すtransport
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000,
            closeTimeoutNanoseconds: 500_000_000
        )
        try await transport.enqueueJSON(["type": "session.created"])
        // updatedはsession.update送信後に必要。並行で待つ。
        let startTask = Task {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithSourceTranscription()
            )
        }
        // When: update送信を待ってupdatedを返す
        try await waitForSent(transport)
        try await transport.enqueueJSON(["type": "session.updated"])
        try await startTask.value

        // Then: connect済みでupdateが送られている
        let connectCount = await transport.connectCount
        let sent = await transport.sent
        XCTAssertEqual(connectCount, 1)
        XCTAssertFalse(sent.isEmpty)
        let encoded = try XCTUnwrap(sent.first)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertEqual(object["type"] as? String, "session.update")
    }

    func testAuthenticationFailureIsFatal() async {
        // Given: error(invalid_api_key)
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )
        try? await transport.enqueueJSON([
            "type": "error",
            "error": [
                "message": "invalid",
                "code": "invalid_api_key",
            ],
        ])

        // When/Then
        do {
            try await connection.start(
                apiKey: "sk-bad",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected authenticationFailed")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(error, .authenticationFailed)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    func testAuthorizationThemedHandshakeErrorIsAuthenticationFailure() async {
        // Given: Authorization 文言を含むhandshake error（codeは非auth）
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )
        try? await transport.enqueueJSON([
            "type": "error",
            "error": [
                "message": "Invalid Authorization header: Bearer sk-leak-example",
                "code": "invalid_request_error",
            ],
        ])

        // When/Then: 認証失敗として扱い、localizedDescriptionにもsk-を出さない
        do {
            try await connection.start(
                apiKey: "sk-bad",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected authenticationFailed")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(error, .authenticationFailed)
            XCTAssertEqual(error.localizedDescription, "OpenAI APIキーが無効です")
            XCTAssertFalse(error.localizedDescription.contains("sk-"))
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    func testMissingBearerHandshakeErrorIsAuthenticationFailure() async {
        // Given: OpenAI の missing bearer 文言（code は非 auth）
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )
        try? await transport.enqueueJSON([
            "type": "error",
            "error": [
                "message": "Missing bearer or basic authentication in header",
                "code": "invalid_request_error",
            ],
        ])

        // When/Then: 認証失敗として扱い、bearer を localizedDescription に出さない
        do {
            try await connection.start(
                apiKey: "sk-bad",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected authenticationFailed")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(error, .authenticationFailed)
            XCTAssertEqual(error.localizedDescription, "OpenAI APIキーが無効です")
            XCTAssertFalse(error.localizedDescription.localizedCaseInsensitiveContains("bearer"))
            XCTAssertFalse(error.localizedDescription.contains("sk-"))
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    func testStartRejectsMalformedApiKeyBeforeConnect() async {
        // Given: 埋め込み改行と時刻が混ざったキー
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )

        // When/Then: 接続前に認証失敗へ倒し、ヘッダを送らない
        do {
            try await connection.start(
                apiKey: "sk-proj-abc\n3:26",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected authenticationFailed")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(error, .authenticationFailed)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
        let connectCount = await transport.connectCount
        XCTAssertEqual(connectCount, 0)
    }

    func testStartStripsEmbeddedWhitespaceFromApiKeyHeader() async throws {
        // Given: 行折り返しされた allowlist キー
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )
        try await transport.enqueueJSON(["type": "session.created"])
        try await transport.enqueueJSON(["type": "session.updated"])

        // When: 埋め込み改行付きキーで開始する
        try await connection.start(
            apiKey: "sk-proj-AAAA\nBBBB",
            config: .englishTargetWithSourceTranscription()
        )

        // Then: Authorization は正規化後のキーだけを載せる
        let headers = await transport.lastHeaders
        XCTAssertEqual(headers["Authorization"], "Bearer sk-proj-AAAABBBB")
        await connection.forceClose()
    }

    func testHandshakeFatalServerErrorRedactsKeyMaterial() async {
        // Given: 非認証だがキー断片を含むhandshake error
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )
        try? await transport.enqueueJSON([
            "type": "error",
            "error": [
                "message": "upstream echo sk-should-not-appear",
                "code": "server_error",
            ],
        ])

        // When/Then: fatalServerErrorでもユーザー向け文言から秘密情報を除去する
        do {
            try await connection.start(
                apiKey: "sk-bad",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected fatalServerError")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(
                error,
                .fatalServerError("翻訳サーバーでエラーが発生しました")
            )
            XCTAssertFalse(error.localizedDescription.contains("sk-"))
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    func testCloseWaitsForSessionClosed() async throws {
        // Given: handshake済み接続
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .japanese,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000,
            closeTimeoutNanoseconds: 1_000_000_000
        )
        try await transport.enqueueJSON(["type": "session.created"])
        let startTask = Task {
            try await connection.start(
                apiKey: "sk-test",
                config: .japaneseTargetWithoutSourceTranscription()
            )
        }
        try await waitForSent(transport)
        try await transport.enqueueJSON(["type": "session.updated"])
        try await startTask.value

        // When: close中にsession.closedを返す
        let closeTask = Task {
            try await connection.closeGracefully()
        }
        try await waitForSentCount(transport, minimum: 2)
        try await transport.enqueueJSON(["type": "session.closed"])
        try await closeTask.value

        // Then: closeが完了しtransportも閉じる（handshake失敗時tearDownと二重でも可）
        let closeCount = await transport.closeCount
        XCTAssertGreaterThanOrEqual(closeCount, 1)
    }

    func testCloseBeforeReadyForceClosesWithoutWaitingForSessionClosed() async throws {
        // Given: handshake前（isReady=false）の翻訳接続。closeTimeoutは長く、誤って待つとテストが固まる。
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            closeTimeoutNanoseconds: 2_000_000_000
        )

        // When: ready前にgraceful closeする
        let started = ContinuousClock.now
        try await connection.closeGracefully()
        let elapsed = ContinuousClock.now - started

        // Then: session.closed待ちへ入らず即完了し、transportを閉じる
        XCTAssertLessThan(elapsed, .milliseconds(500))
        let closeCount = await transport.closeCount
        XCTAssertEqual(closeCount, 1)
        let sentCount = await transport.sent.count
        XCTAssertEqual(sentCount, 0)
    }

    func testStaleCloseDoesNotTearDownSocketAfterRestart() async throws {
        // Given: handshake済みの翻訳接続。session.closed は返さず close 待ちに入る。
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000,
            closeTimeoutNanoseconds: 800_000_000
        )
        try await transport.enqueueJSON(["type": "session.created"])
        let startTask = Task {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithoutSourceTranscription()
            )
        }
        try await waitForSent(transport)
        try await transport.enqueueJSON(["type": "session.updated"])
        try await startTask.value

        // When: graceful close の待ち中に forceClose し、同じ接続で次の start を完了する
        let closeTask = Task {
            try await connection.closeGracefully()
        }
        try await waitForSentCount(transport, minimum: 2)
        await connection.forceClose()
        try await transport.enqueueJSON(["type": "session.created"])
        let restartTask = Task {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithoutSourceTranscription()
            )
        }
        try await waitForSentCount(transport, minimum: 3)
        try await transport.enqueueJSON(["type": "session.updated"])
        try await restartTask.value
        let closeCountAfterRestart = await transport.closeCount
        let connectCountAfterRestart = await transport.connectCount

        _ = await closeTask.result

        // Then: 古い close 待ちは新しいソケットを閉じない
        let closeCountAfterStaleClose = await transport.closeCount
        let connectCountAfterStaleClose = await transport.connectCount
        XCTAssertEqual(closeCountAfterStaleClose, closeCountAfterRestart)
        XCTAssertEqual(connectCountAfterStaleClose, connectCountAfterRestart)
        XCTAssertEqual(connectCountAfterRestart, 2)
    }

    func testOutputAudioDeltaIsNotForwardedToEventStream() async throws {
        // Given: ready な翻訳接続。Stop 後の購読停止中に音声 delta が洪水しても字幕を落とさない。
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety"
        )
        try await transport.enqueueJSON(["type": "session.created"])
        try await transport.enqueueJSON(["type": "session.updated"])
        try await connection.start(
            apiKey: "sk-test",
            config: .englishTargetWithoutSourceTranscription()
        )

        let stream = await connection.events
        // When: output_audio.delta を大量に流したあと訳文 delta を送る
        for _ in 0..<300 {
            try await transport.enqueueJSON([
                "type": "session.output_audio.delta",
                "delta": "AAAA",
            ])
        }
        try await transport.enqueueJSON([
            "type": "session.output_transcript.delta",
            "delta": "kept after audio flood",
            "event_id": "keep-1",
        ])

        // Then: 音声は event stream に出ず、訳文だけが届く
        var sawAudio = false
        var keptTranslation: String?
        let deadline = ContinuousClock.now + .seconds(3)
        for await event in stream {
            if case .outputAudioDelta = event.event {
                sawAudio = true
            }
            if case .outputTranscriptDelta(let delta, _, _) = event.event {
                keptTranslation = delta
                break
            }
            if ContinuousClock.now >= deadline {
                break
            }
        }
        XCTAssertFalse(sawAudio)
        XCTAssertEqual(keptTranslation, "kept after audio flood")
        await connection.forceClose()
    }

    func testInputTranscriptDeltaIsNotForwardedToEventStream() async throws {
        // Given: ready な翻訳接続。原文 authority は専用 transcription のみ。
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety"
        )
        try await transport.enqueueJSON(["type": "session.created"])
        try await transport.enqueueJSON(["type": "session.updated"])
        try await connection.start(
            apiKey: "sk-test",
            config: .englishTargetWithoutSourceTranscription()
        )

        let stream = await connection.events
        // When: 翻訳側 input_transcript のあとに訳文 delta を送る
        try await transport.enqueueJSON([
            "type": "session.input_transcript.delta",
            "delta": "polluting source",
            "event_id": "in-1",
            "elapsed_ms": 10,
        ])
        try await transport.enqueueJSON([
            "type": "session.output_transcript.delta",
            "delta": "kept translation",
            "event_id": "out-1",
        ])

        // Then: input_transcript は event stream に出ず、訳文だけが届く
        var sawInput = false
        var keptTranslation: String?
        let deadline = ContinuousClock.now + .seconds(3)
        for await event in stream {
            if case .inputTranscriptDelta = event.event {
                sawInput = true
            }
            if case .outputTranscriptDelta(let delta, _, _) = event.event {
                keptTranslation = delta
                break
            }
            if ContinuousClock.now >= deadline {
                break
            }
        }
        XCTAssertFalse(sawInput)
        XCTAssertEqual(keptTranslation, "kept translation")
        await connection.forceClose()
    }

    func testSessionUpdateTimeout() async {
        // Given: created後にupdatedが来ない
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 50_000_000
        )
        try? await transport.enqueueJSON(["type": "session.created"])

        // When/Then
        do {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected timeout")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(error, .sessionUpdateTimeout)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    func testHandshakeTimeoutAbortsReceiveThatIgnoresTaskCancellation() async {
        // Given: receive が Swift キャンセルを無視し、close() だけが待ちを解く transport
        let transport = HangUntilCloseWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 50_000_000
        )
        let started = ContinuousClock.now

        // When
        do {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("Expected timeout")
        } catch {
            // Then: TaskGroup が receive 完了を無限待ちせず、約 50ms で失敗する
            let elapsed = ContinuousClock.now - started
            XCTAssertLessThan(elapsed, Duration.seconds(2))
            let closes = await transport.closeCount
            XCTAssertGreaterThanOrEqual(closes, 1)
        }
    }

    func testCancelledHandshakeClosesHungReceive() async {
        // Given: handshake receive が close() まで戻らない
        let transport = HangUntilCloseWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 30_000_000_000
        )
        let started = ContinuousClock.now
        let startTask = Task {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithSourceTranscription()
            )
        }

        // When: start 直後にキャンセルする（timeout 15s を待たない）
        try? await Task.sleep(nanoseconds: 30_000_000)
        startTask.cancel()

        do {
            try await startTask.value
            XCTFail("Expected cancellation")
        } catch {
            // Then: 親 Task キャンセルが transport.close を呼び、停止待ちが解けない
            let elapsed = ContinuousClock.now - started
            XCTAssertLessThan(elapsed, Duration.seconds(2))
            let closes = await transport.closeCount
            XCTAssertGreaterThanOrEqual(closes, 1)
        }
    }

    func testSourceHandshakeTimeoutAbortsReceiveThatIgnoresTaskCancellation() async {
        // Given: 原文 handshake の receive も Swift キャンセルを無視する
        let transport = HangUntilCloseWebSocketTransport()
        let connection = RealtimeSourceTranscriptionConnection(
            transport: transport,
            safetyIdentifier: "safety",
            handshakeTimeoutNanoseconds: 50_000_000
        )
        let started = ContinuousClock.now

        // When
        do {
            try await connection.start(apiKey: "sk-test", tuning: .default, pair: .jaEn)
            XCTFail("Expected timeout")
        } catch {
            // Then: 原文側も約 50ms で失敗し、stop 待ちに残らない
            let elapsed = ContinuousClock.now - started
            XCTAssertLessThan(elapsed, Duration.seconds(2))
            let closes = await transport.closeCount
            XCTAssertGreaterThanOrEqual(closes, 1)
        }
    }

    func testAsyncTimeoutThrowsRecoverableFailure() async {
        // Given: 50msでtimeoutする長時間処理
        // When
        do {
            try await AsyncTimeout.run(nanoseconds: 50_000_000) {
                try await Task.sleep(nanoseconds: 1_000_000_000)
            }
            XCTFail("Expected timeout")
        } catch let error as RealtimeTranslationError {
            // Then: recoverableなsend timeoutになる
            XCTAssertEqual(
                error,
                .recoverableTransportFailure("send timeout")
            )
            XCTAssertTrue(error.isRecoverable)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    func testAsyncTimeoutReturnsBeforeDeadline() async throws {
        // Given: すぐ完了する処理
        // When
        let value = try await AsyncTimeout.run(nanoseconds: 1_000_000_000) {
            42
        }

        // Then: timeoutせず結果を返す
        XCTAssertEqual(value, 42)
    }

    func testAsyncTimeoutOnTimeoutUnblocksUncancellableWait() async {
        // Given: Swift キャンセルを見ない continuation 待ち
        let box = TimeoutResumeBox()
        let started = ContinuousClock.now

        // When
        do {
            try await AsyncTimeout.run(
                nanoseconds: 50_000_000,
                onTimeout: { box.resume() }
            ) {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    box.store(continuation)
                }
                throw RealtimeTranslationError.recoverableTransportFailure("send timeout")
            }
            XCTFail("Expected timeout")
        } catch let error as RealtimeTranslationError {
            // Then: onTimeout が待ちを解き、TaskGroup が固まらない
            let elapsed = ContinuousClock.now - started
            XCTAssertLessThan(elapsed, Duration.seconds(2))
            XCTAssertEqual(error, .recoverableTransportFailure("send timeout"))
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    private func waitForSent(
        _ transport: FakeRealtimeWebSocketTransport,
        timeout: TimeInterval = 1.0
    ) async throws {
        try await waitForSentCount(transport, minimum: 1, timeout: timeout)
    }

    private func waitForSentCount(
        _ transport: FakeRealtimeWebSocketTransport,
        minimum: Int,
        timeout: TimeInterval = 1.0
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let count = await transport.sent.count
            if count >= minimum { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("Timed out waiting for \(minimum) sent messages")
    }
}
