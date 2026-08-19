import XCTest
@testable import RealtimeTranslator

final class AppLoggerTests: XCTestCase {
    func testRedactReplacesAPIKeyFragments() {
        // Given: APIキー断片を含むメッセージ
        let input = "invalid key sk-abcdefghi"

        // When: redact する
        let redacted = AppLogger.redact(input)

        // Then: キー断片は伏字化される
        XCTAssertEqual(redacted, "invalid key \(AppLogger.redactedPlaceholder)")
    }

    func testRedactReplacesUppercaseAPIKeyFragments() {
        // Given: 大文字 SK- 断片
        let redacted = AppLogger.redact("invalid key SK-ABCDEFGHI")

        // Then: キー断片は伏字化される
        XCTAssertFalse(redacted.contains("SK-ABCDEFGHI"))
        XCTAssertFalse(redacted.localizedCaseInsensitiveContains("sk-abcdefgh"))
        XCTAssertTrue(redacted.contains(AppLogger.redactedPlaceholder))
    }

    func testRedactReplacesZeroWidthObfuscatedAPIKeyFragments() {
        // Given: ZWSP を挟んだ sk- 断片
        let redacted = AppLogger.redact("invalid key s\u{200B}k-abcdefghi")

        // Then: 不可視文字を除いたキー断片は残らない
        XCTAssertFalse(redacted.localizedCaseInsensitiveContains("sk-abcdefghi"))
        XCTAssertFalse(redacted.contains("abcdefghi"))
        XCTAssertTrue(redacted.contains(AppLogger.redactedPlaceholder))
    }

    func testRedactReplacesBearerAndAuthorization() {
        // Given: Bearer と Authorization ヘッダ断片
        let bearer = "Bearer abc.def-ghi"
        let authorization = "Authorization: secret-token"

        // When/Then: それぞれ伏字化される
        XCTAssertEqual(AppLogger.redact(bearer), AppLogger.redactedPlaceholder)
        XCTAssertEqual(
            AppLogger.redact(authorization),
            AppLogger.redactedPlaceholder
        )
    }

    func testRedactReplacesCompleteBearerAndAuthorizationCredentials() {
        // Given: Base64 文字を含む Bearer と scheme + 資格情報の Authorization
        let bearer = AppLogger.redact("token Bearer abc+def/ghi== extra")
        let basic = AppLogger.redact("Authorization: Basic YWJjZA==")

        // Then: `+` `/` `=` や Basic の続きも残らない
        XCTAssertFalse(bearer.contains("abc+def/ghi=="))
        XCTAssertEqual(bearer, "token \(AppLogger.redactedPlaceholder) extra")
        XCTAssertFalse(basic.contains("YWJjZA=="))
        XCTAssertFalse(basic.contains("Basic"))
        XCTAssertEqual(basic, AppLogger.redactedPlaceholder)
    }

    func testRedactReplacesTabObfuscatedAPIKeyFragments() {
        // Given: TAB で分断した sk- 断片
        let redacted = AppLogger.redact("invalid key s\u{0009}k-abcdefghi")

        // Then: 制御空白を除いたキー断片は残らない
        XCTAssertFalse(redacted.localizedCaseInsensitiveContains("sk-abcdefghi"))
        XCTAssertFalse(redacted.contains("abcdefghi"))
        XCTAssertTrue(redacted.contains(AppLogger.redactedPlaceholder))
    }

    func testRedactReplacesSafetyIdentifierAndUUID() {
        // Given: Safety Identifier と UUID
        let safety = "OpenAI-Safety-Identifier: deadbeefcafe"
        let uuid = "session 550e8400-e29b-41d4-a716-446655440000 ended"

        // When/Then: 伏字化される
        XCTAssertEqual(AppLogger.redact(safety), AppLogger.redactedPlaceholder)
        XCTAssertEqual(
            AppLogger.redact(uuid),
            "session \(AppLogger.redactedPlaceholder) ended"
        )
    }

    func testRedactPassesThroughPlainMessages() {
        // Given: 秘密を含まないメッセージ
        let input = "translation reconnect attempt 2"

        // When/Then: そのまま残る
        XCTAssertEqual(AppLogger.redact(input), input)
    }
}
