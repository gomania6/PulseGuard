using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Monitoring.Models;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;
using PulseGuard.Modules.Vpn.Models;

namespace PulseGuard.Modules.Overlay;

/// <summary>
/// ViewModel for the always-on-top compact overlay mini-widget.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IDisposable
{
    private static readonly Brush VpnDisconnectedBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);
    private static readonly Brush VpnConnectedBrush = CreateFrozenBrush(0x00, 0xE5, 0xA0);
    private static readonly Brush VpnErrorBrush = CreateFrozenBrush(0xFF, 0x5C, 0x5C);

    private readonly MonitoringPollingService _pollingService;
    private readonly SystemMonitorModule _monitorModule;
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly SettingsService _settingsService;
    private readonly VpnService _vpnService;

    private volatile bool _isActive;
    private double _lastOpacityPercent = -1;

    [ObservableProperty]
    private string _cpuText = "0%";

    [ObservableProperty]
    private string _gpuText = "Н/Д";

    [ObservableProperty]
    private string _ramText = "0%";

    [ObservableProperty]
    private string _networkText = "↓ 0.0 Мбит/с  ↑ 0.0 Мбит/с";

    [ObservableProperty]
    private string _vpnText = "VPN Выкл";

    [ObservableProperty]
    private Brush _vpnDotBrush = VpnDisconnectedBrush;

    [ObservableProperty]
    private bool _showVpn = true;

    [ObservableProperty]
    private bool _isVpnBusy;

    [ObservableProperty]
    private Brush _panelBackground = CreateBackgroundBrush(AppSettings.DefaultOverlayOpacityPercent);

    [ObservableProperty]
    private bool _isRamCleaning;

    [ObservableProperty]
    private bool _showCpu = true;

    [ObservableProperty]
    private bool _showGpu = true;

    [ObservableProperty]
    private bool _showRam = true;

    [ObservableProperty]
    private bool _showNetwork = true;

    [ObservableProperty]
    private bool _showNoMetricsFallback;

    [ObservableProperty]
    private bool _showIcons = true;

    [ObservableProperty]
    private bool _showHoverButtons = true;

    [ObservableProperty]
    private bool _isPositionLocked = true;

    [ObservableProperty]
    private Orientation _layoutOrientation = Orientation.Horizontal;

    [ObservableProperty]
    private Thickness _itemMargin = new(0, 0, 14, 0);

    [ObservableProperty]
    private Thickness _hoverButtonsMargin = new(10, 0, 0, 0);

    public event Action? CloseRequested;

    public OverlayViewModel(
        MonitoringPollingService pollingService,
        SystemMonitorModule monitorModule,
        SafeRamCleanerTool ramCleanerTool,
        SettingsService settingsService,
        VpnService vpnService)
    {
        _pollingService = pollingService;
        _monitorModule = monitorModule;
        _ramCleanerTool = ramCleanerTool;
        _settingsService = settingsService;
        _vpnService = vpnService;

        ApplySettings(_settingsService.Current);
        ApplyVpnStatus(_vpnService.CurrentStatus);

        _pollingService.MetricsUpdated += OnMetricsUpdated;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _vpnService.StatusChanged += OnVpnStatusChanged;
    }

    public void SetActive(bool isActive)
    {
        _isActive = isActive;
        if (isActive)
        {
            ApplySettings(_settingsService.Current);
            ApplySnapshot(_monitorModule.LatestSnapshot);
            ApplyVpnStatus(_vpnService.CurrentStatus);
        }
    }

    public void SavePosition(double left, double top)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
        {
            return;
        }

        _settingsService.Update(s =>
        {
            s.OverlayPosition = OverlayCorner.Custom;
            s.OverlayLeft = Math.Round(left);
            s.OverlayTop = Math.Round(top);
        });
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            ApplySettings(settings);
            if (_isActive)
            {
                ApplySnapshot(_monitorModule.LatestSnapshot);
            }
        });
    }

    private void ApplySettings(AppSettings settings)
    {
        UpdateBackground(settings.OverlayOpacityPercent);

        ShowCpu = settings.OverlayShowCpu;
        ShowGpu = settings.OverlayShowGpu;
        ShowRam = settings.OverlayShowRam;
        ShowNetwork = settings.OverlayShowNetwork;
        ShowVpn = settings.OverlayShowVpn;
        ShowNoMetricsFallback = !ShowCpu && !ShowGpu && !ShowRam && !ShowNetwork && !ShowVpn;

        ShowIcons = settings.OverlayShowIcons;
        ShowHoverButtons = settings.OverlayShowHoverButtons;
        IsPositionLocked = settings.OverlayLockPosition;

        bool isVertical = settings.OverlayLayout == OverlayLayoutMode.Vertical;
        LayoutOrientation = isVertical ? Orientation.Vertical : Orientation.Horizontal;
        ItemMargin = isVertical ? new Thickness(0, 2, 0, 2) : new Thickness(0, 0, 14, 0);
        HoverButtonsMargin = isVertical ? new Thickness(0, 6, 0, 0) : new Thickness(10, 0, 0, 0);
    }

    private void OnMetricsUpdated(SystemMetricsSnapshot snapshot)
    {
        if (!_isActive)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(SystemMetricsSnapshot s)
    {
        bool showTemp = _settingsService.Current.OverlayShowTemperature;

        if (ShowCpu)
        {
            double cpuLoad = Math.Clamp(s.CpuLoadPercent, 0, 100);
            CpuText = showTemp && s.CpuTemperatureC.HasValue
                ? $"{cpuLoad:0}% · {s.CpuTemperatureC.Value:0}°C"
                : $"{cpuLoad:0}%";
        }

        if (ShowGpu)
        {
            if (s.GpuLoadPercent.HasValue)
            {
                double gpuLoad = Math.Clamp(s.GpuLoadPercent.Value, 0, 100);
                GpuText = showTemp && s.GpuTemperatureC.HasValue
                    ? $"{gpuLoad:0}% · {s.GpuTemperatureC.Value:0}°C"
                    : $"{gpuLoad:0}%";
            }
            else if (showTemp && s.GpuTemperatureC.HasValue)
            {
                GpuText = $"{s.GpuTemperatureC.Value:0}°C";
            }
            else
            {
                GpuText = "Н/Д";
            }
        }

        if (ShowRam)
        {
            double ramLoad = Math.Clamp(s.RamLoadPercent, 0, 100);
            RamText = $"{ramLoad:0}%";
        }

        if (ShowNetwork)
        {
            double downMbps = (s.DownloadBytesPerSec * 8.0) / 1_000_000.0;
            double upMbps = (s.UploadBytesPerSec * 8.0) / 1_000_000.0;
            NetworkText = $"↓ {downMbps:0.0} Мбит/с  ↑ {upMbps:0.0} Мбит/с";
        }
    }

    private void UpdateBackground(double opacityPercent)
    {
        double clamped = Math.Clamp(opacityPercent, 40.0, 95.0);
        if (Math.Abs(clamped - _lastOpacityPercent) < 0.5)
        {
            return;
        }

        _lastOpacityPercent = clamped;
        PanelBackground = CreateBackgroundBrush(clamped);
    }

    private static Brush CreateBackgroundBrush(double opacityPercent)
    {
        byte alpha = (byte)Math.Clamp(Math.Round((opacityPercent / 100.0) * 255.0), 102, 243);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x1A, 0x1D, 0x24));
        brush.Freeze();
        return brush;
    }

    private void OnVpnStatusChanged(VpnStatusSnapshot status)
    {
        if (!_isActive)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyVpnStatus(status));
    }

    private void ApplyVpnStatus(VpnStatusSnapshot status)
    {
        switch (status.State)
        {
            case VpnConnectionState.Connected:
                VpnDotBrush = VpnConnectedBrush;
                string label = string.IsNullOrWhiteSpace(status.CountryCode) ? "VPN" : status.CountryCode;
                VpnText = status.PingMs.HasValue
                    ? $"{label} {status.PingMs.Value} мс"
                    : label;
                break;

            case VpnConnectionState.Error:
                VpnDotBrush = VpnErrorBrush;
                VpnText = "VPN Сбой";
                break;

            default:
                VpnDotBrush = VpnDisconnectedBrush;
                VpnText = "VPN Выкл";
                break;
        }
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    [RelayCommand]
    private async Task ToggleVpnAsync()
    {
        if (IsVpnBusy)
        {
            return;
        }

        IsVpnBusy = true;
        try
        {
            await _vpnService.ToggleAsync();
        }
        finally
        {
            IsVpnBusy = false;
        }
    }

    [RelayCommand]
    private async Task QuickRamCleanAsync()
    {
        if (IsRamCleaning)
        {
            return;
        }

        IsRamCleaning = true;
        try
        {
            await _ramCleanerTool.ExecuteAsync();
        }
        finally
        {
            IsRamCleaning = false;
        }
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        CloseRequested?.Invoke();
    }

    public void Dispose()
    {
        _pollingService.MetricsUpdated -= OnMetricsUpdated;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _vpnService.StatusChanged -= OnVpnStatusChanged;
    }
}
