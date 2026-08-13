using System;
using System.IO;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Localization;
using RealtimeTranslator.Core.OpenAI;
using RealtimeTranslator.Core.Settings;
using RealtimeTranslator.Platform.Settings;
using Xunit;

namespace RealtimeTranslator.Platform.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "RealtimeTranslator.Tests",
        Guid.NewGuid().ToString("N"));

    // Given: 設定ファイルが未作成の環境
    // When: 読み込む
    // Then: 既定値が返る
    [Fact]
    public void LoadReturnsDefaultsWhenFileIsMissing() =>
        Assert.Equal(AppSettingsData.Default, CreateStore().Load());

    // Given: 変更した設定
    // When: 保存して読み戻す
    // Then: 同じ値が復元され、API キーはファイルに書かれない
    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = CreateStore();
        var settings = AppSettingsData.Default with
        {
            FontSize = 44,
            HasCustomOverlayOrigin = true,
            OverlayOriginX = 10,
            OverlayOriginY = 20,
            AcceptedConsentVersion = AppSettingsData.CurrentConsentVersion,
            NoiseReduction = RealtimeTranslationNoiseReduction.NearField,
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load());
        Assert.DoesNotContain("apiKey", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    // Given: 表示言語と翻訳ペアを変えた設定
    // When: 保存して読み戻す
    // Then: uiLanguage と languagePair が欠落せず、API キーはファイルに書かれない
    [Fact]
    public void SaveThenLoadRoundTripsUiLanguageAndLanguagePair()
    {
        var store = CreateStore();
        var settings = AppSettingsData.Default with
        {
            UiLanguage = UiLanguagePreference.En,
            LanguagePair = LanguagePair.JaEs,
        };

        store.Save(settings);

        var restored = store.Load();
        Assert.Equal(UiLanguagePreference.En, restored.UiLanguage);
        Assert.Equal(LanguagePair.JaEs, restored.LanguagePair);
        Assert.DoesNotContain("sk-", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    // Given: 途中で壊れた設定ファイル
    // When: 読み込む
    // Then: 例外を投げず既定値で起動できる
    [Fact]
    public void LoadRecoversFromCorruptFile()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ broken");

        Assert.Equal(AppSettingsData.Default, store.Load());
    }

    // Given: 既に保存済みの設定
    // When: 上書き保存する
    // Then: 一時ファイルを残さず置き換わる
    [Fact]
    public void SaveReplacesExistingFileWithoutLeavingTemporaries()
    {
        var store = CreateStore();
        store.Save(AppSettingsData.Default);

        store.Save(AppSettingsData.Default with { FontSize = 20 });

        Assert.Equal(20, store.Load().FontSize);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AppSettingsStore CreateStore() =>
        new(Path.Combine(_directory, AppSettingsStore.FileName));
}
