using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseGuard.Modules.Network;

/// <summary>
/// Extracts and caches small frozen WPF ImageSource icons for executables via Win32 SHGetFileInfoW.
/// </summary>
internal static class ProcessIconResolver
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? GetSmallIcon(string processName, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        if (Cache.TryGetValue(processName, out var cached))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        ImageSource? icon = null;
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var result = SHGetFileInfoW(
                executablePath,
                0,
                out var shfi,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON);

            hIcon = shfi.hIcon;
            if (result != IntPtr.Zero && hIcon != IntPtr.Zero)
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(16, 16));
                bitmap.Freeze();
                icon = bitmap;
            }
        }
        catch
        {
            icon = null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }

        Cache.TryAdd(processName, icon);
        return icon;
    }
}

