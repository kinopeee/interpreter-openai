using System.Collections.Generic;
using RealtimeTranslator.Core.Settings;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleOverlayViewModelTests
{
    // Given: 上限を超える日本語原文
    // When: スナップショットを反映する
    // Then: 表示用に末尾側へ切り詰められる
    [Fact]
    public void ApplyClipsTextToTheSubtitleLimit()
    {
        var viewModel = new SubtitleOverlayViewModel();
        var longSource = new string('あ', SubtitleTailClipper.JapaneseCharacterLimit + 20);

        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle(longSource, "Hello", false), null));

        Assert.Equal(SubtitleTailClipper.Clip(longSource), viewModel.SourceText);
        Assert.True(viewModel.HasSourceText);
        Assert.True(viewModel.HasTranslatedText);
        Assert.False(viewModel.HasStatusBanner);
    }

    // Given: 空の字幕とバナー
    // When: スナップショットを反映する
    // Then: 本文は非表示、バナーだけ表示になる
    [Fact]
    public void EmptySubtitleWithBannerHidesTheTextBlocks()
    {
        var viewModel = new SubtitleOverlayViewModel();

        viewModel.Apply(new SubtitleSnapshot(LiveSubtitle.Empty, SubtitleSnapshotBuilder.IdleBanner));

        Assert.False(viewModel.HasSourceText);
        Assert.False(viewModel.HasTranslatedText);
        Assert.True(viewModel.HasStatusBanner);
    }

    // Given: 範囲外のフォントサイズ
    // When: 設定する
    // Then: 許容範囲へクランプし、派生サイズも追随する
    [Fact]
    public void FontSizeIsClampedAndDrivesDerivedSizes()
    {
        var viewModel = new SubtitleOverlayViewModel { FontSize = 999 };

        Assert.Equal(AppSettingsData.MaximumFontSize, viewModel.FontSize);
        Assert.Equal(
            AppSettingsData.MaximumFontSize * SubtitleOverlayViewModel.SourceFontScale,
            viewModel.SourceFontSize);

        viewModel.FontSize = 0;

        Assert.Equal(AppSettingsData.MinimumFontSize, viewModel.FontSize);
        Assert.Equal(SubtitleOverlayViewModel.MinimumBannerFontSize, viewModel.BannerFontSize);
    }

    // Given: バインド済みのビュー
    // When: 字幕とフォントサイズが変わる
    // Then: 対応するプロパティ変更通知が飛ぶ
    [Fact]
    public void PropertyChangedIsRaisedForBoundProperties()
    {
        var viewModel = new SubtitleOverlayViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle("こんにちは", "Hello", false), null));
        viewModel.FontSize = 40;
        viewModel.IsEditingPosition = true;

        Assert.Contains(nameof(SubtitleOverlayViewModel.SourceText), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.HasTranslatedText), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.StatusBanner), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.BannerFontSize), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.IsEditingPosition), changed);
    }

    // Given: フォントサイズの下限と上限
    // When: 18 と 48 に設定する
    // Then: 行高がフォントサイズに比例する
    [Fact]
    public void LineHeightRatioScalesProportionallyAcrossFontRange()
    {
        var viewModel = new SubtitleOverlayViewModel();

        viewModel.FontSize = 18;
        var minimumLineHeight = viewModel.TranslatedLineHeight;
        viewModel.FontSize = 48;
        var maximumLineHeight = viewModel.TranslatedLineHeight;

        Assert.Equal(18 * SubtitleOverlayViewModel.LineHeightRatio, minimumLineHeight);
        Assert.Equal(48 * SubtitleOverlayViewModel.LineHeightRatio, maximumLineHeight);
        Assert.Equal(30 * SubtitleOverlayViewModel.LineHeightRatio, maximumLineHeight - minimumLineHeight);
    }

    // Given: 短い字幕と長い字幕
    // When: スナップショットを順に反映する
    // Then: 予約されたスロット高さは変化しない
    [Fact]
    public void SlotHeightsRemainReservedRegardlessOfText()
    {
        var viewModel = new SubtitleOverlayViewModel();

        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle("source", "short", false), null));
        var translatedHeight = viewModel.TranslatedSlotHeight;
        var sourceHeight = viewModel.SourceSlotHeight;
        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle("source", new string('x', 400), false), null));

        Assert.Equal(translatedHeight, viewModel.TranslatedSlotHeight);
        Assert.Equal(sourceHeight, viewModel.SourceSlotHeight);
        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle("source", "complete.", true), null));

        Assert.Equal(translatedHeight, viewModel.TranslatedSlotHeight);
        Assert.Equal(sourceHeight, viewModel.SourceSlotHeight);
    }

    // Given: 様々な確定状態と末尾文字
    // When: 翻訳字幕を反映する
    // Then: マーカー表示が条件どおりになる
    [Theory]
    [InlineData("source", "translation", false, true)]
    [InlineData("source", "translation", true, false)]
    [InlineData("source", "", false, true)]
    [InlineData("source", "ends。", false, false)]
    [InlineData("source", "ends.", false, false)]
    [InlineData("source", "ends！", false, false)]
    [InlineData("source", "ends？", false, false)]
    [InlineData("source", "続きはまた…", false, false)]
    [InlineData("", "", false, false)]
    [InlineData("source", "   ", false, true)]
    [InlineData("", "   ", false, false)]
    public void PendingMarkerFollowsFinalizationAndPunctuationRules(
        string sourceText,
        string translatedText,
        bool isFinalized,
        bool expected)
    {
        var viewModel = new SubtitleOverlayViewModel();
        viewModel.Apply(new SubtitleSnapshot(
            new LiveSubtitle(sourceText, translatedText, isFinalized),
            null));

        Assert.Equal(expected, viewModel.ShowsPendingMarker);
        Assert.Equal(expected ? SubtitleOverlayViewModel.PendingMarker : string.Empty, viewModel.PendingMarkerText);
        Assert.Equal(!string.IsNullOrWhiteSpace(translatedText) || expected, viewModel.HasVisibleTranslation);
        if (string.IsNullOrWhiteSpace(translatedText))
        {
            Assert.Equal(string.Empty, viewModel.TranslatedText);
            Assert.False(viewModel.HasTranslatedText);
        }
    }

    // Given: バインドされる派生プロパティ
    // When: フォントサイズを変更する
    // Then: 行高とスロット高の変更通知が飛ぶ
    [Fact]
    public void FontSizeChangeRaisesLineHeightAndSlotHeightNotifications()
    {
        var viewModel = new SubtitleOverlayViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.FontSize = 40;

        Assert.Contains(nameof(SubtitleOverlayViewModel.TranslatedLineHeight), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.SourceLineHeight), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.TranslatedSlotHeight), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.SourceSlotHeight), changed);
    }
}
