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
