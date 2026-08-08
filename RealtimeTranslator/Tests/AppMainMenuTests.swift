import AppKit
import XCTest
@testable import RealtimeTranslator

final class AppMainMenuTests: XCTestCase {
    func testQuitMenuItemUsesTerminateAction() {
        // Given: LSUIElement向けに組み立てた mainMenu
        let menu = AppMainMenu.makeMenu()

        // When: アプリメニューの終了項目を探す
        let quitItem = menu.items.first?.submenu?.items.first {
            $0.title == "Realtime Translator を終了"
        }

        // Then: ⌘Q は NSApp.terminate へ繋がり、applicationShouldTerminate で session.stop する
        XCTAssertEqual(quitItem?.keyEquivalent, "q")
        XCTAssertEqual(quitItem?.action, #selector(NSApplication.terminate(_:)))
    }

    func testEditMenuProvidesPasteKeyEquivalent() {
        // Given: LSUIElement向けに組み立てた mainMenu
        let menu = AppMainMenu.makeMenu()

        // When: 編集メニューからペースト項目を探す
        let editMenu = menu.items.compactMap(\.submenu).first { $0.title == "編集" }
        let pasteItem = editMenu?.items.first { $0.title == "ペースト" }

        // Then: ⌘V が paste: へ繋がる
        XCTAssertNotNil(editMenu)
        XCTAssertEqual(pasteItem?.keyEquivalent, "v")
        XCTAssertEqual(pasteItem?.action, #selector(NSText.paste(_:)))
    }

    func testEditMenuProvidesStandardClipboardShortcuts() {
        // Given: 編集メニュー
        let editMenu = AppMainMenu.makeEditMenu()
        let itemsByTitle = Dictionary(
            uniqueKeysWithValues: editMenu.items
                .filter { !$0.isSeparatorItem }
                .map { ($0.title, $0) }
        )

        // When/Then: カット・コピー・すべて選択のキーが揃っている
        XCTAssertEqual(itemsByTitle["カット"]?.keyEquivalent, "x")
        XCTAssertEqual(itemsByTitle["コピー"]?.keyEquivalent, "c")
        XCTAssertEqual(itemsByTitle["すべてを選択"]?.keyEquivalent, "a")
    }
}
