using System;

namespace RealtimeTranslator.Core.Subtitles;

/// <summary>WPF 非依存の矩形。左上原点・Y 下方向 (Win32 / WPF 座標系)。</summary>
public readonly record struct OverlayRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>字幕ウィンドウの寸法と作業領域クランプ。DPI/多画面計算をここに閉じ込めてテストする。</summary>
public static class SubtitleOverlayGeometry
{
    public const double MaximumWidth = 1_200;
    public const double WidthRatio = 0.70;
    public const double BottomOffset = 24;

    public static double Width(OverlayRect workArea) =>
        Math.Min(Math.Max(0, workArea.Width * WidthRatio), MaximumWidth);

    /// <summary>作業領域の下端中央に置く既定位置。</summary>
    public static OverlayRect DefaultPlacement(OverlayRect workArea, double height)
    {
        var width = Width(workArea);
        return Clamp(
            new OverlayRect(
                workArea.X + ((workArea.Width - width) / 2),
                workArea.Bottom - height - BottomOffset,
                width,
                height),
            workArea);
    }

    /// <summary>作業領域からはみ出さない位置へ寄せる。領域より大きい場合は左上に合わせる。</summary>
    public static OverlayRect Clamp(OverlayRect desired, OverlayRect workArea) =>
        desired with
        {
            X = ClampAxis(desired.X, desired.Width, workArea.X, workArea.Right),
            Y = ClampAxis(desired.Y, desired.Height, workArea.Y, workArea.Bottom),
        };

    private static double ClampAxis(double origin, double extent, double lowerBound, double upperBound)
    {
        if (!double.IsFinite(origin) || extent >= upperBound - lowerBound)
        {
            return lowerBound;
        }

        if (origin < lowerBound)
        {
            return lowerBound;
        }

        return origin + extent > upperBound ? upperBound - extent : origin;
    }
}
