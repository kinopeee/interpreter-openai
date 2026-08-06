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
