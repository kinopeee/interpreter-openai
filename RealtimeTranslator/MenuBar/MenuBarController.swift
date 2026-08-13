import AppKit
import SwiftUI

@MainActor
final class MenuBarController: NSObject {
    private let statusItem: NSStatusItem
    private weak var coordinator: AppCoordinator?
    private var startStopItem: NSMenuItem?

    init(coordinator: AppCoordinator) {
        self.coordinator = coordinator
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        configureStatusItem()
        rebuildMenu()
    }

    func refresh() {
        rebuildMenu()
    }

    private func configureStatusItem() {
        if let button = statusItem.button {
            button.image = NSImage(
                systemSymbolName: "captions.bubble",
                accessibilityDescription: "Realtime Translator"
            )
            button.image?.isTemplate = true
        }
    }

    private func rebuildMenu() {
        let menu = NSMenu()

        let startStop = NSMenuItem(
            title: startStopTitle(),
            action: #selector(toggleStartStop),
            keyEquivalent: ""
        )
        startStop.target = self
        startStopItem = startStop
        menu.addItem(startStop)
        menu.addItem(.separator())

        let directionItem = NSMenuItem(
            title: UiCopy.text("menu.languagePair", ["pair": UiCopy.pairName(pairDisplayPair())]),
            action: nil,
            keyEquivalent: ""
        )
        directionItem.isEnabled = false
        menu.addItem(directionItem)

        let displayItem = NSMenuItem(
            title: UiCopy.text("menu.subtitleDisplay"),
            action: nil,
            keyEquivalent: ""
        )
        displayItem.isEnabled = false
        menu.addItem(displayItem)

        let audioItem = NSMenuItem(
            title: UiCopy.text("menu.translatedAudio"),
            action: nil,
            keyEquivalent: ""
        )
        audioItem.isEnabled = false
        menu.addItem(audioItem)

        menu.addItem(.separator())

        let hasEntries = coordinator?.hasRecordedSubtitles == true
        let exportItem = NSMenuItem(
            title: UiCopy.text("menu.exportSubtitles"),
            action: #selector(exportSubtitles),
            keyEquivalent: ""
        )
        exportItem.target = self
        exportItem.isEnabled = hasEntries
        menu.addItem(exportItem)

        let clearItem = NSMenuItem(
            title: UiCopy.text("menu.clearSubtitles"),
            action: #selector(clearSubtitleTranscript),
            keyEquivalent: ""
        )
        clearItem.target = self
        clearItem.isEnabled = hasEntries
        menu.addItem(clearItem)

        menu.addItem(.separator())
        let editPositionItem = NSMenuItem(
            title: UiCopy.text("menu.editPosition"),
            action: #selector(togglePositionEditing),
            keyEquivalent: ""
        )
        editPositionItem.target = self
        editPositionItem.state = coordinator?.isEditingSubtitlePosition == true ? .on : .off
        menu.addItem(editPositionItem)

        let settingsItem = NSMenuItem(
            title: UiCopy.text("menu.settings"),
            action: #selector(openSettings),
            keyEquivalent: ","
        )
        settingsItem.target = self
        menu.addItem(settingsItem)

        menu.addItem(.separator())
        let quitItem = NSMenuItem(
            title: UiCopy.text("menu.quit"),
            action: #selector(quit),
            keyEquivalent: "q"
        )
        quitItem.target = self
        menu.addItem(quitItem)

        statusItem.menu = menu
        updateIcon()
    }

    private func pairDisplayPair() -> LanguagePair {
        coordinator?.languagePair ?? .jaEn
    }

    private func startStopTitle() -> String {
        switch coordinator?.translationState {
        case .connecting, .listening, .reconnecting, .closing:
            return UiCopy.text("menu.stopTranslation")
        case .idle, .error, .none:
            return UiCopy.text("menu.startTranslation")
        }
    }

    private func updateIcon() {
        guard let button = statusItem.button else { return }
        let state = coordinator?.translationState ?? .idle
        let symbolName: String
        switch state {
        case .idle:
            symbolName = "captions.bubble"
        case .connecting, .closing, .reconnecting:
            symbolName = "ellipsis.bubble"
        case .listening:
            symbolName = "waveform.badge.mic"
        case .error:
            symbolName = "exclamationmark.bubble"
        }
        button.image = NSImage(systemSymbolName: symbolName, accessibilityDescription: state.rawValue)
        button.image?.isTemplate = true
        button.toolTip = "Realtime Translator (\(state.rawValue))"
    }

    @objc private func toggleStartStop() {
        coordinator?.toggleTranslation()
    }

    @objc private func exportSubtitles() {
        coordinator?.exportSubtitles()
    }

    @objc private func clearSubtitleTranscript() {
        coordinator?.clearSubtitleTranscript()
    }

    @objc private func togglePositionEditing() {
        coordinator?.toggleSubtitlePositionEditing()
    }

    @objc private func openSettings() {
        coordinator?.openSettings()
    }

    @objc private func quit() {
        coordinator?.quit()
    }
}
