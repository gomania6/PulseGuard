namespace PulseGuard.Modules.Settings.Models;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom
}

public enum OverlayLayoutMode
{
    Horizontal,
    Vertical
}

/// <summary>
/// Persisted application settings for PulseGuard.
/// </summary>
public sealed class AppSettings
{
    public const int DefaultPollingIntervalMs = 1000;
    public const double DefaultCpuGpuTempThresholdC = 85.0;
    public const double DefaultRamThresholdPercent = 90.0;
    public const double DefaultOverlayOpacityPercent = 80.0;

    private static readonly int[] AllowedIntervals = [500, 1000, 2000, 5000];

    // Block 1: Startup & window behavior
    public bool RunAtStartup { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // Block 2: Performance & monitoring
    public int PollingIntervalMs { get; set; } = DefaultPollingIntervalMs;
    public bool PauseChartsWhenMinimized { get; set; } = true;

    // Block 3: Threshold alerts & peak log
    public double CpuGpuTempThresholdC { get; set; } = DefaultCpuGpuTempThresholdC;
    public double RamThresholdPercent { get; set; } = DefaultRamThresholdPercent;
    public bool ShowThresholdNotifications { get; set; } = true;

    // Block 4: Overlay mini-widget
    public bool IsOverlayEnabled { get; set; }
    public double OverlayOpacityPercent { get; set; } = DefaultOverlayOpacityPercent;
    public OverlayCorner OverlayPosition { get; set; } = OverlayCorner.TopRight;
    public OverlayLayoutMode OverlayLayout { get; set; } = OverlayLayoutMode.Horizontal;
    public bool OverlayLockPosition { get; set; } = true;
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowRam { get; set; } = true;
    public bool OverlayShowNetwork { get; set; } = true;
    public bool OverlayShowTemperature { get; set; } = true;
    public bool OverlayShowIcons { get; set; } = true;
    public bool OverlayShowHoverButtons { get; set; } = true;
    public bool OverlayShowVpn { get; set; } = true;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    // Block 5: VPN integration
    public string VpnExecutablePath { get; set; } = string.Empty;
    public bool VpnAutoStart { get; set; }
    public bool VpnStartMinimized { get; set; } = true;

    public static AppSettings CreateDefaults() => new();

    public AppSettings Clone() => new()
    {
        RunAtStartup = RunAtStartup,
        StartMinimizedToTray = StartMinimizedToTray,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        PollingIntervalMs = PollingIntervalMs,
        PauseChartsWhenMinimized = PauseChartsWhenMinimized,
        CpuGpuTempThresholdC = CpuGpuTempThresholdC,
        RamThresholdPercent = RamThresholdPercent,
        ShowThresholdNotifications = ShowThresholdNotifications,
        IsOverlayEnabled = IsOverlayEnabled,
        OverlayOpacityPercent = OverlayOpacityPercent,
        OverlayPosition = OverlayPosition,
        OverlayLayout = OverlayLayout,
        OverlayLockPosition = OverlayLockPosition,
        OverlayShowCpu = OverlayShowCpu,
        OverlayShowGpu = OverlayShowGpu,
        OverlayShowRam = OverlayShowRam,
        OverlayShowNetwork = OverlayShowNetwork,
        OverlayShowTemperature = OverlayShowTemperature,
        OverlayShowIcons = OverlayShowIcons,
        OverlayShowHoverButtons = OverlayShowHoverButtons,
        OverlayShowVpn = OverlayShowVpn,
        OverlayLeft = OverlayLeft,
        OverlayTop = OverlayTop,
        VpnExecutablePath = VpnExecutablePath,
        VpnAutoStart = VpnAutoStart,
        VpnStartMinimized = VpnStartMinimized
    };

    public void Normalize()
    {
        if (Array.IndexOf(AllowedIntervals, PollingIntervalMs) < 0)
        {
            PollingIntervalMs = DefaultPollingIntervalMs;
        }

        CpuGpuTempThresholdC = Math.Clamp(Math.Round(CpuGpuTempThresholdC), 60.0, 100.0);
        RamThresholdPercent = Math.Clamp(Math.Round(RamThresholdPercent), 70.0, 98.0);
        OverlayOpacityPercent = Math.Clamp(Math.Round(OverlayOpacityPercent), 40.0, 95.0);

        if (!Enum.IsDefined(OverlayPosition))
        {
            OverlayPosition = OverlayCorner.TopRight;
        }

        if (!Enum.IsDefined(OverlayLayout))
        {
            OverlayLayout = OverlayLayoutMode.Horizontal;
        }

        if (OverlayLeft is double left && (double.IsNaN(left) || double.IsInfinity(left)))
        {
            OverlayLeft = null;
        }

        if (OverlayTop is double top && (double.IsNaN(top) || double.IsInfinity(top)))
        {
            OverlayTop = null;
        }
    }
}

