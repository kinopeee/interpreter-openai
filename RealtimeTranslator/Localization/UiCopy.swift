import Foundation

enum UiCopy {
    static let macHotkey = "Control + Option + Space"

    static func text(_ key: String) -> String {
        UserCopyStore.current.text(key)
    }

    static func text(_ key: String, _ substitutions: [String: String]) -> String {
        UserCopyStore.current.text(key, substitutions)
    }

    static func pairName(_ pair: LanguagePair) -> String {
        switch pair {
        case .jaEn:
            return text("settings.languagePair.jaEn")
        case .jaEs:
            return text("settings.languagePair.jaEs")
        case .enEs:
            return text("settings.languagePair.enEs")
        }
    }

    static func presetTitle(_ preset: RealtimeSessionTuning.Preset) -> String {
        switch preset.id {
        case "software_development":
            return text("settings.preset.softwareDevelopment")
        case "business_meeting":
            return text("settings.preset.businessMeeting")
        case "hackathon":
            return text("settings.preset.hackathon")
        default:
            return preset.displayName
        }
    }

    static func installFromSavedPreference() {
        let preference = UiLanguagePreference.parse(UserDefaults.standard.string(forKey: "uiLanguage"))
        let osCode = Locale.current.language.languageCode?.identifier
        guard let url = UserCopyStore.catalogURL(),
            let copy = try? UserCopy.load(from: url, locale: preference.resolve(osLanguageCode: osCode))
        else {
            #if DEBUG
            AppLogger.general.debug("UserCopy catalog missing from bundle")
            #endif
            return
        }
        UserCopyStore.install(copy)
    }
}
