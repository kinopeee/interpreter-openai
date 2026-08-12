using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Realtime;

public readonly record struct TranslationTargetSelection(
    RealtimeTranslationOutputLanguage? Target,
    int ReverseEvidenceCount);

/// <summary>pair と evidence から翻訳出力 target を調停する純粋な状態遷移。</summary>
public static class TranslationTargetSelector
{
    public static TranslationTargetSelection Select(
        LanguagePair pair,
        RealtimeTranslationOutputLanguage? currentTarget,
        int reverseEvidenceCount,
        SpokenLanguageEvidence evidence)
    {
        var candidate = CandidateTarget(pair, evidence, currentTarget is null);
        if (candidate is null)
        {
            return new(currentTarget, 0);
        }

        if (currentTarget == candidate)
        {
            return new(currentTarget, 0);
        }

        if (currentTarget is null || pair != LanguagePair.EnEs)
        {
            return new(candidate, 0);
        }

        var nextCount = reverseEvidenceCount + 1;
        return nextCount >= 2
            ? new(candidate, 0)
            : new(currentTarget, nextCount);
    }

    private static RealtimeTranslationOutputLanguage? CandidateTarget(
        LanguagePair pair,
        SpokenLanguageEvidence evidence,
        bool isInitial)
    {
        if (evidence == SpokenLanguageEvidence.AmbiguousLatin
            && isInitial
            && pair != LanguagePair.EnEs)
        {
            var latinLanguage = pair.Counterpart(SpokenLanguage.Japanese);
            return latinLanguage is { } language
                ? pair.TranslationTarget(language)
                : null;
        }

        return evidence switch
        {
            SpokenLanguageEvidence.Japanese => pair.TranslationTarget(SpokenLanguage.Japanese),
            SpokenLanguageEvidence.English => pair.TranslationTarget(SpokenLanguage.English),
            SpokenLanguageEvidence.Spanish => pair.TranslationTarget(SpokenLanguage.Spanish),
            _ => null,
        };
    }
}
