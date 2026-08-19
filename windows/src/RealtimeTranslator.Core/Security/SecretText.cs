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
        return MapScalars(value).ToLowerInvariant();
    }

    /// <summary>
    /// ログ伏字の前に Format を落とし、制御空白は ASCII 空白へ寄せる。
    /// TAB を消して <c>api key</c> / <c>bearer </c> を連結しない。ZWSP は除去して <c>sk-</c> を復元する。
    /// </summary>
    public static string StripFormatAndControl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return MapScalars(value);
    }

    private static string MapScalars(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format)
            {
                // ZWSP 等。キー断片の間に入れても照合できるよう落とす。
                continue;
            }

            if (category == UnicodeCategory.Control)
            {
                // TAB/CR/LF は語区切りとして残す。消すと api key や 401 判定が壊れる。
                if (Rune.IsWhiteSpace(rune))
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
