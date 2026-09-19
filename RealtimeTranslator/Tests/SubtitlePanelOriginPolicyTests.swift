import XCTest
@testable import RealtimeTranslator

final class SubtitlePanelOriginPolicyTests: XCTestCase {
    func testPersistsOriginWhenEditingOffAndNotMirrored() {
        // Given: 編集 OFF かつミラーリングなし
        // When: 保存可否を判定する
        let result = SubtitlePanelOriginPolicy.shouldPersistOrigin(
            isEditingPosition: false,
            isMirroringActive: false
        )

        // Then: 位置を永続化する
        XCTAssertTrue(result)
    }

    func testSkipsPersistWhenMirrored() {
        // Given: 編集 OFF だがミラーリング中
        // When: 保存可否を判定する
        let result = SubtitlePanelOriginPolicy.shouldPersistOrigin(
            isEditingPosition: false,
            isMirroringActive: true
        )

        // Then: 主画面へクランプされた位置を保存しない
        XCTAssertFalse(result)
    }

    func testSkipsPersistWhileStillEditing() {
        // Given: 編集 ON のまま（ミラーリングなし）
        // When: 保存可否を判定する
        let result = SubtitlePanelOriginPolicy.shouldPersistOrigin(
            isEditingPosition: true,
            isMirroringActive: false
        )

        // Then: 編集中は保存しない
        XCTAssertFalse(result)
    }
}
