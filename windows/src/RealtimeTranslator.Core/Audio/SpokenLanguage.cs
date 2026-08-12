using System;
using System.Collections.Immutable;
using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Audio;

/// <summary>話者の言語。</summary>
public enum SpokenLanguage
{
    Unknown,
    Japanese,
    English,
    Spanish,
}

/// <summary>文字種から推定した言語の証拠。</summary>
/// <remarks>
/// <c>AmbiguousLatin</c> はラテン文字 1 語だけの場合。日本語話者のローマ字発話や
/// 固有名詞の可能性があり、英語と断定できないため <c>English</c> と区別する。
/// </remarks>
public enum SpokenLanguageEvidence
{
    None,
    Japanese,
    English,
    Spanish,
    AmbiguousLatin,
}

public enum LanguagePair
{
    JaEn,
    JaEs,
    EnEs,
}

public static class LanguagePairExtensions
{
    public static ImmutableArray<SpokenLanguage> Languages(this LanguagePair pair) => pair switch
    {
        LanguagePair.JaEn => [SpokenLanguage.Japanese, SpokenLanguage.English],
        LanguagePair.JaEs => [SpokenLanguage.Japanese, SpokenLanguage.Spanish],
        LanguagePair.EnEs => [SpokenLanguage.English, SpokenLanguage.Spanish],
        _ => throw new ArgumentOutOfRangeException(nameof(pair), pair, null),
    };

    public static RealtimeTranslationOutputLanguage? TranslationTarget(
        this LanguagePair pair,
        SpokenLanguage language) =>
        pair.Counterpart(language) is { } counterpart
            ? counterpart.ToOutputLanguage()
            : null;

    public static SpokenLanguage? Counterpart(this LanguagePair pair, SpokenLanguage language)
    {
        var languages = pair.Languages();
        if (language != languages[0] && language != languages[1])
        {
            return null;
        }

        return language == languages[0] ? languages[1] : languages[0];
    }

    /// <summary>
    /// 出力 target に対応する話者言語（source）を返す。
    /// <c>translationTarget(source) == target</c> となる側であり、target と同名の言語ではない。
    /// </summary>
    public static SpokenLanguage? Counterpart(
        this LanguagePair pair,
        RealtimeTranslationOutputLanguage target)
    {
        foreach (var language in pair.Languages())
        {
            if (pair.TranslationTarget(language) == target)
            {
                return language;
            }
        }

        return null;
    }

    public static string ToWireValue(this LanguagePair pair) => pair switch
    {
        LanguagePair.JaEn => "ja-en",
        LanguagePair.JaEs => "ja-es",
        LanguagePair.EnEs => "en-es",
        _ => throw new ArgumentOutOfRangeException(nameof(pair), pair, null),
    };

    public static LanguagePair ParseLanguagePair(string wireValue) => wireValue switch
    {
        "ja-en" => LanguagePair.JaEn,
        "ja-es" => LanguagePair.JaEs,
        "en-es" => LanguagePair.EnEs,
        _ => throw new ArgumentOutOfRangeException(nameof(wireValue), wireValue, null),
    };

    public static RealtimeTranslationOutputLanguage ToOutputLanguage(this SpokenLanguage language) => language switch
    {
        SpokenLanguage.English => RealtimeTranslationOutputLanguage.English,
        SpokenLanguage.Japanese => RealtimeTranslationOutputLanguage.Japanese,
        SpokenLanguage.Spanish => RealtimeTranslationOutputLanguage.Spanish,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}
