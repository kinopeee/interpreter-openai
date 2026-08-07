import XCTest
@testable import RealtimeTranslator

final class TuningFixtureTests: XCTestCase {
    // Given: shared fixture の tuning 上限値
    // When: Swift 実装の定数と照合する
    // Then: keyword 上限・prompt 上限・禁止文字が一致する
    func testLimitsMatchFixture() throws {
        let limits = try SharedFixtures.load("tuning")["limits"] as? [String: Any]
        let typed = try XCTUnwrap(limits)
        XCTAssertEqual(
            SharedFixtures.number(typed["keywordLimit"]),
            RealtimeSessionTuning.keywordLimit
        )
        XCTAssertEqual(
            SharedFixtures.number(typed["promptCharacterLimit"]),
            RealtimeSessionTuning.promptCharacterLimit
        )
        XCTAssertEqual(
            RealtimeSessionTuning.forbiddenKeywordCharacters,
            CharacterSet(charactersIn: SharedFixtures.text(typed["forbiddenKeywordCharacters"]))
        )
    }

    // Given: 1 行 1 語のキーワードテキスト
    // When: ParseKeywords で正規化する
    // Then: fixture の期待配列と一致する
    func testParseKeywordsMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("tuning", "parseKeywords") {
                        let fixture = try SharedFixtures.case("tuning", "parseKeywords", name)
            let expected = try XCTUnwrap(fixture["expected"] as? [Any]).map(SharedFixtures.text)
            XCTAssertEqual(
                RealtimeSessionTuning.parseKeywords(from: SharedFixtures.text(fixture["input"])),
                expected
            )
        }
    }

    // Given: 上限を超える行数のキーワードテキスト
    // When: ParseKeywords で正規化する
    // Then: 上限件数で打ち切られ、先頭と末尾が入力順を保つ
    func testParseKeywordsStopsAtTheLimit() throws {
        let fixture = try XCTUnwrap(
            try SharedFixtures.load("tuning")["parseKeywordsLimit"] as? [String: Any]
        )
        let template = SharedFixtures.text(fixture["lineTemplate"])
        let lineCount = SharedFixtures.number(fixture["lineCount"])
        let input = (0..<lineCount)
            .map { template.replacingOccurrences(of: "{index}", with: String($0)) }
            .joined(separator: "\n")

        let keywords = RealtimeSessionTuning.parseKeywords(
            from: input,
            limit: SharedFixtures.number(fixture["limit"])
        )

        XCTAssertEqual(SharedFixtures.number(fixture["expectedCount"]), keywords.count)
        XCTAssertEqual(SharedFixtures.text(fixture["expectedFirst"]), keywords.first)
        XCTAssertEqual(SharedFixtures.text(fixture["expectedLast"]), keywords.last)
    }

    // Given: 非空キーワードと limit=0
    // When: ParseKeywords で正規化する
    // Then: 1 件も返さない
    func testParseKeywordsReturnsEmptyWhenLimitIsZero() {
        let keywords = RealtimeSessionTuning.parseKeywords(from: "hackathon\ndemo", limit: 0)
        XCTAssertTrue(keywords.isEmpty)
    }

    // Given: 改行や前後空白を含む prompt
    // When: SanitizedPrompt で正規化する
    // Then: fixture の期待文字列と一致する
    func testSanitizedPromptMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("tuning", "sanitizedPrompt") {
                        let fixture = try SharedFixtures.case("tuning", "sanitizedPrompt", name)
            XCTAssertEqual(
                SharedFixtures.text(fixture["expected"]),
                RealtimeSessionTuning.sanitizedPrompt(SharedFixtures.text(fixture["input"]))
            )
        }
    }

    // Given: 上限を超える長さの ASCII prompt
    // When: SanitizedPrompt で正規化する
    // Then: fixture の期待長へ切り詰められる
    func testSanitizedPromptTruncatesAtTheLimit() throws {
        let fixture = try XCTUnwrap(
            try SharedFixtures.load("tuning")["sanitizedPromptLimit"] as? [String: Any]
        )
        let character = SharedFixtures.text(fixture["repeatedCharacter"])
        let input = String(
            repeating: character,
            count: SharedFixtures.number(fixture["inputLength"])
        )
        XCTAssertEqual(
            SharedFixtures.number(fixture["expectedLength"]),
            RealtimeSessionTuning.sanitizedPrompt(input).count
        )
    }

    // Given: サロゲートペアで表される絵文字だけで上限を超える prompt
    // When: SanitizedPrompt で正規化する
    // Then: Swift の Character 数と同じ上限文字数で切り、lone surrogate を残さない
    func testSanitizedPromptTruncatesByTextElementNotCodeUnit() {
        let emoji = "\u{1F600}"
        let limit = RealtimeSessionTuning.promptCharacterLimit
        let truncated = RealtimeSessionTuning.sanitizedPrompt(
            String(repeating: emoji, count: limit + 100)
        )
        XCTAssertEqual(String(repeating: emoji, count: limit), truncated)
    }

    // Given: 結合文字を含む書記素クラスタで上限を超える prompt
    // When: SanitizedPrompt で正規化する
    // Then: 結合文字ごと切り、基底文字だけを残さない
    func testSanitizedPromptTruncatesCombiningGraphemeClusters() {
        let combining = "e\u{0301}"
        let limit = RealtimeSessionTuning.promptCharacterLimit
        let input = String(repeating: "a", count: limit - 1) + combining + combining
        let truncated = RealtimeSessionTuning.sanitizedPrompt(input)
        XCTAssertEqual(String(repeating: "a", count: limit - 1) + combining, truncated)
    }
}
