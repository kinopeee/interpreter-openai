import XCTest
@testable import RealtimeTranslator

final class RealtimeSessionTuningTests: XCTestCase {
    func testParseKeywordsTrimsAndDropsEmptyLines() {
        // Given: 空行と前後空白を含むキーワードテキスト
        let text = """

          ハッカソン
        hackathon

        エンジニア
        """

        // When: 正規化する
        let keywords = RealtimeSessionTuning.parseKeywords(from: text)

        // Then: 空行は捨てられtrimされる
        XCTAssertEqual(keywords, ["ハッカソン", "hackathon", "エンジニア"])
    }

    func testParseKeywordsRespectsLimit() {
        // Given: 上限を超える行
        let lines = (1...80).map { "word\($0)" }.joined(separator: "\n")

        // When: 上限64で正規化する
        let keywords = RealtimeSessionTuning.parseKeywords(from: lines, limit: 64)

        // Then: 先頭64件だけ残る
        XCTAssertEqual(keywords.count, 64)
        XCTAssertEqual(keywords.first, "word1")
        XCTAssertEqual(keywords.last, "word64")
    }

    @MainActor
    func testAppSettingsSessionTuningUsesStoredValues() {
        // Given: カスタム設定を持つAppSettings (終了時に既定値へ戻す)
        let settings = AppSettings()
        let previousPrompt = settings.transcriptionPrompt
        let previousKeywords = settings.transcriptionKeywordsText
        let previousNoise = settings.noiseReductionMode
        defer {
            settings.transcriptionPrompt = previousPrompt
            settings.transcriptionKeywordsText = previousKeywords
            settings.noiseReductionMode = previousNoise
        }
        settings.transcriptionPrompt = "Product launch glossary"
        settings.transcriptionKeywordsText = "Acme\nロードマップ"
        settings.noiseReduction = .nearField

        // When: sessionTuningを作る
        let tuning = settings.sessionTuning()

        // Then: 設定値がそのまま渡る
        XCTAssertEqual(tuning.transcriptionPrompt, "Product launch glossary")
        XCTAssertEqual(tuning.transcriptionKeywords, ["Acme", "ロードマップ"])
        XCTAssertEqual(tuning.noiseReduction, .nearField)
    }
}
