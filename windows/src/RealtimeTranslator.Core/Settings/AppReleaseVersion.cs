using System;

namespace RealtimeTranslator.Core.Settings;

/// <summary>
/// リリースタグやアセンブリへ埋め込んだバージョン文字列を、設定画面向けの表示値へ正規化する。
/// </summary>
public static class AppReleaseVersion
{
    /// <summary>未リリース / 埋め込みなしのときの表示値。</summary>
    public const string Unpublished = "0.0.0";

    /// <summary>
    /// <c>v0.1.0</c> や <c>0.1.0+commit</c> から、設定に出すバージョンを取り出す。
    /// </summary>
    public static string DisplayValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Unpublished;
        }

        var value = raw.Trim();
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        if (value.Length >= 2 && (value[0] is 'v' or 'V') && char.IsAsciiDigit(value[1]))
        {
            value = value[1..];
        }

        value = value.Trim();
        return string.IsNullOrEmpty(value) ? Unpublished : value;
    }
}
