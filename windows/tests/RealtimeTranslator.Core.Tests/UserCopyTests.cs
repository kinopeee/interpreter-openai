using System.Collections.Generic;
using RealtimeTranslator.Core.Localization;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class UserCopyTests
{
    // Given: 正本の ui.json
    // When: キー集合と ja/en を検査する
    // Then: キーは一意で、空文字が無く、プレースホルダ名が一致する
    [Fact]
    public void CatalogKeysAreUniqueAndPlaceholdersMatch()
    {
        var json = SharedFixtures.UiCatalogJson;

        Assert.Empty(UserCopy.DuplicateKeys(json));
        Assert.Empty(UserCopy.PlaceholderMismatches(json));

        var copy = UserCopy.Parse(json, UiLocale.Ja);
        Assert.Equal(UiLocale.Ja, copy.Locale);
        Assert.False(string.IsNullOrEmpty(copy.Text("error.genericServer")));
        Assert.False(string.IsNullOrEmpty(copy.Text("settings.uiLanguage")));
    }

    // Given: 埋め込みリソースとリポジトリ上の ui.json
    // When: 同じキーを引く
    // Then: 値が一致する
    [Fact]
    public void EmbeddedCatalogMatchesRepositoryFile()
    {
        var fromFile = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.Ja);
        var embedded = UserCopy.LoadEmbedded(UiLocale.Ja);

        Assert.Equal(fromFile.Text("menu.startTranslation"), embedded.Text("menu.startTranslation"));
        Assert.Equal(fromFile.Text("error.genericServer"), embedded.Text("error.genericServer"));
        Assert.Equal(fromFile.Text("banner.idle"), embedded.Text("banner.idle"));
    }

    // Given: テストプロセスの Current
    // When: 既定のカタログを読む
    // Then: ja が載っており、Current を切り替えない
    [Fact]
    public void CurrentDefaultsToJapaneseCatalog()
    {
        Assert.Equal(UiLocale.Ja, UserCopy.Current.Locale);
        Assert.Equal("翻訳を開始", UserCopy.Current.Text("menu.startTranslation"));
    }

    // Given: ja に欠けたキーがあるカタログ
    // When: そのキーを引く
    // Then: en へフォールバックし、キー名だけを通知する
    [Fact]
    public void MissingPrimaryKeyFallsBackToEnglishAndLogsKeyName()
    {
        var logged = new List<string>();
        var json = """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                { "key": "only.en", "ja": "placeholder-ja-unused", "en": "English only" }
              ]
            }
            """;
        // ja テーブルからキーを消すため、Parse 後に空 primary を渡す。
        var english = UserCopy.Parse(json, UiLocale.En);
        var copy = new UserCopy(
            UiLocale.Ja,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["only.en"] = english.Text("only.en") },
            logged.Add);

        var text = copy.Text("only.en");

        Assert.Equal("English only", text);
        Assert.Single(logged);
        Assert.Equal("only.en", logged[0]);
    }

    // Given: 未知のキー
    // When: 引く
    // Then: キー名を返し、本文は捏造しない
    [Fact]
    public void UnknownKeyReturnsTheKeyName()
    {
        var logged = new List<string>();
        var copy = new UserCopy(
            UiLocale.Ja,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            logged.Add);

        Assert.Equal("missing.key", copy.Text("missing.key"));
        Assert.Single(logged);
        Assert.Equal("missing.key", logged[0]);
    }

    // Given: {name} プレースホルダを含む文言
    // When: Format する
    // Then: 単純置換され、string.Format は使わない
    [Fact]
    public void FormatReplacesNamedPlaceholders()
    {
        var json = """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                {
                  "key": "banner.idle",
                  "ja": "待機中 — {hotkey} で録音開始",
                  "en": "Idle — press {hotkey} to start"
                }
              ]
            }
            """;
        var copy = UserCopy.Parse(json, UiLocale.Ja);

        Assert.Equal(
            "待機中 — Control + Option + Space で録音開始",
            copy.Format("banner.idle", new Dictionary<string, string>
            {
                ["hotkey"] = "Control + Option + Space",
            }));
    }

    // Given: ja/en でプレースホルダ集合が違うエントリ
    // When: 検査する
    // Then: そのキーが不一致として報告される
    [Fact]
    public void PlaceholderMismatchIsDetected()
    {
        var json = """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                { "key": "bad", "ja": "hello {hotkey}", "en": "hello {name}" }
              ]
            }
            """;

        var mismatches = UserCopy.PlaceholderMismatches(json);
        Assert.Single(mismatches);
        Assert.Equal("bad", mismatches[0]);
    }

    // Given: OS の UI 言語と保存値
    // When: 表示言語を解決する
    // Then: ja OS だけ ja、それ以外と未知値は en / system
    [Theory]
    [InlineData(UiLanguagePreference.Ja, "en", UiLocale.Ja)]
    [InlineData(UiLanguagePreference.En, "ja", UiLocale.En)]
    [InlineData(UiLanguagePreference.System, "ja", UiLocale.Ja)]
    [InlineData(UiLanguagePreference.System, "en", UiLocale.En)]
    [InlineData(UiLanguagePreference.System, "es", UiLocale.En)]
    [InlineData(UiLanguagePreference.System, "fr", UiLocale.En)]
    [InlineData(UiLanguagePreference.System, "JA", UiLocale.Ja)]
    public void ResolveFollowsPreferenceThenOsLanguage(
        UiLanguagePreference preference,
        string osLanguage,
        UiLocale expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(preference, osLanguage));
    }

    // Given: 欠落または未知の wire 値
    // When: 読む
    // Then: system へ倒す
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("es")]
    [InlineData("unknown")]
    public void UnknownWireValueBecomesSystem(string? wire)
    {
        Assert.Equal(UiLanguagePreference.System, UiLanguage.Parse(wire));
        Assert.Equal("system", UiLanguagePreference.System.ToWireValue());
        Assert.Equal("ja", UiLanguagePreference.Ja.ToWireValue());
        Assert.Equal("en", UiLanguagePreference.En.ToWireValue());
    }
}
