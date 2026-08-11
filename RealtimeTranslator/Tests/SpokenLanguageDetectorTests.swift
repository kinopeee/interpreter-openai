import XCTest
@testable import RealtimeTranslator

final class SpokenLanguageDetectorTests: XCTestCase {
    func testDetectsJapaneseWhenTextContainsJapaneseAndEnglish() {
        // Given: 日本語と英単語が混在する認識結果
        let text = "今日はCursorについて説明します"

        // When: 発話言語を判定する
        let result = SpokenLanguageDetector.detect(text, pair: .jaEn)

        // Then: 日本語と判定し、英語を翻訳先にする
        XCTAssertEqual(result, .japanese)
        XCTAssertEqual(LanguagePair.jaEn.translationTarget(for: result), .english)
    }

    func testDetectsEnglishFromLatinCharacters() {
        // Given: 大小文字を含む英語の認識結果
        let text = "Hello, how are you?"

        // When: 発話言語を判定する
        let result = SpokenLanguageDetector.detect(text, pair: .jaEn)

        // Then: 英語と判定し、日本語を翻訳先にする
        XCTAssertEqual(result, .english)
        XCTAssertEqual(LanguagePair.jaEn.translationTarget(for: result), .japanese)
    }

    func testDefersSingleLatinProperNoun() {
        // Given: 日英どちらの発話にも現れ得るLatin固有名詞
        let text = "Cursor"

        // When: 発話言語の証拠と判定結果を調べる
        let evidence = SpokenLanguageDetector.evidence(in: text, pair: .jaEn)
        let result = SpokenLanguageDetector.detect(text, pair: .jaEn)

        // Then: Latin一語だけでは英語に固定しない
        XCTAssertEqual(evidence, .ambiguousLatin)
        XCTAssertEqual(result, .unknown)
        XCTAssertNil(LanguagePair.jaEn.translationTarget(for: result))
    }

    func testDefersSingleLatinAcronym() {
        // Given: 日英どちらでも使われるLatin略語
        let text = "MCP"

        // When: 発話言語の証拠を調べる
        let evidence = SpokenLanguageDetector.evidence(in: text, pair: .jaEn)

        // Then: 略語一語だけでは英語の証拠にしない
        XCTAssertEqual(evidence, .ambiguousLatin)
    }

    func testUsesMultipleLatinWordsAsEnglishEvidence() {
        // Given: 複数のLatin単語からなる英語文
        let text = "Open the file"

        // When: 発話言語の証拠を調べる
        let evidence = SpokenLanguageDetector.evidence(in: text, pair: .jaEn)

        // Then: 複数語は英語の証拠として扱う
        XCTAssertEqual(evidence, .english)
        XCTAssertEqual(SpokenLanguageDetector.detect(text, pair: .jaEn), .english)
    }

    func testReturnsUnknownForEmptyText() {
        // Given: 空の認識結果
        let text = ""

        // When: 発話言語を判定する
        let result = SpokenLanguageDetector.detect(text, pair: .jaEn)

        // Then: 言語・翻訳先ともに未判定になる
        XCTAssertEqual(result, .unknown)
        XCTAssertNil(LanguagePair.jaEn.translationTarget(for: result))
    }

    func testReturnsUnknownForNumbersAndSymbols() {
        // Given: 言語を示す文字を含まない認識結果
        let text = "1234 / 56%"

        // When: 発話言語を判定する
        let result = SpokenLanguageDetector.detect(text, pair: .jaEn)

        // Then: 誤って日英どちらにも分類しない
        XCTAssertEqual(result, .unknown)
        XCTAssertNil(LanguagePair.jaEn.translationTarget(for: result))
    }

    func testRecentEvidenceDetectsEnglishAfterJapanesePrefix() {
        // Given: 先頭は日本語、末尾はウィンドウを埋める複数の英単語
        let text = "今日は会議です Hello how are you doing today"

        // When: 全文判定と末尾ウィンドウ判定を比較する
        let full = SpokenLanguageDetector.evidence(in: text, pair: .jaEn)
        let recent = SpokenLanguageDetector.recentEvidence(in: text, pair: .jaEn, window: 16)

        // Then: 全文は日本語のまま、末尾は英語切替を検出する
        // （空白を残すことでラテン語が1語に潰れず english になる）
        XCTAssertEqual(full, .japanese)
        XCTAssertEqual(recent, .english)
    }

    func testRecentEvidenceDetectsJapaneseAfterEnglishPrefix() {
        // Given: 先頭は英語、末尾は日本語
        let text = "Hello how are you 今日は会議です"

        // When: 末尾ウィンドウで判定する
        let recent = SpokenLanguageDetector.recentEvidence(in: text, pair: .jaEn, window: 16)

        // Then: 日本語切替を検出する
        XCTAssertEqual(recent, .japanese)
    }
}
