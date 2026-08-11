using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.Core.OpenAI;

/// <summary>録音開始時に Realtime セッションへ渡す認識・ノイズ低減チューニング。</summary>
public sealed record RealtimeSessionTuning(
    RealtimeTranslationNoiseReduction NoiseReduction,
    RealtimeTranscriptionDelay TranscriptionDelay,
    string TranscriptionPrompt,
    ImmutableArray<string> TranscriptionKeywords)
{
    /// <summary>OpenAI が keywords 内で拒否する文字。session.update 全体が失敗するため除去する。</summary>
    public const string ForbiddenKeywordCharacters = "<>";

    public const int KeywordLimit = 64;

    /// <summary>prompt 長さ上限の安全マージン。超過すると session.update が拒否される。</summary>
    public const int PromptCharacterLimit = 1_000;

    public const string DefaultPrompt =
        "Japanese and English conversation about software development, programming, and hackathons.";

    public static readonly ImmutableArray<string> DefaultKeywords =
    [
        "ハッカソン",
        "hackathon",
        "エンジニア",
        "エンジニアリング",
        "クレジット",
        "モデル",
    ];

    public static readonly RealtimeSessionTuning Default = new(
        RealtimeTranslationNoiseReduction.FarField,
        RealtimeTranscriptionDelay.Low,
        DefaultPrompt,
        DefaultKeywords);

    public static string DefaultPromptForPair(LanguagePair pair) => pair switch
    {
        LanguagePair.JaEn => DefaultPrompt,
        LanguagePair.JaEs => "Japanese and Spanish conversation about software development, programming, and hackathons.",
        LanguagePair.EnEs => "English and Spanish conversation about software development, programming, and hackathons.",
        _ => throw new ArgumentOutOfRangeException(nameof(pair), pair, null),
    };

    public static ImmutableArray<string> DefaultKeywordsForPair(LanguagePair pair) => pair == LanguagePair.JaEn
        ? DefaultKeywords
        : ["hackathon", "software", "programming", "desarrollo", "programación"];

    public RealtimeSessionTuning ForPair(LanguagePair pair)
    {
        var prompt = IsKnownDefaultPrompt(TranscriptionPrompt)
            ? DefaultPromptForPair(pair)
            : TranscriptionPrompt;
        var keywords = IsKnownDefaultKeywords(TranscriptionKeywords)
            ? DefaultKeywordsForPair(pair)
            : TranscriptionKeywords;
        return this with { TranscriptionPrompt = prompt, TranscriptionKeywords = keywords };
    }

    private static bool IsKnownDefaultPrompt(string prompt) =>
        prompt == DefaultPromptForPair(LanguagePair.JaEn)
        || prompt == DefaultPromptForPair(LanguagePair.JaEs)
        || prompt == DefaultPromptForPair(LanguagePair.EnEs);

    private static bool IsKnownDefaultKeywords(ImmutableArray<string> keywords) =>
        keywords.AsSpan().SequenceEqual(DefaultKeywordsForPair(LanguagePair.JaEn).AsSpan())
        || keywords.AsSpan().SequenceEqual(DefaultKeywordsForPair(LanguagePair.JaEs).AsSpan())
        || keywords.AsSpan().SequenceEqual(DefaultKeywordsForPair(LanguagePair.EnEs).AsSpan());

    /// <summary>用途別の認識ヒントプリセット。</summary>
    public sealed record Preset(string Id, string DisplayName, string Prompt, ImmutableArray<string> Keywords)
    {
        public static readonly Preset SoftwareDevelopment = new(
            "software_development",
            "ソフトウェア開発",
            DefaultPrompt,
            DefaultKeywords);

        public static readonly Preset BusinessMeeting = new(
            "business_meeting",
            "ビジネス会議",
            "Japanese and English business meeting about agenda, decisions, schedule, and action items.",
            [
                "アジェンダ",
                "agenda",
                "議事録",
                "アクションアイテム",
                "action item",
                "スケジュール",
                "決定事項",
                "ステークホルダー",
                "stakeholder",
                "フォローアップ",
            ]);

        public static readonly Preset Hackathon = new(
            "hackathon",
            "ハッカソン",
            "Japanese and English hackathon conversation about demos, judging, teams, and pitches.",
            [
                "ハッカソン",
                "hackathon",
                "デモ",
                "demo",
                "ピッチ",
                "pitch",
                "審査",
                "ジャッジ",
                "チーム",
                "プロトタイプ",
                "prototype",
            ]);

        public static readonly ImmutableArray<Preset> All = [SoftwareDevelopment, BusinessMeeting, Hackathon];
    }

    /// <summary>
    /// 1 行 1 語のテキストをキーワード配列へ正規化する。trim 後に <c>&lt;</c> <c>&gt;</c> を除去し、空になった行は捨てる。
    /// </summary>
    public static ImmutableArray<string> ParseKeywords(string text, int limit = KeywordLimit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        if (limit == 0)
        {
            return [];
        }

        var result = new List<string>(Math.Min(limit, 16));
        foreach (var line in text.Split(LineSeparators, StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var sanitized = StripForbiddenCharacters(trimmed).Trim();
            if (sanitized.Length == 0)
            {
                continue;
            }

            result.Add(sanitized);
            if (result.Count >= limit)
            {
                break;
            }
        }

        return [.. result];
    }

    /// <summary>改行を空白へ潰し trim した prompt（切り詰め前）。</summary>
    public static string CollapsedPrompt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>書記素クラスタ数（Swift の <c>Character</c> 相当）。</summary>
    public static int CountTextElements(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var count = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            offset += StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            count += 1;
        }

        return count;
    }

    /// <summary>正規化後・切り詰め前の文字数が送信上限を超えるか。</summary>
    public static bool IsPromptOverCharacterLimit(string text) =>
        CountTextElements(CollapsedPrompt(text)) > PromptCharacterLimit;

    /// <summary>送信対象キーワード数が上限を超えるか（<c>&lt;&gt;</c> のみの行など送信されない行は数えない）。</summary>
    public static bool IsKeywordCountOverLimit(string text) =>
        ParseKeywords(text, KeywordLimit + 1).Length > KeywordLimit;

    /// <summary>prompt を送信可能な形へ正規化する。改行→空白、trim、文字数上限。</summary>
    public static string SanitizedPrompt(string text)
    {
        var collapsed = CollapsedPrompt(text);

        // 上限は Swift の Character 数、つまり書記素クラスタ数で数える。
        // UTF-16 code unit で切ると絵文字などでサロゲートペアを割り、 lone surrogate が送信される。
        return TruncateToTextElements(collapsed, PromptCharacterLimit);
    }

    private static string TruncateToTextElements(string text, int limit)
    {
        var offset = 0;
        var count = 0;
        while (offset < text.Length)
        {
            if (count == limit)
            {
                return text[..offset];
            }

            offset += StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            count += 1;
        }

        return text;
    }

    public static string KeywordsText(IEnumerable<string> keywords) => string.Join("\n", keywords);

    /// <summary>生の設定値から送信用 tuning を組み立てる。</summary>
    public static RealtimeSessionTuning Make(
        RealtimeTranslationNoiseReduction noiseReduction,
        RealtimeTranscriptionDelay transcriptionDelay,
        string prompt,
        string keywordsText) =>
        new(noiseReduction, transcriptionDelay, SanitizedPrompt(prompt), ParseKeywords(keywordsText));

    private static string StripForbiddenCharacters(string value)
    {
        if (value.IndexOfAny(ForbiddenKeywordCharacterArray) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (Array.IndexOf(ForbiddenKeywordCharacterArray, character) < 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static readonly char[] ForbiddenKeywordCharacterArray = ForbiddenKeywordCharacters.ToCharArray();

    private static readonly string[] LineSeparators = ["\r\n", "\n", "\r", "\u2028", "\u2029", "\u0085"];
}
