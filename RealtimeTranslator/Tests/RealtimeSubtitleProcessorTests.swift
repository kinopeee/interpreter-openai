import XCTest
@testable import RealtimeTranslator

final class RealtimeSubtitleProcessorTests: XCTestCase {
    private let origin = Date(timeIntervalSince1970: 1_700_000_000)

    // Given: ja-en の epoch 1 を開始した processor
    // When: 判定前の日本語原文 delta を取り込む
    // Then: 英語 target が選択され、原文 update が返る
    func testJapaneseSourceSelectsEnglishTarget() {
        var processor = makeProcessor()

        let result = processor.process(
            source("今日は晴れです。", "s1", 1),
            now: origin
        )

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.routingAction, .select(.english))
        XCTAssertEqual(result?.isSourceUpdate, true)
        XCTAssertEqual(result?.updates.count, 1)
        XCTAssertEqual(result?.updates[0], result?.ingestedUpdate)
        XCTAssertEqual(result?.updates[0].sourceText, "今日は晴れです。")
        XCTAssertEqual(result?.updates[0].shouldFinalize, false)
        XCTAssertEqual(processor.routingSourceText, "今日は晴れです。")
    }

    // Given: 日本語原文で英語 target が選択済み
    // When: 英語の訳文 delta を取り込む
    // Then: routing は変わらず訳文 update だけが返る
    func testTranslationDeltaDoesNotChangeRouting() {
        var processor = makeProcessor()
        _ = processor.process(source("今日は晴れです。", "s1", 1), now: origin)

        let result = processor.process(
            translation(.english, "It is sunny today.", "t1", 2),
            now: origin.addingTimeInterval(0.002)
        )

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.routingAction, RealtimeSubtitleRoutingAction.none)
        XCTAssertEqual(result?.isSourceUpdate, false)
        XCTAssertEqual(result?.updates.count, 1)
        XCTAssertEqual(result?.updates[0].translatedText, "It is sunny today.")
        XCTAssertEqual(result?.updates[0].isTranslationCurrent, true)
        XCTAssertEqual(result?.updates[0].shouldFinalize, false)
    }

    // Given: 日本語原文と英訳で英語 target が選択済み
    // When: 原文が英語へ切り替わる delta を取り込む
    // Then: 確定プレフィックスと現行サフィックスを返し target を日本語へ切り替える
    func testLanguageSwitchFinalizesPrefixAndKeepsCurrentSuffix() {
        var processor = makeProcessor()
        _ = processor.process(source("今日は晴れです。", "s1", 1), now: origin)
        _ = processor.process(
            translation(.english, "It is sunny today.", "t1", 2),
            now: origin.addingTimeInterval(0.002)
        )

        // 直近16 scalar 窓に日本語が残るため、この時点では切り替わらない
        let partial = processor.process(
            source("To", "s2", 3),
            now: origin.addingTimeInterval(0.003)
        )
        XCTAssertEqual(partial?.routingAction, RealtimeSubtitleRoutingAction.none)

        let result = processor.process(
            source("day it is sunny outside", "s3", 4),
            now: origin.addingTimeInterval(0.004)
        )

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.routingAction, .switch(.japanese))
        XCTAssertEqual(result?.updates.count, 2)
        XCTAssertEqual(result?.updates[0].shouldFinalize, true)
        XCTAssertEqual(result?.updates[0].sourceText, "今日は晴れです。")
        XCTAssertEqual(result?.updates[0].translatedText, "It is sunny today.")
        XCTAssertEqual(result?.updates[1].shouldFinalize, false)
        XCTAssertEqual(result?.updates[1].sourceText, "Today it is sunny outside")
        // 切替後は RoutingSourceTextWindow が末尾16非空白 scalar に切り詰める
        XCTAssertEqual(processor.routingSourceText, "it is sunny outside")
    }

    // Given: 日本語原文で英語 target が選択済み
    // When: discardUnconfirmed を呼ぶ
    // Then: 無効化 update が返り routing がリセットされて再選択できる
    func testDiscardUnconfirmedInvalidatesAndResetsRouting() {
        var processor = makeProcessor()
        _ = processor.process(source("今日は晴れです。", "s1", 1), now: origin)

        let invalidation = processor.discardUnconfirmed()

        XCTAssertEqual(invalidation.isInvalidation, true)
        XCTAssertEqual(invalidation.sourceText, "")
        XCTAssertEqual(invalidation.translatedText, "")
        XCTAssertEqual(invalidation.shouldFinalize, false)
        XCTAssertEqual(processor.routingSourceText, "")

        // selected target がリセットされたので次の日本語原文で再選択される
        let result = processor.process(
            source("こんにちは", "s4", 5),
            now: origin.addingTimeInterval(0.005)
        )
        XCTAssertEqual(result?.routingAction, .select(.english))
    }

    // Given: epoch 1 を開始した直後
    // When: 旧 epoch の原文 delta を取り込む
    // Then: イベントは無視され state は変わらない
    func testStaleEpochEventIsIgnored() {
        var processor = makeProcessor()

        let result = processor.process(
            source("こんにちは", "s0", 1, epoch: 0),
            now: origin
        )

        XCTAssertNil(result)
        XCTAssertEqual(processor.routingSourceText, "")
        XCTAssertEqual(processor.currentSourceLength, 0)
    }

    // Given: 日本語原文で英語 target が選択済み
    // When: resetRoutingForNextSegment 後に次セグメントの原文を取り込む
    // Then: target が再選択される
    func testResetRoutingForNextSegmentAllowsReselection() {
        var processor = makeProcessor()
        _ = processor.process(source("今日は晴れです。", "s1", 1), now: origin)

        processor.resetRoutingForNextSegment()
        let result = processor.process(
            source("こんにちは", "s5", 6),
            now: origin.addingTimeInterval(0.006)
        )

        XCTAssertEqual(result?.routingAction, .select(.english))
        XCTAssertEqual(processor.routingSourceText, "こんにちは")
    }

    private func makeProcessor() -> RealtimeSubtitleProcessor {
        var processor = RealtimeSubtitleProcessor()
        processor.beginEpoch(1, pair: .jaEn)
        return processor
    }

    private func source(
        _ text: String,
        _ eventID: String,
        _ elapsedMs: Int,
        epoch: Int = 1
    ) -> RealtimeTranslationStreamEvent {
        RealtimeTranslationStreamEvent(
            target: .english,
            event: .inputTranscriptDelta(delta: text, eventID: eventID, elapsedMs: elapsedMs),
            epoch: epoch
        )
    }

    private func translation(
        _ target: RealtimeTranslationOutputLanguage,
        _ text: String,
        _ eventID: String,
        _ elapsedMs: Int
    ) -> RealtimeTranslationStreamEvent {
        RealtimeTranslationStreamEvent(
            target: target,
            event: .outputTranscriptDelta(delta: text, eventID: eventID, elapsedMs: elapsedMs),
            epoch: 1
        )
    }
}
