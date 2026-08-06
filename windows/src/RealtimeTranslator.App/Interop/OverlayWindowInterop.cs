using System;
using System.Runtime.InteropServices;

namespace RealtimeTranslator.App.Interop;

/// <summary>字幕オーバーレイのクリックスルー制御に必要な最小限の Win32 呼び出し。</summary>
internal static class OverlayWindowInterop
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x0000_0020;
    private const int WsExToolWindow = 0x0000_0080;
    private const int WsExNoActivate = 0x0800_0000;

    /// <summary>タスク切替に出さず、フォーカスも奪わないオーバーレイにする。</summary>
    internal static void ApplyOverlayStyles(IntPtr handle, bool clickThrough)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = (int)NativeMethods.GetWindowLongPtr(handle, GwlExStyle);
        style |= WsExToolWindow | WsExNoActivate;
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        _ = NativeMethods.SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private static class NativeMethods
    {
        internal static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

        internal static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(handle, index, value)
                : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
