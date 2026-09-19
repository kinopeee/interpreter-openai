import XCTest
@testable import RealtimeTranslator

final class SubtitleSnapshotBannerTests: XCTestCase {
    func testReplacingStatusBannerLeavesOriginalUnchanged() {
        // Given: 確定済みペアを持ちバナー未設定の snapshot
        let original = SubtitleSnapshot(
            current: LiveSubtitle(
                sourceText: "こんにちは",
                translatedText: "Hello",
                lastUpdatedAt: .distantPast,
                state: .finalized
            ),
            statusBanner: nil
        )
        let storedCopy = original

        // When: バナーを差し替えたコピーを作る
        let copy = original.replacingStatusBanner("hint")

        // Then: コピーだけバナーが変わり、元は変更されない
        XCTAssertEqual(copy.statusBanner, "hint")
        XCTAssertEqual(copy.current, original.current)
        XCTAssertNil(original.statusBanner)
        XCTAssertEqual(original, storedCopy)
    }

    func testHintOverlaysSizeLimitBannerAndRestores() {
        // Given: 記録上限バナーを持つ snapshot
        let original = SubtitleSnapshot(
            current: .empty,
            statusBanner: SubtitleTranscriptStore.sizeLimitBanner
        )

        // When: ヒントで上書きし、元のバナーへ戻す
        let copy = original.replacingStatusBanner("hint")
        let restored = copy.replacingStatusBanner(original.statusBanner)

        // Then: 上書きと復元で元の snapshot と一致する
        XCTAssertEqual(copy.statusBanner, "hint")
        XCTAssertEqual(restored, original)
    }

    func testReplacingWithNilClearsBanner() {
        // Given: バナー付きの snapshot
        let original = SubtitleSnapshot(
            current: LiveSubtitle(
                sourceText: "こんにちは",
                translatedText: "Hello",
                lastUpdatedAt: .distantPast,
                state: .finalized
            ),
            statusBanner: "x"
        )

        // When: nil でバナーを差し替える
        let copy = original.replacingStatusBanner(nil)

        // Then: バナーだけ消え、字幕本文は同じ
        XCTAssertNil(copy.statusBanner)
        XCTAssertEqual(copy.current, original.current)
    }
}
