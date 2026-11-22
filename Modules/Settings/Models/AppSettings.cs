using PulseGuard.Modules.BatteryDevices.Models;
using PulseGuard.Modules.Optimization.Models;

namespace PulseGuard.Modules.Settings.Models;

public sealed class CachedBatteryDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int LastKnownBatteryPercent { get; set; }
    public BatteryDeviceType DeviceType { get; set; }
}

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

public enum NotificationProfile
{
    Standard,
    Silent,
    GamingFocus,
    Strict
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
    public string Language { get; set; } = "ru";
    public bool RunAtStartup { get; set; }
    public bool StartMinimizedToTray { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // Block 2: Performance & monitoring
    public int PollingIntervalMs { get; set; } = DefaultPollingIntervalMs;
    public bool PauseChartsWhenMinimized { get; set; } = true;
    public bool DisableCpuKernelDriver { get; set; }

    // Block 3: Threshold alerts & peak log
    public double CpuGpuTempThresholdC { get; set; } = DefaultCpuGpuTempThresholdC;
    public double RamThresholdPercent { get; set; } = DefaultRamThresholdPercent;
    public int PacketLossAlertThreshold { get; set; } = 10;
    public bool ShowThresholdNotifications { get; set; } = true;
    public NotificationProfile NotificationProfile { get; set; } = NotificationProfile.Standard;

    // Block 3.1: RAM Auto-Clean Scheduler
    public bool IsAutoRamCleanEnabled { get; set; }
    public double AutoRamCleanThresholdPercent { get; set; } = 85.0;
    public int AutoRamCleanCooldownMinutes { get; set; } = 10;
    public int AutoRamCleanIntervalMinutes { get; set; }

    // Block 4: Overlay mini-widget
    public bool IsOverlayEnabled { get; set; }
    public bool IsOverlayClickThrough { get; set; } = true;
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
    public bool OverlayShowInputLayout { get; set; } = true;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    // Block 5: VPN integration
    public string VpnExecutablePath { get; set; } = string.Empty;
    public bool VpnAutoStart { get; set; }
    public bool VpnStartMinimized { get; set; } = true;

    // Block 6: Battery devices pinned to overlay & last known charges
    public List<string> PinnedOverlayDeviceIds { get; set; } = [];
    public List<CachedBatteryDevice> CachedBatteryDevices { get; set; } = [];

    // Block 7: Discord RPC integration
    public string DiscordClientId { get; set; } = string.Empty;
    public string DiscordClientSecret { get; set; } = string.Empty;
    public string DiscordAccessToken { get; set; } = string.Empty;
    public bool IsDiscordOverlayEnabled { get; set; } = true;

    // Block 8: Auto Game Mode & Global Hotkeys
    public bool IsAutoGameModeEnabled { get; set; } = true;
    public bool DetectFullscreenApps { get; set; } = true;
    public List<string> GameProcessNames { get; set; } = CreateDefaultGameProcessNames();
    public bool IsGlobalHotkeysEnabled { get; set; } = true;
    public string ToggleWindowHotkey { get; set; } = "Ctrl + Alt + P";
    public int BreakReminderMinutes { get; set; } = 90;

    // Block 9: Optimization Profiles & Whitelist Suspend
    public OptimizationProfileType ActiveOptimizationProfile { get; set; } = OptimizationProfileType.NormalReset;
    public List<string> FreezeWhitelistApps { get; set; } = [];
    public bool StopSysMainAndWSearchInMaxFps { get; set; }
    public bool DisableHvciInMaxFps { get; set; }

    public static List<string> CreateDefaultGameProcessNames() =>
    [
        "cs2.exe",
        "dota2.exe",
        "valorant.exe",
        "VALORANT-Win64-Shipping.exe",
        "r5apex.exe",
        "FortniteClient-Win64-Shipping.exe",
        "GTA5.exe",
        "rdr2.exe",
        "cyberpunk2077.exe",
        "witcher3.exe",
        "eldenring.exe",
        "Overwatch.exe",
        "TslGame.exe",
        "helldivers2.exe",
        "EscapeFromTarkov.exe",
        "javaw.exe",
        "Tanki.exe"
    ];

    public static AppSettings CreateDefaults() => new();

    public AppSettings Clone() => new()
    {
        Language = Language,
        RunAtStartup = RunAtStartup,
        StartMinimizedToTray = StartMinimizedToTray,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        PollingIntervalMs = PollingIntervalMs,
        PauseChartsWhenMinimized = PauseChartsWhenMinimized,
        DisableCpuKernelDriver = DisableCpuKernelDriver,
        CpuGpuTempThresholdC = CpuGpuTempThresholdC,
        RamThresholdPercent = RamThresholdPercent,
        PacketLossAlertThreshold = PacketLossAlertThreshold,
        ShowThresholdNotifications = ShowThresholdNotifications,
        NotificationProfile = NotificationProfile,
        IsAutoRamCleanEnabled = IsAutoRamCleanEnabled,
        AutoRamCleanThresholdPercent = AutoRamCleanThresholdPercent,
        AutoRamCleanCooldownMinutes = AutoRamCleanCooldownMinutes,
        AutoRamCleanIntervalMinutes = AutoRamCleanIntervalMinutes,
        IsOverlayEnabled = IsOverlayEnabled,
        IsOverlayClickThrough = IsOverlayClickThrough,
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
        OverlayShowInputLayout = OverlayShowInputLayout,
        OverlayLeft = OverlayLeft,
        OverlayTop = OverlayTop,
        VpnExecutablePath = VpnExecutablePath,
        VpnAutoStart = VpnAutoStart,
        VpnStartMinimized = VpnStartMinimized,
        PinnedOverlayDeviceIds = [.. PinnedOverlayDeviceIds],
        CachedBatteryDevices = CachedBatteryDevices
            .Select(c => new CachedBatteryDevice
            {
                DeviceId = c.DeviceId,
                Name = c.Name,
                LastKnownBatteryPercent = c.LastKnownBatteryPercent,
                DeviceType = c.DeviceType
            })
            .ToList(),
        DiscordClientId = DiscordClientId,
        DiscordClientSecret = DiscordClientSecret,
        DiscordAccessToken = DiscordAccessToken,
        IsDiscordOverlayEnabled = IsDiscordOverlayEnabled,
        IsAutoGameModeEnabled = IsAutoGameModeEnabled,
        DetectFullscreenApps = DetectFullscreenApps,
        GameProcessNames = [.. GameProcessNames],
        IsGlobalHotkeysEnabled = IsGlobalHotkeysEnabled,
        ToggleWindowHotkey = ToggleWindowHotkey,
        BreakReminderMinutes = BreakReminderMinutes,
        ActiveOptimizationProfile = ActiveOptimizationProfile,
        FreezeWhitelistApps = [.. FreezeWhitelistApps],
        StopSysMainAndWSearchInMaxFps = StopSysMainAndWSearchInMaxFps,
        DisableHvciInMaxFps = DisableHvciInMaxFps
    };

    public void Normalize()
    {
        Language = Services.LocalizationService.NormalizeLanguageCode(Language);
        PinnedOverlayDeviceIds = (PinnedOverlayDeviceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        CachedBatteryDevices = (CachedBatteryDevices ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.DeviceId))
            .GroupBy(c => c.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new CachedBatteryDevice
                {
                    DeviceId = first.DeviceId,
                    Name = first.Name ?? string.Empty,
                    LastKnownBatteryPercent = Math.Clamp(first.LastKnownBatteryPercent, 0, 100),
                    DeviceType = Enum.IsDefined(first.DeviceType) ? first.DeviceType : BatteryDeviceType.Unknown
                };
            })
            .ToList();
        if (Array.IndexOf(AllowedIntervals, PollingIntervalMs) < 0)
        {
            PollingIntervalMs = DefaultPollingIntervalMs;
        }

        CpuGpuTempThresholdC = Math.Clamp(Math.Round(CpuGpuTempThresholdC), 60.0, 100.0);
        RamThresholdPercent = Math.Clamp(Math.Round(RamThresholdPercent), 70.0, 98.0);
        AutoRamCleanThresholdPercent = Math.Clamp(Math.Round(AutoRamCleanThresholdPercent), 65.0, 95.0);
        AutoRamCleanCooldownMinutes = Math.Clamp(AutoRamCleanCooldownMinutes, 1, 60);
        AutoRamCleanIntervalMinutes = Math.Clamp(AutoRamCleanIntervalMinutes, 0, 240);
        if (!Enum.IsDefined(NotificationProfile))
        {
            NotificationProfile = NotificationProfile.Standard;
        }
        PacketLossAlertThreshold = Math.Clamp(PacketLossAlertThreshold, 1, 99);
        BreakReminderMinutes = Math.Clamp(BreakReminderMinutes, 0, 480);
        if (string.IsNullOrWhiteSpace(ToggleWindowHotkey))
        {
            ToggleWindowHotkey = "Ctrl + Alt + P";
        }
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

        DiscordClientId = (DiscordClientId ?? string.Empty).Trim();
        DiscordClientSecret = (DiscordClientSecret ?? string.Empty).Trim();
        DiscordAccessToken = (DiscordAccessToken ?? string.Empty).Trim();

        GameProcessNames = (GameProcessNames ?? CreateDefaultGameProcessNames())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!Enum.IsDefined(ActiveOptimizationProfile))
        {
            ActiveOptimizationProfile = OptimizationProfileType.NormalReset;
        }

        FreezeWhitelistApps = (FreezeWhitelistApps ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

