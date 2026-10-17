import Foundation
import XCTest
@testable import RealtimeTranslator

final class SettingsFixtureTests: XCTestCase {
    // Given: shared fixture の保存値を一時 UserDefaults suite へ書き込む
    // When: AppSettings(defaults:) で読み込み、ペア変更ケースはヒント更新へ進める
    // Then: expected の各フィールドが一致する
    @MainActor
    func testDecodeRestoresExpectedSettings() throws {
        for name in try SharedFixtures.caseNames("settings", "decode") {
            let fixture = try SharedFixtures.case("settings", "decode", name)
            let suite = "SettingsFixtureTests.\(UUID().uuidString)"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: suite), name)
            defer { defaults.removePersistentDomain(forName: suite) }

            if let input = fixture["input"] as? [String: Any] {
                writeInput(input, to: defaults)
            }

            let settings = AppSettings(defaults: defaults)
            if let change = fixture["pairChange"] as? String {
                settings.languagePair = LanguagePair(rawValue: change)!
            }

            for (key, expected) in fixture["expected"] as! [String: Any] {
                assertField(key, expected: expected, in: settings, caseName: name)
            }
        }
    }

    // Given: fixture の defaults を一時 suite から読む
    // Then: 既定値と一致する
    @MainActor
    func testDefaultsMatchFixture() throws {
        let fixture = try SharedFixtures.load("settings")
        let suite = "SettingsFixtureTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let settings = AppSettings(defaults: defaults)
        for (key, expected) in fixture["defaults"] as! [String: Any] {
            assertField(key, expected: expected, in: settings, caseName: "defaults")
        }
    }

    // Given: 設定を変更して保存する
    // When: 同じ suite から AppSettings を再生成する
    // Then: 値が永続化されている
    @MainActor
    func testChangesPersistAcrossReload() throws {
        let suite = "SettingsFixtureTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let settings = AppSettings(defaults: defaults)
        settings.fontSize = 40
        settings.languagePair = .jaEs
        settings.recordSubtitles = true

        let reloaded = AppSettings(defaults: defaults)
        XCTAssertEqual(reloaded.fontSize, 40)
        XCTAssertEqual(reloaded.languagePair, .jaEs)
        XCTAssertTrue(reloaded.recordSubtitles)
    }

    /// 論理名 → UserDefaults キーへ写像して保存値を作る。
    @MainActor
    private func writeInput(_ input: [String: Any], to defaults: UserDefaults) {
        for (key, value) in input {
            switch key {
            case "fontSize":
                defaults.set(SharedFixtures.real(value), forKey: "subtitleFontSize")
            case "hasCustomOverlayOrigin":
                defaults.set(SharedFixtures.flag(value), forKey: "hasCustomPanelOrigin")
            case "overlayOriginX":
                defaults.set(SharedFixtures.real(value), forKey: "panelOriginX")
            case "overlayOriginY":
                defaults.set(SharedFixtures.real(value), forKey: "panelOriginY")
            case "acceptedConsentVersion":
                defaults.set(SharedFixtures.number(value), forKey: "openAIConsentVersion")
            case "transcriptionPrompt":
                defaults.set(resolveHintText(key, value), forKey: "transcriptionPrompt")
            case "transcriptionKeywordsText":
                defaults.set(
                    resolveHintText(key, value),
                    forKey: "transcriptionKeywordsText"
                )
            case "noiseReduction":
                defaults.set(SharedFixtures.text(value), forKey: "noiseReductionMode")
            case "transcriptionDelay":
                defaults.set(SharedFixtures.text(value), forKey: "transcriptionDelayMode")
            case "recordSubtitles":
                defaults.set(SharedFixtures.flag(value), forKey: "recordSubtitles")
            case "languagePair":
                defaults.set(SharedFixtures.text(value), forKey: "languagePair")
            case "uiLanguage":
                defaults.set(SharedFixtures.text(value), forKey: "uiLanguage")
            case "automaticGainEnabled":
                defaults.set(SharedFixtures.flag(value), forKey: "automaticGainEnabled")
            default:
                XCTFail("unknown input field \(key)")
            }
        }
    }

    /// "defaultForPair:<pair>" センチネルを実装の既定ヒントへ解決する。
    private func resolveHintText(_ key: String, _ value: Any) -> String {
        let text = SharedFixtures.text(value)
        let prefix = "defaultForPair:"
        guard text.hasPrefix(prefix) else { return text }
        let pair = LanguagePair(rawValue: String(text.dropFirst(prefix.count)))!
        if key == "transcriptionPrompt" {
            return RealtimeSessionTuning.defaultPrompt(for: pair)
        }
        return RealtimeSessionTuning.keywordsText(
            from: RealtimeSessionTuning.defaultKeywords(for: pair)
        )
    }

    @MainActor
    private func assertField(
        _ key: String,
        expected: Any,
        in settings: AppSettings,
        caseName: String
    ) {
        switch key {
        case "fontSize":
            XCTAssertEqual(SharedFixtures.real(expected), settings.fontSize, caseName)
        case "hasCustomOverlayOrigin":
            XCTAssertEqual(
                SharedFixtures.flag(expected),
                settings.hasCustomPanelOrigin,
                caseName
            )
        case "overlayOriginX":
            XCTAssertEqual(SharedFixtures.real(expected), settings.panelOriginX, caseName)
        case "overlayOriginY":
            XCTAssertEqual(SharedFixtures.real(expected), settings.panelOriginY, caseName)
        case "acceptedConsentVersion":
            XCTAssertEqual(
                SharedFixtures.number(expected),
                settings.acceptedOpenAIConsentVersion,
                caseName
            )
        case "transcriptionPrompt":
            XCTAssertEqual(
                resolveHintText(key, expected),
                settings.transcriptionPrompt,
                caseName
            )
        case "transcriptionKeywordsText":
            XCTAssertEqual(
                resolveHintText(key, expected),
                settings.transcriptionKeywordsText,
                caseName
            )
        case "noiseReduction":
            XCTAssertEqual(
                SharedFixtures.text(expected),
                settings.noiseReductionMode,
                caseName
            )
        case "transcriptionDelay":
            XCTAssertEqual(
                SharedFixtures.text(expected),
                settings.transcriptionDelayMode,
                caseName
            )
        case "recordSubtitles":
            XCTAssertEqual(
                SharedFixtures.flag(expected),
                settings.recordSubtitles,
                caseName
            )
        case "languagePair":
            XCTAssertEqual(
                SharedFixtures.text(expected),
                settings.languagePair.rawValue,
                caseName
            )
        case "uiLanguage":
            XCTAssertEqual(
                SharedFixtures.text(expected),
                settings.uiLanguage.rawValue,
                caseName
            )
        case "automaticGainEnabled":
            XCTAssertEqual(
                SharedFixtures.flag(expected),
                settings.automaticGainEnabled,
                caseName
            )
        default:
            XCTFail("\(caseName): unknown expected field \(key)")
        }
    }
}
