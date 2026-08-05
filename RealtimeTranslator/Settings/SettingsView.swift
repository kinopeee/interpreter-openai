import SwiftUI

struct SettingsView: View {
    @Bindable var settings: AppSettings
    let apiKeyStore: any APIKeyStore
    var onSave: (() -> Void)?

    @State private var apiKeyDraft = ""
    @State private var hasStoredKey = false
    @State private var statusMessage: String?
    @State private var statusIsError = false

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
                        saveAPIKey()
                    }
                    .disabled(apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    Button("削除", role: .destructive) {
                        deleteAPIKey()
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

            Section("音声認識") {
                Picker("ノイズ低減", selection: $settings.noiseReductionMode) {
                    Text("近距離マイク").tag(
                        RealtimeTranslationNoiseReduction.nearField.rawValue
                    )
                    Text("会議・遠距離").tag(
                        RealtimeTranslationNoiseReduction.farField.rawValue
                    )
                }

                TextField("認識プロンプト", text: $settings.transcriptionPrompt)
                    .textFieldStyle(.roundedBorder)

                Text("キーワード (1行1語)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                TextEditor(text: $settings.transcriptionKeywordsText)
                    .font(.body)
                    .frame(minHeight: 88, maxHeight: 120)
                    .border(Color.secondary.opacity(0.3))

                Text("変更は次回の録音開始から反映されます。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("字幕") {
                Stepper(value: $settings.fontSize, in: 18...48, step: 2) {
                    Text("フォントサイズ: \(Int(settings.fontSize))pt")
                }
            }

            Section("操作") {
                Text("字幕上の「録音開始」「録音終了」ボタン、または Control + Option + Space を使用します。")
                    .font(.callout)
            }
        }
        .padding(20)
        .frame(width: 560, height: 640)
        .onAppear {
            refreshStoredKeyState()
        }
        .onDisappear {
            onSave?()
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
