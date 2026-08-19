using System;
using System.Globalization;
using System.Text;

namespace RealtimeTranslator.Core.Security;

/// <summary>
/// 秘密検出の前処理。Format/Control の挿入や Unicode 空白でサニタイザを迂回させない。
/// </summary>
public static class SecretText
{
    /// <summary>
    /// 部分一致判定用。不可視文字を落とし、空白類を ASCII 空白へ寄せてから小文字化する。
    /// </summary>
    public static string NormalizeForMatch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                builder.Append(' ');
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(rune);
        }

        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>ログ伏字の前に Format/Control を落とし、ZWSP 挿入キーを正規表現へ載せる。</summary>
    public static string StripFormatAndControl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                builder.Append(rune);
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
