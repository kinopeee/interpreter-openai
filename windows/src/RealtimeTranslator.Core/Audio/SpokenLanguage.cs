using RealtimeTranslator.Core.OpenAI;

namespace RealtimeTranslator.Core.Audio;

/// <summary>話者の言語。</summary>
public enum SpokenLanguage
{
    Unknown,
    Japanese,
    English,
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
    AmbiguousLatin,
}

public static class SpokenLanguageExtensions
{
    /// <summary>この言語を話しているときに翻訳すべき出力先。unknown では翻訳を開始しない。</summary>
    public static RealtimeTranslationOutputLanguage? TranslationTarget(this SpokenLanguage language) => language switch
    {
        SpokenLanguage.Japanese => RealtimeTranslationOutputLanguage.English,
        SpokenLanguage.English => RealtimeTranslationOutputLanguage.Japanese,
        _ => null,
    };
}
