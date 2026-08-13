import XCTest
@testable import RealtimeTranslator

final class UserCopyTests: XCTestCase {
    // Given: 正本の ui.json
    // When: キー集合と ja/en を検査する
    // Then: キーは一意で、空文字が無く、プレースホルダ名が一致する
    func testCatalogKeysAreUniqueAndPlaceholdersMatch() throws {
        let json = try SharedFixtures.uiCatalogJSON()

        XCTAssertEqual(try UserCopy.duplicateKeys(in: json), [])
        XCTAssertEqual(try UserCopy.placeholderMismatches(in: json), [])

        let copy = try UserCopy.parse(json: json, locale: .ja)
        XCTAssertEqual(copy.locale, .ja)
        XCTAssertFalse(copy.text("error.genericServer").isEmpty)
        XCTAssertFalse(copy.text("settings.uiLanguage").isEmpty)
    }

    // Given: テストプロセスの Current
    // When: 既定のカタログを読む
    // Then: ja が載っており、Current を切り替えない
    func testCurrentDefaultsToJapaneseCatalog() {
        XCTAssertEqual(UserCopyStore.current.locale, .ja)
        XCTAssertEqual(UserCopyStore.current.text("menu.startTranslation"), "翻訳を開始")
    }

    // Given: ja に欠けたキーがあるカタログ
    // When: そのキーを引く
    // Then: en へフォールバックし、キー名だけを通知する
    func testMissingPrimaryKeyFallsBackToEnglishAndLogsKeyName() {
        let logged = KeyLog()
        let copy = UserCopy(
            locale: .ja,
            primary: [:],
            english: ["only.en": "English only"],
            missingKeyHandler: { key in logged.append(key) }
        )

        XCTAssertEqual(copy.text("only.en"), "English only")
        XCTAssertEqual(logged.keys, ["only.en"])
    }

    // Given: 未知のキー
    // When: 引く
    // Then: キー名を返し、本文は捏造しない
    func testUnknownKeyReturnsTheKeyName() {
        let logged = KeyLog()
        let copy = UserCopy(
            locale: .ja,
            primary: [:],
            english: [:],
            missingKeyHandler: { key in logged.append(key) }
        )

        XCTAssertEqual(copy.text("missing.key"), "missing.key")
        XCTAssertEqual(logged.keys, ["missing.key"])
    }

    // Given: {name} プレースホルダを含む文言
    // When: Format する
    // Then: 単純置換され、String(format:) は使わない
    func testFormatReplacesNamedPlaceholders() throws {
        let json = Data(
            """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                {
                  "key": "banner.idle",
                  "ja": "待機中 — {hotkey} で録音開始",
                  "en": "Idle — press {hotkey} to start"
                }
              ]
            }
            """.utf8
        )
        let copy = try UserCopy.parse(json: json, locale: .ja)

        XCTAssertEqual(
            copy.text("banner.idle", ["hotkey": "Control + Option + Space"]),
            "待機中 — Control + Option + Space で録音開始"
        )
    }

    // Given: ja/en でプレースホルダ集合が違うエントリ
    // When: 検査する
    // Then: そのキーが不一致として報告される
    func testPlaceholderMismatchIsDetected() throws {
        let json = Data(
            """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                { "key": "bad", "ja": "hello {hotkey}", "en": "hello {name}" }
              ]
            }
            """.utf8
        )

        XCTAssertEqual(try UserCopy.placeholderMismatches(in: json), ["bad"])
    }

    // Given: OS の UI 言語と保存値
    // When: 表示言語を解決する
    // Then: ja OS だけ ja、それ以外と未知値は en / system
    func testResolveFollowsPreferenceThenOsLanguage() {
        XCTAssertEqual(UiLanguagePreference.ja.resolve(osLanguageCode: "en"), .ja)
        XCTAssertEqual(UiLanguagePreference.en.resolve(osLanguageCode: "ja"), .en)
        XCTAssertEqual(UiLanguagePreference.system.resolve(osLanguageCode: "ja"), .ja)
        XCTAssertEqual(UiLanguagePreference.system.resolve(osLanguageCode: "en"), .en)
        XCTAssertEqual(UiLanguagePreference.system.resolve(osLanguageCode: "es"), .en)
        XCTAssertEqual(UiLanguagePreference.system.resolve(osLanguageCode: "fr"), .en)
    }

    // Given: 欠落または未知の wire 値
    // When: 読む
    // Then: system へ倒す
    func testUnknownWireValueBecomesSystem() {
        XCTAssertEqual(UiLanguagePreference.parse(nil), .system)
        XCTAssertEqual(UiLanguagePreference.parse(""), .system)
        XCTAssertEqual(UiLanguagePreference.parse("es"), .system)
        XCTAssertEqual(UiLanguagePreference.parse("unknown"), .system)
        XCTAssertEqual(UiLanguagePreference.parse("ja"), .ja)
        XCTAssertEqual(UiLanguagePreference.parse("en"), .en)
        XCTAssertEqual(UiLanguagePreference.system.rawValue, "system")
    }
}

private final class KeyLog: @unchecked Sendable {
    private(set) var keys: [String] = []

    func append(_ key: String) {
        keys.append(key)
    }
}
