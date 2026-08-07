using System;
using System.IO;
using System.Text;
using RealtimeTranslator.Core.Subtitles;
using RealtimeTranslator.Platform.Subtitles;
using Xunit;

namespace RealtimeTranslator.Platform.Tests;

public sealed class SubtitleTranscriptStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly DateTimeOffset _fixedNow =
        new(2026, 8, 7, 15, 40, 12, TimeSpan.FromHours(9));

    public SubtitleTranscriptStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "transcript-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "session.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // Given: 空のストア
    // When: セッション開始マーカーと確定ペアを追記する
    // Then: ファイルにマーカーとペアが書かれ HasEntries が true になる
    [Fact]
    public void AppendSessionStartAndEntry()
    {
        var store = MakeStore();

        Assert.False(store.HasEntries);
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.MarkSessionStart());
        Assert.Equal(
            SubtitleTranscriptAppendResult.Appended,
            store.AppendEntry("こんにちは", "Hello"));
        Assert.True(store.HasEntries);

        var text = File.ReadAllText(_filePath, Encoding.UTF8);
        Assert.Contains("=== 録音開始 2026-08-07T15:40:12+09:00", text, StringComparison.Ordinal);
        Assert.Contains("--- 2026-08-07T15:40:12+09:00", text, StringComparison.Ordinal);
        Assert.Contains("原文: こんにちは", text, StringComparison.Ordinal);
        Assert.Contains("訳文: Hello", text, StringComparison.Ordinal);
    }

    // Given: 同じ確定ペアを連続で受け取る
    // When: 2回 append する
    // Then: 2回目は skip されファイルは1エントリのまま
    [Fact]
    public void DeduplicatesIdenticalConsecutiveEntries()
    {
        var store = MakeStore();
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.AppendEntry("こんにちは", "Hello"));
        Assert.Equal(
            SubtitleTranscriptAppendResult.SkippedDuplicate,
            store.AppendEntry("こんにちは", "Hello"));

        var text = File.ReadAllText(_filePath, Encoding.UTF8);
        Assert.Equal(1, text.Split("--- ", StringSplitOptions.None).Length - 1);
    }

    // Given: 直前セッション末尾と同じペア
    // When: MarkSessionStart のあと再度 Append する
    // Then: 新セッションでは重複スキップせず追記される
    [Fact]
    public void MarkSessionStartClearsConsecutiveDedup()
    {
        var store = MakeStore();
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.AppendEntry("こんにちは", "Hello"));
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.MarkSessionStart());
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.AppendEntry("こんにちは", "Hello"));

        var text = File.ReadAllText(_filePath, Encoding.UTF8);
        Assert.Equal(2, text.Split("--- ", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, text.Split("=== 録音開始 ", StringSplitOptions.None).Length - 1);
    }

    // Given: 書き込み不能なパス
    // When: 同じペアを連続で Append する
    // Then: 初回は Failed、2回目は SkippedDuplicate になり再試行しない
    [Fact]
    public void FailedWriteRemembersPairToAvoidRetrySpam()
    {
        var blockedPath = Path.Combine(_directory, "blocked-dir");
        Directory.CreateDirectory(blockedPath);
        var store = new SubtitleTranscriptStore(blockedPath, () => _fixedNow);

        Assert.Equal(
            SubtitleTranscriptAppendResult.Failed,
            store.AppendEntry("こんにちは", "Hello"));
        Assert.Equal(
            SubtitleTranscriptAppendResult.SkippedDuplicate,
            store.AppendEntry("こんにちは", "Hello"));
    }

    // Given: 空白のみの原文または訳文
    // When: append する
    // Then: SkippedEmpty になり HasEntries は false
    [Fact]
    public void SkipsEmptyPairs()
    {
        var store = MakeStore();
        Assert.Equal(SubtitleTranscriptAppendResult.SkippedEmpty, store.AppendEntry("  ", "Hello"));
        Assert.Equal(SubtitleTranscriptAppendResult.SkippedEmpty, store.AppendEntry("こんにちは", "\n"));
        Assert.False(store.HasEntries);
    }

    // Given: 上限直前まで埋まったファイル
    // When: 追記しようとする
    // Then: Capped を返し以後も no-op、クリア後は再開できる
    [Fact]
    public void StopsAtSizeCapAndResumesAfterClear()
    {
        var store = MakeStore(maxFileBytes: 64);
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.AppendEntry("短い", "short"));
        var first = File.ReadAllText(_filePath, Encoding.UTF8);

        Assert.Equal(
            SubtitleTranscriptAppendResult.Capped,
            store.AppendEntry("とても長い原文を追加して上限を超える", "overflow"));
        Assert.Equal(
            SubtitleTranscriptAppendResult.Capped,
            store.AppendEntry("別の文", "another"));
        Assert.Equal(first, File.ReadAllText(_filePath, Encoding.UTF8));

        store.Clear();
        Assert.False(store.HasEntries);
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.AppendEntry("再開", "resume"));
        Assert.True(store.HasEntries);
    }

    // Given: 追記済みのセッションファイル
    // When: 別パスへ ExportCopy する
    // Then: 内容がコピーされる
    [Fact]
    public void ExportCopy()
    {
        var store = MakeStore();
        Assert.Equal(SubtitleTranscriptAppendResult.Appended, store.AppendEntry("こんにちは", "Hello"));
        var destination = Path.Combine(_directory, "export.txt");
        store.ExportCopy(destination);
        Assert.Equal(
            File.ReadAllText(_filePath, Encoding.UTF8),
            File.ReadAllText(destination, Encoding.UTF8));
    }

    // Given: 固定時刻
    // When: 既定の書き出しファイル名を生成する
    // Then: subtitles-YYYYMMDD-HHmmss.txt になる
    [Fact]
    public void DefaultExportFileName()
    {
        Assert.Equal(
            "subtitles-20260807-154012.txt",
            SubtitleTranscriptStore.DefaultExportFileName(_fixedNow));
    }

    // Given: 上限・失敗バナー定数
    // When: 文言を確認する
    // Then: 原文・訳文を含まない
    [Fact]
    public void BannerMessagesDoNotContainSubtitleBody()
    {
        Assert.DoesNotContain("原文", SubtitleTranscriptStore.SizeLimitBanner, StringComparison.Ordinal);
        Assert.DoesNotContain("訳文", SubtitleTranscriptStore.WriteFailureBanner, StringComparison.Ordinal);
        Assert.Equal(SubtitleTranscriptLimits.MaxFileBytes, SubtitleTranscriptStore.MaxFileBytes);
    }

    private SubtitleTranscriptStore MakeStore(int maxFileBytes = SubtitleTranscriptLimits.MaxFileBytes) =>
        new(_filePath, () => _fixedNow, maxFileBytes);
}
