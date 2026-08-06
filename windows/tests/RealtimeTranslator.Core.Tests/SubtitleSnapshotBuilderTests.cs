using RealtimeTranslator.Core.Realtime;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleSnapshotBuilderTests
{
    // Given: 起動直後
    // When: idle 状態を反映する
    // Then: 待機バナーだけが出る
    [Fact]
    public void IdleShowsWaitingBanner()
    {
        var snapshot = new SubtitleSnapshotBuilder().Apply(TranslationState.Idle);

        Assert.True(snapshot.Current.IsEmpty);
        Assert.Equal(SubtitleSnapshotBuilder.IdleBanner, snapshot.StatusBanner);
    }

    // Given: 接続中/再接続中
    // When: 状態を反映する
    // Then: それぞれのバナーが出る
    [Fact]
    public void ConnectingAndReconnectingHaveTheirOwnBanners()
    {
        var builder = new SubtitleSnapshotBuilder();

        Assert.Equal(SubtitleSnapshotBuilder.ConnectingBanner, builder.Apply(TranslationState.Connecting).StatusBanner);
        Assert.Equal(
            SubtitleSnapshotBuilder.ReconnectingBanner,
            builder.Apply(TranslationState.Reconnecting).StatusBanner);
    }

    // Given: listening 中の字幕更新
    // When: 原文と訳文が届く
    // Then: バナーを出さずに両方表示する
    [Fact]
    public void ListeningUpdateShowsBothTextsWithoutBanner()
    {
        var builder = new SubtitleSnapshotBuilder();

        var snapshot = builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        Assert.Equal("こんにちは", snapshot.Current.SourceText);
        Assert.Equal("Hello", snapshot.Current.TranslatedText);
        Assert.Null(snapshot.StatusBanner);
    }

    // Given: 確定した字幕の次に新しいセグメントが来る
    // When: 世代が進んだ更新を反映する
    // Then: 前セグメントの残骸を引きずらない
    [Fact]
    public void NewSegmentReplacesThePreviousSlot()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: true, 0),
            TranslationState.Listening);

        var snapshot = builder.Apply(
            new RealtimeSubtitleUpdate("ありがとう", string.Empty, IsTranslationCurrent: false, ShouldFinalize: false, 1),
            TranslationState.Listening);

        Assert.Equal("ありがとう", snapshot.Current.SourceText);
        Assert.Equal(string.Empty, snapshot.Current.TranslatedText);
    }

    // Given: 字幕を表示したまま停止した場合
    // When: idle へ戻る
    // Then: 直前の字幕は残し、待機バナーで覆わない
    [Fact]
    public void StoppingKeepsTheLastSubtitleVisible()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        var snapshot = builder.Apply(TranslationState.Idle);

        Assert.Equal("Hello", snapshot.Current.TranslatedText);
        Assert.Null(snapshot.StatusBanner);
    }

    // Given: 表示済みの字幕
    // When: リセットする
    // Then: 空スロットと待機バナーへ戻る
    [Fact]
    public void ResetClearsTheSlot()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        var snapshot = builder.Reset(TranslationState.Idle);

        Assert.True(snapshot.Current.IsEmpty);
        Assert.Equal(SubtitleSnapshotBuilder.IdleBanner, snapshot.StatusBanner);
    }

    // Given: エラー状態で字幕クリアタイマーが発火する
    // When: Reset する
    // Then: 待機バナーを出さず、エラートレイ表示と矛盾しない
    [Fact]
    public void ResetInErrorDoesNotShowIdleBanner()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        var snapshot = builder.Reset(TranslationState.Error);

        Assert.True(snapshot.Current.IsEmpty);
        Assert.Null(snapshot.StatusBanner);
    }
}
