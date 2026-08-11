import Foundation

enum SpokenLanguage: Equatable, Hashable, Sendable {
    case japanese
    case english
    case spanish
    case unknown
}

enum LanguagePair: String, CaseIterable, Codable, Sendable, Equatable, Hashable {
    case jaEn = "ja-en"
    case jaEs = "ja-es"
    case enEs = "en-es"

    var languages: [SpokenLanguage] {
        switch self {
        case .jaEn:
            return [.japanese, .english]
        case .jaEs:
            return [.japanese, .spanish]
        case .enEs:
            return [.english, .spanish]
        }
    }

    func translationTarget(for language: SpokenLanguage) -> RealtimeTranslationOutputLanguage? {
        guard let counterpart = counterpart(of: language) else { return nil }
        switch counterpart {
        case .japanese:
            return .japanese
        case .english:
            return .english
        case .spanish:
            return .spanish
        case .unknown:
            return nil
        }
    }

    func counterpart(of language: SpokenLanguage) -> SpokenLanguage? {
        guard let index = languages.firstIndex(of: language) else { return nil }
        return languages[index == languages.startIndex ? languages.index(after: index) : languages.startIndex]
    }

    func counterpart(of target: RealtimeTranslationOutputLanguage) -> SpokenLanguage? {
        languages.first { translationTarget(for: $0) == target }
    }
}
