using System;
using System.Collections.Generic;
using System.Text;

namespace RealtimeTranslator.Core.Audio;

/// <summary>テキストの文字種 (ひらがな・カタカナ・漢字・ラテン文字) から話者言語を推定する。</summary>
public static class SpokenLanguageDetector
{
    /// <summary>言語切替検出用の末尾 Unicode scalar 数 (空白除く)。</summary>
    public const int RecentEvidenceWindow = 16;

    public static SpokenLanguage Detect(string text) => Evidence(text) switch
    {
        SpokenLanguageEvidence.Japanese => SpokenLanguage.Japanese,
        SpokenLanguageEvidence.English => SpokenLanguage.English,
        _ => SpokenLanguage.Unknown,
    };

    /// <summary>
    /// 空白を除いた末尾 N 個の Unicode scalar (code point) 分の範囲だけで証拠を評価する。
    /// 空白 scalar は語境界判定のため残す。日本語がウィンドウ外へ流れ出ると英語切替を検出できる。
    /// 単位は UTF-16 <see cref="char"/> でも書記素クラスタでもない (shared/protocol/routing.md 正本)。
    /// </summary>
    public static SpokenLanguageEvidence RecentEvidence(string text, int window = RecentEvidenceWindow)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (window <= 0 || text.Length == 0)
        {
            return Evidence(text);
        }

        var starts = ScalarStarts(text);
        var nonWhitespaceCount = 0;
        var position = starts.Count;
        var start = text.Length;
        while (position > 0 && nonWhitespaceCount < window)
        {
            position -= 1;
            start = starts[position];
            if (!Rune.IsWhiteSpace(Rune.GetRuneAt(text, start)))
            {
                nonWhitespaceCount += 1;
            }
        }

        return nonWhitespaceCount == 0 ? SpokenLanguageEvidence.None : Evidence(text[start..]);
    }

    public static SpokenLanguageEvidence Evidence(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

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
            _ => SpokenLanguageEvidence.English,
        };
    }

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
