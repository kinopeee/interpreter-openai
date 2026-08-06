import XCTest
@testable import RealtimeTranslator

final class LanguageFixtureTests: XCTestCase {
    // Given: shared fixture の直近証拠ウィンドウ定数
    // When: 検出器の定数と照合する
    // Then: 非空白 scalar 数の上限が一致する
    func testWindowSizeMatchesFixture() throws {
        let fixture = try SharedFixtures.load("language")
        XCTAssertEqual(
            SharedFixtures.number(fixture["recentEvidenceWindow"]),
            SpokenLanguageDetector.recentEvidenceWindow
        )
    }

    // Given: fixture の日英混在・曖昧・不明テキスト
    // When: 言語証拠を集計し言語を判定する
    // Then: 期待する証拠と検出結果になる
    func testEvidenceAndDetectMatchFixture() throws {
        for name in try SharedFixtures.caseNames("language", "evidence") {
                        let fixture = try SharedFixtures.case("language", "evidence", name)
            let input = SharedFixtures.text(fixture["input"])
            XCTAssertEqual(
                parseEvidence(SharedFixtures.text(fixture["evidence"])),
                SpokenLanguageDetector.evidence(in: input)
            )
            XCTAssertEqual(
                parseLanguage(SharedFixtures.text(fixture["detect"])),
                SpokenLanguageDetector.detect(input)
            )
        }
    }

    // Given: ウィンドウを超える長さのテキスト
    // When: 末尾から Unicode scalar 単位で直近証拠を切り出す
    // Then: fixture の期待証拠と全体証拠の両方に一致する
    func testRecentEvidenceMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("language", "recentEvidence") {
                        let fixture = try SharedFixtures.case("language", "recentEvidence", name)
            let input = SharedFixtures.text(fixture["input"])
            XCTAssertEqual(
                parseEvidence(SharedFixtures.text(fixture["expected"])),
                SpokenLanguageDetector.recentEvidence(
                    in: input,
                    window: SharedFixtures.number(fixture["window"])
                )
            )
            XCTAssertEqual(
                parseEvidence(SharedFixtures.text(fixture["fullEvidence"])),
                SpokenLanguageDetector.evidence(in: input)
            )
        }
    }

    // Given: fixture の言語→翻訳先対応表
    // When: 各言語の翻訳先を求める
    // Then: 日本語は英語へ、英語は日本語へ向かう
    func testTranslationTargetsMatchFixture() throws {
        for item in try SharedFixtures.section("language", "targets") {
            let language = parseLanguage(SharedFixtures.text(item["language"]))
            let expectedRaw = SharedFixtures.optionalText(item["translationTarget"])
            let expected = expectedRaw.flatMap(TranslationTarget.init(rawValue:))
            XCTAssertEqual(language.translationTarget, expected)
        }
    }

    private func parseEvidence(_ value: String) -> SpokenLanguageEvidence {
        switch value {
        case "japanese":
            return .japanese
        case "english":
            return .english
        case "ambiguousLatin":
            return .ambiguousLatin
        case "none":
            return .none
        default:
            fatalError("unhandled evidence \(value)")
        }
    }

    private func parseLanguage(_ value: String) -> SpokenLanguage {
        switch value {
        case "japanese":
            return .japanese
        case "english":
            return .english
        case "unknown":
            return .unknown
        default:
            fatalError("unhandled language \(value)")
        }
    }
}
