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

        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle(longSource, "Hello"), null));

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

        viewModel.Apply(new SubtitleSnapshot(new LiveSubtitle("こんにちは", "Hello"), null));
        viewModel.FontSize = 40;
        viewModel.IsEditingPosition = true;

        Assert.Contains(nameof(SubtitleOverlayViewModel.SourceText), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.HasTranslatedText), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.StatusBanner), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.BannerFontSize), changed);
        Assert.Contains(nameof(SubtitleOverlayViewModel.IsEditingPosition), changed);
    }
}
