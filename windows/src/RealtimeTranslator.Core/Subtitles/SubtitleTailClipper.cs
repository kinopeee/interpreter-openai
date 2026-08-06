using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealtimeTranslator.Core.Subtitles;

/// <summary>字幕表示用に末尾 N 文字だけを残す。</summary>
public static class SubtitleTailClipper
{
    public const int JapaneseCharacterLimit = 60;
    public const int EnglishCharacterLimit = 120;
    public const string Ellipsis = "…";

    public static string Clip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            // 空白のみの入力は呼び出し元の空判定を壊さないようそのまま返す。
            return text;
        }

        var isCjk = ContainsCjk(trimmed);
        var limit = isCjk ? JapaneseCharacterLimit : EnglishCharacterLimit;
        var elements = TextElements(trimmed);
        if (elements.Count <= limit)
        {
            return trimmed;
        }

        var suffix = string.Concat(elements.GetRange(elements.Count - limit, limit));
        var tail = isCjk ? suffix : DropLeadingPartialWord(suffix);
        return tail.Length == 0 ? Ellipsis + suffix : Ellipsis + tail;
    }

    public static bool ContainsCjk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is (>= 0x3040 and <= 0x30FF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>英語の単語途中切れを避けるため、先頭から最初の空白までを捨てる。</summary>
    private static string DropLeadingPartialWord(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            var afterSpace = index + 1;
            return afterSpace < text.Length ? text[afterSpace..] : text;
        }

        return text;
    }

    /// <summary>Swift の Character 単位カウントに合わせて書記素クラスタで分割する。</summary>
    private static List<string> TextElements(string text)
    {
        var elements = new List<string>(text.Length);
        var offset = 0;
        while (offset < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            elements.Add(text.Substring(offset, length));
            offset += length;
        }

        return elements;
    }
}
