using System.Runtime.InteropServices;

namespace PulseGuard.Modules.Overlay;

/// <summary>
/// Win32 P/Invoke declarations for click-through extended window styles and Alt-key polling.
/// </summary>
internal static class OverlayNativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;

    internal const int VkLMenu = 0xA4;
    internal const int VkRMenu = 0xA5;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    internal static bool IsAltKeyPressed() =>
        (GetAsyncKeyState(VkLMenu) & 0x8000) != 0
        || (GetAsyncKeyState(VkRMenu) & 0x8000) != 0;
}

