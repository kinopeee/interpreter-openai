import XCTest
@testable import RealtimeTranslator

final class CodecFixtureTests: XCTestCase {
    // Given: fixture の翻訳クライアントイベント
    // When: 翻訳 codec でエンコードする
    // Then: 期待する JSON ペイロードと一致する
    func testEncodeMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("codec", "encode") {
                        let fixture = try SharedFixtures.case("codec", "encode", name)
            let eventObject = try XCTUnwrap(fixture["event"] as? [String: Any])
            let encoded = try RealtimeTranslationMessageCodec.encode(clientEvent(eventObject))
            let actual = try SharedFixtures.parseUTF8(encoded)
            let expected = try XCTUnwrap(fixture["expected"])
            XCTAssertTrue(
                SharedFixtures.jsonEquals(actual, expected),
                "encoded JSON did not match fixture for \(name)"
            )
        }
    }

    // Given: fixture の翻訳サーバーメッセージ
    // When: 翻訳 codec でデコードする
    // Then: 期待するサーバーイベント種別と値になる
    func testDecodeMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("codec", "decode") {
                        let fixture = try SharedFixtures.case("codec", "decode", name)
            let utf8 = Data(SharedFixtures.text(fixture["json"]).utf8)
            let actual = try RealtimeTranslationMessageCodec.decodeServerEvent(from: utf8)
            let expected = try XCTUnwrap(fixture["expected"] as? [String: Any])
            switch SharedFixtures.text(expected["kind"]) {
            case "sessionCreated":
                XCTAssertEqual(actual, .sessionCreated)
            case "sessionUpdated":
                XCTAssertEqual(actual, .sessionUpdated)
            case "sessionClosed":
                XCTAssertEqual(actual, .sessionClosed)
            case "outputAudioDelta":
                XCTAssertEqual(actual, .outputAudioDelta)
            case "inputTranscriptDelta":
                guard case .inputTranscriptDelta(let delta, let eventID, let elapsedMs) = actual else {
                    return XCTFail("expected inputTranscriptDelta")
                }
                assertDelta(expected, delta: delta, eventID: eventID, elapsedMs: elapsedMs)
            case "outputTranscriptDelta":
                guard case .outputTranscriptDelta(let delta, let eventID, let elapsedMs) = actual else {
                    return XCTFail("expected outputTranscriptDelta")
                }
                assertDelta(expected, delta: delta, eventID: eventID, elapsedMs: elapsedMs)
            case "error":
                guard case .error(let message, let code) = actual else {
                    return XCTFail("expected error")
                }
                XCTAssertEqual(SharedFixtures.text(expected["message"]), message)
                XCTAssertEqual(SharedFixtures.optionalText(expected["code"]), code)
            case "unknown":
                guard case .unknown(let type) = actual else {
                    return XCTFail("expected unknown")
                }
                XCTAssertEqual(SharedFixtures.text(expected["type"]), type)
            default:
                XCTFail("unhandled fixture kind \(SharedFixtures.text(expected["kind"]))")
            }
        }
    }

    // Given: 不正または欠損したサーバーメッセージ
    // When: 翻訳 codec でデコードする
    // Then: fixture が指定するエラー種別へ正規化される
    func testDecodeFailureMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("codec", "decodeFailures") {
                        let fixture = try SharedFixtures.case("codec", "decodeFailures", name)
            let utf8 = Data(SharedFixtures.text(fixture["json"]).utf8)
            XCTAssertThrowsError(
                try RealtimeTranslationMessageCodec.decodeServerEvent(from: utf8)
            ) { error in
                XCTAssertEqual(error as? RealtimeTranslationError, .invalidMessage)
            }
        }
    }

    // Given: fixture のソース文字起こしクライアントイベント
    // When: FakeRealtimeWebSocketTransport 経由で接続へ投入する
    // Then: 送信 JSON が期待ペイロードと一致する
    func testTranscriptionEncodeMatchesFixture() async throws {
        for name in try SharedFixtures.caseNames("codec", "transcriptionEncode") {
            let fixture = try SharedFixtures.case("codec", "transcriptionEncode", name)
            let eventObject = try XCTUnwrap(fixture["event"] as? [String: Any])
            let expected = try XCTUnwrap(fixture["expected"])
            let transport = FakeRealtimeWebSocketTransport()
            let connection = RealtimeSourceTranscriptionConnection(
                transport: transport,
                safetyIdentifier: "test-safety",
                handshakeTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            )

            switch SharedFixtures.text(eventObject["kind"]) {
            case "sessionUpdate":
                let tuning = transcriptionTuning(eventObject)
                try await transport.enqueueJSON(["type": "session.created"])
                let startTask = Task {
                    try await connection.start(apiKey: "sk-test", tuning: tuning)
                }
                try await waitUntilSent(transport, minimum: 1)
                try await transport.enqueueJSON(["type": "session.updated"])
                try await startTask.value
                let sentMessages = await transport.sent
                let sent = try XCTUnwrap(sentMessages.first)
                let actual = try SharedFixtures.parseUTF8(sent)
                XCTAssertTrue(
                    SharedFixtures.jsonEquals(actual, expected),
                    "session.update JSON did not match for \(name)"
                )

            case "inputAudioBufferAppend":
                try await startTranscription(connection, transport: transport)
                let audio = try XCTUnwrap(
                    Data(base64Encoded: SharedFixtures.text(eventObject["base64Audio"]))
                )
                try await connection.appendAudioFrame(audio)
                let sentMessages = await transport.sent
                let appendData = try XCTUnwrap(sentMessages.last)
                let append = try SharedFixtures.parseUTF8(appendData)
                XCTAssertTrue(
                    SharedFixtures.jsonEquals(append, expected),
                    "append JSON did not match for \(name)"
                )

            case "commit":
                try await startTranscription(connection, transport: transport)
                let closeTask = Task {
                    try await connection.closeGracefully()
                }
                try await waitUntilSentContains(transport, type: "input_audio_buffer.commit")
                try await transport.enqueueJSON([
                    "type": "conversation.item.input_audio_transcription.completed"
                ])
                try await closeTask.value
                let commitObject = await findSent(transport, type: "input_audio_buffer.commit")
                let commit = try XCTUnwrap(commitObject)
                XCTAssertTrue(
                    SharedFixtures.jsonEquals(commit, expected),
                    "commit JSON did not match for \(name)"
                )

            default:
                XCTFail("unhandled transcription encode kind \(name)")
            }

            await connection.forceClose()
        }
    }

    // Given: fixture の文字起こし接続からのサーバーメッセージ
    // When: 接続の inbound へ投入して events を観測する
    // Then: 期待イベントになり、ignored はイベントを出さない
    func testTranscriptionDecodeMatchesFixture() async throws {
        for name in try SharedFixtures.caseNames("codec", "transcriptionDecode") {
            let fixture = try SharedFixtures.case("codec", "transcriptionDecode", name)
            let expected = try XCTUnwrap(fixture["expected"] as? [String: Any])
            let kind = SharedFixtures.text(expected["kind"])
            let transport = FakeRealtimeWebSocketTransport()
            let connection = RealtimeSourceTranscriptionConnection(
                transport: transport,
                safetyIdentifier: "test-safety",
                handshakeTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: 500_000_000
            )
            try await startTranscription(connection, transport: transport)
            let stream = await connection.events
            let box = EventBox()
            let collector = Task {
                for await event in stream {
                    await box.append(event)
                }
            }

            let inbound = Data(SharedFixtures.text(fixture["json"]).utf8)
            await transport.enqueueInbound(inbound)

            switch kind {
            case "inputTranscriptDelta":
                let event = try await waitForEvent(box)
                guard case .inputTranscriptDelta(let delta, let eventID, let elapsedMs) = event.event
                else {
                    XCTFail("expected inputTranscriptDelta for \(name)")
                    collector.cancel()
                    await connection.forceClose()
                    continue
                }
                XCTAssertEqual(SharedFixtures.text(expected["delta"]), delta, name)
                XCTAssertEqual(SharedFixtures.optionalText(expected["eventId"]), eventID, name)
                XCTAssertNil(elapsedMs, name)
                XCTAssertNil(SharedFixtures.optionalNumber(expected["elapsedMs"]), name)

            case "ignored":
                try await Task.sleep(nanoseconds: 150_000_000)
                let events = await box.snapshot()
                XCTAssertTrue(events.isEmpty, "ignored payload must not emit an event (\(name))")

            case "transcriptionCompleted":
                // 完了は events へ出さず、commit drain の内部フラグとして扱う。
                let closeTask = Task {
                    try await connection.closeGracefully()
                }
                try await waitUntilSentContains(transport, type: "input_audio_buffer.commit")
                try await closeTask.value

            case "error":
                let event = try await waitForEvent(box)
                guard case .error(let message, let code) = event.event else {
                    XCTFail("expected error for \(name)")
                    collector.cancel()
                    await connection.forceClose()
                    continue
                }
                XCTAssertEqual(SharedFixtures.text(expected["message"]), message, name)
                XCTAssertEqual(SharedFixtures.optionalText(expected["code"]), code, name)

            default:
                XCTFail("unhandled transcription decode kind \(kind)")
            }

            collector.cancel()
            await connection.forceClose()
        }
    }

    private func assertDelta(
        _ expected: [String: Any],
        delta: String,
        eventID: String?,
        elapsedMs: Int?
    ) {
        XCTAssertEqual(SharedFixtures.text(expected["delta"]), delta)
        XCTAssertEqual(SharedFixtures.optionalText(expected["eventId"]), eventID)
        XCTAssertEqual(SharedFixtures.optionalNumber(expected["elapsedMs"]), elapsedMs)
    }

    private func clientEvent(_ fixture: [String: Any]) throws -> RealtimeTranslationClientEvent {
        switch SharedFixtures.text(fixture["kind"]) {
        case "sessionUpdate":
            let noiseReduction = SharedFixtures.optionalText(fixture["noiseReduction"]).flatMap {
                RealtimeTranslationNoiseReduction(rawValue: $0)
            }
            return .sessionUpdate(
                RealtimeTranslationSessionConfig(
                    outputLanguage: try XCTUnwrap(
                        RealtimeTranslationOutputLanguage(
                            rawValue: SharedFixtures.text(fixture["outputLanguage"])
                        )
                    ),
                    inputTranscriptionModel: SharedFixtures.optionalText(
                        fixture["inputTranscriptionModel"]
                    ),
                    noiseReduction: noiseReduction
                )
            )
        case "inputAudioBufferAppend":
            return .inputAudioBufferAppend(
                base64Audio: SharedFixtures.text(fixture["base64Audio"])
            )
        case "sessionClose":
            return .sessionClose
        default:
            throw NSError(
                domain: "CodecFixtureTests",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: "unhandled client event kind"]
            )
        }
    }

    private func transcriptionTuning(_ fixture: [String: Any]) -> RealtimeSessionTuning {
        let keywords = (fixture["keywords"] as? [Any] ?? []).map(SharedFixtures.text)
        return RealtimeSessionTuning(
            noiseReduction: RealtimeTranslationNoiseReduction(
                rawValue: SharedFixtures.text(fixture["noiseReduction"])
            ) ?? .farField,
            transcriptionDelay: RealtimeTranscriptionDelay(
                rawValue: SharedFixtures.text(fixture["transcriptionDelay"])
            ) ?? .low,
            transcriptionPrompt: SharedFixtures.text(fixture["prompt"]),
            transcriptionKeywords: keywords
        )
    }

    private func startTranscription(
        _ connection: RealtimeSourceTranscriptionConnection,
        transport: FakeRealtimeWebSocketTransport,
        tuning: RealtimeSessionTuning = .default
    ) async throws {
        try await transport.enqueueJSON(["type": "session.created"])
        let startTask = Task {
            try await connection.start(apiKey: "sk-test", tuning: tuning)
        }
        try await waitUntilSent(transport, minimum: 1)
        try await transport.enqueueJSON(["type": "session.updated"])
        try await startTask.value
    }

    private func waitUntilSent(
        _ transport: FakeRealtimeWebSocketTransport,
        minimum: Int,
        timeout: TimeInterval = 1.0
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await transport.sent.count >= minimum { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("Timed out waiting for sent count \(minimum)")
    }

    private func waitUntilSentContains(
        _ transport: FakeRealtimeWebSocketTransport,
        type: String,
        timeout: TimeInterval = 1.0
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await findSent(transport, type: type) != nil { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("Timed out waiting for sent type \(type)")
    }

    private func findSent(
        _ transport: FakeRealtimeWebSocketTransport,
        type: String
    ) async -> Any? {
        for data in await transport.sent {
            guard let object = try? SharedFixtures.parseUTF8(data) as? [String: Any],
                object["type"] as? String == type
            else {
                continue
            }
            return object
        }
        return nil
    }

    private func waitForEvent(
        _ box: EventBox,
        timeout: TimeInterval = 1.0
    ) async throws -> RealtimeTranslationStreamEvent {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let event = await box.first() {
                return event
            }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        throw NSError(
            domain: "CodecFixtureTests",
            code: 2,
            userInfo: [NSLocalizedDescriptionKey: "Timed out waiting for transcription event"]
        )
    }
}

private actor EventBox {
    private var events: [RealtimeTranslationStreamEvent] = []

    func append(_ event: RealtimeTranslationStreamEvent) {
        events.append(event)
    }

    func first() -> RealtimeTranslationStreamEvent? {
        events.first
    }

    func snapshot() -> [RealtimeTranslationStreamEvent] {
        events
    }
}
