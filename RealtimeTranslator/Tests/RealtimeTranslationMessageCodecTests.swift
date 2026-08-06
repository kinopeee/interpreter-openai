import XCTest
@testable import RealtimeTranslator

final class RealtimeTranslationMessageCodecTests: XCTestCase {
    func testEncodeSessionUpdateIncludesEnglishTranscriptionAndFarField() throws {
        // Given: 英語targetで原文transcription付き設定
        let config = RealtimeTranslationSessionConfig.englishTargetWithSourceTranscription()

        // When: session.updateをencodeする
        let data = try RealtimeTranslationMessageCodec.encode(.sessionUpdate(config))
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )

        // Then: typeとlanguage・transcription・noise_reductionが含まれる
        XCTAssertEqual(object["type"] as? String, "session.update")
        let session = try XCTUnwrap(object["session"] as? [String: Any])
        let audio = try XCTUnwrap(session["audio"] as? [String: Any])
        let output = try XCTUnwrap(audio["output"] as? [String: Any])
        XCTAssertEqual(output["language"] as? String, "en")
        let input = try XCTUnwrap(audio["input"] as? [String: Any])
        let transcription = try XCTUnwrap(input["transcription"] as? [String: Any])
        XCTAssertEqual(transcription["model"] as? String, "gpt-realtime-whisper")
        let noise = try XCTUnwrap(input["noise_reduction"] as? [String: Any])
        XCTAssertEqual(noise["type"] as? String, "far_field")
    }

    func testEncodeJapaneseTargetOmitsTranscriptionModel() throws {
        // Given: 日本語targetでtranscription無効
        let config = RealtimeTranslationSessionConfig.japaneseTargetWithoutSourceTranscription()

        // When: encodeする
        let data = try RealtimeTranslationMessageCodec.encode(.sessionUpdate(config))
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        let session = try XCTUnwrap(object["session"] as? [String: Any])
        let audio = try XCTUnwrap(session["audio"] as? [String: Any])
        let input = try XCTUnwrap(audio["input"] as? [String: Any])

        // Then: transcriptionキーが無い
        XCTAssertNil(input["transcription"])
        XCTAssertEqual((audio["output"] as? [String: Any])?["language"] as? String, "ja")
    }

    func testEncodeAppendAndClose() throws {
        // Given: appendとcloseイベント
        let append = try RealtimeTranslationMessageCodec.encode(
            .inputAudioBufferAppend(base64Audio: "AAAA")
        )
        let close = try RealtimeTranslationMessageCodec.encode(.sessionClose)

        // When/Then: 期待typeとpayload
        let appendObject = try XCTUnwrap(
            JSONSerialization.jsonObject(with: append) as? [String: Any]
        )
        XCTAssertEqual(appendObject["type"] as? String, "session.input_audio_buffer.append")
        XCTAssertEqual(appendObject["audio"] as? String, "AAAA")

        let closeObject = try XCTUnwrap(
            JSONSerialization.jsonObject(with: close) as? [String: Any]
        )
        XCTAssertEqual(closeObject["type"] as? String, "session.close")
    }

    func testDecodeTranscriptDeltasWithoutInsertingWhitespace() throws {
        // Given: 空白を含まないdelta JSON
        let inputJSON = """
        {"type":"session.input_transcript.delta","delta":"Hello","event_id":"e1","elapsed_ms":12}
        """
        let outputJSON = """
        {"type":"session.output_transcript.delta","delta":"こんにちは","event_id":"e2"}
        """

        // When: decodeする
        let input = try RealtimeTranslationMessageCodec.decodeServerEvent(
            from: Data(inputJSON.utf8)
        )
        let output = try RealtimeTranslationMessageCodec.decodeServerEvent(
            from: Data(outputJSON.utf8)
        )

        // Then: deltaは原文のまま、欠落elapsedはnil
        XCTAssertEqual(
            input,
            .inputTranscriptDelta(delta: "Hello", eventID: "e1", elapsedMs: 12)
        )
        XCTAssertEqual(
            output,
            .outputTranscriptDelta(delta: "こんにちは", eventID: "e2", elapsedMs: nil)
        )
    }

    func testDecodeOutputAudioDeltaDoesNotRequirePayload() throws {
        // Given: base64音声付きoutput_audio.delta
        let json = """
        {"type":"session.output_audio.delta","delta":"AAAA"}
        """

        // When: decodeする
        let event = try RealtimeTranslationMessageCodec.decodeServerEvent(from: Data(json.utf8))

        // Then: 非decodeのマーカーイベントになる
        XCTAssertEqual(event, .outputAudioDelta)
    }

    func testDecodeUnknownAndBrokenJSON() {
        // Given: 未知typeと壊れたJSON/空payload
        let unknownJSON = Data(#"{"type":"session.future_event"}"#.utf8)
        let broken = Data("not-json".utf8)
        let empty = Data()

        // When/Then: 未知は型名だけ保持、壊れたJSONと空payloadはinvalidMessageへ正規化する
        XCTAssertEqual(
            try? RealtimeTranslationMessageCodec.decodeServerEvent(from: unknownJSON),
            .unknown(type: "session.future_event")
        )
        XCTAssertThrowsError(
            try RealtimeTranslationMessageCodec.decodeServerEvent(from: broken)
        ) { error in
            XCTAssertEqual(error as? RealtimeTranslationError, .invalidMessage)
        }
        XCTAssertThrowsError(
            try RealtimeTranslationMessageCodec.decodeServerEvent(from: empty)
        ) { error in
            XCTAssertEqual(error as? RealtimeTranslationError, .invalidMessage)
        }
    }

    func testDecodeCreatedUpdatedClosedError() throws {
        // Given: 制御系イベント
        let cases: [(String, RealtimeTranslationServerEvent)] = [
            (#"{"type":"session.created"}"#, .sessionCreated),
            (#"{"type":"session.updated"}"#, .sessionUpdated),
            (#"{"type":"session.closed"}"#, .sessionClosed),
            (
                #"{"type":"error","error":{"message":"bad key","code":"invalid_api_key"}}"#,
                .error(message: "bad key", code: "invalid_api_key")
            ),
        ]

        // When/Then: それぞれ期待値へdecodeされる
        for (json, expected) in cases {
            let event = try RealtimeTranslationMessageCodec.decodeServerEvent(from: Data(json.utf8))
            XCTAssertEqual(event, expected)
        }
    }
}
