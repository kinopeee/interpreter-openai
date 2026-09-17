using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RealtimeTranslator.Core.Localization;

namespace RealtimeTranslator.Core.Tests;

/// <summary>`shared/locales/ui.json` 用の lint。実行時経路では呼ばれないためテスト側に置く。</summary>
internal static class UserCopyCatalogLint
{
    private static readonly Regex PlaceholderPattern = new(
        @"\{([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    public static IReadOnlyList<string> DuplicateKeys(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (var item in UserCopy.Strings(json))
        {
            var key = UserCopy.RequiredText(item, "key");
            if (!seen.Add(key))
            {
                duplicates.Add(key);
            }
        }

        return duplicates;
    }

    public static IReadOnlyList<string> PlaceholderMismatches(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var mismatches = new List<string>();
        foreach (var item in UserCopy.Strings(json))
        {
            var key = UserCopy.RequiredText(item, "key");
            var ja = PlaceholderNames(UserCopy.RequiredText(item, "ja"));
            var en = PlaceholderNames(UserCopy.RequiredText(item, "en"));
            if (!ja.SetEquals(en))
            {
                mismatches.Add(key);
            }
        }

        return mismatches;
    }

    public static HashSet<string> PlaceholderNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            names.Add(match.Groups[1].Value);
        }

        return names;
    }
}
