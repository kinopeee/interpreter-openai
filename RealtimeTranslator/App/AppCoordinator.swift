import AppKit
import SwiftUI

@MainActor
final class AppCoordinator: NSObject {
    let settings = AppSettings()
    let apiKeyStore: any APIKeyStore

    private(set) var translationState: TranslationState = .idle
    private(set) var isEditingSubtitlePosition = false

    private var menuBarController: MenuBarController!
    private lazy var subtitleWindow = SubtitleWindowController()
    private lazy var interpretationSession = InterpretationSession(
        apiKeyStore: apiKeyStore,
        tuningProvider: { [settings] in
            settings.sessionTuning()
        }
    )
    private let hotKeys = HotKeyManager()
    private var settingsWindow: NSWindow?
    private var lastSnapshot = SubtitleSnapshot.empty

    init(apiKeyStore: any APIKeyStore = KeychainAPIKeyStore()) {
        self.apiKeyStore = apiKeyStore
        super.init()
    }

    func start() {
        NSApp.setActivationPolicy(.accessory)
        do {
            _ = try APIKeyBootstrap.importFromEnvironmentIfNeeded(store: apiKeyStore)
        } catch {
            AppLogger.general.error(
                "API key bootstrap failed: \(error.localizedDescription, privacy: .public)"
            )
        }

        interpretationSession.delegate = self
        menuBarController = MenuBarController(coordinator: self)
        subtitleWindow.setRecordingHandler { [weak self] in
            self?.toggleTranslation()
        }
        subtitleWindow.applySavedOrigin(settings.customPanelOrigin())
        lastSnapshot = idleSnapshot
        subtitleWindow.update(
            snapshot: idleSnapshot,
            fontSize: settings.fontSize,
            translationState: translationState
        )
        subtitleWindow.show()
        registerHotKeys()
        menuBarController.refresh()
        AppLogger.general.info("AppCoordinator started with OpenAI Realtime translation")
    }

    private var idleSnapshot: SubtitleSnapshot {
        SubtitleSnapshot(
            current: .empty,
            statusBanner: "待機中 — 「録音開始」を押してください"
        )
    }

    func toggleTranslation() {
        switch translationState {
        case .idle, .error:
            beginTranslation()
        case .connecting, .listening, .reconnecting:
            Task { await interpretationSession.stop() }
        case .closing:
            break
        }
    }

    private func beginTranslation() {
        guard settings.hasAcceptedCurrentOpenAIConsent else {
            presentMessage("録音を開始する前に、設定でOpenAIへの送信に同意してください。")
            openSettings()
            return
        }
        guard apiKeyStore.hasStoredKey else {
            presentMessage("録音を開始する前に、設定でOpenAI APIキーを保存してください。")
            openSettings()
            return
        }

        writeStatusFile("starting")
        Task { await interpretationSession.start() }
    }

    private func writeStatusFile(_ status: String) {
        AppStatusFile.write(status, state: translationState.rawValue)
    }

    func toggleSubtitlePositionEditing() {
        isEditingSubtitlePosition.toggle()
        subtitleWindow.setPositionEditingEnabled(isEditingSubtitlePosition)
        if !isEditingSubtitlePosition {
            settings.savePanelOrigin(subtitleWindow.currentOrigin)
        }
        menuBarController.refresh()
    }

    func openSettings() {
        if let settingsWindow, settingsWindow.isVisible {
            settingsWindow.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let view = SettingsView(
            settings: settings,
            apiKeyStore: apiKeyStore
        ) { [weak self] in
            self?.menuBarController.refresh()
        }
        let hosting = NSHostingController(rootView: view)
        let window = NSWindow(contentViewController: hosting)
        window.title = "Realtime Translator 設定"
        window.styleMask = [.titled, .closable]
        window.setContentSize(NSSize(width: 560, height: 640))
        window.center()
        window.isReleasedWhenClosed = false
        settingsWindow = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func quit() {
        Task {
            await interpretationSession.stop()
            hotKeys.unregisterAll()
            NSApp.terminate(nil)
        }
    }

    private func registerHotKeys() {
        hotKeys.handler = { [weak self] action in
            guard let self else { return }
            switch action {
            case .toggleStartStop:
                self.toggleTranslation()
            }
        }
        hotKeys.registerDefaults()
    }

    private func presentMessage(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Realtime Translator"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.runModal()
    }
}

extension AppCoordinator: InterpretationSessionDelegate {
    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateState state: TranslationState
    ) {
        translationState = state
        menuBarController.refresh()
        writeStatusFile(state.rawValue)
        if state == .idle, lastSnapshot.current.isEmpty {
            lastSnapshot = idleSnapshot
        }
        subtitleWindow.update(
            snapshot: lastSnapshot,
            fontSize: settings.fontSize,
            translationState: state
        )
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didUpdateSubtitles snapshot: SubtitleSnapshot
    ) {
        let displayedSnapshot: SubtitleSnapshot
        if translationState == .idle,
           snapshot.current.isEmpty,
           snapshot.statusBanner == nil
        {
            displayedSnapshot = idleSnapshot
        } else {
            displayedSnapshot = snapshot
        }
        lastSnapshot = displayedSnapshot
        subtitleWindow.update(
            snapshot: displayedSnapshot,
            fontSize: settings.fontSize,
            translationState: translationState
        )
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didEncounterMessage message: String
    ) {
        if translationState == .error {
            presentMessage(message)
        }
        AppLogger.general.notice("Session message: \(message, privacy: .public)")
    }
}
