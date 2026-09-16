using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace RealtimeTranslator.Core.Localization;

/// <summary>設定に保存する表示言語。翻訳ペア (<c>languagePair</c>) とは別。</summary>
public enum UiLanguagePreference
{
    System,
    Ja,
    En,
}

/// <summary>カタログから引く解決済みロケール。ja 以外の OS は en。</summary>
public enum UiLocale
{
    Ja,
    En,
}

/// <summary><c>uiLanguage</c> の wire 値と OS 言語からの解決。</summary>
public static class UiLanguage
{
    public const string SystemWire = "system";
    public const string JaWire = "ja";
    public const string EnWire = "en";

    public static UiLanguagePreference Parse(string? wireValue) => wireValue switch
    {
        JaWire => UiLanguagePreference.Ja,
        EnWire => UiLanguagePreference.En,
        _ => UiLanguagePreference.System,
    };

    public static string ToWireValue(this UiLanguagePreference preference) => preference switch
    {
        UiLanguagePreference.Ja => JaWire,
        UiLanguagePreference.En => EnWire,
        _ => SystemWire,
    };

    public static UiLocale Resolve(UiLanguagePreference preference, string? osTwoLetterLanguage) =>
        preference switch
        {
            UiLanguagePreference.Ja => UiLocale.Ja,
            UiLanguagePreference.En => UiLocale.En,
            _ => string.Equals(osTwoLetterLanguage, JaWire, StringComparison.OrdinalIgnoreCase)
                ? UiLocale.Ja
                : UiLocale.En,
        };
}

/// <summary>
/// ユーザー向け文言カタログ。ログや字幕本文ではない。
/// 起動時に 1 回ロードし、プロセス内で不変。テストの Current は常に ja。
/// </summary>
public sealed class UserCopy
{
    public const string EmbeddedResourceName = "RealtimeTranslator.Core.locales.ui.json";

    private static readonly object Gate = new();
    private static UserCopy? _current;

    private readonly IReadOnlyDictionary<string, string> _primary;
    private readonly IReadOnlyDictionary<string, string> _english;
    private readonly Action<string>? _missingKeyLogger;

    public UserCopy(
        UiLocale locale,
        IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, string> english,
        Action<string>? missingKeyLogger = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(english);

        Locale = locale;
        _primary = primary;
        _english = english;
        _missingKeyLogger = missingKeyLogger;
    }

    public UiLocale Locale { get; }

    /// <summary>プロセス広域の文言。未インストール時は埋め込み ja。テストは切り替えない。</summary>
    public static UserCopy Current
    {
        get
        {
            var existing = Volatile.Read(ref _current);
            if (existing is not null)
            {
                return existing;
            }

            lock (Gate)
            {
                return _current ??= LoadEmbedded(UiLocale.Ja);
            }
        }
    }

    public static void Install(UserCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        Volatile.Write(ref _current, copy);
    }

    public static void InstallFromPreference(UiLanguagePreference preference, string? osTwoLetterLanguage)
    {
        Install(LoadEmbedded(UiLanguage.Resolve(preference, osTwoLetterLanguage)));
    }

    public string this[string key] => Text(key);

    public string Text(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_primary.TryGetValue(key, out var value))
        {
            return value;
        }

        LogMissing(key);
        if (_english.TryGetValue(key, out value))
        {
            return value;
        }

        return key;
    }

    public string Format(string key, string name, string value) =>
        Format(key, new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value });

    public string Format(string key, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(substitutions);

        var template = Text(key);
        foreach (var pair in substitutions)
        {
            template = template.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
        }

        return template;
    }

    public static UserCopy LoadEmbedded(UiLocale locale, Action<string>? missingKeyLogger = null)
    {
        var assembly = typeof(UserCopy).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("embedded ui.json is missing");
        return Load(stream, locale, missingKeyLogger);
    }

    public static UserCopy Load(Stream utf8Json, UiLocale locale, Action<string>? missingKeyLogger = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        using var reader = new StreamReader(utf8Json, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader.ReadToEnd(), locale, missingKeyLogger);
    }

    public static UserCopy Parse(string json, UiLocale locale, Action<string>? missingKeyLogger = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        var tables = ReadLocaleTables(json);
        var primary = locale == UiLocale.Ja ? tables.Ja : tables.En;
        return new UserCopy(locale, primary, tables.En, missingKeyLogger);
    }

    private static (Dictionary<string, string> Ja, Dictionary<string, string> En) ReadLocaleTables(string json)
    {
        var ja = new Dictionary<string, string>(StringComparer.Ordinal);
        var en = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Strings(json))
        {
            var key = RequiredText(item, "key");
            ja[key] = RequiredText(item, "ja");
            en[key] = RequiredText(item, "en");
        }

        return (ja, en);
    }

    internal static IEnumerable<JsonObject> Strings(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("ui.json is not valid JSON", exception);
        }

        if (root is not JsonObject catalog || catalog["strings"] is not JsonArray strings)
        {
            throw new InvalidOperationException("ui.json is missing strings");
        }

        foreach (var node in strings)
        {
            if (node is JsonObject item)
            {
                yield return item;
            }
        }
    }

    internal static string RequiredText(JsonObject item, string name)
    {
        if (item[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            return text;
        }

        throw new InvalidOperationException($"ui.json entry is missing {name}");
    }

    private void LogMissing(string key)
    {
        _missingKeyLogger?.Invoke(key);
#if DEBUG
        Debug.WriteLine("UserCopy missing key: " + key);
#endif
    }
}
