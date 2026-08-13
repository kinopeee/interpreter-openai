import AppKit
import SwiftUI

enum SettingsWindowMetrics {
    static let contentWidth: CGFloat = 560
    /// 一般タブの表示言語・バージョン行と英語折り返しを、初回表示でスクロールなしに収める。
    static let contentHeight: CGFloat = 760
}

struct SettingsView: View {
    @Bindable var settings: AppSettings
    let apiKeyStore: any APIKeyStore
    var onSave: (() -> Void)?
    var onTuningChanged: (() -> Void)?

    @State private var apiKeyDraft = ""
    @State private var hasStoredKey = false
    @State private var statusMessage: String?
    @State private var statusIsError = false
    @State private var tuningDebounceTask: Task<Void, Never>?

    var body: some View {
        TabView {
            SettingsGeneralTab(
                settings: settings,
                apiKeyDraft: $apiKeyDraft,
                hasStoredKey: hasStoredKey,
                statusMessage: statusMessage,
                statusIsError: statusIsError,
                onSaveAPIKey: saveAPIKey,
                onDeleteAPIKey: deleteAPIKey
            )
            .tabItem {
                Label(UiCopy.text("settings.tab.general"), systemImage: "gearshape")
            }

            SettingsSpeechRecognitionTab(settings: settings)
                .tabItem {
                    Label(UiCopy.text("settings.tab.speech"), systemImage: "waveform")
                }

            SettingsSubtitleAndControlsTab(settings: settings)
                .tabItem {
                    Label(UiCopy.text("settings.tab.subtitles"), systemImage: "captions.bubble")
                }
        }
        .frame(
            width: SettingsWindowMetrics.contentWidth,
            height: SettingsWindowMetrics.contentHeight
        )
        .onAppear {
            refreshStoredKeyState()
        }
        .onDisappear {
            tuningDebounceTask?.cancel()
            tuningDebounceTask = nil
            onSave?()
        }
        .onChange(of: settings.transcriptionPrompt) { _, _ in
            scheduleTuningChangeNotification()
        }
        .onChange(of: settings.transcriptionKeywordsText) { _, _ in
            scheduleTuningChangeNotification()
        }
        .onChange(of: settings.transcriptionDelayMode) { _, _ in
            scheduleTuningChangeNotification()
        }
        .onChange(of: settings.languagePair) { _, _ in
            onSave?()
        }
        .onChange(of: settings.uiLanguage) { _, _ in
            onSave?()
        }
    }

    private func scheduleTuningChangeNotification() {
        tuningDebounceTask?.cancel()
        tuningDebounceTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 800_000_000)
            guard !Task.isCancelled else { return }
            onTuningChanged?()
        }
    }

    private func refreshStoredKeyState() {
        hasStoredKey = (try? apiKeyStore.load()?.isEmpty == false) == true
    }

    private func saveAPIKey() {
        do {
            try apiKeyStore.save(apiKeyDraft)
            apiKeyDraft = ""
            hasStoredKey = true
            statusIsError = false
            statusMessage = UiCopy.text("settings.apiKeySaveOk.mac")
        } catch {
            statusIsError = true
            statusMessage = error.localizedDescription
        }
    }

    private func deleteAPIKey() {
        do {
            try apiKeyStore.delete()
            apiKeyDraft = ""
            hasStoredKey = false
            statusIsError = false
            statusMessage = UiCopy.text("settings.apiKeyDeleteOk")
        } catch {
            statusIsError = true
            statusMessage = error.localizedDescription
        }
    }
}

// MARK: - Components

/// 文章入力を前提とした固定高さの複数行フィールド。
/// 入力量でレイアウトが動かないよう高さを固定し、超過分は内部スクロールへ逃がす。
private struct SettingsMultilineField: View {
    let title: String
    @Binding var text: String
    let height: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .accessibilityHidden(true)

            TextEditor(text: $text)
                .font(.body)
                .textEditorStyle(.plain)
                .scrollContentBackground(.hidden)
                .padding(.vertical, 5)
                .padding(.horizontal, 3)
                .frame(height: height)
                .background(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(Color(nsColor: .textBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .strokeBorder(Color(nsColor: .separatorColor), lineWidth: 1)
                )
                .accessibilityLabel(Text(title))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - Tabs

private struct SettingsGeneralTab: View {
    @Bindable var settings: AppSettings
    @Binding var apiKeyDraft: String
    let hasStoredKey: Bool
    let statusMessage: String?
    let statusIsError: Bool
    let onSaveAPIKey: () -> Void
    let onDeleteAPIKey: () -> Void

    var body: some View {
        Form {
            Section(UiCopy.text("settings.section.openai")) {
                LabeledContent(UiCopy.text("settings.model"), value: "gpt-realtime-translate")
                Picker(UiCopy.text("settings.languagePair"), selection: $settings.languagePair) {
                    Text(UiCopy.pairName(.jaEn)).tag(LanguagePair.jaEn)
                    Text(UiCopy.pairName(.jaEs)).tag(LanguagePair.jaEs)
                    Text(UiCopy.pairName(.enEs)).tag(LanguagePair.enEs)
                }
                Text(UiCopy.text("settings.languagePairAppliesNextRecording"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                LabeledContent(
                    UiCopy.text("settings.subtitleDisplay"),
                    value: UiCopy.text("settings.subtitleDisplayValue")
                )
                LabeledContent(
                    UiCopy.text("settings.translatedAudio"),
                    value: UiCopy.text("settings.translatedAudioValue")
                )

                Toggle(
                    UiCopy.text("settings.consentToggle"),
                    isOn: Binding(
                        get: { settings.hasAcceptedCurrentOpenAIConsent },
                        set: { accepted in
                            if accepted {
                                settings.acceptOpenAIConsent()
                            } else {
                                settings.acceptedOpenAIConsentVersion = 0
                            }
                        }
                    )
                )

                Text(UiCopy.text("settings.consentHelp"))
                .font(.caption)
                .foregroundStyle(.secondary)

                Link(
                    "OpenAI Pricing",
                    destination: URL(string: "https://developers.openai.com/api/docs/pricing")!
                )
                Link(
                    "OpenAI Data controls",
                    destination: URL(string: "https://developers.openai.com/api/docs/guides/your-data")!
                )
            }

            Section(UiCopy.text("settings.uiLanguage")) {
                Picker(UiCopy.text("settings.uiLanguage"), selection: $settings.uiLanguage) {
                    Text(UiCopy.text("settings.uiLanguage.system")).tag(UiLanguagePreference.system)
                    Text(UiCopy.text("settings.uiLanguage.ja")).tag(UiLanguagePreference.ja)
                    Text(UiCopy.text("settings.uiLanguage.en")).tag(UiLanguagePreference.en)
                }
                Text(UiCopy.text("settings.uiLanguageRestartHint"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Text(UiCopy.text("settings.appVersion", ["version": AppReleaseVersion.current]))
                    .foregroundStyle(.secondary)
            }

            Section(UiCopy.text("settings.section.apiKey")) {
                SecureField("sk-...", text: $apiKeyDraft)
                    .textFieldStyle(.roundedBorder)

                HStack {
                    Button(UiCopy.text("settings.save")) {
                        onSaveAPIKey()
                    }
                    .disabled(apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    Button(UiCopy.text("settings.delete"), role: .destructive) {
                        onDeleteAPIKey()
                    }
                    .disabled(!hasStoredKey)

                    Spacer()

                    Text(
                        hasStoredKey
                            ? UiCopy.text("settings.apiKeySaved.mac")
                            : UiCopy.text("settings.apiKeyNotSaved")
                    )
                        .font(.caption)
                        .foregroundStyle(hasStoredKey ? Color.secondary : Color.orange)
                }

                if let statusMessage {
                    Text(statusMessage)
                        .font(.caption)
                        .foregroundStyle(statusIsError ? .red : .secondary)
                }

                Text(UiCopy.text("settings.apiKeyStorageHelp.mac"))
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct SettingsSpeechRecognitionTab: View {
    @Bindable var settings: AppSettings

    private var keywordCount: Int {
        RealtimeSessionTuning.parseKeywords(from: settings.transcriptionKeywordsText).count
    }

    private var promptCharacterCount: Int {
        RealtimeSessionTuning.sanitizedPrompt(settings.transcriptionPrompt).count
    }

    private var keywordsContainForbiddenCharacters: Bool {
        settings.transcriptionKeywordsText.unicodeScalars.contains {
            RealtimeSessionTuning.forbiddenKeywordCharacters.contains($0)
        }
    }

    private var isPromptOverLimit: Bool {
        RealtimeSessionTuning.isPromptOverCharacterLimit(settings.transcriptionPrompt)
    }

    private var isKeywordLineCountOverLimit: Bool {
        RealtimeSessionTuning.isKeywordCountOverLimit(from: settings.transcriptionKeywordsText)
    }

    var body: some View {
        Form {
            Section(UiCopy.text("settings.section.recognition")) {
                Picker(UiCopy.text("settings.noiseReduction"), selection: $settings.noiseReductionMode) {
                    ForEach(RealtimeTranslationNoiseReduction.allCases, id: \.rawValue) { mode in
                        Text(mode.displayName).tag(mode.rawValue)
                    }
                }

                Picker(UiCopy.text("settings.transcriptionDelay"), selection: $settings.transcriptionDelayMode) {
                    ForEach(RealtimeTranscriptionDelay.allCases, id: \.rawValue) { delay in
                        Text(delay.displayName).tag(delay.rawValue)
                    }
                }

                Text(UiCopy.text("settings.delayHelp"))
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack {
                    Menu(UiCopy.text("settings.applyPreset")) {
                        ForEach(RealtimeSessionTuning.Preset.all) { preset in
                            Button(UiCopy.presetTitle(preset)) {
                                settings.applyPreset(preset)
                            }
                        }
                    }
                    Button(UiCopy.text("settings.restoreDefaults")) {
                        settings.restoreDefaultTranscriptionHints()
                    }
                }
            }

            Section(UiCopy.text("settings.section.hints")) {
                SettingsMultilineField(
                    title: UiCopy.text("settings.promptTitle"),
                    text: $settings.transcriptionPrompt,
                    height: 96
                )
                Text(UiCopy.text("settings.promptHelp"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(
                    UiCopy.text(
                        "settings.promptCounter",
                        [
                            "count": String(promptCharacterCount),
                            "limit": String(RealtimeSessionTuning.promptCharacterLimit),
                        ]
                    )
                        + (isPromptOverLimit ? UiCopy.text("settings.promptOverLimit") : "")
                )
                .font(.caption)
                .foregroundStyle(.secondary)

                SettingsMultilineField(
                    title: UiCopy.text("settings.keywordsTitle"),
                    text: $settings.transcriptionKeywordsText,
                    height: 112
                )
                Text(
                    UiCopy.text(
                        "settings.keywordCounter",
                        [
                            "count": String(keywordCount),
                            "limit": String(RealtimeSessionTuning.keywordLimit),
                        ]
                    )
                        + (isKeywordLineCountOverLimit ? UiCopy.text("settings.keywordOverLimit") : "")
                )
                .font(.caption)
                .foregroundStyle(.secondary)

                if keywordsContainForbiddenCharacters {
                    Text(UiCopy.text("settings.keywordForbidden"))
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }

            Section {
                Text(UiCopy.text("settings.tuningLiveHelp"))
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct SettingsSubtitleAndControlsTab: View {
    @Bindable var settings: AppSettings

    var body: some View {
        Form {
            Section(UiCopy.text("settings.section.subtitles")) {
                Stepper(value: $settings.fontSize, in: 18...48, step: 2) {
                    Text(
                        UiCopy.text(
                            "settings.fontSize",
                            ["size": String(Int(settings.fontSize))]
                        )
                    )
                }

                Toggle(UiCopy.text("settings.recordSubtitles"), isOn: $settings.recordSubtitles)
                Text(UiCopy.text("settings.recordSubtitlesHelp.mac"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section(UiCopy.text("settings.section.controls")) {
                Text(UiCopy.text("settings.controlsHelp.mac"))
                    .font(.callout)
            }
        }
        .formStyle(.grouped)
    }
}
