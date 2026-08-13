import XCTest
@testable import RealtimeTranslator

final class RealtimeSessionTuningTests: XCTestCase {
    func testForPairUsesMatchingDefaultsAndPreservesCustomValues() {
        // Given: 保存済み既定 tuning と選択された言語ペア
        let jaEs = RealtimeSessionTuning.default.forPair(.jaEs)

        // When: ペア向け tuning を解決する
        // Then: 既定値だけが現在のペア向けに置き換わる
        XCTAssertEqual(
            jaEs.transcriptionPrompt,
            RealtimeSessionTuning.defaultPrompt(for: .jaEs)
        )
        XCTAssertEqual(
            jaEs.transcriptionKeywords,
            RealtimeSessionTuning.defaultKeywords(for: .jaEs)
        )

        let custom = RealtimeSessionTuning(
            noiseReduction: .farField,
            transcriptionDelay: .low,
            transcriptionPrompt: "Custom prompt",
            transcriptionKeywords: ["Custom keyword"]
        )
        let preserved = custom.forPair(.enEs)
        XCTAssertEqual(preserved.transcriptionPrompt, "Custom prompt")
        XCTAssertEqual(preserved.transcriptionKeywords, ["Custom keyword"])
    }

    func testForPairMigratesKnownDefaultsAcrossEveryPair() {
        // Given: 3 ペアすべての既定 prompt / keywords
        let transitions: [(LanguagePair, LanguagePair)] = [
            (.jaEn, .jaEs),
            (.jaEs, .enEs),
            (.enEs, .jaEn),
        ]

        for (from, to) in transitions {
            // When: 任意の既定値から別ペアへ forPair する
            let migrated = RealtimeSessionTuning.default.forPair(from).forPair(to)

            // Then: 遷移先ペアの既定へ置き換わる
            XCTAssertEqual(migrated.transcriptionPrompt, RealtimeSessionTuning.defaultPrompt(for: to))
            XCTAssertEqual(migrated.transcriptionKeywords, RealtimeSessionTuning.defaultKeywords(for: to))

            var customPromptOnly = RealtimeSessionTuning.default.forPair(from)
            customPromptOnly = RealtimeSessionTuning(
                noiseReduction: customPromptOnly.noiseReduction,
                transcriptionDelay: customPromptOnly.transcriptionDelay,
                transcriptionPrompt: "Keep this prompt",
                transcriptionKeywords: customPromptOnly.transcriptionKeywords
            )
            let promptPreserved = customPromptOnly.forPair(to)
            XCTAssertEqual(promptPreserved.transcriptionPrompt, "Keep this prompt")
            XCTAssertEqual(
                promptPreserved.transcriptionKeywords,
                RealtimeSessionTuning.defaultKeywords(for: to)
            )

            var customKeywordsOnly = RealtimeSessionTuning.default.forPair(from)
            customKeywordsOnly = RealtimeSessionTuning(
                noiseReduction: customKeywordsOnly.noiseReduction,
                transcriptionDelay: customKeywordsOnly.transcriptionDelay,
                transcriptionPrompt: customKeywordsOnly.transcriptionPrompt,
                transcriptionKeywords: ["Keep", "these"]
            )
            let keywordsPreserved = customKeywordsOnly.forPair(to)
            XCTAssertEqual(
                keywordsPreserved.transcriptionPrompt,
                RealtimeSessionTuning.defaultPrompt(for: to)
            )
            XCTAssertEqual(keywordsPreserved.transcriptionKeywords, ["Keep", "these"])
        }
    }

    @MainActor
    func testAppSettingsLanguagePairChangeRefreshesDefaultHintsOnly() {
        // Given: 既定ヒントのままの設定
        let settings = AppSettings()
        let previousPair = settings.languagePair
        let previousPrompt = settings.transcriptionPrompt
        let previousKeywords = settings.transcriptionKeywordsText
        defer {
            settings.languagePair = previousPair
            settings.transcriptionPrompt = previousPrompt
            settings.transcriptionKeywordsText = previousKeywords
        }
        settings.languagePair = .jaEn
        settings.restoreDefaultTranscriptionHints()

        // When: 言語ペアを ja-es へ変える
        settings.languagePair = .jaEs

        // Then: 既定ヒントは新ペア向けへ更新される
        XCTAssertEqual(
            settings.transcriptionPrompt,
            RealtimeSessionTuning.defaultPrompt(for: .jaEs)
        )
        XCTAssertEqual(
            settings.transcriptionKeywords,
            RealtimeSessionTuning.defaultKeywords(for: .jaEs)
        )

        // When: カスタムヒントのまま別ペアへ変える
        settings.transcriptionPrompt = "custom prompt"
        settings.transcriptionKeywordsText = "custom\nwords"
        settings.languagePair = .enEs

        // Then: カスタム値は上書きされない
        XCTAssertEqual(settings.transcriptionPrompt, "custom prompt")
        XCTAssertEqual(settings.transcriptionKeywordsText, "custom\nwords")
    }

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

    func testIsPromptOverCharacterLimitUsesCollapsedLength() {
        // Given: 末尾改行で生文字数だけ上限を超えるprompt
        let text = String(repeating: "a", count: RealtimeSessionTuning.promptCharacterLimit) + "\n"

        // When: 上限判定する
        let overLimit = RealtimeSessionTuning.isPromptOverCharacterLimit(text)

        // Then: 改行は空白化のあと trim され、送信値は切り詰めなし
        XCTAssertEqual(text.count, RealtimeSessionTuning.promptCharacterLimit + 1)
        XCTAssertEqual(
            RealtimeSessionTuning.sanitizedPrompt(text).count,
            RealtimeSessionTuning.promptCharacterLimit
        )
        XCTAssertFalse(overLimit)
        XCTAssertTrue(
            RealtimeSessionTuning.isPromptOverCharacterLimit(
                String(repeating: "a", count: RealtimeSessionTuning.promptCharacterLimit + 1)
            )
        )
    }

    func testIsKeywordCountOverLimitIgnoresNonSubmittedLines() {
        // Given: 送信されない <> 行を含み、実送信は上限ちょうど
        let keywords = (1...RealtimeSessionTuning.keywordLimit)
            .map { "word\($0)" }
            .joined(separator: "\n") + "\n<>\n"

        // When: 上限判定する
        let overLimit = RealtimeSessionTuning.isKeywordCountOverLimit(from: keywords)

        // Then: 送信対象は64語なので超過ではない
        XCTAssertEqual(
            RealtimeSessionTuning.parseKeywords(from: keywords).count,
            RealtimeSessionTuning.keywordLimit
        )
        XCTAssertFalse(overLimit)
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

    @MainActor
    func testAppSettingsPersistsUiLanguagePreference() {
        // Given: 保存済みの表示言語
        let defaults = UserDefaults.standard
        let previousRawValue = defaults.object(forKey: "uiLanguage")
        let settings = AppSettings()
        defer {
            if let previousRawValue {
                defaults.set(previousRawValue, forKey: "uiLanguage")
            } else {
                defaults.removeObject(forKey: "uiLanguage")
            }
        }

        // When: English を保存して読み直す
        settings.uiLanguage = .en
        let reloaded = AppSettings()

        // Then: wire 値 en が残り、system へ倒れない
        XCTAssertEqual(reloaded.uiLanguage, .en)
        XCTAssertEqual(
            UserDefaults.standard.string(forKey: "uiLanguage"),
            UiLanguagePreference.en.rawValue
        )
    }
}
