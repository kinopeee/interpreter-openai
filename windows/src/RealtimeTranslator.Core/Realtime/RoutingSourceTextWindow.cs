using System;
using System.Collections.Generic;
using System.Text;
using RealtimeTranslator.Core.Audio;

namespace RealtimeTranslator.Core.Realtime;

/// <summary>
/// ルーティング判定用に保持する原文バッファの窓切り詰め。
/// <see cref="SpokenLanguageDetector.RecentEvidence"/> と同じ判定窓を残す純粋ロジックで、
/// セッションのライフサイクル（接続・世代・状態遷移）から独立している。
/// </summary>
internal static class RoutingSourceTextWindow
{
    /// <summary>
    /// ルーティング判定用に保持する原文の上限 (UTF-16 char)。
    /// ja-* は末尾の非空白 scalar ウィンドウへ切り詰め、ウィンドウ内の空白が異常に長い場合の
    /// 安全弁として空白 run を圧縮してこの長さへ収める。en-es は語窓へ切り詰め、空白 run を圧縮し、
    /// なお上限を超える場合は Unicode scalar 境界で先頭から切り詰める。
    /// </summary>
    internal const int MaxLength = 16 * SpokenLanguageDetector.RecentEvidenceWindow;

    /// <summary>
    /// <c>en-es</c> は語窓へ切り詰めたあと空白 run を圧縮し、上限を超えた分は scalar 境界で切る。
    /// 語が無く空白だけになった入力は空文字にする。
    /// それ以外は末尾非空白 scalar 窓で、空白 run が異常に長い場合だけ圧縮する。
    /// </summary>
    internal static string Trim(string text, LanguagePair pair)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        if (pair == LanguagePair.EnEs)
        {
            var wordStart = SpokenLanguageDetector.RecentWordWindowStart(
                text,
                SpokenLanguageDetector.EnEsWindow);
            var collapsed = CollapseWhitespaceRuns(text[wordStart..]);
            return string.IsNullOrWhiteSpace(collapsed)
                ? string.Empty
                : PrefixCappedToMaxLength(collapsed);
        }

        var window = RecentEvidenceWindowSubstring(text, SpokenLanguageDetector.RecentEvidenceWindow);
        if (window.Length <= MaxLength)
        {
            return window;
        }

        return CollapseWhitespaceRuns(window);
    }

    /// <summary>
    /// 末尾から空白以外の Unicode scalar を <paramref name="window"/> 個含む範囲の部分文字列。
    /// <see cref="SpokenLanguageDetector.RecentEvidence"/> と同じ走査契約。
    /// </summary>
    private static string RecentEvidenceWindowSubstring(string text, int window)
    {
        if (window <= 0 || text.Length == 0)
        {
            return text;
        }

        var starts = new List<int>(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            starts.Add(offset);
            offset += Rune.GetRuneAt(text, offset).Utf16SequenceLength;
        }

        var nonWhitespaceCount = 0;
        var position = starts.Count;
        var start = 0;
        while (position > 0 && nonWhitespaceCount < window)
        {
            position -= 1;
            start = starts[position];
            if (!Rune.IsWhiteSpace(Rune.GetRuneAt(text, start)))
            {
                nonWhitespaceCount += 1;
            }
        }

        return nonWhitespaceCount == 0 ? string.Empty : text[start..];
    }

    /// <summary>UTF-16 上限を超える分を捨て、切り位置は Unicode scalar 境界に揃える。</summary>
    private static string PrefixCappedToMaxLength(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        var utf16Count = 0;
        var builder = new StringBuilder(MaxLength);
        foreach (var rune in text.EnumerateRunes())
        {
            var width = rune.Utf16SequenceLength;
            if (utf16Count + width > MaxLength)
            {
                break;
            }

            utf16Count += width;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>連続する空白 scalar を U+0020 1 個へ潰す。ラテン語境界を残しつつ保持長を抑える。</summary>
    private static string CollapseWhitespaceRuns(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxLength));
        var previousWasWhitespace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                previousWasWhitespace = true;
                builder.Append(' ');
                continue;
            }

            previousWasWhitespace = false;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
