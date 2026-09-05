using RealtimeTranslator.Core.Localization;
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

    // Given: 確定済みの字幕更新
    // When: スナップショットビルダーへ適用する
    // Then: 確定状態が LiveSubtitle へ伝播する
    [Fact]
    public void ShouldFinalizePropagatesToLiveSubtitle()
    {
        var builder = new SubtitleSnapshotBuilder();

        var snapshot = builder.Apply(
            new RealtimeSubtitleUpdate("source", "translation", IsTranslationCurrent: true, ShouldFinalize: true, 0),
            TranslationState.Listening);

        Assert.True(snapshot.Current.IsFinalized);
    }

    // Given: ShouldFinalize 付きの更新
    // When: builder → ViewModel へ通す
    // Then: 確定後は翻訳中マーカーを出さない
    [Fact]
    public void FinalizedSnapshotHidesPendingMarkerThroughViewModel()
    {
        var builder = new SubtitleSnapshotBuilder();
        var snapshot = builder.Apply(
            new RealtimeSubtitleUpdate("source", "translation", IsTranslationCurrent: true, ShouldFinalize: true, 0),
            TranslationState.Listening);
        var viewModel = new SubtitleOverlayViewModel();

        viewModel.Apply(snapshot);

        Assert.True(viewModel.IsFinalized);
        Assert.False(viewModel.ShowsPendingMarker);
        Assert.Equal(string.Empty, viewModel.PendingMarkerText);
    }

    // Given: 字幕を表示したままエラーへ遷移する
    // When: Error 状態を適用する
    // Then: 字幕は残し、待機バナーで失敗表示を覆わない
    [Fact]
    public void ErrorKeepsVisibleSubtitleWithoutIdleBanner()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        var snapshot = builder.Apply(TranslationState.Error);

        Assert.Equal("こんにちは", snapshot.Current.SourceText);
        Assert.Equal("Hello", snapshot.Current.TranslatedText);
        Assert.Null(snapshot.StatusBanner);
    }

    // Given: 確定済みの字幕
    // When: Reset する
    // Then: 未確定の空スロットへ戻る
    [Fact]
    public void ResetReturnsToUnfinalizedSubtitle()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("source", "translation", IsTranslationCurrent: true, ShouldFinalize: true, 0),
            TranslationState.Listening);

        var snapshot = builder.Reset(TranslationState.Idle);

        Assert.False(snapshot.Current.IsFinalized);
    }

    // Given: カタログのバナーキー
    // When: idle / connecting / reconnecting を組み立てる
    // Then: 直書き日本語ではなく UserCopy のキーへ解決する
    [Fact]
    public void BannersComeFromCatalogAndIdleSubstitutesHotkey()
    {
        Assert.Equal(
            UserCopy.Current.Text("banner.connecting"),
            SubtitleSnapshotBuilder.ConnectingBanner);
        Assert.Equal(
            UserCopy.Current.Text("banner.reconnecting"),
            SubtitleSnapshotBuilder.ReconnectingBanner);
        Assert.Equal(
            UserCopy.Current.Format("banner.idle", "hotkey", "Ctrl + Alt + Space"),
            SubtitleSnapshotBuilder.IdleBanner);
        Assert.Equal(
            UserCopy.Current.Format("banner.idle", "hotkey", "Control + Option + Space"),
            SubtitleSnapshotBuilder.IdleBannerFor("Control + Option + Space"));
        Assert.Contains("Control + Option + Space", SubtitleSnapshotBuilder.IdleBannerFor("Control + Option + Space"));
    }

    // Given: 未確定字幕を Reset（停止後約 5 秒クリア）した builder
    // When: 同じ SegmentGeneration の更新が届く
    // Then: Reset は世代を進めないので同一 gen でスロットが再充填される
    // （世代を 0 クリアすると新セグメントまで拒否する別契約になる）
    [Fact]
    public void ResetLeavesSegmentGenerationSoSameGenerationCanRefill()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate("こんにちは", "Hello", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);
        builder.Reset(TranslationState.Idle);

        var snapshot = builder.Apply(
            new RealtimeSubtitleUpdate("遅延原文", "Late", IsTranslationCurrent: true, ShouldFinalize: false, 0),
            TranslationState.Listening);

        Assert.Equal("遅延原文", snapshot.Current.SourceText);
        Assert.Equal("Late", snapshot.Current.TranslatedText);
    }

    // Given: 確定済み字幕と新しいシーケンス番号
    // When: 古い更新と無効化更新を適用する
    // Then: 古い更新は無視され、確定済み字幕は保持される
    [Fact]
    public void SequenceOrderingIgnoresStaleUpdatesAndInvalidationPreservesFinalizedContent()
    {
        var builder = new SubtitleSnapshotBuilder();
        builder.Apply(
            new RealtimeSubtitleUpdate(
                "確定",
                "Final",
                IsTranslationCurrent: true,
                ShouldFinalize: true,
                SegmentGeneration: 0,
                Sequence: 2),
            TranslationState.Listening);

        var stale = builder.Apply(
            new RealtimeSubtitleUpdate(
                "古い",
                "Stale",
                IsTranslationCurrent: true,
                ShouldFinalize: false,
                SegmentGeneration: 0,
                Sequence: 1),
            TranslationState.Listening);
        var invalidated = builder.Apply(
            new RealtimeSubtitleUpdate(
                string.Empty,
                string.Empty,
                IsTranslationCurrent: false,
                ShouldFinalize: false,
                SegmentGeneration: 1,
                IsInvalidation: true,
                Sequence: 3),
            TranslationState.Listening);

        Assert.Equal("Final", stale.Current.TranslatedText);
        Assert.True(stale.Current.IsFinalized);
        Assert.Equal("Final", invalidated.Current.TranslatedText);
        Assert.True(invalidated.Current.IsFinalized);
    }
}
