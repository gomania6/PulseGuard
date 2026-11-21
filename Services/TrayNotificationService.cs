using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Wpf.Ui.Tray.Controls;

namespace PulseGuard.Services;

/// <summary>
/// Sends rate-limited Windows balloon notifications from the active WPF-UI system tray NotifyIcon.
/// </summary>
public sealed class TrayNotificationService
{
    private static readonly TimeSpan MinimumBalloonInterval = TimeSpan.FromMinutes(2);
    private static readonly FieldInfo? InternalNotifyIconField = typeof(NotifyIcon)
        .GetField("internalNotifyIcon", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly object _rateLimitLock = new();
    private NotifyIcon? _notifyIcon;
    private DateTime _lastBalloonUtc = DateTime.MinValue;

    public void Attach(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public bool TryShowWarningBalloon(string title, string message) =>
        TryShowBalloon(title, message, NiifWarning);

    public bool TryShowInfoBalloon(string title, string message) =>
        TryShowBalloon(title, message, NiifInfo);

    private bool TryShowBalloon(string title, string message, uint infoFlags)
    {
        lock (_rateLimitLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastBalloonUtc) < MinimumBalloonInterval)
            {
                return false;
            }

            _lastBalloonUtc = now;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return false;
        }

        dispatcher.BeginInvoke(() => ShowBalloonOnUiThread(title, message, infoFlags));
        return true;
    }

    private void ShowBalloonOnUiThread(string title, string message, uint infoFlags)
    {
        try
        {
            if (_notifyIcon is null || !_notifyIcon.IsRegistered)
            {
                return;
            }

            object? internalIcon = InternalNotifyIconField?.GetValue(_notifyIcon);
            if (internalIcon is null)
            {
                return;
            }

            object? shellData = internalIcon.GetType()
                .GetProperty("ShellIconData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(internalIcon);

            if (shellData is null)
            {
                return;
            }

            Type shellDataType = shellData.GetType();
            var hWndObj = shellDataType.GetField("hWnd", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shellData);
            var uIdObj = shellDataType.GetField("uID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shellData);

            if (hWndObj is not IntPtr hWnd || hWnd == IntPtr.Zero || uIdObj is not uint uId)
            {
                return;
            }

            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hWnd,
                uID = uId,
                uFlags = NifInfo,
                dwInfoFlags = infoFlags,
                szInfoTitle = Truncate(title, 63),
                szInfo = Truncate(message, 255)
            };

            Shell_NotifyIconW(NimModify, ref data);
        }
        catch
        {
            // Ignore tray notification errors if shell icon is unavailable.
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private const uint NimModify = 0x00000001;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
