using System;
using System.Runtime.InteropServices;

namespace RealtimeTranslator.Platform.App;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,

    /// <summary>WM_KEYDOWN の自動リピートで start/stop が連打されるのを防ぐ。</summary>
    NoRepeat = 0x4000,
}

/// <summary>ホットキー登録。ウィンドウを持たない層からも差し替えられるよう抽象化する。</summary>
public interface IGlobalHotkeyRegistrar
{
    bool Register(IntPtr windowHandle, int id, HotkeyModifiers modifiers, uint virtualKey);

    bool Unregister(IntPtr windowHandle, int id);
}

public sealed class Win32HotkeyRegistrar : IGlobalHotkeyRegistrar
{
    public bool Register(IntPtr windowHandle, int id, HotkeyModifiers modifiers, uint virtualKey) =>
        NativeMethods.RegisterHotKey(windowHandle, id, (uint)modifiers, virtualKey);

    public bool Unregister(IntPtr windowHandle, int id) =>
        NativeMethods.UnregisterHotKey(windowHandle, id);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

/// <summary>
/// 録音の開始/停止をトグルするグローバルホットキー。
/// macOS 版の Control+Option+Space に対応する既定は Ctrl+Alt+Space。
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    public const int DefaultHotkeyId = 0xA731;
    public const uint VirtualKeySpace = 0x20;
    public const HotkeyModifiers DefaultModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat;

    /// <summary>ホットキー押下を通知する Windows メッセージ。</summary>
    public const int WmHotkey = 0x0312;

    private readonly IGlobalHotkeyRegistrar _registrar;
    private readonly int _hotkeyId;

    private IntPtr _windowHandle;
    private bool _registered;

    public GlobalHotkeyManager(IGlobalHotkeyRegistrar? registrar = null, int hotkeyId = DefaultHotkeyId)
    {
        _registrar = registrar ?? new Win32HotkeyRegistrar();
        _hotkeyId = hotkeyId;
    }

    public event EventHandler? Pressed;

    public bool IsRegistered => _registered;

    /// <summary>登録できたら true。他アプリが同じ組み合わせを握っている場合は false を返し、例外にはしない。</summary>
    public bool Register(
        IntPtr windowHandle,
        HotkeyModifiers modifiers = DefaultModifiers,
        uint virtualKey = VirtualKeySpace)
    {
        Unregister();

        if (!_registrar.Register(windowHandle, _hotkeyId, modifiers, virtualKey))
        {
            return false;
        }

        _windowHandle = windowHandle;
        _registered = true;
        return true;
    }

    /// <summary>ウィンドウプロシージャから受け取ったメッセージを処理し、扱った場合 true を返す。</summary>
    public bool HandleMessage(int message, IntPtr wParam)
    {
        if (message != WmHotkey || wParam.ToInt64() != _hotkeyId)
        {
            return false;
        }

        Pressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registrar.Unregister(_windowHandle, _hotkeyId);
        _registered = false;
        _windowHandle = IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}
