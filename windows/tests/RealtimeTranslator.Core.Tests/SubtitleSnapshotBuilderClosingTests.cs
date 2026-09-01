using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>
/// 停止中 (Closing) のバナー契約。Idle / Error / Listening の既存 snapshot テストとは交差しない。
/// </summary>
public sealed class SubtitleSnapshotBuilderClosingTests
{
    // Given: Listening 中に字幕が出ている
    // When: Closing へ遷移する
    // Then: 直前の字幕は残し、待機/接続バナーで覆わない
    [Fact]
    public void ClosingKeepsVisibleSubtitleWithoutBanner()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        var snapshot = builder.Apply(TranslationState.Closing);

        Assert.Equal("こんにちは", snapshot.Current.SourceText);
        Assert.Equal("Hello", snapshot.Current.TranslatedText);
        Assert.Null(snapshot.StatusBanner);
    }

    // Given: 空スロット
    // When: Closing を適用する
    // Then: Idle の待機バナーも Connecting バナーも出さない（停止中を待機と誤認しない）
    [Fact]
    public void ClosingEmptySlotDoesNotShowIdleOrConnectingBanner()
    {
        var builder = new SubtitleSnapshotBuilder();

        var snapshot = builder.Apply(TranslationState.Closing);

        Assert.True(snapshot.Current.IsEmpty);
        Assert.Null(snapshot.StatusBanner);
        Assert.NotEqual(SubtitleSnapshotBuilder.IdleBanner, snapshot.StatusBanner);
        Assert.NotEqual(SubtitleSnapshotBuilder.ConnectingBanner, snapshot.StatusBanner);
    }
}
