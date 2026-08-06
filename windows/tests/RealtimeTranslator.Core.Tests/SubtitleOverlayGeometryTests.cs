using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleOverlayGeometryTests
{
    // Given: 幅 1000 の作業領域
    // When: 字幕幅を求める
    // Then: 作業領域の 70% になる
    [Fact]
    public void WidthUsesSeventyPercentOfWorkArea() =>
        Assert.Equal(700, SubtitleOverlayGeometry.Width(new OverlayRect(0, 0, 1_000, 800)));

    // Given: 非常に広い作業領域
    // When: 字幕幅を求める
    // Then: 上限 1200 で頭打ちになる
    [Fact]
    public void WidthIsCappedAtMaximum() =>
        Assert.Equal(
            SubtitleOverlayGeometry.MaximumWidth,
            SubtitleOverlayGeometry.Width(new OverlayRect(0, 0, 5_000, 800)));

    // Given: 原点がずれた作業領域
    // When: 既定配置を求める
    // Then: 水平中央かつ下端から 24px 上に置かれる
    [Fact]
    public void DefaultPlacementCentersAboveTheBottomEdge()
    {
        var workArea = new OverlayRect(100, 50, 1_000, 800);

        var placement = SubtitleOverlayGeometry.DefaultPlacement(workArea, height: 120);

        Assert.Equal(700, placement.Width);
        Assert.Equal(250, placement.X);
        Assert.Equal(workArea.Bottom - 120 - SubtitleOverlayGeometry.BottomOffset, placement.Y);
    }

    // Given: 作業領域の外へ出た保存位置
    // When: クランプする
    // Then: 右下端に収まる位置へ寄せられる
    [Fact]
    public void ClampPullsWindowBackInsideWorkArea()
    {
        var workArea = new OverlayRect(0, 0, 1_000, 800);

        var clamped = SubtitleOverlayGeometry.Clamp(new OverlayRect(1_500, 900, 400, 200), workArea);

        Assert.Equal(600, clamped.X);
        Assert.Equal(600, clamped.Y);
    }

    // Given: 左上より手前の負座標
    // When: クランプする
    // Then: 作業領域の原点へ寄せられる
    [Fact]
    public void ClampPushesNegativeOriginToWorkAreaOrigin()
    {
        var clamped = SubtitleOverlayGeometry.Clamp(
            new OverlayRect(-300, -200, 400, 200),
            new OverlayRect(20, 10, 1_000, 800));

        Assert.Equal(20, clamped.X);
        Assert.Equal(10, clamped.Y);
    }

    // Given: 作業領域より大きい字幕、あるいは NaN 座標
    // When: クランプする
    // Then: 原点に合わせて画面外へ飛ばさない
    [Fact]
    public void ClampFallsBackToOriginForOversizedOrInvalidInput()
    {
        var workArea = new OverlayRect(0, 0, 500, 400);

        Assert.Equal(0, SubtitleOverlayGeometry.Clamp(new OverlayRect(120, 0, 900, 100), workArea).X);
        Assert.Equal(0, SubtitleOverlayGeometry.Clamp(new OverlayRect(double.NaN, 0, 100, 100), workArea).X);
    }
}
