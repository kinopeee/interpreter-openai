import XCTest
@testable import RealtimeTranslator

final class PrivacyFixtureTests: XCTestCase {
    // Given: shared fixture の汎用サーバーエラー文言
    // When: 実装定数と照合する
    // Then: 利用者へ見せる文言が完全に一致する
    func testGenericMessageMatchesFixture() throws {
        let fixture = try SharedFixtures.load("privacy")
        XCTAssertEqual(
            SharedFixtures.text(fixture["genericErrorMessage"]),
            RealtimeTranslationError.genericServerMessage
        )
    }

    // Given: ui.json の error.genericServer ja
    // When: privacy fixture の genericErrorMessage と照合する
    // Then: fixtures/v1 を変えずにカタログ ja が一致する
    func testCatalogJapaneseGenericServerMatchesFixture() throws {
        let json = try SharedFixtures.uiCatalogJSON()
        let ja = try UserCopy.parse(json: json, locale: .ja)
        let fixture = try SharedFixtures.load("privacy")
        XCTAssertEqual(
            SharedFixtures.text(fixture["genericErrorMessage"]),
            ja.text("error.genericServer")
        )
    }

    // Given: 資格情報や内部情報を含みうるサーバーメッセージ
    // When: プライバシー安全な正規化を行う
    // Then: fixture が許容する文言だけが残る
    func testSanitizeMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("privacy", "sanitizedServerMessage") {
                        let fixture = try SharedFixtures.case("privacy", "sanitizedServerMessage", name)
            XCTAssertEqual(
                SharedFixtures.text(fixture["expected"]),
                RealtimeTranslationError.sanitizedServerMessage(
                    SharedFixtures.text(fixture["input"])
                )
            )
        }
    }

    // Given: fixture の認証失敗・非認証エラー
    // When: 認証失敗判定を行う
    // Then: 期待どおりに認証失敗だけを検出する
    func testAuthenticationDetectionMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("privacy", "isAuthenticationFailure") {
                        let fixture = try SharedFixtures.case("privacy", "isAuthenticationFailure", name)
            XCTAssertEqual(
                SharedFixtures.flag(fixture["expected"]),
                RealtimeTranslationError.isAuthenticationFailure(
                    code: SharedFixtures.optionalText(fixture["code"]),
                    message: SharedFixtures.text(fixture["message"])
                )
            )
        }
    }

    // Given: fixture のエラー種別と回復可否対応表
    // When: 各エラーの回復可否を求める
    // Then: 再接続対象と致命エラーの区別が一致する
    func testRecoverabilityMatchesFixture() throws {
        for item in try SharedFixtures.section("privacy", "recoverability") {
            let kind = SharedFixtures.text(item["error"])
            let error = makeError(named: kind)
            XCTAssertEqual(
                SharedFixtures.flag(item["isRecoverable"]),
                error.isRecoverable,
                kind
            )
        }
    }

    // Given: API キーらしき文字列を含む致命的サーバーエラー
    // When: 例外メッセージを取得する
    // Then: 汎用文言に置換され資格情報が表に出ない
    func testFatalServerErrorNeverLeaksCredentials() {
        let error = RealtimeTranslationError.fatalServerError("Bearer sk-should-never-surface")
        XCTAssertEqual(error.errorDescription, RealtimeTranslationError.genericServerMessage)
    }

    // Given: 各エラー種別
    // When: 例外メッセージを取る
    // Then: Current（ja）カタログの対応キーと一致する
    func testErrorKindMessageMatchesJapaneseCatalog() throws {
        let json = try SharedFixtures.uiCatalogJSON()
        let ja = try UserCopy.parse(json: json, locale: .ja)
        let en = try UserCopy.parse(json: json, locale: .en)
        let cases: [(RealtimeTranslationError, String)] = [
            (.missingAPIKey, "error.missingApiKey"),
            (.notConnected, "error.notConnected"),
            (.invalidMessage, "error.invalidMessage"),
            (.authenticationFailed, "error.authenticationFailed"),
            (.recoverableTransportFailure("test"), "error.transportDisconnected"),
            (.sessionUpdateTimeout, "error.sessionUpdateTimeout"),
            (.closeTimeout, "error.closeTimeout"),
            (.cancelled, "error.cancelled"),
        ]

        for (error, key) in cases {
            XCTAssertEqual(error.errorDescription, ja.text(key), key)
            XCTAssertNotEqual(ja.text(key), en.text(key), key)
            XCTAssertFalse(en.text(key).localizedCaseInsensitiveContains("sk-"), key)
        }
    }

    // Given: 空のサーバー文言、または英語カタログの "API key" を含む認証・欠落キー文言
    // When: Sanitize する / 英語 copy で例外を組み立てる
    // Then: 空は generic。例外経路はカタログ文言のまま（再サニタイズしない）
    func testCatalogErrorCopyIsNotReSanitized() throws {
        XCTAssertEqual(
            RealtimeTranslationError.sanitizedServerMessage(""),
            RealtimeTranslationError.genericServerMessage
        )
        XCTAssertEqual(
            RealtimeTranslationError.fatalServerError("").errorDescription,
            RealtimeTranslationError.genericServerMessage
        )

        let json = try SharedFixtures.uiCatalogJSON()
        let en = try UserCopy.parse(json: json, locale: .en)
        let cases: [(RealtimeTranslationError, String)] = [
            (.authenticationFailed, "error.authenticationFailed"),
            (.missingAPIKey, "error.missingApiKey"),
        ]
        for (error, key) in cases {
            let catalogText = en.text(key)
            XCTAssertTrue(catalogText.localizedCaseInsensitiveContains("API key"), key)
            XCTAssertEqual(
                RealtimeTranslationError.sanitizedServerMessage(catalogText),
                RealtimeTranslationError.genericServerMessage,
                key
            )
            XCTAssertEqual(error.description(using: en), catalogText, key)
        }
    }

    private func makeError(named name: String) -> RealtimeTranslationError {
        switch name {
        case "missingAPIKey":
            return .missingAPIKey
        case "notConnected":
            return .notConnected
        case "invalidMessage":
            return .invalidMessage
        case "authenticationFailed":
            return .authenticationFailed
        case "fatalServerError":
            return .fatalServerError("test")
        case "recoverableTransportFailure":
            return .recoverableTransportFailure("test")
        case "sessionUpdateTimeout":
            return .sessionUpdateTimeout
        case "closeTimeout":
            return .closeTimeout
        case "cancelled":
            return .cancelled
        default:
            fatalError("unhandled error kind \(name)")
        }
    }
}
