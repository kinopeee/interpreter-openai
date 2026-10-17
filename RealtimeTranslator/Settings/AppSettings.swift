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
        static let automaticGainEnabled = "automaticGainEnabled"
        static let languagePair = "languagePair"
        static let uiLanguage = "uiLanguage"
    }

    /// 現在有効な同意バージョン。文言変更時にインクリメントする。
    static let currentOpenAIConsentVersion = 1

    static let minimumFontSize: Double = 18
    static let maximumFontSize: Double = 48
    static let defaultFontSize: Double = 32

    /// C# `AppSettingsCodec.ClampFontSize` と同義。fixture `v1/settings` の契約。
    static func clampFontSize(_ value: Double) -> Double {
        value.isFinite
            ? min(max(value, minimumFontSize), maximumFontSize)
            : defaultFontSize
    }

    private let defaults: UserDefaults

    var fontSize: Double {
        didSet {
            // C# の encode 時 ClampFontSize と同義。ライブ値もクランプし、
            // 再起動前後で表示サイズが変わらないようにする。
            let clamped = Self.clampFontSize(fontSize)
            if clamped != fontSize {
                fontSize = clamped
            }
            defaults.set(clamped, forKey: Keys.fontSize)
        }
    }

    var hasCustomPanelOrigin: Bool {
        didSet { defaults.set(hasCustomPanelOrigin, forKey: Keys.hasCustomPanelOrigin) }
    }

    var panelOriginX: Double {
        didSet { defaults.set(panelOriginX, forKey: Keys.panelOriginX) }
    }

    var panelOriginY: Double {
        didSet { defaults.set(panelOriginY, forKey: Keys.panelOriginY) }
    }

    var acceptedOpenAIConsentVersion: Int {
        didSet {
            defaults.set(
                acceptedOpenAIConsentVersion,
                forKey: Keys.openAIConsentVersion
            )
        }
    }

    var transcriptionPrompt: String {
        didSet { defaults.set(transcriptionPrompt, forKey: Keys.transcriptionPrompt) }
    }

    /// 1行1語のキーワードテキスト。
    var transcriptionKeywordsText: String {
        didSet {
            defaults.set(
                transcriptionKeywordsText,
                forKey: Keys.transcriptionKeywordsText
            )
        }
    }

    /// `RealtimeTranslationNoiseReduction.rawValue` を保存する。
    var noiseReductionMode: String {
        didSet { defaults.set(noiseReductionMode, forKey: Keys.noiseReductionMode) }
    }

    /// `RealtimeTranscriptionDelay.rawValue` を保存する。
    var transcriptionDelayMode: String {
        didSet {
            defaults.set(transcriptionDelayMode, forKey: Keys.transcriptionDelayMode)
        }
    }

    /// オプトイン時のみ確定字幕をローカルファイルへ追記する。
    var recordSubtitles: Bool {
        didSet { defaults.set(recordSubtitles, forKey: Keys.recordSubtitles) }
    }

    /// 適応マイクゲイン。変更は次の録音開始から反映される。
    var automaticGainEnabled: Bool {
        didSet {
            defaults.set(automaticGainEnabled, forKey: Keys.automaticGainEnabled)
        }
    }

    var languagePair: LanguagePair {
        didSet {
            defaults.set(languagePair.rawValue, forKey: Keys.languagePair)
            refreshDefaultTranscriptionHintsIfNeeded(from: oldValue, to: languagePair)
        }
    }

    /// アプリ枠の表示言語。翻訳ペアとは独立。反映はプロセス再起動後。
    var uiLanguage: UiLanguagePreference {
        didSet { defaults.set(uiLanguage.rawValue, forKey: Keys.uiLanguage) }
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

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // 未保存・契約外型（Bool 等）は既定値、数値は範囲へクランプする。
        // Bool は NSNumber へブリッジされるため先に弾く（C# Number() の null と同義）。
        let storedFont = defaults.object(forKey: Keys.fontSize)
        if storedFont is Bool {
            fontSize = Self.defaultFontSize
        } else if let stored = storedFont as? NSNumber {
            fontSize = Self.clampFontSize(stored.doubleValue)
        } else {
            fontSize = Self.defaultFontSize
        }
        hasCustomPanelOrigin = defaults.bool(forKey: Keys.hasCustomPanelOrigin)
        panelOriginX = defaults.double(forKey: Keys.panelOriginX)
        panelOriginY = defaults.double(forKey: Keys.panelOriginY)
        acceptedOpenAIConsentVersion = defaults.integer(forKey: Keys.openAIConsentVersion)
        let pair =
            LanguagePair(
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
        automaticGainEnabled = defaults.object(forKey: Keys.automaticGainEnabled) as? Bool ?? true
        uiLanguage = UiLanguagePreference.parse(defaults.string(forKey: Keys.uiLanguage))
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
