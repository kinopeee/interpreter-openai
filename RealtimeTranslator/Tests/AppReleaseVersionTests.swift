import XCTest
@testable import RealtimeTranslator

final class AppReleaseVersionTests: XCTestCase {
    // Given: 空や空白だけの入力
    // When: 表示値へ正規化する
    // Then: 未リリースの 0.0.0 を返す
    func testBlankRawBecomesUnpublished() {
        XCTAssertEqual(AppReleaseVersion.displayValue(from: nil), AppReleaseVersion.unpublished)
        XCTAssertEqual(AppReleaseVersion.displayValue(from: ""), AppReleaseVersion.unpublished)
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "   "), AppReleaseVersion.unpublished)
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "+build"), AppReleaseVersion.unpublished)
    }

    // Given: リリースタグや InformationalVersion
    // When: 表示値へ正規化する
    // Then: 先頭の v と + 以降のビルドメタデータを落とし、本文だけ残す
    func testDisplayValueStripsTagPrefixAndBuildMetadata() {
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "0.1.0"), "0.1.0")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "v0.1.0"), "0.1.0")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "V0.1.0"), "0.1.0")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "  v0.1.0  "), "0.1.0")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "0.1.0+abc123"), "0.1.0")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "v0.1.0-rc.1+sha"), "0.1.0-rc.1")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "pr12"), "pr12")
        XCTAssertEqual(AppReleaseVersion.displayValue(from: "very"), "very")
    }

    // Given: テストホストの Info.plist
    // When: current を読む
    // Then: CFBundleShortVersionString と同じ正規化結果になる
    func testCurrentMatchesBundleShortVersionString() {
        let raw = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        XCTAssertEqual(AppReleaseVersion.current, AppReleaseVersion.displayValue(from: raw))
        XCTAssertFalse(AppReleaseVersion.current.isEmpty)
    }
}
