import XCTest
@testable import RealtimeTranslator

actor FakeRealtimeWebSocketTransport: RealtimeWebSocketTransport {
    private var inbound: [Data] = []
    private var waiters: [CheckedContinuation<Data, Error>] = []
    private(set) var sent: [Data] = []
    private(set) var connectCount = 0
    private(set) var closeCount = 0
    private(set) var sendAttemptCount = 0
    var connectError: Error?
    var sendError: Error?
    private var failNextSend = false
    /// セットするとsendがこの時間だけ待機してから通常処理へ進む。
    var sendHangNanoseconds: UInt64 = 0

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
        if let connectError {
            throw connectError
        }
        _ = url
        _ = headers
    }

    func failNextSendOnce() {
        failNextSend = true
    }

    func send(_ data: Data) async throws {
        sendAttemptCount += 1
        if sendHangNanoseconds > 0 {
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
