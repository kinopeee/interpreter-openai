import CoreGraphics
import Foundation
import Observation

enum TranslationState: String, Sendable {
    case idle
    case connecting
    case listening
    case reconnecting
    case closing
    case error
}

@MainActor
@Observable
final class AppSettings {
    private enum Keys {
        static let fontSize = "subtitleFontSize"
        static let panelOriginX = "panelOriginX"
        static let panelOriginY = "panelOriginY"
        static let hasCustomPanelOrigin = "hasCustomPanelOrigin"
        /// 同意文言が変わったらバージョンを上げ、再同意を求める。
        static let openAIConsentVersion = "openAIConsentVersion"
        static let transcriptionPrompt = "transcriptionPrompt"
        static let transcriptionKeywordsText = "transcriptionKeywordsText"
        static let noiseReductionMode = "noiseReductionMode"
    }

    /// 現在有効な同意バージョン。文言変更時にインクリメントする。
    static let currentOpenAIConsentVersion = 1

    var fontSize: Double {
        didSet { UserDefaults.standard.set(fontSize, forKey: Keys.fontSize) }
    }

    var hasCustomPanelOrigin: Bool {
        didSet { UserDefaults.standard.set(hasCustomPanelOrigin, forKey: Keys.hasCustomPanelOrigin) }
    }

    var panelOriginX: Double {
        didSet { UserDefaults.standard.set(panelOriginX, forKey: Keys.panelOriginX) }
    }

    var panelOriginY: Double {
        didSet { UserDefaults.standard.set(panelOriginY, forKey: Keys.panelOriginY) }
    }

    var acceptedOpenAIConsentVersion: Int {
        didSet {
            UserDefaults.standard.set(
                acceptedOpenAIConsentVersion,
                forKey: Keys.openAIConsentVersion
            )
        }
    }

    var transcriptionPrompt: String {
        didSet { UserDefaults.standard.set(transcriptionPrompt, forKey: Keys.transcriptionPrompt) }
    }

    /// 1行1語のキーワードテキスト。
    var transcriptionKeywordsText: String {
        didSet {
            UserDefaults.standard.set(
                transcriptionKeywordsText,
                forKey: Keys.transcriptionKeywordsText
            )
        }
    }

    /// `RealtimeTranslationNoiseReduction.rawValue` を保存する。
    var noiseReductionMode: String {
        didSet { UserDefaults.standard.set(noiseReductionMode, forKey: Keys.noiseReductionMode) }
    }

    var hasAcceptedCurrentOpenAIConsent: Bool {
        acceptedOpenAIConsentVersion >= Self.currentOpenAIConsentVersion
    }

    var transcriptionKeywords: [String] {
        RealtimeSessionTuning.parseKeywords(from: transcriptionKeywordsText)
    }

    var noiseReduction: RealtimeTranslationNoiseReduction {
        get {
            RealtimeTranslationNoiseReduction(rawValue: noiseReductionMode) ?? .farField
        }
        set {
            noiseReductionMode = newValue.rawValue
        }
    }

    init() {
        let defaults = UserDefaults.standard
        let storedFont = defaults.double(forKey: Keys.fontSize)
        fontSize = storedFont > 0 ? storedFont : 32
        hasCustomPanelOrigin = defaults.bool(forKey: Keys.hasCustomPanelOrigin)
        panelOriginX = defaults.double(forKey: Keys.panelOriginX)
        panelOriginY = defaults.double(forKey: Keys.panelOriginY)
        acceptedOpenAIConsentVersion = defaults.integer(forKey: Keys.openAIConsentVersion)

        if let storedPrompt = defaults.string(forKey: Keys.transcriptionPrompt),
           !storedPrompt.isEmpty
        {
            transcriptionPrompt = storedPrompt
        } else {
            transcriptionPrompt = RealtimeSessionTuning.defaultPrompt
        }

        if let storedKeywords = defaults.string(forKey: Keys.transcriptionKeywordsText) {
            transcriptionKeywordsText = storedKeywords
        } else {
            transcriptionKeywordsText = RealtimeSessionTuning.keywordsText(
                from: RealtimeSessionTuning.defaultKeywords
            )
        }

        if let storedNoise = defaults.string(forKey: Keys.noiseReductionMode),
           RealtimeTranslationNoiseReduction(rawValue: storedNoise) != nil
        {
            noiseReductionMode = storedNoise
        } else {
            noiseReductionMode = RealtimeTranslationNoiseReduction.farField.rawValue
        }
    }

    func acceptOpenAIConsent() {
        acceptedOpenAIConsentVersion = Self.currentOpenAIConsentVersion
    }

    func customPanelOrigin() -> CGPoint? {
        guard hasCustomPanelOrigin else { return nil }
        return CGPoint(x: panelOriginX, y: panelOriginY)
    }

    func savePanelOrigin(_ origin: CGPoint) {
        panelOriginX = origin.x
        panelOriginY = origin.y
        hasCustomPanelOrigin = true
    }

    func sessionTuning() -> RealtimeSessionTuning {
        RealtimeSessionTuning.make(
            noiseReduction: noiseReduction,
            prompt: transcriptionPrompt,
            keywordsText: transcriptionKeywordsText
        )
    }

    func applyPreset(_ preset: RealtimeSessionTuning.Preset) {
        transcriptionPrompt = preset.prompt
        transcriptionKeywordsText = RealtimeSessionTuning.keywordsText(from: preset.keywords)
    }

    func restoreDefaultTranscriptionHints() {
        applyPreset(.softwareDevelopment)
    }
}
