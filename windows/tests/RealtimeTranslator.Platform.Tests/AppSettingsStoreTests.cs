using System;
using System.IO;
using System.Text;
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
        Guid.NewGuid().ToString("N")
    );

    // Given: 設定ファイルが未作成の環境
    // When: 読み込む
    // Then: 既定値が返る
    [Fact]
    public void LoadReturnsDefaultsWhenFileIsMissing() => Assert.Equal(AppSettingsData.Default, CreateStore().Load());

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

    // Given: JSON として読めない settings.json と固定した現在時刻
    // When: 読み込む
    // Then: 既定値が返り、元のファイルは同じ中身のまま settings.json.corrupt-<UTC 時刻> へ退避される
    [Fact]
    public void LoadMovesCorruptFileAsideWithTimestamp()
    {
        var store = CreateStore(FixedClock);
        var corruptBytes = Encoding.UTF8.GetBytes("{ broken");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(store.FilePath, corruptBytes);

        var settings = store.Load();

        Assert.Equal(AppSettingsData.Default, settings);
        Assert.False(File.Exists(store.FilePath));
        var backupPath = store.FilePath + ".corrupt-" + FixedTimestamp;
        Assert.True(File.Exists(backupPath));
        Assert.Equal(corruptBytes, File.ReadAllBytes(backupPath));
    }

    // Given: 同じ時刻の退避ファイルが既にある状態で壊れた settings.json
    // When: 読み込む
    // Then: 既存の退避ファイルを上書きせず、-1 を付けた名前へ退避する
    [Fact]
    public void LoadAddsSuffixWhenCorruptBackupAlreadyExists()
    {
        var store = CreateStore(FixedClock);
        Directory.CreateDirectory(_directory);
        var existingBackupPath = store.FilePath + ".corrupt-" + FixedTimestamp;
        File.WriteAllText(existingBackupPath, "earlier backup");
        File.WriteAllText(store.FilePath, "[1,2,3]");

        Assert.Equal(AppSettingsData.Default, store.Load());

        Assert.False(File.Exists(store.FilePath));
        Assert.Equal("earlier backup", File.ReadAllText(existingBackupPath));
        Assert.Equal("[1,2,3]", File.ReadAllText(existingBackupPath + "-1"));
    }

    // Given: 正常な settings.json、またはフィールドの型だけが不正な settings.json
    // When: 読み込む
    // Then: 壊れている扱いにせず、ファイルを退避しない
    [Theory]
    [InlineData("""{"fontSize":40}""")]
    [InlineData("""{"fontSize":"40","recordSubtitles":"true"}""")]
    public void LoadDoesNotMoveReadableFileAside(string json)
    {
        var store = CreateStore(FixedClock);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, json);

        store.Load();

        Assert.Equal(json, File.ReadAllText(store.FilePath));
        Assert.Empty(Directory.GetFiles(_directory, AppSettingsStore.FileName + ".corrupt-*"));
    }

    // Given: 書き込める保存先
    // When: 保存する
    // Then: true が返る
    [Fact]
    public void SaveReturnsTrueOnSuccess() => Assert.True(CreateStore().Save(AppSettingsData.Default));

    // Given: 保存先の親ディレクトリに当たる場所に通常ファイルがあり、ディレクトリを作れない保存先
    // When: 保存する
    // Then: 例外を投げず false が返る
    [Fact]
    public void SaveReturnsFalseWhenDirectoryCannotBeCreated()
    {
        Directory.CreateDirectory(_directory);
        var blockingFile = Path.Combine(_directory, "blocker");
        File.WriteAllText(blockingFile, "not a directory");
        var store = new AppSettingsStore(Path.Combine(blockingFile, AppSettingsStore.FileName));

        Assert.False(store.Save(AppSettingsData.Default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string FixedTimestamp = "20261031T104400Z";

    private static readonly FixedTimeProvider FixedClock = new(
        new DateTimeOffset(2026, 10, 31, 10, 44, 0, TimeSpan.Zero)
    );

    private AppSettingsStore CreateStore(TimeProvider? timeProvider = null) =>
        new(Path.Combine(_directory, AppSettingsStore.FileName), timeProvider);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
