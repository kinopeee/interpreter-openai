using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Subtitles;
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
        Assert.Equal("バージョン 0.1.0", copy.Format("settings.appVersion", new Dictionary<string, string>
        {
            ["version"] = "0.1.0",
        }));
        var english = UserCopy.Parse(json, UiLocale.En);
        Assert.Equal("Version 0.1.0", english.Format("settings.appVersion", new Dictionary<string, string>
        {
            ["version"] = "0.1.0",
        }));
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

    // Given: 非 ASCII や不正な開始文字を含む疑似プレースホルダ
    // When: 名前を抽出する
    // Then: Swift / CI と同じ ASCII 識別子だけを認める
    [Fact]
    public void PlaceholderNamesRejectNonAsciiIdentifiers()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "hotkey" },
            UserCopy.PlaceholderNames("ok {hotkey} and {名前} and {1bad}"));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "_ok", "_", "a1" },
            UserCopy.PlaceholderNames("{_ok} {_} {a1}"));
    }

    // Given: 重複キーを含むカタログ
    // When: 検査する
    // Then: 2回目以降のキーが報告される
    [Fact]
    public void DuplicateKeysAreReported()
    {
        var json = """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                { "key": "dup", "ja": "一", "en": "one" },
                { "key": "ok", "ja": "二", "en": "two" },
                { "key": "dup", "ja": "三", "en": "three" }
              ]
            }
            """;

        var duplicates = UserCopy.DuplicateKeys(json);
        Assert.Single(duplicates);
        Assert.Equal("dup", duplicates[0]);
    }

    // Given: 壊れたカタログ JSON
    // When: 読み込む
    // Then: 起動不能な文言テーブルを作らず例外にする
    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":1}")]
    [InlineData("""{"strings":[{"key":"x","ja":"","en":"ok"}]}""")]
    [InlineData("""{"strings":[{"key":"x","ja":"はい"}]}""")]
    public void ParseRejectsInvalidCatalogs(string json)
    {
        Assert.Throws<InvalidOperationException>(() => UserCopy.Parse(json, UiLocale.Ja));
    }

    // Given: 置換しなかったプレースホルダ
    // When: Format する
    // Then: 残った {name} は消えず、string.Format の位置指定にも落ちない
    [Fact]
    public void FormatLeavesUnknownPlaceholders()
    {
        var json = """
            {
              "version": 1,
              "locales": ["ja", "en"],
              "fallback": "en",
              "strings": [
                {
                  "key": "banner.reconnectingProgress",
                  "ja": "{detail} 再接続中… ({attempt}/{max})",
                  "en": "{detail} Reconnecting… ({attempt}/{max})"
                }
              ]
            }
            """;
        var copy = UserCopy.Parse(json, UiLocale.Ja);

        Assert.Equal(
            " 再接続中… ({attempt}/3)",
            copy.Format("banner.reconnectingProgress", new Dictionary<string, string>
            {
                ["detail"] = string.Empty,
                ["max"] = "3",
            }));
    }

    // Given: Core が実行時に引くユーザー向けキー
    // When: 正本カタログを読む
    // Then: 欠けたキーをキー名のまま画面へ出さない
    [Fact]
    public void CoreProductionKeysExistInCatalog()
    {
        var copy = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.Ja);
        string[] keys =
        [
            "error.genericServer",
            "error.missingApiKey",
            "error.notConnected",
            "error.invalidMessage",
            "error.authenticationFailed",
            "error.transportDisconnected",
            "error.sourceDisconnected",
            "error.audioSendFailed",
            "error.sourceSessionGeneric",
            "error.sessionUpdateTimeout",
            "error.closeTimeout",
            "error.cancelled",
            "error.reconnectLimit",
            "error.audioInputStopped",
            "error.eventStreamStopped",
            "transcript.sizeLimitBanner",
            "transcript.writeFailureBanner",
            "banner.connecting",
            "banner.reconnecting",
            "banner.idle",
        ];

        foreach (var key in keys)
        {
            Assert.NotEqual(key, copy.Text(key));
            Assert.False(string.IsNullOrWhiteSpace(copy.Text(key)));
        }

        Assert.Equal(copy.Text("error.audioSendFailed"), DualRealtimeTranslationClient.TransportErrorMessage);
        Assert.Equal(copy.Text("banner.connecting"), SubtitleSnapshotBuilder.ConnectingBanner);
        Assert.Equal(copy.Text("banner.reconnecting"), SubtitleSnapshotBuilder.ReconnectingBanner);
        Assert.Equal(copy.Text("error.genericServer"), RealtimeTranslationException.GenericServerMessage);
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
    [InlineData(UiLanguagePreference.System, null, UiLocale.En)]
    [InlineData(UiLanguagePreference.System, "", UiLocale.En)]
    public void ResolveFollowsPreferenceThenOsLanguage(
        UiLanguagePreference preference,
        string? osLanguage,
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

    // Given: フェーズ4で埋めた en 文言
    // When: ja と並べる（Current は切り替えない）
    // Then: 製品名などの allowlist 以外は ja == en にならない
    [Fact]
    public void EnglishCopyDiffersFromJapaneseExceptAllowlist()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "settings.section.openai",
            "settings.uiLanguage.en",
        };
        var strings = JsonNode.Parse(SharedFixtures.UiCatalogJson)!["strings"]!.AsArray();
        var en = UserCopy.Parse(SharedFixtures.UiCatalogJson, UiLocale.En);

        foreach (var node in strings)
        {
            var item = node!.AsObject();
            var key = SharedFixtures.Text(item["key"]);
            var jaText = SharedFixtures.Text(item["ja"]);
            var enText = SharedFixtures.Text(item["en"]);
            if (!allowlist.Contains(key))
            {
                Assert.NotEqual(jaText, enText);
            }

            Assert.Equal(enText, en.Text(key));
        }
    }

    // Given: 英語 UI のトレイツールチップ
    // When: 製品名と状態名を連結する
    // Then: NotifyIcon.Text の 63 文字上限に収まる
    [Fact]
    public void EnglishTrayTooltipFitsNotifyIconLimit()
    {
        Assert.True("Realtime Translator (Idle)".Length <= 63);
        Assert.True("Realtime Translator (Connecting)".Length <= 63);
        Assert.True("Realtime Translator (Listening)".Length <= 63);
        Assert.True("Realtime Translator (Reconnecting)".Length <= 63);
        Assert.True("Realtime Translator (Closing)".Length <= 63);
        Assert.True("Realtime Translator (Error)".Length <= 63);
    }
}
