import XCTest
@testable import RealtimeTranslator

final class RealtimeTranslationErrorTests: XCTestCase {
    func testAuthenticationFailureMatchesKnownAuthSignals() {
        // Given/When/Then: 既知の認証失敗シグナルは true
        XCTAssertTrue(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "invalid_api_key",
                message: "Incorrect API key provided: sk-example"
            )
        )
        XCTAssertTrue(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "unauthorized",
                message: "request rejected"
            )
        )
        XCTAssertTrue(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "401",
                message: "forbidden"
            )
        )
        XCTAssertTrue(
            RealtimeTranslationError.isAuthenticationFailure(
                code: nil,
                message: "HTTP 403 Forbidden"
            )
        )
        XCTAssertTrue(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "server_error",
                message: "authentication error while validating credentials"
            )
        )
        XCTAssertTrue(
            RealtimeTranslationError.isAuthenticationFailure(
                code: nil,
                message: "Invalid Authorization header provided"
            )
        )
    }

    func testAuthenticationFailureIgnoresUnrelatedSubstrings() {
        // Given/When/Then: authority / oauth / 4010 などへ誤爆しない
        XCTAssertFalse(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "authority_mismatch",
                message: "certificate authority rejected the peer"
            )
        )
        XCTAssertFalse(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "oauth_scope_missing",
                message: "oauth provider temporarily unavailable"
            )
        )
        XCTAssertFalse(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "E4010",
                message: "internal error code 4010"
            )
        )
        XCTAssertFalse(
            RealtimeTranslationError.isAuthenticationFailure(
                code: "rate_limit_exceeded",
                message: "too many requests"
            )
        )
    }

    func testSanitizedServerMessageRedactsKeyMaterial() {
        // Given/When/Then: キー断片や Authorization を含む文言は汎用エラーになる
        XCTAssertEqual(
            RealtimeTranslationError.sanitizedServerMessage(
                "Provider echo included sk-should-not-appear"
            ),
            "翻訳サーバーでエラーが発生しました"
        )
        XCTAssertEqual(
            RealtimeTranslationError.sanitizedServerMessage(
                "bearer token rejected"
            ),
            "翻訳サーバーでエラーが発生しました"
        )
        XCTAssertEqual(
            RealtimeTranslationError.sanitizedServerMessage("timeout waiting for peer"),
            "timeout waiting for peer"
        )
    }
}
