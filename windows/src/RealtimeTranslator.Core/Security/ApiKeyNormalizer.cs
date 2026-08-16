using System;
using System.Globalization;
using System.Text;

namespace RealtimeTranslator.Core.Security;

public enum ApiKeyNormalizationStatus
{
    Empty,
    Malformed,
    Valid,
}

public readonly record struct ApiKeyNormalizationResult(
    ApiKeyNormalizationStatus Status,
    string? Value);

/// <summary>
/// BYOK キーの正規化。埋め込み空白・制御文字を落とし、ヘッダ破壊と貼り付けゴミを防ぐ。
/// </summary>
public static class ApiKeyNormalizer
{
    public static ApiKeyNormalizationResult Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new ApiKeyNormalizationResult(ApiKeyNormalizationStatus.Empty, null);
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var rune in raw.EnumerateRunes())
        {
            if (ShouldStrip(rune))
            {
                continue;
            }

            builder.Append(rune);
        }

        var stripped = builder.ToString();
        if (stripped.Length == 0)
        {
            return new ApiKeyNormalizationResult(ApiKeyNormalizationStatus.Empty, null);
        }

        if (!IsAllowed(stripped))
        {
            return new ApiKeyNormalizationResult(ApiKeyNormalizationStatus.Malformed, null);
        }

        return new ApiKeyNormalizationResult(ApiKeyNormalizationStatus.Valid, stripped);
    }

    private static bool ShouldStrip(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune))
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned;
    }

    private static bool IsAllowed(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>設定保存時の空・形式不正。メッセージはカタログ文言のみ。</summary>
public sealed class ApiKeyFormatException : ArgumentException
{
    public ApiKeyFormatException(string message)
        : base(message)
    {
    }
}
