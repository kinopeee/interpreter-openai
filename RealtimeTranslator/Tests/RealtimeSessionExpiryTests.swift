import XCTest
@testable import RealtimeTranslator

final class RealtimeSessionExpiryTests: XCTestCase {
    private func session(_ expiresAt: Any) -> [String: Any] {
        ["id": "sess_x", "type": "translation", "expires_at": expiresAt]
    }

    // Given: 整数値の expires_at を持つ session
    // When: parseExpiresAt で読む
    // Then: unix 秒の整数として返る
    func testParseValidIntegerExpiresAt() {
        XCTAssertEqual(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(1_756_324_625)),
            1_756_324_625
        )
    }

    // Given: expires_at が数字文字列の session
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseStringExpiresAtIsUnknown() {
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session("1756324625"))
        )
    }

    // Given: 負数の expires_at を持つ session
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseNegativeExpiresAtIsUnknown() {
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(-1))
        )
    }

    // Given: 小数の expires_at を持つ session
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseFractionalExpiresAtIsUnknown() {
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(1_756_324_625.5))
        )
    }

    // Given: expires_at が null の session
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseNullExpiresAtIsUnknown() {
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(NSNull()))
        )
    }

    // Given: expires_at が bool の session（JSONSerialization では NSNumber として届く）
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseBoolExpiresAtIsUnknown() {
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(true))
        )
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(false))
        )
    }

    // Given: session 自体が欠落している
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseMissingSessionIsUnknown() {
        XCTAssertNil(RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: nil))
    }

    // Given: expires_at を持たない session
    // When: parseExpiresAt で読む
    // Then: 不明（nil）になる
    func testParseMissingExpiresAtIsUnknown() {
        XCTAssertNil(
            RealtimeSessionExpiry.parseExpiresAt(
                fromSessionPayload: ["id": "sess_x", "type": "translation"]
            )
        )
    }

    // Given: expires_at が 0 の session
    // When: parseExpiresAt で読む
    // Then: 0 が有効値として返る
    func testParseZeroExpiresAtIsValid() {
        XCTAssertEqual(
            RealtimeSessionExpiry.parseExpiresAt(fromSessionPayload: session(0)),
            0
        )
    }
}

final class EventDeliveryStateReceiveAndExpiryTests: XCTestCase {
    // Given: 新しい EventDeliveryState
    // When: lane ごとに recordReceive する
    // Then: lane ごとの受信数が独立して数えられる
    func testReceiveCountTracksEachLane() {
        let state = EventDeliveryState(epoch: 1)

        XCTAssertEqual(state.receiveCount(.source), 0)
        XCTAssertEqual(state.receiveCount(.translation(.english)), 0)

        state.recordReceive(lane: .source)
        state.recordReceive(lane: .source)
        state.recordReceive(lane: .translation(.english))

        XCTAssertEqual(state.receiveCount(.source), 2)
        XCTAssertEqual(state.receiveCount(.translation(.english)), 1)
        XCTAssertEqual(state.receiveCount(.translation(.japanese)), 0)
    }

    // Given: EventDeliveryState
    // When: recordSessionExpiry で lane の期限を記録・上書き・不明化する
    // Then: sessionExpiry が記録値を返し、不明化すると nil になる
    func testSessionExpiryRecordOverwriteAndClear() {
        let state = EventDeliveryState(epoch: 1)

        XCTAssertNil(state.sessionExpiry(.translation(.english)))

        state.recordSessionExpiry(lane: .translation(.english), expiresAtUnixSeconds: 1_756_324_625)
        XCTAssertEqual(state.sessionExpiry(.translation(.english)), 1_756_324_625)
        XCTAssertNil(state.sessionExpiry(.source))

        state.recordSessionExpiry(lane: .translation(.english), expiresAtUnixSeconds: 1_756_325_000)
        XCTAssertEqual(state.sessionExpiry(.translation(.english)), 1_756_325_000)

        state.recordSessionExpiry(lane: .translation(.english), expiresAtUnixSeconds: nil)
        XCTAssertNil(state.sessionExpiry(.translation(.english)))
    }
}

final class RealtimeSessionExpiryRemainingTests: XCTestCase {
    // Given: 正常な expires_at と現在の壁時計
    // When: remaining で残り時間へ変換する
    // Then: 130 秒の Duration が返る
    func testRemainingReturnsNormalDifference() {
        let remaining = RealtimeSessionExpiry.remaining(
            expiresAtUnixSeconds: 1_756_324_625 + 130,
            wallNowUnixSeconds: 1_756_324_625
        )
        XCTAssertEqual(remaining, .seconds(130))
    }

    // Given: Int.max 相当の巨大な expires_at
    // When: remaining で変換する
    // Then: ±10 年の範囲外として nil が返る（例外にならない）
    func testRemainingRejectsHugeValue() {
        XCTAssertNil(
            RealtimeSessionExpiry.remaining(
                expiresAtUnixSeconds: Int.max,
                wallNowUnixSeconds: 0
            )
        )
        XCTAssertNil(
            RealtimeSessionExpiry.remaining(
                expiresAtUnixSeconds: 0,
                wallNowUnixSeconds: Int64.max
            )
        )
    }
}
