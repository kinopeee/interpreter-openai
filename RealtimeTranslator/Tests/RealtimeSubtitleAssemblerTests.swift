import XCTest
@testable import RealtimeTranslator

final class RealtimeSubtitleAssemblerTests: XCTestCase {
    func testJapaneseToEnglishLaneSelection() {
        // Given: 新しいepochのassembler
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)

        // When: 原文と英訳だけが来る
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 1)))
        let update = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 2))
        )

        // Then: 英訳laneが選ばれ表示される
        XCTAssertEqual(update?.sourceText, "こんにちは")
        XCTAssertEqual(update?.translatedText, "Hello")
        XCTAssertEqual(update?.isTranslationCurrent, true)
    }

    func testEnglishToJapaneseLaneSelection() {
        // Given
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)

        // When: 原文と和訳だけが来る
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "Hello there", eventID: "s1", elapsedMs: 1)))
        let update = assembler.ingest(
            event(.japanese, .outputTranscriptDelta(delta: "こんにちは", eventID: "t1", elapsedMs: 2))
        )

        // Then: 和訳laneが選ばれる
        XCTAssertEqual(update?.translatedText, "こんにちは")
        XCTAssertEqual(update?.isTranslationCurrent, true)
    }

    func testFirstOutputLaneWinsWhenBothEventuallyEmit() {
        // Given: 原文がambiguous Latin
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "Tokyo", eventID: "s1", elapsedMs: 1)))

        // When: 英訳が先に出て、後から和訳も来る
        _ = assembler.ingest(event(.english, .outputTranscriptDelta(delta: "Tokyo", eventID: "e1", elapsedMs: 2)))
        let update = assembler.ingest(
            event(.japanese, .outputTranscriptDelta(delta: "東京", eventID: "j1", elapsedMs: 2))
        )

        // Then: 先に出力したlaneを固定し、後着の非選択laneは表示に混ぜない
        XCTAssertEqual(update?.translatedText, "Tokyo")
        XCTAssertEqual(update?.isTranslationCurrent, true)
    }

    func testSelectedLaneSilenceInMixedSpeechStillDisplaysSource() {
        // Given: 英訳lane選択済み
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "今日は", eventID: "s1", elapsedMs: 1)))
        _ = assembler.ingest(event(.english, .outputTranscriptDelta(delta: "Today", eventID: "t1", elapsedMs: 2)))

        // When: 英語断片で英訳laneが沈黙し、和訳だけ増える
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: " meeting", eventID: "s2", elapsedMs: 3)))
        let update = assembler.ingest(
            event(.japanese, .outputTranscriptDelta(delta: "会議", eventID: "j1", elapsedMs: 4))
        )

        // Then: 選択laneの訳は維持され、非選択laneは表示に混ざらない
        XCTAssertEqual(update?.sourceText, "今日は meeting")
        XCTAssertEqual(update?.translatedText, "Today")
        XCTAssertEqual(update?.isTranslationCurrent, false)
        XCTAssertEqual(update?.shouldFinalize, false)
    }

    func testDuplicateEventIDAndStaleEpochAreIgnored() {
        // Given
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(2)
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "A", eventID: "s1", elapsedMs: 1), epoch: 2))

        // When: 同じevent IDと旧epochを送る
        let duplicate = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "B", eventID: "s1", elapsedMs: 2), epoch: 2)
        )
        let stale = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "C", eventID: "s2", elapsedMs: 3), epoch: 1)
        )

        // Then: 無視される
        XCTAssertNil(duplicate)
        XCTAssertNil(stale)
    }

    func testSharedElapsedMsAllowsMultipleDeltas() {
        // Given
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)

        // When: 同じelapsed_msの複数delta
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "Hi", eventID: "s1", elapsedMs: 5)))
        let second = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: " there", eventID: "s2", elapsedMs: 5))
        )

        // Then: 両方appendされる
        XCTAssertEqual(second?.sourceText, "Hi there")
    }

    func testPunctuationDoesNotFinalizeStreamingSegment() {
        // Given: 句点付きの完全ペア
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "こんにちは。", eventID: "s1", elapsedMs: 1)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello.", eventID: "t1", elapsedMs: 2)),
            now: start
        )

        // When: 句点直後にtickする
        let update = assembler.tick(now: start.addingTimeInterval(0.5))

        // Then: APIは句点後もdeltaを継続するため確定しない
        XCTAssertNil(update)
    }

    func testLateTranslationAfterFinalizeIsIgnoredUntilNextSource() {
        // Given: 十分なidleで完全ペアを確定したassembler
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "こんにちは。", eventID: "s1", elapsedMs: 1)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello.", eventID: "t1", elapsedMs: 2)),
            now: start
        )
        let finalized = assembler.tick(now: start.addingTimeInterval(8.1))
        XCTAssertEqual(finalized?.shouldFinalize, true)

        // When: 確定済みsegmentの遅延訳文が届く
        let lateUpdate = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: " Late", eventID: "t2", elapsedMs: 3)),
            now: start.addingTimeInterval(8.2)
        )

        // Then: 原文なしの次segmentとして表示しない
        XCTAssertNil(lateUpdate)
    }

    func testIdleFinalize() {
        // Given: 句点なしの完全ペア
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "hello world", eventID: "s1", elapsedMs: 1)),
            now: start
        )
        _ = assembler.ingest(
            event(.japanese, .outputTranscriptDelta(delta: "こんにちは世界", eventID: "t1", elapsedMs: 2)),
            now: start
        )

        // When: APIで観測した5秒の文中pauseと、8秒超のidleでtick
        let duringTranslationPause = assembler.tick(
            now: start.addingTimeInterval(5.5)
        )
        let finalized = assembler.tick(now: start.addingTimeInterval(8.1))

        // Then: 文中pauseでは維持し、十分なidle後だけ確定する
        XCTAssertNil(duringTranslationPause)
        XCTAssertEqual(finalized?.shouldFinalize, true)
    }

    func testFinalizeForLanguageSwitchWithCompletePair() {
        // Given: 完全ペアがあるassembler
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 1)))
        _ = assembler.ingest(event(.english, .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 2)))

        // When: 言語切替で確定する
        let finalized = assembler.finalizeForLanguageSwitch()

        // Then: 完全ペアが確定する
        XCTAssertEqual(finalized?.shouldFinalize, true)
        XCTAssertEqual(finalized?.sourceText, "こんにちは")
        XCTAssertEqual(finalized?.translatedText, "Hello")
    }

    func testFinalizeForLanguageSwitchWithoutPairClearsBuffers() {
        // Given: 原文だけのassembler
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 1)))

        // When: 言語切替する
        let finalized = assembler.finalizeForLanguageSwitch()
        let next = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "Hello there", eventID: "s2", elapsedMs: 2))
        )

        // Then: 原文だけの確定はせず、次の原文が新segmentになる
        XCTAssertNil(finalized)
        XCTAssertEqual(next?.sourceText, "Hello there")
        XCTAssertEqual(next?.segmentGeneration, 1)
    }

    func testExpectedLaneIgnoresEchoFromOtherLane() {
        // Given: 英語発話なので和訳laneを期待しているassembler
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.expectLane(.japanese)
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "Hello there", eventID: "s1", elapsedMs: 1))
        )

        // When: 旧英訳laneから同言語echoが先に届き、後から和訳が来る
        let echo = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello there", eventID: "e1", elapsedMs: 2))
        )
        let update = assembler.ingest(
            event(.japanese, .outputTranscriptDelta(delta: "こんにちは", eventID: "j1", elapsedMs: 3))
        )

        // Then: echoではlaneを確定せず、期待laneの和訳を表示する
        XCTAssertEqual(echo?.translatedText, "")
        XCTAssertEqual(update?.translatedText, "こんにちは")
        XCTAssertEqual(update?.isTranslationCurrent, true)
    }

    func testIdleTickDoesNotFinalizeStaleTranslationAfterSourceContinues() {
        // Given: 訳文が付いたあとに原文だけが伸びたセグメント
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.expectLane(.english)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 100)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 200)),
            now: start
        )

        // When: 原文だけ継続し、同じ期待 lane を再指定してから idle tick する
        let continued = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "、皆さん", eventID: "s2", elapsedMs: 300)),
            now: start.addingTimeInterval(0.4)
        )
        assembler.expectLane(.english)
        let idle = assembler.tick(now: start.addingTimeInterval(9))

        // Then: 同じ期待 lane の再指定だけでは旧訳文を現行に戻して確定しない
        XCTAssertEqual(continued?.sourceText, "こんにちは、皆さん")
        XCTAssertEqual(continued?.translatedText, "Hello")
        XCTAssertEqual(continued?.isTranslationCurrent, false)
        XCTAssertNil(idle)
    }

    func testIdleTickAbandonsStaleTranslationSoNextSourceStartsFresh() {
        // Given: 訳文が stale のまま idle したセグメント
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.expectLane(.english)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 100)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 200)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "、皆さん", eventID: "s2", elapsedMs: 300)),
            now: start.addingTimeInterval(0.4)
        )
        XCTAssertNil(assembler.tick(now: start.addingTimeInterval(9)))

        // When: 次の発話の原文が届く
        let next = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "ありがとう", eventID: "s3", elapsedMs: 400)),
            now: start.addingTimeInterval(9.2)
        )

        // Then: 前の原文へ連結せず、新セグメントとして表示する
        XCTAssertEqual(next?.sourceText, "ありがとう")
        XCTAssertEqual(next?.translatedText, "")
        XCTAssertEqual(next?.isTranslationCurrent, false)
        XCTAssertEqual(next?.shouldFinalize, false)
        XCTAssertEqual(next?.segmentGeneration, 1)
    }

    func testLateTranslationAfterStaleIdleAbandonIsIgnoredByCutoff() {
        // Given: stale idle で境界だけ進めたあと、次の原文が始まっている
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.expectLane(.english)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 100)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 200)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "、皆さん", eventID: "s2", elapsedMs: 300)),
            now: start.addingTimeInterval(0.4)
        )
        XCTAssertNil(assembler.tick(now: start.addingTimeInterval(9)))
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "ありがとう", eventID: "s3", elapsedMs: nil)),
            now: start.addingTimeInterval(9.2)
        )

        // When: idle-finalize と同じ帯の追いつき訳と、seen+idle 境界の次発話訳が届く
        let late = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: " Late", eventID: "t-late", elapsedMs: 200)),
            now: start.addingTimeInterval(9.3)
        )
        let sameRangeAsIdleFinalizeFresh = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: " everyone", eventID: "t-catchup", elapsedMs: 400)),
            now: start.addingTimeInterval(9.35)
        )
        let catchUp = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: " all", eventID: "t-catchup-late", elapsedMs: 450)),
            now: start.addingTimeInterval(9.36)
        )
        let fresh = assembler.ingest(
            event(
                .english,
                .outputTranscriptDelta(delta: "Thank you", eventID: "t-new", elapsedMs: 8_200)
            ),
            now: start.addingTimeInterval(9.4)
        )

        // Then: 400/450 は旧発話の追いつきとして捨て、seen+idle の訳だけを現行にする
        XCTAssertNil(late)
        XCTAssertNil(sameRangeAsIdleFinalizeFresh)
        XCTAssertNil(catchUp)
        XCTAssertEqual(fresh?.translatedText, "Thank you")
        XCTAssertEqual(fresh?.isTranslationCurrent, true)
    }

    func testExpectLaneOverridesFirstOutputEchoLock() {
        // Given: 期待 lane がまだ無く、同言語 echo が first-output で lock した
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        _ = assembler.ingest(event(.english, .inputTranscriptDelta(delta: "Tokyo", eventID: "s1", elapsedMs: 100)))
        let echo = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Tokyo", eventID: "echo", elapsedMs: 150))
        )

        // When: ExpectLane で本命 lane を指定し、本命の訳文が来る
        assembler.expectLane(.japanese)
        let update = assembler.ingest(
            event(.japanese, .outputTranscriptDelta(delta: "東京", eventID: "ja", elapsedMs: 200))
        )

        // Then: echo では lane を固定せず、期待 lane の訳文を表示する
        XCTAssertEqual(echo?.translatedText, "Tokyo")
        XCTAssertEqual(update?.translatedText, "東京")
        XCTAssertEqual(update?.isTranslationCurrent, true)
    }

    func testLateTranslationAfterNextSourceIsIgnoredByFinalizedCutoff() {
        // Given: idle 確定したセグメントのあと次の原文が始まっている
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.expectLane(.english)
        let start = Date()
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "こんにちは", eventID: "s1", elapsedMs: 100)),
            now: start
        )
        _ = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Hello", eventID: "t1", elapsedMs: 200)),
            now: start
        )
        let finalized = assembler.tick(now: start.addingTimeInterval(9))
        assembler.expectLane(.english)
        _ = assembler.ingest(
            event(.english, .inputTranscriptDelta(delta: "ありがとう", eventID: "s2", elapsedMs: nil)),
            now: start.addingTimeInterval(9.2)
        )

        // When: 確定済みより古い elapsed の訳文と、新しい訳文が届く
        let late = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: " Late", eventID: "t-late", elapsedMs: 200)),
            now: start.addingTimeInterval(9.3)
        )
        let fresh = assembler.ingest(
            event(.english, .outputTranscriptDelta(delta: "Thank you", eventID: "t-new", elapsedMs: 400)),
            now: start.addingTimeInterval(9.4)
        )

        // Then: 古い訳文は次発話に混ぜず、新しい訳文だけを現行にする
        XCTAssertEqual(finalized?.shouldFinalize, true)
        XCTAssertNil(late)
        XCTAssertEqual(fresh?.translatedText, "Thank you")
        XCTAssertEqual(fresh?.isTranslationCurrent, true)
    }

    private func event(
        _ target: RealtimeTranslationOutputLanguage,
        _ serverEvent: RealtimeTranslationServerEvent,
        epoch: Int = 1
    ) -> RealtimeTranslationStreamEvent {
        let lane: RealtimeTranslationLane
        if case .inputTranscriptDelta = serverEvent {
            lane = .source
        } else {
            lane = .translation(target)
        }
        return RealtimeTranslationStreamEvent(lane: lane, event: serverEvent, epoch: epoch)
    }
}
