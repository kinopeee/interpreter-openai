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

    func testParseKeywordsStripsForbiddenCharacters() {
        // Given: OpenAIが拒否する < > を含む行
        let text = "Acme<Corp>\n<>\nロードマップ"

        // When: 正規化する
        let keywords = RealtimeSessionTuning.parseKeywords(from: text)

        // Then: 禁止文字は除去され、空になった行は捨てられる
        XCTAssertEqual(keywords, ["AcmeCorp", "ロードマップ"])
    }

    func testSanitizedPromptCollapsesNewlinesAndTruncates() {
        // Given: 改行と上限超えのprompt
        let longTail = String(repeating: "a", count: 1_200)
        let text = "Hello\nworld\r\n" + longTail

        // When: sanitizeする
        let prompt = RealtimeSessionTuning.sanitizedPrompt(text)

        // Then: 改行は空白になり、1,000文字へ切り詰められる
        XCTAssertTrue(prompt.hasPrefix("Hello world "))
        XCTAssertFalse(prompt.contains("\n"))
        XCTAssertFalse(prompt.contains("\r"))
        XCTAssertEqual(prompt.count, RealtimeSessionTuning.promptCharacterLimit)
    }

    @MainActor
    func testAppSettingsSessionTuningUsesStoredValues() {
        // Given: カスタム設定を持つAppSettings (終了時に既定値へ戻す)
        let settings = AppSettings()
        let previousPrompt = settings.transcriptionPrompt
        let previousKeywords = settings.transcriptionKeywordsText
        let previousNoise = settings.noiseReductionMode
        let previousDelay = settings.transcriptionDelayMode
        defer {
            settings.transcriptionPrompt = previousPrompt
            settings.transcriptionKeywordsText = previousKeywords
            settings.noiseReductionMode = previousNoise
            settings.transcriptionDelayMode = previousDelay
        }
        settings.transcriptionPrompt = "Product launch glossary"
        settings.transcriptionKeywordsText = "Acme\nロードマップ"
        settings.noiseReduction = .nearField
        settings.transcriptionDelay = .high

        // When: sessionTuningを作る
        let tuning = settings.sessionTuning()

        // Then: sanitize済みの設定値が渡る
        XCTAssertEqual(tuning.transcriptionPrompt, "Product launch glossary")
        XCTAssertEqual(tuning.transcriptionKeywords, ["Acme", "ロードマップ"])
        XCTAssertEqual(tuning.noiseReduction, .nearField)
        XCTAssertEqual(tuning.transcriptionDelay, .high)
    }

    @MainActor
    func testAppSettingsTranscriptionDelayFallsBackToLowOnInvalidValue() {
        // Given: 不正なdelay rawValueを持つAppSettings
        let settings = AppSettings()
        let previousDelay = settings.transcriptionDelayMode
        defer { settings.transcriptionDelayMode = previousDelay }
        settings.transcriptionDelayMode = "not-a-valid-delay"

        // When: computed propertyとsessionTuningを読む
        let delay = settings.transcriptionDelay
        let tuning = settings.sessionTuning()

        // Then: lowへフォールバックする
        XCTAssertEqual(delay, .low)
        XCTAssertEqual(tuning.transcriptionDelay, .low)
    }

    @MainActor
    func testAppSettingsApplyPresetUpdatesPromptAndKeywords() {
        // Given: 別内容の設定
        let settings = AppSettings()
        let previousPrompt = settings.transcriptionPrompt
        let previousKeywords = settings.transcriptionKeywordsText
        defer {
            settings.transcriptionPrompt = previousPrompt
            settings.transcriptionKeywordsText = previousKeywords
        }
        settings.transcriptionPrompt = "custom"
        settings.transcriptionKeywordsText = "custom-word"

        // When: ビジネス会議プリセットを適用する
        settings.applyPreset(.businessMeeting)

        // Then: promptとkeywordsがプリセット内容になる
        XCTAssertEqual(settings.transcriptionPrompt, RealtimeSessionTuning.Preset.businessMeeting.prompt)
        XCTAssertEqual(
            settings.transcriptionKeywords,
            RealtimeSessionTuning.Preset.businessMeeting.keywords
        )
    }

    @MainActor
    func testAppSettingsSessionTuningSanitizesForbiddenKeywordCharacters() {
        // Given: 禁止文字を含むキーワード
        let settings = AppSettings()
        let previousKeywords = settings.transcriptionKeywordsText
        defer { settings.transcriptionKeywordsText = previousKeywords }
        settings.transcriptionKeywordsText = "Foo<Bar>\n<>"

        // When: sessionTuningを作る
        let tuning = settings.sessionTuning()

        // Then: 送信値からは禁止文字が消える
        XCTAssertEqual(tuning.transcriptionKeywords, ["FooBar"])
    }
}
