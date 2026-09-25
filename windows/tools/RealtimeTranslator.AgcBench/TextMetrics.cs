using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealtimeTranslator.AgcBench;

/// <summary>transcript 比較指標。ja は文字単位 CER、en/es は語単位 WER。</summary>
internal static class TextMetrics
{
    /// <summary>
    /// FormKC 正規化 → 小文字化 → 句読点/記号/空白を除去する。
    /// keepSpaces=true (WER 用) では落とす rune をすべて空白へ置き換え、
    /// トークン分割時に連続空白は潰れる ("hello-world" は "hello world" と同値)。
    /// keepSpaces=false (CER / false-subtitle 用) では落とす rune は消える。
    /// </summary>
    public static string Normalize(string text, bool keepSpaces)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (IsDropped(category))
            {
                if (keepSpaces)
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>日本語 transcript 用: コードポイント列のレーベンシュタイン距離 / 参照長。</summary>
    public static double Cer(string reference, string hypothesis)
    {
        var referenceRunes = ToRuneStrings(Normalize(reference, keepSpaces: false));
        var hypothesisRunes = ToRuneStrings(Normalize(hypothesis, keepSpaces: false));
        return Rate(referenceRunes, hypothesisRunes);
    }

    /// <summary>英・西語 transcript 用: 空白トークン列のレーベンシュタイン距離 / 参照語数。</summary>
    public static double Wer(string reference, string hypothesis)
    {
        var referenceTokens = Tokenize(Normalize(reference, keepSpaces: true));
        var hypothesisTokens = Tokenize(Normalize(hypothesis, keepSpaces: true));
        return Rate(referenceTokens, hypothesisTokens);
    }

    /// <summary>言語に応じて CER/WER を選ぶ。未知言語は CER に倒す。</summary>
    public static double ErrorRate(string language, string reference, string hypothesis) =>
        string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase)
            ? Cer(reference, hypothesis)
            : Wer(reference, hypothesis);

    /// <summary>無音クリップで誤って出た字幕の正規化後文字数。</summary>
    public static int FalseSubtitleChars(string transcript)
    {
        var count = 0;
        var runes = Normalize(transcript, keepSpaces: false).EnumerateRunes();
        while (runes.MoveNext())
        {
            count += 1;
        }

        return count;
    }

    private static bool IsDropped(UnicodeCategory category) =>
        category
            is UnicodeCategory.SpaceSeparator
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol
                or UnicodeCategory.OtherNotAssigned;

    private static List<string> ToRuneStrings(string text)
    {
        var list = new List<string>();
        foreach (var rune in text.EnumerateRunes())
        {
            list.Add(rune.ToString());
        }

        return list;
    }

    private static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            list.Add(token);
        }

        return list;
    }

    private static double Rate(List<string> reference, List<string> hypothesis) =>
        reference.Count == 0
            ? (hypothesis.Count == 0 ? 0.0 : 1.0)
            : Levenshtein(reference, hypothesis) / (double)reference.Count;

    public static int Levenshtein(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var column = 0; column <= b.Count; column += 1)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= a.Count; row += 1)
        {
            current[0] = row;
            for (var column = 1; column <= b.Count; column += 1)
            {
                var cost = a[row - 1] == b[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }
}
