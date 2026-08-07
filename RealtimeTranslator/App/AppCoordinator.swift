import AppKit
import SwiftUI
import UniformTypeIdentifiers

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
    private lazy var transcriptStore: SubtitleTranscriptStore = {
        let url = (try? SubtitleTranscriptStore.defaultFileURL())
            ?? FileManager.default.temporaryDirectory
            .appendingPathComponent("realtimetranslator-session.txt")
        return SubtitleTranscriptStore(fileURL: url)
    }()
    private let hotKeys = HotKeyManager()
    private var settingsWindow: NSWindow?
    private var lastSnapshot = SubtitleSnapshot.empty
    private var didAnnounceTranscriptCap = false

    init(apiKeyStore: any APIKeyStore = KeychainAPIKeyStore()) {
        self.apiKeyStore = apiKeyStore
        super.init()
    }

    var hasRecordedSubtitles: Bool {
        transcriptStore.hasEntries
    }

    func start() {
        NSApp.setActivationPolicy(.accessory)
        #if DEBUG
        do {
            _ = try APIKeyBootstrap.importFromEnvironmentIfNeeded(store: apiKeyStore)
        } catch {
            AppLogger.general.error(
                "API key bootstrap failed: \(AppLogger.redact(error.localizedDescription), privacy: .public)"
            )
        }
        #endif

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
            statusBanner: "待機中 — Control + Option + Space で録音開始"
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

        if settings.recordSubtitles {
            handleTranscriptResult(transcriptStore.markSessionStart())
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

    func exportSubtitles() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.plainText]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        panel.nameFieldStringValue = SubtitleTranscriptStore.defaultExportFileName()
        panel.begin { [weak self] response in
            guard let self else { return }
            guard response == .OK, let url = panel.url else { return }
            do {
                try self.transcriptStore.exportCopy(to: url)
            } catch {
                AppLogger.general.error("subtitle transcript export failed")
                self.presentMessage(SubtitleTranscriptStore.writeFailureBanner)
            }
        }
    }

    func clearSubtitleTranscript() {
        let alert = NSAlert()
        alert.messageText = "字幕記録をクリアしますか？"
        alert.informativeText = "ローカルの字幕記録ファイルを空にします。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "クリア")
        alert.addButton(withTitle: "キャンセル")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        do {
            try transcriptStore.clear()
            didAnnounceTranscriptCap = false
            menuBarController.refresh()
        } catch {
            AppLogger.general.error("subtitle transcript clear failed")
            presentMessage(SubtitleTranscriptStore.writeFailureBanner)
        }
    }

    func openSettings() {
        if let settingsWindow, settingsWindow.isVisible {
            settingsWindow.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let view = SettingsView(
            settings: settings,
            apiKeyStore: apiKeyStore,
            onSave: { [weak self] in
                self?.menuBarController.refresh()
            },
            onTuningChanged: { [weak self] in
                guard let self else { return }
                Task { await self.interpretationSession.applyTuningChange() }
            }
        )
        let hosting = NSHostingController(rootView: view)
        let window = NSWindow(contentViewController: hosting)
        window.title = "Realtime Translator 設定"
        window.styleMask = [.titled, .closable]
        window.setContentSize(
            NSSize(
                width: SettingsWindowMetrics.contentWidth,
                height: SettingsWindowMetrics.contentHeight
            )
        )
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

    private func recordFinalizedSubtitleIfNeeded(_ subtitle: LiveSubtitle) {
        guard settings.recordSubtitles else { return }
        guard subtitle.state == .finalized, !subtitle.isEmpty else { return }
        handleTranscriptResult(
            transcriptStore.appendEntry(
                sourceText: subtitle.sourceText,
                translatedText: subtitle.translatedText
            )
        )
    }

    private func handleTranscriptResult(_ result: SubtitleTranscriptAppendResult) {
        switch result {
        case .appended:
            menuBarController.refresh()
        case .capped:
            guard !didAnnounceTranscriptCap else { return }
            didAnnounceTranscriptCap = true
            applyTranscriptBanner(SubtitleTranscriptStore.sizeLimitBanner)
            AppLogger.general.notice("subtitle transcript reached size limit")
        case .failed:
            applyTranscriptBanner(SubtitleTranscriptStore.writeFailureBanner)
            AppLogger.general.error("subtitle transcript write failed")
        case .skippedDuplicate, .skippedEmpty:
            break
        }
    }

    private func applyTranscriptBanner(_ message: String) {
        var snapshot = lastSnapshot
        snapshot.statusBanner = message
        lastSnapshot = snapshot
        subtitleWindow.update(
            snapshot: snapshot,
            fontSize: settings.fontSize,
            translationState: translationState
        )
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
        recordFinalizedSubtitleIfNeeded(snapshot.current)

        let displayedSnapshot: SubtitleSnapshot
        if translationState == .idle,
           snapshot.current.isEmpty,
           snapshot.statusBanner == nil
        {
            displayedSnapshot = idleSnapshot
        } else {
            displayedSnapshot = snapshot
        }
        // 記録上限バナーを、直後の session snapshot で上書きしない。
        if didAnnounceTranscriptCap,
           lastSnapshot.statusBanner == SubtitleTranscriptStore.sizeLimitBanner,
           displayedSnapshot.statusBanner == nil
        {
            var merged = displayedSnapshot
            merged.statusBanner = SubtitleTranscriptStore.sizeLimitBanner
            lastSnapshot = merged
        } else {
            lastSnapshot = displayedSnapshot
        }
        subtitleWindow.update(
            snapshot: lastSnapshot,
            fontSize: settings.fontSize,
            translationState: translationState
        )
    }

    func interpretationSession(
        _ session: InterpretationSession,
        didEncounterMessage message: String
    ) {
        // error 時は InterpretationSession が既に statusBanner へ載せている。
        // runModal はホットキー／トレイ操作を止めるため使わない。
        AppLogger.general.notice(
            "Session message: \(AppLogger.redact(message), privacy: .public)"
        )
    }
}
