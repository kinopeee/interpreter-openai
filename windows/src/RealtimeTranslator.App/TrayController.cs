using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using RealtimeTranslator.Core.Realtime;

namespace RealtimeTranslator.App;

/// <summary>
/// 常駐 tray アイコンとメニュー。macOS 版のメニューバー項目と同じ操作を提供する。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startStopItem;
    private readonly ToolStripMenuItem _editPositionItem;
    private readonly Dictionary<TranslationState, Icon> _icons = new();

    private bool _disposed;

    public TrayController()
    {
        _startStopItem = new ToolStripMenuItem("翻訳を開始", null, (_, _) => StartStopRequested?.Invoke(this, EventArgs.Empty));
        _editPositionItem = new ToolStripMenuItem(
            "字幕位置を編集",
            null,
            (_, _) => EditPositionRequested?.Invoke(this, EventArgs.Empty))
        {
            CheckOnClick = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startStopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Disabled("翻訳方向: 自動（日本語 ↔ 英語）"));
        menu.Items.Add(Disabled("字幕表示: 原文＋翻訳"));
        menu.Items.Add(Disabled("翻訳音声: 字幕のみ"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_editPositionItem);
        menu.Items.Add(new ToolStripMenuItem("設定…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Realtime Translator",
        };
        _notifyIcon.DoubleClick += (_, _) => StartStopRequested?.Invoke(this, EventArgs.Empty);

        UpdateState(TranslationState.Idle);
    }

    public event EventHandler? StartStopRequested;

    public event EventHandler? EditPositionRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public static bool IsRunning(TranslationState state) => state
        is TranslationState.Connecting
        or TranslationState.Listening
        or TranslationState.Reconnecting
        or TranslationState.Closing;

    public void UpdateState(TranslationState state)
    {
        _startStopItem.Text = IsRunning(state) ? "翻訳を停止" : "翻訳を開始";
        _startStopItem.Enabled = state != TranslationState.Closing;
        _notifyIcon.Icon = IconFor(state);
        // NotifyIcon.Text は 63 文字までなので状態名だけを足す。
        _notifyIcon.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Realtime Translator ({state})");
    }

    public void SetEditingPosition(bool isEditing) => _editPositionItem.Checked = isEditing;

    /// <summary>OS 通知でエラーや案内を出す。字幕本文は通知に載せない。</summary>
    public void ShowMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _notifyIcon.ShowBalloonTip(5_000, "Realtime Translator", message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    private Icon IconFor(TranslationState state)
    {
        if (_icons.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var icon = CreateIcon(ColorFor(state));
        _icons[state] = icon;
        return icon;
    }

    private static Color ColorFor(TranslationState state) => state switch
    {
        TranslationState.Listening => Color.FromArgb(0x3C, 0xC4, 0x5B),
        TranslationState.Connecting or TranslationState.Reconnecting or TranslationState.Closing
            => Color.FromArgb(0xF0, 0xA8, 0x30),
        TranslationState.Error => Color.FromArgb(0xE0, 0x45, 0x3A),
        _ => Color.FromArgb(0xB0, 0xB6, 0xBE),
    };

    /// <summary>状態色の丸を描いた 32x32 アイコン。バイナリ資産を持たずに状態を判別できる。</summary>
    private static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var outline = new Pen(Color.FromArgb(0xC0, 0x20, 0x20, 0x20), 2f);
            graphics.FillEllipse(fill, 3, 3, 26, 26);
            graphics.DrawEllipse(outline, 3, 3, 26, 26);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var shared = Icon.FromHandle(handle);
            return (Icon)shared.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
