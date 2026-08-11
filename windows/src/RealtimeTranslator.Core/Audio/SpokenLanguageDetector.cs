using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace RealtimeTranslator.Core.Audio;

/// <summary>テキストの文字種 (ひらがな・カタカナ・漢字・ラテン文字) から話者言語を推定する。</summary>
public static class SpokenLanguageDetector
{
    /// <summary>言語切替検出用の末尾 Unicode scalar 数 (空白除く)。</summary>
    public const int RecentEvidenceWindow = 16;
    public const int EnEsWindow = 8;

    public static readonly ImmutableArray<string> SpanishExclusiveWords =
        ["el", "la", "los", "las", "es", "está", "que", "y", "de", "del", "con", "por", "para", "pero", "más", "sí"];

    public static readonly ImmutableArray<string> EnglishExclusiveWords =
        ["the", "and", "is", "are", "of", "to", "it", "that", "this", "with", "for", "you", "they"];

    public static SpokenLanguage Detect(string text, LanguagePair pair) =>
        Evidence(text, pair) switch
    {
        SpokenLanguageEvidence.Japanese => SpokenLanguage.Japanese,
        SpokenLanguageEvidence.English => SpokenLanguage.English,
        SpokenLanguageEvidence.Spanish => SpokenLanguage.Spanish,
        _ => SpokenLanguage.Unknown,
    };

    /// <summary>
    /// 空白を除いた末尾 N 個の Unicode scalar (code point) 分の範囲だけで証拠を評価する。
    /// 空白 scalar は語境界判定のため残す。日本語がウィンドウ外へ流れ出ると英語切替を検出できる。
    /// 単位は UTF-16 <see cref="char"/> でも書記素クラスタでもない (shared/protocol/routing.md 正本)。
    /// </summary>
    public static SpokenLanguageEvidence RecentEvidence(
        string text,
        LanguagePair pair,
        int? window = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var effectiveWindow = window ?? (pair == LanguagePair.EnEs ? EnEsWindow : RecentEvidenceWindow);
        if (effectiveWindow <= 0 || text.Length == 0)
        {
            return Evidence(text, pair);
        }

        if (pair == LanguagePair.EnEs)
        {
            var words = TokenizeWordSpans(text);
            return words.Count <= effectiveWindow
                ? Evidence(text, pair)
                : Evidence(
                    text[WordWindowStart(text, words, effectiveWindow)..words[^1].End],
                    pair);
        }

        var starts = ScalarStarts(text);
        var nonWhitespaceCount = 0;
        var position = starts.Count;
        var start = text.Length;
        while (position > 0 && nonWhitespaceCount < effectiveWindow)
        {
            position -= 1;
            start = starts[position];
            if (!Rune.IsWhiteSpace(Rune.GetRuneAt(text, start)))
            {
                nonWhitespaceCount += 1;
            }
        }

        return nonWhitespaceCount == 0 ? SpokenLanguageEvidence.None : Evidence(text[start..], pair);
    }

    public static SpokenLanguageEvidence Evidence(
        string text,
        LanguagePair pair)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (pair == LanguagePair.EnEs)
        {
            return EvidenceEnEs(text);
        }

        var hasJapanese = false;
        var latinWordCount = 0;
        var isInsideLatinWord = false;

        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case >= 0x3040 and <= 0x30FF:
                case >= 0x3400 and <= 0x4DBF:
                case >= 0x4E00 and <= 0x9FFF:
                    hasJapanese = true;
                    isInsideLatinWord = false;
                    break;

                case >= 0x0041 and <= 0x005A:
                case >= 0x0061 and <= 0x007A:
                    if (!isInsideLatinWord)
                    {
                        latinWordCount += 1;
                        isInsideLatinWord = true;
                    }

                    break;

                default:
                    isInsideLatinWord = false;
                    break;
            }
        }

        if (hasJapanese)
        {
            return SpokenLanguageEvidence.Japanese;
        }

        return latinWordCount switch
        {
            0 => SpokenLanguageEvidence.None,
            1 => SpokenLanguageEvidence.AmbiguousLatin,
            _ => pair == LanguagePair.JaEs
                ? SpokenLanguageEvidence.Spanish
                : SpokenLanguageEvidence.English,
        };
    }

    private static SpokenLanguageEvidence EvidenceEnEs(string text)
    {
        var words = TokenizeWords(text);
        if (text.IndexOfAny(['¿', '¡', 'ñ', 'Ñ']) >= 0)
        {
            return SpokenLanguageEvidence.Spanish;
        }

        var spanishScore = 0;
        var englishScore = 0;
        foreach (var word in words)
        {
            var lower = word.ToLowerInvariant();
            if (SpanishExclusiveWords.Contains(lower))
            {
                spanishScore += 1;
            }

            if (EnglishExclusiveWords.Contains(lower))
            {
                englishScore += 1;
            }

            if (word.Any(character => "áéíóúüÁÉÍÓÚÜ".Contains(character)))
            {
                spanishScore += 2;
            }
        }

        if (Math.Abs(spanishScore - englishScore) < 2)
        {
            return SpokenLanguageEvidence.AmbiguousLatin;
        }

        return spanishScore > englishScore
            ? SpokenLanguageEvidence.Spanish
            : SpokenLanguageEvidence.English;
    }

    private static List<string> TokenizeWords(string text)
    {
        var words = new List<string>();
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsLatinWordRune(rune))
            {
                builder.Append(rune.ToString());
            }
            else if (builder.Length > 0)
            {
                words.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            words.Add(builder.ToString());
        }

        return words;
    }

    private static List<(int Start, int End)> TokenizeWordSpans(string text)
    {
        var words = new List<(int Start, int End)>();
        var start = -1;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsLatinWordRune(rune))
            {
                start = start < 0 ? index : start;
            }
            else if (start >= 0)
            {
                words.Add((start, index));
                start = -1;
            }

            index += rune.Utf16SequenceLength;
        }

        if (start >= 0)
        {
            words.Add((start, text.Length));
        }

        return words;
    }

    private static int WordWindowStart(
        string text,
        List<(int Start, int End)> words,
        int window)
    {
        var start = words[^window].Start;
        while (start > 0)
        {
            if (text[start - 1] is not ('¿' or '¡'))
            {
                break;
            }

            start -= 1;
        }

        return start;
    }

    private static bool IsLatinWordRune(Rune rune) =>
        rune.Value is >= 0x0041 and <= 0x005A
            or >= 0x0061 and <= 0x007A
            or >= 0x00C0 and <= 0x00D6
            or >= 0x00D8 and <= 0x00F6
            or >= 0x00F8 and <= 0x00FF;

    /// <summary>Unicode scalar 単位で末尾から走査するための開始 UTF-16 オフセット列。</summary>
    private static List<int> ScalarStarts(string text)
    {
        var starts = new List<int>(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            starts.Add(offset);
            offset += Rune.GetRuneAt(text, offset).Utf16SequenceLength;
        }

        return starts;
    }
}
