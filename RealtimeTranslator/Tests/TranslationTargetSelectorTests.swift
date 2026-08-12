import XCTest
@testable import RealtimeTranslator

final class TranslationTargetSelectorTests: XCTestCase {
    // Given: shared fixtureのtargetSelection契約
    // When: 全ての証拠列を純粋な調停器へ渡す
    // Then: 各stepの出力targetがfixtureと一致する
    func testTargetSelectionMatchesSharedFixture() throws {
        for item in try SharedFixtures.section("routing", "targetSelection") {
            let pair = try XCTUnwrap(
                LanguagePair(rawValue: SharedFixtures.text(item["pair"]))
            )
            var target = SharedFixtures.optionalText(item["initialTarget"])
                .flatMap(RealtimeTranslationOutputLanguage.init(rawValue:))
            var reverseCount = 0
            let steps = try XCTUnwrap(item["evidence"] as? [[String: Any]])
            for step in steps {
                let result = TranslationTargetSelector.select(
                    pair: pair,
                    currentTarget: target,
                    reverseEvidenceCount: reverseCount,
                    evidence: parseEvidence(SharedFixtures.text(step["evidence"]))
                )
                target = result.target
                reverseCount = result.reverseEvidenceCount
                XCTAssertEqual(
                    target?.rawValue,
                    SharedFixtures.optionalText(step["expectedTarget"])
                )
                if let expectedCount = SharedFixtures.optionalNumber(step["expectedReverseEvidenceCount"]) {
                    XCTAssertEqual(reverseCount, expectedCount)
                }
            }
        }
    }

    // Given: en-esで初期targetが未選択
    // When: スペイン語の証拠を1回渡す
    // Then: 出力言語enを即時選択する
    func testInitialSpanishSelectsEnglishTarget() {
        let result = TranslationTargetSelector.select(
            pair: .enEs,
            currentTarget: nil,
            reverseEvidenceCount: 0,
            evidence: .spanish
        )
        XCTAssertEqual(result.target, .english)
        XCTAssertEqual(result.reverseEvidenceCount, 0)
    }

    // Given: en-esで話者スペイン語のtarget=en
    // When: 逆方向の英語証拠を2回連続で渡す
    // Then: 出力言語esへ切り替える
    func testTwoReverseEvidenceSwitchesEnEsTarget() {
        let first = TranslationTargetSelector.select(
            pair: .enEs,
            currentTarget: .english,
            reverseEvidenceCount: 0,
            evidence: .english
        )
        let second = TranslationTargetSelector.select(
            pair: .enEs,
            currentTarget: first.target,
            reverseEvidenceCount: first.reverseEvidenceCount,
            evidence: .english
        )
        XCTAssertEqual(first.target, .english)
        XCTAssertEqual(second.target, .spanish)
    }

    // Given: ja-esでtarget未選択
    // When: 曖昧なLatin証拠を初回に渡す
    // Then: 日本語話者の相手側である出力言語jaを選択する
    func testInitialAmbiguousLatinSelectsJapaneseTargetForJaEs() {
        let result = TranslationTargetSelector.select(
            pair: .jaEs,
            currentTarget: nil,
            reverseEvidenceCount: 0,
            evidence: .ambiguousLatin
        )
        XCTAssertEqual(result.target, .japanese)
    }

    private func parseEvidence(_ value: String) -> SpokenLanguageEvidence {
        switch value {
        case "japanese": return .japanese
        case "english": return .english
        case "spanish": return .spanish
        case "ambiguousLatin": return .ambiguousLatin
        case "none": return .none
        default: fatalError("unhandled evidence \(value)")
        }
    }
}
