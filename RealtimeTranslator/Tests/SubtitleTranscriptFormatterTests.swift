import XCTest
@testable import RealtimeTranslator

final class SubtitleTranscriptFormatterTests: XCTestCase {
    // Given: shared fixture のファイル上限とバナー文言
    // When: ストア定数と照合する
    // Then: 10 MB 上限とバナー文言が一致する
    func testLimitsAndMessagesMatchFixture() throws {
        let root = try SharedFixtures.load("transcript")
        let limits = try XCTUnwrap(root["limits"] as? [String: Any])
        let messages = try XCTUnwrap(root["messages"] as? [String: Any])

        XCTAssertEqual(
            SharedFixtures.number(limits["maxFileBytes"]),
            SubtitleTranscriptLimits.maxFileBytes
        )
        XCTAssertEqual(
            SharedFixtures.text(messages["sizeLimitBanner"]),
            SubtitleTranscriptLimits.sizeLimitBanner
        )
        XCTAssertEqual(
            SharedFixtures.text(messages["writeFailureBanner"]),
            SubtitleTranscriptLimits.writeFailureBanner
        )
    }

    // Given: fixture の entry / sessionStart ケース
    // When: フォーマッタで整形する
    // Then: 期待するプレーンテキストになる
    func testFormatMatchesFixture() throws {
        for name in try SharedFixtures.caseNames("transcript", "format") {
            let fixture = try SharedFixtures.case("transcript", "format", name)
            let kind = SharedFixtures.text(fixture["kind"])
            let timestamp = SharedFixtures.text(fixture["timestamp"])
            let expected = SharedFixtures.text(fixture["expected"])

            switch kind {
            case "entry":
                let actual = SubtitleTranscriptFormatter.formatEntry(
                    timestamp: timestamp,
                    sourceText: SharedFixtures.text(fixture["sourceText"]),
                    translatedText: SharedFixtures.text(fixture["translatedText"])
                )
                XCTAssertEqual(actual, expected, name)
            case "sessionStart":
                let actual = SubtitleTranscriptFormatter.formatSessionStart(timestamp: timestamp)
                XCTAssertEqual(actual, expected, name)
            default:
                XCTFail("unhandled kind \(kind)")
            }
        }
    }

    // Given: 固定の日時とタイムゾーン
    // When: タイムスタンプを整形する
    // Then: オフセット付き ISO8601 になる
    func testFormatTimestampIncludesOffset() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 9 * 3600)!
        let components = DateComponents(
            calendar: calendar,
            timeZone: calendar.timeZone,
            year: 2026,
            month: 8,
            day: 7,
            hour: 15,
            minute: 40,
            second: 12
        )
        let date = try! XCTUnwrap(components.date)

        XCTAssertEqual(
            SubtitleTranscriptFormatter.formatTimestamp(date, timeZone: calendar.timeZone),
            "2026-08-07T15:40:12+09:00"
        )
    }

    // Given: UTC の固定時刻
    // When: タイムスタンプを整形する
    // Then: オフセットは Z 表記になる
    func testFormatTimestampUsesZForUTC() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let components = DateComponents(
            calendar: calendar,
            timeZone: calendar.timeZone,
            year: 2026,
            month: 8,
            day: 7,
            hour: 16,
            minute: 0,
            second: 0
        )
        let date = try! XCTUnwrap(components.date)

        XCTAssertEqual(
            SubtitleTranscriptFormatter.formatTimestamp(date, timeZone: calendar.timeZone),
            "2026-08-07T16:00:00Z"
        )
    }
}
