import AppKit
import SwiftUI

enum SettingsWindowMetrics {
    static let contentWidth: CGFloat = 560
    static let contentHeight: CGFloat = 520
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
                Label("一般", systemImage: "gearshape")
            }

            SettingsSpeechRecognitionTab(settings: settings)
                .tabItem {
                    Label("音声認識", systemImage: "waveform")
                }

            SettingsSubtitleAndControlsTab(settings: settings)
                .tabItem {
                    Label("字幕・操作", systemImage: "captions.bubble")
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
            statusMessage = "APIキーをKeychainへ保存しました"
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
            statusMessage = "APIキーを削除しました"
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
            Section("OpenAI Realtime") {
                LabeledContent("モデル", value: "gpt-realtime-translate")
                LabeledContent("翻訳方向", value: "自動（日本語 ↔ 英語）")
                LabeledContent("字幕表示", value: "原文＋翻訳")
                LabeledContent("翻訳音声", value: "字幕のみ（再生なし）")

                Toggle(
                    "マイク音声をOpenAI APIへ送信することに同意する",
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

                Text(
                    "録音中はマイク音声・原文・訳文がOpenAIへ送信されます。オンライン接続とAPI料金が必要です。データ取扱いはOpenAIのData controlsを確認してください。"
                )
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

            Section("APIキー") {
                SecureField("sk-...", text: $apiKeyDraft)
                    .textFieldStyle(.roundedBorder)

                HStack {
                    Button("保存") {
                        onSaveAPIKey()
                    }
                    .disabled(apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    Button("削除", role: .destructive) {
                        onDeleteAPIKey()
                    }
                    .disabled(!hasStoredKey)

                    Spacer()

                    Text(hasStoredKey ? "Keychainに保存済み" : "未保存")
                        .font(.caption)
                        .foregroundStyle(hasStoredKey ? Color.secondary : Color.orange)
                }

                if let statusMessage {
                    Text(statusMessage)
                        .font(.caption)
                        .foregroundStyle(statusIsError ? .red : .secondary)
                }

                Text(
                    "APIキーはKeychainへ保存します。初回は環境変数 OPENAI_API_KEY からも自動取り込みできます。"
                )
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
        settings.transcriptionPrompt.count > RealtimeSessionTuning.promptCharacterLimit
    }

    private var isKeywordLineCountOverLimit: Bool {
        settings.transcriptionKeywordsText
            .split(whereSeparator: \.isNewline)
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .count > RealtimeSessionTuning.keywordLimit
    }

    var body: some View {
        Form {
            Section("認識設定") {
                Picker("ノイズ低減", selection: $settings.noiseReductionMode) {
                    ForEach(RealtimeTranslationNoiseReduction.allCases, id: \.rawValue) { mode in
                        Text(mode.displayName).tag(mode.rawValue)
                    }
                }

                Picker("認識遅延", selection: $settings.transcriptionDelayMode) {
                    ForEach(RealtimeTranscriptionDelay.allCases, id: \.rawValue) { delay in
                        Text(delay.displayName).tag(delay.rawValue)
                    }
                }

                Text("値を上げると短い発話の認識精度が上がり、字幕表示は遅くなります。")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack {
                    Menu("プリセットを適用") {
                        ForEach(RealtimeSessionTuning.Preset.all) { preset in
                            Button(preset.displayName) {
                                settings.applyPreset(preset)
                            }
                        }
                    }
                    Button("デフォルトに戻す") {
                        settings.restoreDefaultTranscriptionHints()
                    }
                }
            }

            Section("認識ヒント") {
                SettingsMultilineField(
                    title: "認識プロンプト",
                    text: $settings.transcriptionPrompt,
                    height: 96
                )
                Text("会議のテーマや話者、話題を文章で書くと認識精度が上がります。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(
                    "\(promptCharacterCount)/\(RealtimeSessionTuning.promptCharacterLimit) 文字"
                        + (isPromptOverLimit ? "（超過分は切り詰められます）" : "")
                )
                .font(.caption)
                .foregroundStyle(.secondary)

                SettingsMultilineField(
                    title: "キーワード (1行1語)",
                    text: $settings.transcriptionKeywordsText,
                    height: 112
                )
                Text(
                    "\(keywordCount)/\(RealtimeSessionTuning.keywordLimit) 語"
                        + (isKeywordLineCountOverLimit ? "（超過分は送信されません）" : "")
                )
                .font(.caption)
                .foregroundStyle(.secondary)

                if keywordsContainForbiddenCharacters {
                    Text("「<」「>」は送信時に自動除去されます。")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }

            Section {
                Text(
                    "プロンプト・キーワード・認識遅延の変更は録音中でも数秒で反映されます。ノイズ低減は次回の録音開始から反映されます。"
                )
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
            Section("字幕") {
                Stepper(value: $settings.fontSize, in: 18...48, step: 2) {
                    Text("フォントサイズ: \(Int(settings.fontSize))pt")
                }
            }

            Section("操作") {
                Text("メニューバーの開始/停止、または Control + Option + Space を使用します。")
                    .font(.callout)
            }
        }
        .formStyle(.grouped)
    }
}
