import XCTest
@testable import RealtimeTranslator

final class SubtitleFixtureTests: XCTestCase {
    private static let origin = Date(timeIntervalSince1970: 1_767_225_600) // 2026-01-01T00:00:00Z

    // Given: shared fixture の字幕文字数上限
    // When: clipper の定数と照合する
    // Then: 日本語 60 / 英語 120 / 省略記号が一致する
    func testLimitsMatchFixture() throws {
        let limits = try XCTUnwrap(
            try SharedFixtures.load("subtitle")["limits"] as? [String: Any]
        )
        XCTAssertEqual(
            SharedFixtures.number(limits["japaneseCharacterLimit"]),
            SubtitleTailClipper.japaneseCharacterLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(limits["englishCharacterLimit"]),
            SubtitleTailClipper.englishCharacterLimit
        )
        XCTAssertEqual(
            SharedFixtures.text(limits["ellipsis"]),
            SubtitleTailClipper.ellipsis
        )
    }

    // Given: fixture の長文・短文・空白のみの字幕候補
    // When: 末尾優先でクリップする
    // Then: 期待する表示文字列になる
    func testClipMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("subtitle", "clip") {
                        let fixture = try SharedFixtures.case("subtitle", "clip", name)
            let input = SharedFixtures.fixtureString(try XCTUnwrap(fixture["input"]))
            let expected = SharedFixtures.fixtureString(try XCTUnwrap(fixture["expected"]))
            XCTAssertEqual(SubtitleTailClipper.clip(input), expected)
        }
    }

    // Given: shared fixture の無採取 finalize 間隔
    // When: assembler の定数と照合する
    // Then: 8 秒の idle finalize 間隔が一致する
    func testIdleIntervalMatchesFixture() throws {
        let assembler = try XCTUnwrap(
            try SharedFixtures.load("subtitle")["assembler"] as? [String: Any]
        )
        XCTAssertEqual(
            TimeInterval(SharedFixtures.number(assembler["idleFinalizeSeconds"])),
            RealtimeSubtitleAssembler.idleFinalizeInterval
        )
    }

    // Given: fixture の原文・翻訳 delta シナリオ（epoch / 重複 ID / lane 期待値を含む）
    // When: assembler へ順に投入し時間を進める
    // Then: finalize タイミングと字幕内容が期待どおりになる
    func testAssemblerMatchesFixture() throws {
        let assemblerRoot = try XCTUnwrap(
            try SharedFixtures.load("subtitle")["assembler"] as? [String: Any]
        )
        let cases = try XCTUnwrap(assemblerRoot["cases"] as? [Any])
        for caseItem in cases {
            let fixture = try XCTUnwrap(caseItem as? [String: Any])
            let name = SharedFixtures.text(fixture["name"])
                        let epoch = SharedFixtures.number(fixture["epoch"])
            var assembler = RealtimeSubtitleAssembler()
            assembler.reset(epoch: epoch)
            if let lane = SharedFixtures.optionalText(fixture["expectLane"]) {
                assembler.expectLane(
                    RealtimeTranslationOutputLanguage(rawValue: lane)
                )
            } else {
                assembler.expectLane(nil)
            }

            var last: RealtimeSubtitleUpdate?
            let steps = try XCTUnwrap(fixture["steps"] as? [Any])
            for stepItem in steps {
                let step = try XCTUnwrap(stepItem as? [String: Any])
                let now = Self.origin.addingTimeInterval(SharedFixtures.real(step["at"]))
                let kind = SharedFixtures.text(step["kind"])
                let update: RealtimeSubtitleUpdate?
                switch kind {
                case "tick":
                    update = assembler.tick(now: now)
                case "sourceDelta", "translationDelta":
                    update = assembler.ingest(
                        RealtimeTranslationStreamEvent(
                            target: try XCTUnwrap(
                                RealtimeTranslationOutputLanguage(
                                    rawValue: SharedFixtures.text(step["lane"])
                                )
                            ),
                            event: serverEvent(kind: kind, step: step),
                            epoch: SharedFixtures.optionalNumber(step["epoch"]) ?? epoch
                        ),
                        now: now
                    )
                default:
                    return XCTFail("unhandled step kind \(kind)")
                }
                if let update {
                    last = update
                }
            }

            let expected = try XCTUnwrap(fixture["expectedFinal"] as? [String: Any])
            let finalUpdate = try XCTUnwrap(last)
            XCTAssertEqual(SharedFixtures.text(expected["sourceText"]), finalUpdate.sourceText)
            XCTAssertEqual(
                SharedFixtures.text(expected["translatedText"]),
                finalUpdate.translatedText
            )
            XCTAssertEqual(
                SharedFixtures.flag(expected["isTranslationCurrent"]),
                finalUpdate.isTranslationCurrent
            )
            XCTAssertEqual(
                SharedFixtures.flag(expected["shouldFinalize"]),
                finalUpdate.shouldFinalize
            )
        }
    }

    private func serverEvent(kind: String, step: [String: Any]) -> RealtimeTranslationServerEvent {
        let text = SharedFixtures.text(step["text"])
        let eventID = SharedFixtures.optionalText(step["eventId"])
        let elapsedMs = SharedFixtures.optionalNumber(step["elapsedMs"])
        if kind == "sourceDelta" {
            return .inputTranscriptDelta(delta: text, eventID: eventID, elapsedMs: elapsedMs)
        }
        return .outputTranscriptDelta(delta: text, eventID: eventID, elapsedMs: elapsedMs)
    }
}
