import XCTest
@testable import RealtimeTranslator

final class RealtimeSubtitleAssemblerAudioLossTests: XCTestCase {
    private let origin = Date(timeIntervalSince1970: 1_700_000_000)

    func testTaintedSegmentIsNotFinalizedAfterIdle() {
        // Given: 音声欠落を検知して汚染窓を開始する
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.markAudioLoss(now: origin)
        _ = assembler.ingest(source("こんにちは", "s1"), now: origin.addingTimeInterval(1))
        _ = assembler.ingest(translation("Hello", "t1"), now: origin.addingTimeInterval(1))

        // When: idle finalize を越えて評価する
        let update = assembler.tick(now: origin.addingTimeInterval(10))

        // Then: 確定せず、汚染バッファが破棄される
        XCTAssertNil(update)
        XCTAssertEqual(assembler.currentSourceText, "")
        XCTAssertFalse(assembler.isCurrentSegmentTainted)
    }

    func testLossOlderThanWindowAllowsNormalFinalize() {
        // Given: 欠落から8秒を超えて新しいsegmentを開始する
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.markAudioLoss(now: origin)
        _ = assembler.ingest(source("こんにちは", "s1"), now: origin.addingTimeInterval(9))
        _ = assembler.ingest(translation("Hello", "t1"), now: origin.addingTimeInterval(9))

        // When: idle finalize を評価する
        let update = assembler.tick(now: origin.addingTimeInterval(17))

        // Then: 通常どおり確定する
        XCTAssertEqual(update?.shouldFinalize, true)
        XCTAssertEqual(update?.sourceText, "こんにちは")
    }

    func testFinalizedSegmentBeforeLossIsPreserved() {
        // Given: 欠落前にsegmentを確定し、その後に欠落を通知する
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        _ = assembler.ingest(source("こんにちは", "s1"), now: origin)
        _ = assembler.ingest(translation("Hello", "t1"), now: origin)
        XCTAssertEqual(assembler.tick(now: origin.addingTimeInterval(8))?.shouldFinalize, true)

        // When: 欠落を通知する
        assembler.markAudioLoss(now: origin.addingTimeInterval(9))

        // Then: 既に確定したsegmentの状態を変更しない
        XCTAssertFalse(assembler.isCurrentSegmentTainted)
        XCTAssertEqual(assembler.currentSourceText, "")
    }

    func testLanguageSwitchDoesNotFinalizeTaintedSegment() {
        // Given: 汚染されたsegmentに原文と訳文を取り込む
        var assembler = RealtimeSubtitleAssembler()
        assembler.beginNewEpoch(1)
        assembler.markAudioLoss(now: origin)
        _ = assembler.ingest(source("こんにちは。", "s1"), now: origin.addingTimeInterval(1))
        _ = assembler.ingest(translation("Hello.", "t1"), now: origin.addingTimeInterval(1))

        // When: 言語切替で分割する
        let split = assembler.splitForLanguageSwitch(at: 6, now: origin.addingTimeInterval(2))

        // Then: 汚染されたprefixは確定しない
        XCTAssertNil(split.finalized)
    }

    private func source(_ value: String, _ id: String) -> RealtimeTranslationStreamEvent {
        RealtimeTranslationStreamEvent(
            lane: .source,
            event: .inputTranscriptDelta(delta: value, eventID: id, elapsedMs: nil),
            epoch: 1
        )
    }

    private func translation(_ value: String, _ id: String) -> RealtimeTranslationStreamEvent {
        RealtimeTranslationStreamEvent(
            lane: .translation(.english),
            event: .outputTranscriptDelta(delta: value, eventID: id, elapsedMs: nil),
            epoch: 1
        )
    }
}
