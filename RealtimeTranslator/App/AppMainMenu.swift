import AppKit

/// LSUIElement / accessory アプリ向けの最小 mainMenu。
/// Edit がないと設定画面の TextField / TextEditor で ⌘V などが効かない。
enum AppMainMenu {
    static let quitIdentifier = NSUserInterfaceItemIdentifier("menu.quitApp")
    static let editIdentifier = NSUserInterfaceItemIdentifier("menu.edit")

    @MainActor
    static func install() {
        NSApp.mainMenu = makeMenu()
    }

    static func makeMenu() -> NSMenu {
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu()
        let quitItem = appMenu.addItem(
            withTitle: UiCopy.text("menu.quitApp"),
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        quitItem.identifier = quitIdentifier
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        let editMenuItem = NSMenuItem()
        editMenuItem.identifier = editIdentifier
        editMenuItem.submenu = makeEditMenu()
        mainMenu.addItem(editMenuItem)

        return mainMenu
    }

    static func makeEditMenu() -> NSMenu {
        let editMenu = NSMenu(title: UiCopy.text("menu.edit"))
        editMenu.addItem(withTitle: UiCopy.text("menu.undo"), action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: UiCopy.text("menu.redo"), action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: UiCopy.text("menu.cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: UiCopy.text("menu.copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: UiCopy.text("menu.paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: UiCopy.text("menu.delete"), action: #selector(NSText.delete(_:)), keyEquivalent: "")
        editMenu.addItem(
            withTitle: UiCopy.text("menu.selectAll"),
            action: #selector(NSText.selectAll(_:)),
            keyEquivalent: "a"
        )
        return editMenu
    }
}
