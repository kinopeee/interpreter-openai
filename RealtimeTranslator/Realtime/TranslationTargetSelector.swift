import Foundation

struct TranslationTargetSelection: Equatable, Sendable {
    let target: RealtimeTranslationOutputLanguage?
    let reverseEvidenceCount: Int
}

enum TranslationTargetSelector {
    static let scriptSwitchMinimumLatinWords = 4
    static let scriptSwitchMinimumLatinScalars = SpokenLanguageDetector.recentEvidenceWindow
    static let scriptSwitchMinimumJapaneseScalars = 2

    static func select(
        pair: LanguagePair,
        currentTarget: RealtimeTranslationOutputLanguage?,
        reverseEvidenceCount: Int,
        evidence: SpokenLanguageEvidence,
        oppositeRun: OppositeScriptRun? = nil
    ) -> TranslationTargetSelection {
        let initial = currentTarget == nil
        guard let candidate = candidateTarget(pair: pair, evidence: evidence, isInitial: initial)
        else {
            return TranslationTargetSelection(
                target: currentTarget,
                reverseEvidenceCount: 0
            )
        }

        guard let currentTarget else {
            return TranslationTargetSelection(target: candidate, reverseEvidenceCount: 0)
        }
        guard candidate != currentTarget else {
            return TranslationTargetSelection(target: currentTarget, reverseEvidenceCount: 0)
        }

        if pair != .enEs {
            guard let oppositeRun else {
                return TranslationTargetSelection(target: currentTarget, reverseEvidenceCount: 0)
            }
            let shouldSwitch: Bool
            switch evidence {
            case .english, .spanish:
                shouldSwitch =
                    oppositeRun.latinWordCount >= scriptSwitchMinimumLatinWords
                    && oppositeRun.latinScalarCount >= scriptSwitchMinimumLatinScalars
            case .japanese:
                shouldSwitch = oppositeRun.japaneseScalarCount >= scriptSwitchMinimumJapaneseScalars
            case .ambiguousLatin, .none:
                shouldSwitch = false
            }
            return TranslationTargetSelection(
                target: shouldSwitch ? candidate : currentTarget,
                reverseEvidenceCount: 0
            )
        }

        let nextCount = reverseEvidenceCount + 1
        return TranslationTargetSelection(
            target: nextCount >= 2 ? candidate : currentTarget,
            reverseEvidenceCount: nextCount >= 2 ? 0 : nextCount
        )
    }

    private static func candidateTarget(
        pair: LanguagePair,
        evidence: SpokenLanguageEvidence,
        isInitial: Bool
    ) -> RealtimeTranslationOutputLanguage? {
        if evidence == .ambiguousLatin, isInitial, pair != .enEs {
            guard let latinLanguage = pair.counterpart(of: SpokenLanguage.japanese) else {
                return nil
            }
            return pair.translationTarget(for: latinLanguage)
        }

        switch evidence {
        case .japanese:
            return pair.translationTarget(for: .japanese)
        case .english:
            return pair.translationTarget(for: .english)
        case .spanish:
            return pair.translationTarget(for: .spanish)
        case .ambiguousLatin, .none:
            return nil
        }
    }
}
