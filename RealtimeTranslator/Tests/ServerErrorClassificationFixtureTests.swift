import Foundation
import XCTest
@testable import RealtimeTranslator

final class ServerErrorClassificationFixtureTests: XCTestCase {
    // Given: shared/fixtures/v1/server-error.json の各 case
    // When: type / code / message を別々に渡して分類する
    // Then: disposition と termination が fixture と一致し、致命以外は文言を保持しない
    func testClassificationMatchesFixture() throws {
        for item in try SharedFixtures.section("server-error", "cases") {
            let name = SharedFixtures.text(item["name"])
            let expected = try XCTUnwrap(item["expected"] as? [String: Any], name)
            let actual = RealtimeServerErrorClassification.classify(
                errorType: SharedFixtures.optionalText(item["errorType"]),
                code: SharedFixtures.optionalText(item["code"]),
                message: SharedFixtures.text(item["message"])
            )

            XCTAssertEqual(parseDisposition(expected["disposition"]), actual.disposition, name)
            let sanitized = SharedFixtures.optionalText(expected["sanitizedMessage"])
            XCTAssertEqual(
                parseTermination(expected["termination"], sanitizedMessage: sanitized),
                actual.termination,
                name
            )
            if case .fatalServerError(let message) = actual.termination {
                XCTAssertFalse(message.contains("sk-"), name)
            }
        }
    }

    // Given: source transcription failure の shared fixture 各 case
    // When: transcription failure 用分類器へ type / code を渡す
    // Then: keepAlive を既定値として disposition と termination が一致する
    func testTranscriptionFailureClassificationMatchesFixture() throws {
        let fixture = try SharedFixtures.load("server-error")
        let contract = try XCTUnwrap(fixture["transcriptionFailed"] as? [String: Any])
        XCTAssertEqual(SharedFixtures.text(contract["unknownDisposition"]), "keepAlive")
        for item in try XCTUnwrap(contract["cases"] as? [[String: Any]]) {
            let expected = try XCTUnwrap(item["expected"] as? [String: Any])
            let actual = RealtimeServerErrorClassification.classifyTranscriptionFailure(
                errorType: SharedFixtures.optionalText(item["errorType"]),
                code: SharedFixtures.optionalText(item["code"])
            )
            XCTAssertEqual(parseDisposition(expected["disposition"]), actual.disposition)
            XCTAssertEqual(
                parseTermination(expected["termination"], sanitizedMessage: nil),
                actual.termination
            )
        }
    }

    // Given: fixture の許可リストと優先順位
    // When: macOS の定数と比較する
    // Then: transport code、終了理由の優先順位、回復可能サーバーエラーの文言が一致する
    func testAllowlistAndPrecedenceMatchFixture() throws {
        let fixture = try SharedFixtures.load("server-error")
        let allowlists = try XCTUnwrap(fixture["allowlists"] as? [String: Any])
        XCTAssertEqual(
            SharedFixtures.text(allowlists["transportCode"]),
            RealtimeServerErrorClassification.transportCode
        )

        let precedence = try XCTUnwrap(fixture["terminationPrecedence"] as? [Any])
            .map { parseTermination($0, sanitizedMessage: "x") }
        for (higher, lower) in zip(precedence, precedence.dropFirst()) {
            XCTAssertGreaterThan(higher, lower)
        }
        XCTAssertTrue(precedence.allSatisfy { $0 > EventDeliveryTermination.none })
        XCTAssertEqual(precedence.count, 5)

        // 認証失敗の根拠があれば code=transport でも再接続へ回さない
        let authOverTransport = RealtimeServerErrorClassification.classify(
            errorType: nil,
            code: RealtimeServerErrorClassification.transportCode,
            message: "Incorrect API key provided: sk-secret"
        )
        XCTAssertEqual(authOverTransport.disposition, .halt)
        XCTAssertEqual(authOverTransport.termination, .authenticationFailed)

        let recoverable = try XCTUnwrap(fixture["recoverableServerError"] as? [String: Any])
        XCTAssertTrue(RealtimeTranslationError.recoverableServerError.isRecoverable)
        XCTAssertEqual(
            RealtimeTranslationError.recoverableServerError.errorDescription,
            UiCopy.text(SharedFixtures.text(recoverable["errorMessageKey"]))
        )
    }

    // Given: 回復候補・接続維持・致命の各 error を EventDeliveryState へ記録する
    // When: 優先順位どおりに記録する
    // Then: 接続維持は記録されず、致命が回復候補を上書きし、逆は起きない
    func testKeepAliveIsNotRecordedAndFatalOutranksRecoverable() {
        let state = EventDeliveryState(epoch: 1)

        XCTAssertFalse(state.tryRecordTermination(
            RealtimeServerErrorClassification.classify(
                errorType: nil,
                code: "input_audio_buffer_commit_empty",
                message: "empty"
            )
        ))
        XCTAssertEqual(state.termination, .none)

        XCTAssertTrue(state.tryRecordTermination(
            RealtimeServerErrorClassification.classify(errorType: "server_error", code: nil, message: "boom")
        ))
        XCTAssertEqual(state.makeError(), .recoverableServerError)
        XCTAssertTrue(state.makeError().isRecoverable)

        XCTAssertTrue(state.tryRecordTermination(
            RealtimeServerErrorClassification.classify(errorType: nil, code: "unknown_code", message: "bearer sk-x")
        ))
        XCTAssertFalse(state.tryRecordTermination(
            RealtimeServerErrorClassification.classify(errorType: "server_error", code: nil, message: "boom")
        ))
        XCTAssertEqual(
            state.termination,
            .fatalServerError(RealtimeTranslationError.genericServerMessage)
        )
    }

    // Given: 翻訳接続と原文接続の codec
    // When: type と code を両方持つ error を復号する
    // Then: 両接続とも type / code を別々に保持し、分類結果が一致する
    func testTranslationAndSourceCodecsPreserveTypeAndCodeIdentically() throws {
        let json = Data(
            #"{"type":"error","error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit_exceeded"}}"#
                .utf8
        )

        let translation = try RealtimeTranslationMessageCodec.decodeServerEvent(from: json)
        guard case .error(let message, let code, let errorType) = translation else {
            return XCTFail("expected translation error")
        }
        XCTAssertEqual(message, "Rate limit reached")
        XCTAssertEqual(code, "rate_limit_exceeded")
        XCTAssertEqual(errorType, "rate_limit_error")

        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: json) as? [String: Any]
        )
        let source = RealtimeSourceTranscriptionConnection.serverError(object)
        XCTAssertEqual(source.message, message)
        XCTAssertEqual(source.code, code)
        XCTAssertEqual(source.errorType, errorType)

        let translationClass = RealtimeServerErrorClassification.classify(
            errorType: errorType,
            code: code,
            message: message
        )
        let sourceClass = RealtimeServerErrorClassification.classify(
            errorType: source.errorType,
            code: source.code,
            message: source.message
        )
        XCTAssertEqual(translationClass, sourceClass)
        XCTAssertEqual(translationClass.disposition, .recover)
        XCTAssertEqual(translationClass.termination, .recoverableServerError)
    }

    // Given: handshake 中に input_audio_buffer_commit_empty が届く翻訳接続
    // When: その後 session.created / session.updated が届く
    // Then: keep-alive error は握手を壊さず start が成功する
    func testTranslationHandshakeSkipsKeepAliveError() async throws {
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeTranslationConnection(
            target: .english,
            transport: transport,
            safetyIdentifier: "safety",
            sessionUpdateTimeoutNanoseconds: 1_000_000_000
        )
        try await transport.enqueueJSON([
            "type": "error",
            "error": [
                "message": "buffer too small",
                "type": "invalid_request_error",
                "code": "input_audio_buffer_commit_empty",
            ],
        ])
        try await transport.enqueueJSON(["type": "session.created"])
        try await transport.enqueueJSON(["type": "session.updated"])

        try await connection.start(
            apiKey: "sk-test",
            config: .englishTargetWithSourceTranscription()
        )
        await connection.forceClose()
    }

    // Given: handshake 中に回復候補の server_error が届く翻訳接続
    // When: start する
    // Then: recoverableServerError として失敗し、生の文言は保持しない
    func testTranslationHandshakeRecoverableErrorIsRecoverable() async {
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
                "message": "upstream sk-should-not-appear",
                "type": "server_error",
            ],
        ])

        do {
            try await connection.start(
                apiKey: "sk-test",
                config: .englishTargetWithSourceTranscription()
            )
            XCTFail("expected recoverableServerError")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(error, .recoverableServerError)
            XCTAssertTrue(error.isRecoverable)
            XCTAssertFalse(error.localizedDescription.contains("sk-"))
        } catch {
            XCTFail("unexpected error \(error)")
        }
    }

    // Given: handshake 中に keep-alive error が届く原文接続
    // When: session.created / session.updated が続く
    // Then: 握手は成功する
    func testSourceHandshakeSkipsKeepAliveError() async throws {
        let transport = FakeRealtimeWebSocketTransport()
        let connection = RealtimeSourceTranscriptionConnection(
            transport: transport,
            safetyIdentifier: "safety",
            handshakeTimeoutNanoseconds: 1_000_000_000,
            closeTimeoutNanoseconds: 500_000_000
        )
        try await transport.enqueueJSON([
            "type": "error",
            "error": [
                "message": "buffer too small",
                "code": "input_audio_buffer_commit_empty",
            ],
        ])
        try await transport.enqueueJSON(["type": "session.created"])
        try await transport.enqueueJSON(["type": "session.updated"])

        try await connection.start(apiKey: "sk-test", pair: .jaEn)
        await connection.forceClose()
    }

    // Given: listening 中の session に keep-alive error が届く
    // When: その直後に回復候補の error が届く
    // Then: keep-alive では state が変わらず、回復候補で再接続する
    @MainActor
    func testSessionIgnoresKeepAliveAndReconnectsOnRecoverableServerError() async {
        let dual = FakeDualRealtimeTranslationClient()
        let session = InterpretationSession(
            apiKeyStore: InMemoryAPIKeyStore(initialKey: "sk-test"),
            audioCapture: FakeRealtimeAudioCaptureService(),
            dualClient: dual
        )
        await session.start()
        await waitUntil { session.state == .listening }
        let startCount = dual.startCallCount

        dual.emit(
            target: .english,
            event: .error(
                message: "buffer too small",
                code: "input_audio_buffer_commit_empty",
                errorType: "invalid_request_error"
            )
        )
        try? await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(session.state, .listening)
        XCTAssertEqual(dual.startCallCount, startCount)

        dual.emit(
            target: .english,
            event: .error(message: "boom", code: nil, errorType: "server_error")
        )
        await waitUntil(timeout: 3) {
            session.state == .listening && dual.startCallCount > startCount
        }
        XCTAssertGreaterThan(dual.startCallCount, startCount)
        await session.stop()
    }

    private func parseDisposition(_ value: Any?) -> RealtimeServerErrorDisposition {
        switch SharedFixtures.text(value) {
        case "keepAlive": return .keepAlive
        case "recover": return .recover
        case "halt": return .halt
        default:
            XCTFail("unknown disposition: \(SharedFixtures.text(value))")
            return .halt
        }
    }

    private func parseTermination(_ value: Any?, sanitizedMessage: String?) -> EventDeliveryTermination {
        switch SharedFixtures.text(value) {
        case "none": return .none
        case "authenticationFailed": return .authenticationFailed
        case "fatalServerError":
            return .fatalServerError(sanitizedMessage ?? RealtimeTranslationError.genericServerMessage)
        case "receiveOverflow": return .receiveOverflow
        case "recoverableServerError": return .recoverableServerError
        case "transportFailure": return .transportFailure
        default:
            XCTFail("unknown termination: \(SharedFixtures.text(value))")
            return .none
        }
    }
}
