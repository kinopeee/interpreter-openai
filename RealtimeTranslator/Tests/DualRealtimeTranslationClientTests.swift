import XCTest
@testable import RealtimeTranslator

final class DualRealtimeTranslationClientTests: XCTestCase {
    func testSourceConnectionPublishesEveryDeltaForSameItem() async throws {
        // Given: 専用transcriptionを開始したclient
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let received = expectation(description: "同一itemの2 deltaを受信")
        let stream = await dual.events
        let collector = Task {
            var deltas: [String] = []
            for await event in stream {
                guard case .inputTranscriptDelta(let delta, _, _) = event.event else {
                    continue
                }
                deltas.append(delta)
                if deltas.count == 2 {
                    received.fulfill()
                    return deltas
                }
            }
            return deltas
        }

        // When: event_idなし・同一item_idの連続deltaを受け取る
        try await sourceTransport.enqueueJSON([
            "type": "conversation.item.input_audio_transcription.delta",
            "item_id": "item-1",
            "delta": "それ",
        ])
        try await sourceTransport.enqueueJSON([
            "type": "conversation.item.input_audio_transcription.delta",
            "item_id": "item-1",
            "delta": "ぞれ",
        ])
        await fulfillment(of: [received], timeout: 1)

        // Then: item_idで誤って重複排除しない
        let deltas = await collector.value
        XCTAssertEqual(deltas, ["それ", "ぞれ"])
        await dual.forceClose()
    }

    func testUndeterminedLanguageBuffersPrerollUntilEnglishDetected() async throws {
        // Given: 3接続がreadyだが発話言語は未判定
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )

        // When: 2frame送信後に英語発話と判定する
        let frameA = Data(repeating: 0x11, count: PCM16FramePacketizer.bytesPerFrame)
        let frameB = Data(repeating: 0x22, count: PCM16FramePacketizer.bytesPerFrame)
        try await dual.appendAudioFrame(frameA)
        try await dual.appendAudioFrame(frameB)
        let japaneseBeforeDetection = try decodeAppendPayloads(
            await japaneseTransport.sent
        )
        let englishBeforeDetection = try decodeAppendPayloads(
            await englishTransport.sent
        )
        try await dual.setSpokenLanguage(.english)
        try await waitUntilAppendCount(japaneseTransport, minimum: 2)

        // Then: 判定前は原文のみ。判定後に日本語targetへprerollが届く
        let sourceAppends = try decodeAppendPayloads(await sourceTransport.sent)
        let englishAppends = try decodeAppendPayloads(await englishTransport.sent)
        let japaneseAppends = try decodeAppendPayloads(await japaneseTransport.sent)
        XCTAssertTrue(japaneseBeforeDetection.isEmpty)
        XCTAssertTrue(englishBeforeDetection.isEmpty)
        XCTAssertEqual(sourceAppends, [frameA.base64EncodedString(), frameB.base64EncodedString()])
        XCTAssertTrue(englishAppends.isEmpty)
        XCTAssertEqual(japaneseAppends, sourceAppends)
    }

    func testJapaneseDetectionFlushesPrerollToEnglishOnly() async throws {
        // Given: 未判定中にframeを保持しているdual client
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let frameA = Data(repeating: 0x33, count: PCM16FramePacketizer.bytesPerFrame)
        let frameB = Data(repeating: 0x44, count: PCM16FramePacketizer.bytesPerFrame)
        try await dual.appendAudioFrame(frameA)

        // When: 日本語発話と判定後に次frameを送る
        try await dual.setSpokenLanguage(.japanese)
        try await dual.appendAudioFrame(frameB)
        try await waitUntilAppendCount(englishTransport, minimum: 2)

        // Then: prerollと後続frameは英語targetだけへ送られる
        let englishAppends = try decodeAppendPayloads(await englishTransport.sent)
        let japaneseAppends = try decodeAppendPayloads(await japaneseTransport.sent)
        XCTAssertEqual(
            englishAppends,
            [frameA.base64EncodedString(), frameB.base64EncodedString()]
        )
        XCTAssertTrue(japaneseAppends.isEmpty)
    }

    func testLanguageSwitchFlushesRollingPrerollToNewTarget() async throws {
        // Given: 日本語判定後にframeを送っているdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let frameA = Data(repeating: 0x11, count: PCM16FramePacketizer.bytesPerFrame)
        let frameB = Data(repeating: 0x22, count: PCM16FramePacketizer.bytesPerFrame)
        let frameC = Data(repeating: 0x33, count: PCM16FramePacketizer.bytesPerFrame)
        try await dual.appendAudioFrame(frameA)
        try await dual.setSpokenLanguage(.japanese)
        try await dual.appendAudioFrame(frameB)
        try await waitUntilAppendCount(englishTransport, minimum: 2)

        // When: 英語へ切り替えて次frameを送る
        try await dual.setSpokenLanguage(.english)
        try await dual.appendAudioFrame(frameC)
        try await waitUntilAppendCount(japaneseTransport, minimum: 3)

        // Then: rolling prerollが和訳targetへflushされ、切替後は英語targetへ送らない
        let englishAppends = try decodeAppendPayloads(await englishTransport.sent)
        let japaneseAppends = try decodeAppendPayloads(await japaneseTransport.sent)
        XCTAssertEqual(
            englishAppends,
            [frameA.base64EncodedString(), frameB.base64EncodedString()]
        )
        XCTAssertEqual(
            japaneseAppends,
            [
                frameA.base64EncodedString(),
                frameB.base64EncodedString(),
                frameC.base64EncodedString(),
            ]
        )
    }

    func testSourceAppendContinuesWhenTranslationSendHangs() async throws {
        // Given: 英語targetの送信が長時間停滞するdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let frameA = Data(repeating: 0x55, count: PCM16FramePacketizer.bytesPerFrame)
        let frameB = Data(repeating: 0x66, count: PCM16FramePacketizer.bytesPerFrame)
        let frameC = Data(repeating: 0x77, count: PCM16FramePacketizer.bytesPerFrame)
        try await dual.appendAudioFrame(frameA)
        try await dual.setSpokenLanguage(.japanese)
        await englishTransport.setSendHangNanoseconds(2_000_000_000)

        // When: 翻訳停滞中でも原文へ連続送信できる
        try await dual.appendAudioFrame(frameB)
        try await dual.appendAudioFrame(frameC)

        // Then: 原文側は翻訳停滞を待たず3frameを受け取る
        let sourceAppends = try decodeAppendPayloads(await sourceTransport.sent)
        XCTAssertEqual(
            sourceAppends,
            [
                frameA.base64EncodedString(),
                frameB.base64EncodedString(),
                frameC.base64EncodedString(),
            ]
        )
        await dual.forceClose()
    }

    func testDedicatedSourceConfigRequestsLiveTranscription() async throws {
        // Given
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )

        // When
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )

        // Then: 専用原文接続だけがgpt-live-transcribeを要求する
        let sourceUpdate = try await firstSessionUpdate(sourceTransport)
        let englishUpdate = try await firstSessionUpdate(englishTransport)
        let japaneseUpdate = try await firstSessionUpdate(japaneseTransport)
        let sourceInput = try XCTUnwrap(
            ((sourceUpdate["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let englishInput = try XCTUnwrap(
            ((englishUpdate["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let japaneseInput = try XCTUnwrap(
            ((japaneseUpdate["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let sourceTranscription = try XCTUnwrap(
            sourceInput["transcription"] as? [String: Any]
        )
        let sourceNoiseReduction = try XCTUnwrap(
            sourceInput["noise_reduction"] as? [String: Any]
        )
        XCTAssertEqual(sourceTranscription["model"] as? String, "gpt-live-transcribe")
        XCTAssertEqual(sourceTranscription["delay"] as? String, "low")
        XCTAssertEqual(
            sourceTranscription["prompt"] as? String,
            RealtimeSessionTuning.defaultPrompt
        )
        XCTAssertEqual(
            sourceTranscription["keywords"] as? [String],
            RealtimeSessionTuning.defaultKeywords
        )
        XCTAssertEqual(sourceNoiseReduction["type"] as? String, "far_field")
        XCTAssertNil(englishInput["transcription"])
        XCTAssertNil(japaneseInput["transcription"])
    }

    func testStartAppliesCustomTuningToSourceAndTranslationSessions() async throws {
        // Given: near_fieldとカスタムprompt/keywordsのtuning
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let tuning = RealtimeSessionTuning(
            noiseReduction: .nearField,
            transcriptionDelay: .high,
            transcriptionPrompt: "Custom domain glossary hints",
            transcriptionKeywords: ["固有名詞", "Acme"]
        )

        // When: custom tuningで開始する
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport,
            tuning: tuning
        )

        // Then: 原文・翻訳双方のsession.updateへ反映される
        let sourceUpdate = try await firstSessionUpdate(sourceTransport)
        let englishUpdate = try await firstSessionUpdate(englishTransport)
        let japaneseUpdate = try await firstSessionUpdate(japaneseTransport)
        let sourceInput = try XCTUnwrap(
            ((sourceUpdate["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let englishInput = try XCTUnwrap(
            ((englishUpdate["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let japaneseInput = try XCTUnwrap(
            ((japaneseUpdate["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let sourceTranscription = try XCTUnwrap(sourceInput["transcription"] as? [String: Any])
        XCTAssertEqual(sourceTranscription["prompt"] as? String, tuning.transcriptionPrompt)
        XCTAssertEqual(
            sourceTranscription["keywords"] as? [String],
            tuning.transcriptionKeywords
        )
        XCTAssertEqual(sourceTranscription["delay"] as? String, "high")
        XCTAssertEqual(
            (sourceInput["noise_reduction"] as? [String: Any])?["type"] as? String,
            "near_field"
        )
        XCTAssertEqual(
            (englishInput["noise_reduction"] as? [String: Any])?["type"] as? String,
            "near_field"
        )
        XCTAssertEqual(
            (japaneseInput["noise_reduction"] as? [String: Any])?["type"] as? String,
            "near_field"
        )
        await dual.forceClose()
    }

    func testRollingPrerollKeepsAtMostFortyFrames() async throws {
        // Given: readyなdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )

        // When: 45 frame送ったあと英語判定する
        var frames: [Data] = []
        for index in 0..<45 {
            let frame = Data(repeating: UInt8(index % 250), count: PCM16FramePacketizer.bytesPerFrame)
            frames.append(frame)
            try await dual.appendAudioFrame(frame)
        }
        try await dual.setSpokenLanguage(.japanese)
        try await waitUntilAppendCount(englishTransport, minimum: 40)

        // Then: 直近40 frameだけが英語targetへflushされる
        let englishAppends = try decodeAppendPayloads(await englishTransport.sent)
        let expected = frames.suffix(40).map { $0.base64EncodedString() }
        XCTAssertEqual(englishAppends, Array(expected))
        await dual.forceClose()
    }

    func testUpdateTranscriptionTuningSendsSecondSessionUpdate() async throws {
        // Given: readyなdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let sentBefore = await sourceTransport.sent.count

        // When: 録音中に新しいprompt/keywordsでupdateする
        let updated = RealtimeSessionTuning(
            noiseReduction: .farField,
            transcriptionDelay: .medium,
            transcriptionPrompt: "Live glossary update",
            transcriptionKeywords: ["Acme", "ロードマップ"]
        )
        try await dual.updateTranscriptionTuning(updated)
        try await waitUntilSent(sourceTransport, minimum: sentBefore + 1)

        // Then: 2通目のsession.updateに新値が載る
        let updates = try await sessionUpdates(from: sourceTransport)
        XCTAssertGreaterThanOrEqual(updates.count, 2)
        let second = try XCTUnwrap(updates.last)
        let transcription = try XCTUnwrap(
            ((second["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )["transcription"] as? [String: Any]
        let body = try XCTUnwrap(transcription)
        XCTAssertEqual(body["prompt"] as? String, "Live glossary update")
        XCTAssertEqual(body["keywords"] as? [String], ["Acme", "ロードマップ"])
        XCTAssertEqual(body["delay"] as? String, "medium")
        await dual.forceClose()
    }

    func testUpdateTranscriptionTuningPreservesConnectedNoiseReduction() async throws {
        // Given: far_fieldで開始したdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport,
            tuning: RealtimeSessionTuning(
                noiseReduction: .farField,
                transcriptionDelay: .low,
                transcriptionPrompt: RealtimeSessionTuning.defaultPrompt,
                transcriptionKeywords: RealtimeSessionTuning.defaultKeywords
            )
        )
        let sentBefore = await sourceTransport.sent.count

        // When: 設定側がnear_fieldに変わったtuningでlive updateする
        try await dual.updateTranscriptionTuning(
            RealtimeSessionTuning(
                noiseReduction: .nearField,
                transcriptionDelay: .high,
                transcriptionPrompt: "Keep noise reduction pinned",
                transcriptionKeywords: ["Acme"]
            )
        )
        try await waitUntilSent(sourceTransport, minimum: sentBefore + 1)

        // Then: prompt/delayは更新され、noise_reductionは接続時のfar_fieldのまま
        let updates = try await sessionUpdates(from: sourceTransport)
        let second = try XCTUnwrap(updates.last)
        let input = try XCTUnwrap(
            ((second["session"] as? [String: Any])?["audio"] as? [String: Any])?["input"]
                as? [String: Any]
        )
        let transcription = try XCTUnwrap(input["transcription"] as? [String: Any])
        XCTAssertEqual(transcription["prompt"] as? String, "Keep noise reduction pinned")
        XCTAssertEqual(transcription["delay"] as? String, "high")
        XCTAssertEqual(
            (input["noise_reduction"] as? [String: Any])?["type"] as? String,
            "far_field"
        )
        await dual.forceClose()
    }

    func testTranslationAppendFailuresEmitSingleTransportErrorWithoutPumpRestart() async throws {
        // Given: 翻訳laneだけ送信失敗するdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await dual.setSpokenLanguage(.japanese)

        let stream = await dual.events
        let firstError = expectation(description: "first transport error")
        let secondError = expectation(description: "unexpected second transport error")
        secondError.isInverted = true
        let collector = Task {
            var transportErrors = 0
            for await event in stream {
                if case .error(_, let code) = event.event, code == "transport" {
                    transportErrors += 1
                    if transportErrors == 1 {
                        firstError.fulfill()
                    } else if transportErrors == 2 {
                        secondError.fulfill()
                    }
                }
            }
            return transportErrors
        }

        // When: 連続失敗でtransport errorを出し、その後もfeedが続く
        await englishTransport.setSendError(
            RealtimeTranslationError.recoverableTransportFailure("translation send failed")
        )
        let frame = Data(repeating: 0x55, count: PCM16FramePacketizer.bytesPerFrame)
        for _ in 0..<3 {
            try await dual.appendAudioFrame(frame)
        }
        await fulfillment(of: [firstError], timeout: 1.0)

        // ポンプ停止後の追加appendでもenqueue経由の再起動が起きないこと
        for _ in 0..<4 {
            try await dual.appendAudioFrame(frame)
        }

        // Then: transport errorは1回だけ。dying socketへの追加送信もない
        await fulfillment(of: [secondError], timeout: 0.3)
        collector.cancel()
        let transportErrors = await collector.value
        let englishAppends = try decodeAppendPayloads(await englishTransport.sent)
        XCTAssertEqual(transportErrors, 1)
        XCTAssertEqual(englishAppends.count, 0)
        await dual.forceClose()
    }

    func testOneSidedFailureForceClosesPair() async throws {
        // Given: readyなdual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        let epochBefore = await dual.connectionEpoch

        // When: 原文接続で送信失敗させる
        await sourceTransport.setSendError(RealtimeTranslationError.recoverableTransportFailure("boom"))
        do {
            try await dual.appendAudioFrame(Data(count: PCM16FramePacketizer.bytesPerFrame))
            XCTFail("Expected append failure")
        } catch {
            await dual.forceClose()
        }

        // Then: epochが進み全接続が閉じる
        let epochAfter = await dual.connectionEpoch
        let sourceClose = await sourceTransport.closeCount
        let englishClose = await englishTransport.closeCount
        let japaneseClose = await japaneseTransport.closeCount
        XCTAssertGreaterThan(epochAfter, epochBefore)
        XCTAssertGreaterThanOrEqual(sourceClose, 1)
        XCTAssertGreaterThanOrEqual(englishClose, 1)
        XCTAssertGreaterThanOrEqual(japaneseClose, 1)
    }

    func testCloseGracefullyReturnsDrainEventsCapturedBeforeWait() async throws {
        // Given: consumer 相当が止まり、stop drain 武装済みの dual
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        await dual.beginStopDrainCapture()

        // When: translation pump drain / session.close 前に最終原文が届き、close は timeout する
        try await sourceTransport.enqueueJSON([
            "type": "conversation.item.input_audio_transcription.delta",
            "item_id": "close-drain-1",
            "delta": "drain前の原文",
        ])
        // merge が stopDrainBuffer へ載るまで短い間を空ける（completed は送らず closeTimeout へ）
        try await Task.sleep(nanoseconds: 50_000_000)
        let drained = await dual.closeGracefully()

        // Then: close 失敗経路でも武装後の delta を返す
        let sourceTexts = drained.compactMap { event -> String? in
            guard case .inputTranscriptDelta(let delta, _, _) = event.event else { return nil }
            return delta
        }
        XCTAssertTrue(sourceTexts.contains("drain前の原文"))
    }

    func testStopDrainIgnoresOutputAudioAndKeepsTranscript() async throws {
        // Given: stop drain 武装済み。Windows DropOldest 相当の洪水でも訳文を守る
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        await dual.beginStopDrainCapture()

        // When: output_audio.delta 洪水のあと訳文 delta が届く
        for _ in 0..<300 {
            try await englishTransport.enqueueJSON([
                "type": "session.output_audio.delta",
                "delta": "AAAA",
            ])
        }
        try await englishTransport.enqueueJSON([
            "type": "session.output_transcript.delta",
            "delta": "drain survivor",
            "event_id": "drain-1",
        ])
        try await Task.sleep(nanoseconds: 80_000_000)
        let drained = await dual.closeGracefully()

        // Then: 音声は drain に載らず、訳文だけが残る
        XCTAssertFalse(drained.contains { event in
            if case .outputAudioDelta = event.event { return true }
            return false
        })
        let translations = drained.compactMap { event -> String? in
            guard case .outputTranscriptDelta(let delta, _, _) = event.event else { return nil }
            return delta
        }
        XCTAssertTrue(translations.contains("drain survivor"))
    }

    func testWaitForTranslationDrainTimesOutWhenSendStalls() async throws {
        // Given: 翻訳送信が完了しないdual（言語判定済みで翻訳laneへ流れる）
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await dual.setSpokenLanguage(.japanese)
        await englishTransport.setSendHangNanoseconds(30_000_000_000)
        let frame = Data(repeating: 0x11, count: PCM16FramePacketizer.bytesPerFrame)
        try await dual.appendAudioFrame(frame)

        // When: 短いtimeoutでdrainを待つ
        // Then: ポンプ待ちでハングせず、timeoutエラーになる
        do {
            try await dual.waitForTranslationDrain(timeoutNanoseconds: 50_000_000)
            XCTFail("Expected translation pump drain timeout")
        } catch let error as RealtimeTranslationError {
            XCTAssertEqual(
                error,
                .recoverableTransportFailure("translation pump drain timeout")
            )
        }
        await dual.forceClose()
    }

    func testCloseGracefullyScalesDrainTimeoutWithPendingFrames() async throws {
        // Given: base drain timeout だけでは送り切れない pending と緩い送信遅延
        let sourceTransport = FakeRealtimeWebSocketTransport()
        let englishTransport = FakeRealtimeWebSocketTransport()
        let japaneseTransport = FakeRealtimeWebSocketTransport()
        await sourceTransport.setAutoCloseResponses(true)
        await englishTransport.setAutoCloseResponses(true)
        await japaneseTransport.setAutoCloseResponses(true)
        let dual = makeDual(
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport,
            translationDrainTimeoutNanoseconds: 200_000_000,
            closeTimeoutNanoseconds: 500_000_000
        )
        try await startDual(
            dual,
            sourceTransport: sourceTransport,
            englishTransport: englishTransport,
            japaneseTransport: japaneseTransport
        )
        try await dual.setSpokenLanguage(.japanese)
        await englishTransport.setAudioAppendHangNanoseconds(80_000_000)
        for index in 0..<6 {
            let frame = Data(repeating: UInt8(0x40 + index), count: PCM16FramePacketizer.bytesPerFrame)
            try await dual.appendAudioFrame(frame)
        }

        // When: CloseGracefully（pending 比例で予算が伸びる）
        _ = await dual.closeGracefully()

        // Then: session.close より前に全 frame が翻訳 lane へ届く
        let appends = try decodeAppendPayloads(await englishTransport.sent)
        XCTAssertEqual(appends.count, 6)
        let types = try decodeSentTypes(await englishTransport.sent)
        let lastAppend = types.lastIndex(of: "session.input_audio_buffer.append")
        let closeIndex = types.lastIndex(of: "session.close")
        XCTAssertNotNil(lastAppend)
        XCTAssertNotNil(closeIndex)
        XCTAssertGreaterThan(closeIndex!, lastAppend!)
    }

    func testResolveTranslationDrainTimeoutScalesAndCaps() {
        // Given/When/Then: base を下限、cap を上限、pending 比例の加算
        let base = DualRealtimeTranslationClient.defaultTranslationDrainTimeoutNanoseconds
        XCTAssertEqual(
            DualRealtimeTranslationClient.resolveTranslationDrainTimeoutNanoseconds(
                baseNanoseconds: base,
                pendingFrameCount: 0
            ),
            base
        )
        XCTAssertEqual(
            DualRealtimeTranslationClient.resolveTranslationDrainTimeoutNanoseconds(
                baseNanoseconds: base,
                pendingFrameCount: 40
            ),
            base + (40 * DualRealtimeTranslationClient.translationDrainTimeoutNanosecondsPerPendingFrame)
        )
        XCTAssertEqual(
            DualRealtimeTranslationClient.resolveTranslationDrainTimeoutNanoseconds(
                baseNanoseconds: base,
                pendingFrameCount: 200
            ),
            DualRealtimeTranslationClient.translationDrainTimeoutCapNanoseconds
        )
        XCTAssertEqual(
            DualRealtimeTranslationClient.resolveTranslationDrainTimeoutNanoseconds(
                baseNanoseconds: 50_000_000,
                pendingFrameCount: 0
            ),
            50_000_000
        )
    }

    private func makeDual(
        sourceTransport: FakeRealtimeWebSocketTransport,
        englishTransport: FakeRealtimeWebSocketTransport,
        japaneseTransport: FakeRealtimeWebSocketTransport,
        translationDrainTimeoutNanoseconds: UInt64 = DualRealtimeTranslationClient
            .defaultTranslationDrainTimeoutNanoseconds,
        closeTimeoutNanoseconds: UInt64 = 500_000_000
    ) -> DualRealtimeTranslationClient {
        DualRealtimeTranslationClient(
            sourceConnection: RealtimeSourceTranscriptionConnection(
                transport: sourceTransport,
                safetyIdentifier: "test-safety",
                handshakeTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: closeTimeoutNanoseconds
            ),
            englishConnection: RealtimeTranslationConnection(
                target: .english,
                transport: englishTransport,
                safetyIdentifier: "test-safety",
                sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: closeTimeoutNanoseconds
            ),
            japaneseConnection: RealtimeTranslationConnection(
                target: .japanese,
                transport: japaneseTransport,
                safetyIdentifier: "test-safety",
                sessionUpdateTimeoutNanoseconds: 1_000_000_000,
                closeTimeoutNanoseconds: closeTimeoutNanoseconds
            ),
            translationDrainTimeoutNanoseconds: translationDrainTimeoutNanoseconds
        )
    }

    private func startDual(
        _ dual: DualRealtimeTranslationClient,
        sourceTransport: FakeRealtimeWebSocketTransport,
        englishTransport: FakeRealtimeWebSocketTransport,
        japaneseTransport: FakeRealtimeWebSocketTransport,
        tuning: RealtimeSessionTuning = .default
    ) async throws {
        try await sourceTransport.enqueueJSON(["type": "session.created"])
        try await englishTransport.enqueueJSON(["type": "session.created"])
        try await japaneseTransport.enqueueJSON(["type": "session.created"])

        let startTask = Task {
            try await dual.start(apiKey: "sk-test", tuning: tuning)
        }

        try await waitUntilSent(sourceTransport, minimum: 1)
        try await waitUntilSent(englishTransport, minimum: 1)
        try await waitUntilSent(japaneseTransport, minimum: 1)
        try await sourceTransport.enqueueJSON(["type": "session.updated"])
        try await englishTransport.enqueueJSON(["type": "session.updated"])
        try await japaneseTransport.enqueueJSON(["type": "session.updated"])
        try await startTask.value
    }

    private func decodeSentTypes(_ sent: [Data]) throws -> [String] {
        try sent.compactMap { data in
            let object = try XCTUnwrap(
                JSONSerialization.jsonObject(with: data) as? [String: Any]
            )
            return object["type"] as? String
        }
    }

    private func decodeAppendPayloads(_ sent: [Data]) throws -> [String] {
        try sent.compactMap { data in
            let object = try XCTUnwrap(
                JSONSerialization.jsonObject(with: data) as? [String: Any]
            )
            guard let type = object["type"] as? String,
                type == "session.input_audio_buffer.append"
                    || type == "input_audio_buffer.append"
            else {
                return nil
            }
            return object["audio"] as? String
        }
    }

    private func firstSessionUpdate(
        _ transport: FakeRealtimeWebSocketTransport
    ) async throws -> [String: Any] {
        let updates = try await sessionUpdates(from: transport)
        let first = try XCTUnwrap(updates.first)
        return first
    }

    private func sessionUpdates(
        from transport: FakeRealtimeWebSocketTransport
    ) async throws -> [[String: Any]] {
        let sent = await transport.sent
        return try sent.compactMap { data in
            let object = try XCTUnwrap(
                JSONSerialization.jsonObject(with: data) as? [String: Any]
            )
            guard object["type"] as? String == "session.update" else { return nil }
            return object
        }
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

    private func waitUntilAppendCount(
        _ transport: FakeRealtimeWebSocketTransport,
        minimum: Int,
        timeout: TimeInterval = 1.0
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let appends = try decodeAppendPayloads(await transport.sent)
            if appends.count >= minimum { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTFail("Timed out waiting for append count \(minimum)")
    }
}

extension FakeRealtimeWebSocketTransport {
    func setSendError(_ error: Error?) {
        sendError = error
    }

    func setSendHangNanoseconds(_ nanoseconds: UInt64) {
        sendHangNanoseconds = nanoseconds
    }

    func setAudioAppendHangNanoseconds(_ nanoseconds: UInt64) {
        audioAppendHangNanoseconds = nanoseconds
    }

    func setAutoCloseResponses(_ enabled: Bool) {
        autoCloseResponses = enabled
    }
}
