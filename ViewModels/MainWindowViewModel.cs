using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Helpers;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Overlay;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;

namespace PulseGuard.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly MonitoringPollingService _pollingService;
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly SettingsService _settingsService;
    private readonly OverlayService _overlayService;
    private readonly VpnService _vpnService;

    [ObservableProperty]
    private string _applicationTitle = "PulseGuard";

    [ObservableProperty]
    private string _statusMessage = "Система работает стабильно";

    [ObservableProperty]
    private bool _isMinimizedToTray;

    [ObservableProperty]
    private bool _isOverlayVisible;

    public ImageSource TrayIconSource { get; } = TrayIconHelper.CreatePulseIcon();

    public bool IsExplicitExitRequested { get; private set; }

    public bool ShouldMinimizeToTrayOnClose => _settingsService.Current.MinimizeToTrayOnClose;

    public MainWindowViewModel(
        MonitoringPollingService pollingService,
        SafeRamCleanerTool ramCleanerTool,
        SettingsService settingsService,
        OverlayService overlayService,
        VpnService vpnService)
    {
        _pollingService = pollingService;
        _ramCleanerTool = ramCleanerTool;
        _settingsService = settingsService;
        _overlayService = overlayService;
        _vpnService = vpnService;

        _isOverlayVisible = _overlayService.IsVisible;
        _overlayService.VisibilityChanged += OnOverlayVisibilityChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    partial void OnIsMinimizedToTrayChanged(bool value)
    {
        UpdateMonitoringPauseState();

        if (value)
        {
            GC.Collect(2, GCCollectionMode.Optimized);
        }
    }

    private void OnOverlayVisibilityChanged(bool isVisible)
    {
        IsOverlayVisible = isVisible;
        UpdateMonitoringPauseState();
    }

    private void OnSettingsChanged(AppSettings _)
    {
        UpdateMonitoringPauseState();
    }

    private void UpdateMonitoringPauseState()
    {
        bool shouldPauseMonitoring = IsMinimizedToTray
            && _settingsService.Current.PauseChartsWhenMinimized
            && !_overlayService.IsVisible;

        _pollingService.SetPaused(shouldPauseMonitoring);

        StatusMessage = IsMinimizedToTray
            ? shouldPauseMonitoring
                ? "Свёрнуто в трей (мониторинг на паузе)"
                : "Свёрнуто в трей (мониторинг активен)"
            : "Система работает стабильно";
    }

    [RelayCommand]
    private void ToggleOverlay()
    {
        _overlayService.Toggle();
    }

    [RelayCommand]
    private void ShowWindow()
    {
        var window = Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        IsMinimizedToTray = false;
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }

    [RelayCommand]
    private void MinimizeToTray()
    {
        var window = Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        IsMinimizedToTray = true;
        window.Hide();
    }

    [RelayCommand]
    private async Task QuickRamCleanAsync()
    {
        StatusMessage = "Выполняется быстрая очистка ОЗУ...";
        var result = await _ramCleanerTool.ExecuteAsync();
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private async Task ToggleVpnAsync()
    {
        var (_, message) = await _vpnService.ToggleAsync();
        StatusMessage = message;
    }

    [RelayCommand]
    private void ExitApplication()
    {
        IsExplicitExitRequested = true;
        _overlayService.VisibilityChanged -= OnOverlayVisibilityChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _overlayService.Dispose();
        _vpnService.Dispose();
        _pollingService.Dispose();
        Application.Current.Shutdown();
    }
}
