import AppKit
import XCTest
@testable import RealtimeTranslator

final class AppMainMenuTests: XCTestCase {
    func testQuitMenuItemUsesTerminateAction() {
        // Given: LSUIElement向けに組み立てた mainMenu
        let menu = AppMainMenu.makeMenu()

        // When: アプリメニューの終了項目を identifier / action で探す
        let quitItem = menu.items.first?.submenu?.items.first {
            $0.identifier == AppMainMenu.quitIdentifier
        }

        // Then: ⌘Q は NSApp.terminate へ繋がり、applicationShouldTerminate で session.stop する
        XCTAssertEqual(quitItem?.keyEquivalent, "q")
        XCTAssertEqual(quitItem?.action, #selector(NSApplication.terminate(_:)))
    }

    func testEditMenuProvidesPasteKeyEquivalent() {
        // Given: LSUIElement向けに組み立てた mainMenu
        let menu = AppMainMenu.makeMenu()

        // When: 編集メニューを identifier で探し、ペーストを action で探す
        let editMenu = menu.items.first { $0.identifier == AppMainMenu.editIdentifier }?.submenu
        let pasteItem = editMenu?.items.first { $0.action == #selector(NSText.paste(_:)) }

        // Then: ⌘V が paste: へ繋がる
        XCTAssertNotNil(editMenu)
        XCTAssertEqual(pasteItem?.keyEquivalent, "v")
        XCTAssertEqual(pasteItem?.action, #selector(NSText.paste(_:)))
    }

    func testEditMenuProvidesStandardClipboardShortcuts() {
        // Given: 編集メニュー
        let editMenu = AppMainMenu.makeEditMenu()
        let cut = editMenu.items.first { $0.action == #selector(NSText.cut(_:)) }
        let copy = editMenu.items.first { $0.action == #selector(NSText.copy(_:)) }
        let selectAll = editMenu.items.first { $0.action == #selector(NSText.selectAll(_:)) }

        // When/Then: カット・コピー・すべて選択のキーが揃っている
        XCTAssertEqual(cut?.keyEquivalent, "x")
        XCTAssertEqual(copy?.keyEquivalent, "c")
        XCTAssertEqual(selectAll?.keyEquivalent, "a")
    }
}
