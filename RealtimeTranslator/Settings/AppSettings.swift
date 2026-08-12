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
        static let transcriptionDelayMode = "transcriptionDelayMode"
        static let recordSubtitles = "recordSubtitles"
        static let languagePair = "languagePair"
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

    /// `RealtimeTranscriptionDelay.rawValue` を保存する。
    var transcriptionDelayMode: String {
        didSet {
            UserDefaults.standard.set(transcriptionDelayMode, forKey: Keys.transcriptionDelayMode)
        }
    }

    /// オプトイン時のみ確定字幕をローカルファイルへ追記する。
    var recordSubtitles: Bool {
        didSet { UserDefaults.standard.set(recordSubtitles, forKey: Keys.recordSubtitles) }
    }

    var languagePair: LanguagePair {
        didSet {
            UserDefaults.standard.set(languagePair.rawValue, forKey: Keys.languagePair)
            refreshDefaultTranscriptionHintsIfNeeded(from: oldValue, to: languagePair)
        }
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

    var transcriptionDelay: RealtimeTranscriptionDelay {
        get {
            RealtimeTranscriptionDelay(rawValue: transcriptionDelayMode) ?? .low
        }
        set {
            transcriptionDelayMode = newValue.rawValue
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
        let pair = LanguagePair(
            rawValue: defaults.string(forKey: Keys.languagePair) ?? ""
        ) ?? .jaEn

        if let storedPrompt = defaults.string(forKey: Keys.transcriptionPrompt),
           !storedPrompt.isEmpty
        {
            transcriptionPrompt = storedPrompt
        } else {
            transcriptionPrompt = RealtimeSessionTuning.defaultPrompt(for: pair)
        }

        if let storedKeywords = defaults.string(forKey: Keys.transcriptionKeywordsText) {
            transcriptionKeywordsText = storedKeywords
        } else {
            transcriptionKeywordsText = RealtimeSessionTuning.keywordsText(
                from: RealtimeSessionTuning.defaultKeywords(for: pair)
            )
        }

        if let storedNoise = defaults.string(forKey: Keys.noiseReductionMode),
           RealtimeTranslationNoiseReduction(rawValue: storedNoise) != nil
        {
            noiseReductionMode = storedNoise
        } else {
            noiseReductionMode = RealtimeTranslationNoiseReduction.farField.rawValue
        }

        if let storedDelay = defaults.string(forKey: Keys.transcriptionDelayMode),
           RealtimeTranscriptionDelay(rawValue: storedDelay) != nil
        {
            transcriptionDelayMode = storedDelay
        } else {
            transcriptionDelayMode = RealtimeTranscriptionDelay.low.rawValue
        }

        recordSubtitles = defaults.bool(forKey: Keys.recordSubtitles)
        // 他プロパティ初期化後に代入し、init 中の self 参照を避ける。
        languagePair = pair
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
            transcriptionDelay: transcriptionDelay,
            prompt: transcriptionPrompt,
            keywordsText: transcriptionKeywordsText
        )
    }

    func applyPreset(_ preset: RealtimeSessionTuning.Preset) {
        transcriptionPrompt = preset.prompt
        transcriptionKeywordsText = RealtimeSessionTuning.keywordsText(from: preset.keywords)
    }

    func restoreDefaultTranscriptionHints() {
        transcriptionPrompt = RealtimeSessionTuning.defaultPrompt(for: languagePair)
        transcriptionKeywordsText = RealtimeSessionTuning.keywordsText(
            from: RealtimeSessionTuning.defaultKeywords(for: languagePair)
        )
    }

    /// 既定ヒントのままなら、言語ペア変更に合わせて prompt/keywords を更新する。
    private func refreshDefaultTranscriptionHintsIfNeeded(
        from oldPair: LanguagePair,
        to newPair: LanguagePair
    ) {
        guard oldPair != newPair else { return }
        let knownDefaultPrompts = LanguagePair.allCases.map {
            RealtimeSessionTuning.defaultPrompt(for: $0)
        }
        let knownDefaultKeywords = LanguagePair.allCases.map {
            RealtimeSessionTuning.keywordsText(from: RealtimeSessionTuning.defaultKeywords(for: $0))
        }
        if knownDefaultPrompts.contains(transcriptionPrompt) {
            transcriptionPrompt = RealtimeSessionTuning.defaultPrompt(for: newPair)
        }
        if knownDefaultKeywords.contains(transcriptionKeywordsText) {
            transcriptionKeywordsText = RealtimeSessionTuning.keywordsText(
                from: RealtimeSessionTuning.defaultKeywords(for: newPair)
            )
        }
    }
}
