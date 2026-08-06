using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RealtimeTranslator.App.Interop;
using RealtimeTranslator.Core.Subtitles;
using FormsScreen = System.Windows.Forms.Screen;

namespace RealtimeTranslator.App;

/// <summary>常時最前面のクリックスルー字幕ウィンドウ。位置編集中のみドラッグを受け付ける。</summary>
public partial class SubtitleOverlayWindow : Window
{
    private static readonly SolidColorBrush EditingBorderBrush = CreateEditingBrush();
    private static readonly SolidColorBrush EditingBackgroundBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0, 0, 0));

    private readonly SubtitleOverlayViewModel _viewModel;

    public SubtitleOverlayWindow(SubtitleOverlayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public bool IsEditingPosition { get; private set; }

    /// <summary>WM_HOTKEY を拾うためのハンドル。オーバーレイは常駐なので hotkey の受け皿を兼ねる。</summary>
    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public void SetEditingPosition(bool isEditing)
    {
        IsEditingPosition = isEditing;
        _viewModel.IsEditingPosition = isEditing;
        EditingFrame.BorderBrush = isEditing ? EditingBorderBrush : Brushes.Transparent;
        EditingFrame.Background = isEditing ? EditingBackgroundBrush : Brushes.Transparent;
        OverlayWindowInterop.ApplyOverlayStyles(Handle, clickThrough: !isEditing);
    }

    /// <summary>保存済み位置があればそこへ、無ければ作業領域の下端中央へ配置する。</summary>
    public void ApplyPlacement(bool hasCustomOrigin, double originX, double originY)
    {
        var workArea = CurrentWorkArea();
        Width = SubtitleOverlayGeometry.Width(workArea);
        UpdateLayout();
        var height = ActualHeight > 0 ? ActualHeight : Math.Min(160, workArea.Height);

        var placement = hasCustomOrigin
            ? SubtitleOverlayGeometry.Clamp(
                new OverlayRect(originX, originY, Width, height),
                workArea)
            : SubtitleOverlayGeometry.DefaultPlacement(workArea, height);

        Left = placement.X;
        Top = placement.Y;
    }

    /// <summary>作業領域内へ収め直す。文字量でウィンドウ高さが変わるたびに呼ぶ。</summary>
    public void ClampIntoWorkArea()
    {
        var workArea = CurrentWorkArea();
        var clamped = SubtitleOverlayGeometry.Clamp(
            new OverlayRect(Left, Top, ActualWidth, ActualHeight),
            workArea);
        Left = clamped.X;
        Top = clamped.Y;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayWindowInterop.ApplyOverlayStyles(Handle, clickThrough: !IsEditingPosition);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsEditingPosition)
        {
            DragMove();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClampIntoWorkArea();
    }

    /// <summary>
    /// オーバーレイが載っているモニターの作業領域を DIP で返す。
    /// プライマリ固定の <see cref="SystemParameters.WorkArea"/> だと副モニター配置が毎回吸い寄せられる。
    /// </summary>
    private OverlayRect CurrentWorkArea()
    {
        var handle = Handle;
        var workingArea = FormsScreen.FromHandle(handle).WorkingArea;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var topLeft = target.TransformFromDevice.Transform(new Point(workingArea.Left, workingArea.Top));
            var bottomRight = target.TransformFromDevice.Transform(new Point(workingArea.Right, workingArea.Bottom));
            var width = bottomRight.X - topLeft.X;
            var height = bottomRight.Y - topLeft.Y;
            if (width > 0 && height > 0)
            {
                return new OverlayRect(topLeft.X, topLeft.Y, width, height);
            }
        }

        // Source 未初期化時のフォールバック (起動直後など)。
        var workArea = SystemParameters.WorkArea;
        return workArea.Width <= 0 || workArea.Height <= 0
            ? new OverlayRect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)
            : new OverlayRect(workArea.X, workArea.Y, workArea.Width, workArea.Height);
    }

    private static SolidColorBrush CreateEditingBrush() =>
        CreateFrozenBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
