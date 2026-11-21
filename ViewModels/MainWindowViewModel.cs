using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Helpers;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Network;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Overlay;
using PulseGuard.Modules.Playtime;
using PulseGuard.Modules.Processes;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;
using PulseGuard.Services;

namespace PulseGuard.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly MonitoringPollingService _pollingService;
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly SettingsService _settingsService;
    private readonly OverlayService _overlayService;
    private readonly VpnService _vpnService;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly NetworkRadarModule _networkRadarModule;
    private readonly ProcessMonitorModule _processMonitorModule;
    private readonly StutterMonitorModule _stutterMonitorModule;
    private readonly PlaytimeTrackerModule _playtimeTrackerModule;

    [ObservableProperty]
    private string _applicationTitle = "PulseGuard";

    [ObservableProperty]
    private string _statusMessage = "Система работает стабильно";

    [ObservableProperty]
    private bool _isMinimizedToTray;

    [ObservableProperty]
    private bool _isOverlayVisible;

    [ObservableProperty]
    private bool _isOverlayClickThrough;

    public ImageSource TrayIconSource { get; } = TrayIconHelper.CreatePulseIcon();

    public bool IsExplicitExitRequested { get; private set; }

    public bool ShouldMinimizeToTrayOnClose => _settingsService.Current.MinimizeToTrayOnClose;

    public MainWindowViewModel(
        MonitoringPollingService pollingService,
        SafeRamCleanerTool ramCleanerTool,
        SettingsService settingsService,
        OverlayService overlayService,
        VpnService vpnService,
        TrayNotificationService trayNotificationService,
        NetworkRadarModule networkRadarModule,
        ProcessMonitorModule processMonitorModule,
        StutterMonitorModule stutterMonitorModule,
        PlaytimeTrackerModule playtimeTrackerModule)
    {
        _pollingService = pollingService;
        _ramCleanerTool = ramCleanerTool;
        _settingsService = settingsService;
        _overlayService = overlayService;
        _vpnService = vpnService;
        _trayNotificationService = trayNotificationService;
        _networkRadarModule = networkRadarModule;
        _processMonitorModule = processMonitorModule;
        _stutterMonitorModule = stutterMonitorModule;
        _playtimeTrackerModule = playtimeTrackerModule;

        _isOverlayVisible = _overlayService.IsVisible;
        _isOverlayClickThrough = _settingsService.Current.IsOverlayClickThrough;
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

    private void OnSettingsChanged(AppSettings settings)
    {
        IsOverlayClickThrough = settings.IsOverlayClickThrough;
        UpdateMonitoringPauseState();
    }

    private void UpdateMonitoringPauseState()
    {
        bool shouldPauseMonitoring = IsMinimizedToTray
            && _settingsService.Current.PauseChartsWhenMinimized
            && !_overlayService.IsVisible;

        _pollingService.SetPaused(shouldPauseMonitoring);
        _networkRadarModule.SetPaused(shouldPauseMonitoring);
        _stutterMonitorModule.SetPaused(shouldPauseMonitoring);

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
    private void ToggleOverlayClickThrough()
    {
        _settingsService.Update(s => s.IsOverlayClickThrough = !s.IsOverlayClickThrough);
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
        window.ShowInTaskbar = true;
        window.ShowActivated = true;
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.WindowState = WindowState.Normal;
        window.Activate();
        window.Focus();
    }

    [RelayCommand]
    private void ToggleWindow()
    {
        var window = Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        if (window.IsVisible && window.WindowState != WindowState.Minimized && window.IsActive)
        {
            MinimizeToTray();
            return;
        }

        ShowWindow();

        // Briefly toggle Topmost so global hotkeys reliably bring the window above other apps.
        bool wasTopmost = window.Topmost;
        if (!wasTopmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }
    }

    [RelayCommand]
    private void MinimizeToTray()
    {
        var window = Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        _playtimeTrackerModule.FlushSave();
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
        var (success, message) = await _vpnService.ToggleAsync();
        StatusMessage = message;
        if (!success)
        {
            _trayNotificationService.TryShowWarningBalloon("PulseGuard — VPN", message);
        }
    }

    [RelayCommand]
    private void ExitApplication()
    {
        IsExplicitExitRequested = true;
        _overlayService.VisibilityChanged -= OnOverlayVisibilityChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _processMonitorModule.ResumeAllSuspendedProcesses();
        _playtimeTrackerModule.FlushSave();
        _networkRadarModule.Dispose();
        _overlayService.Dispose();
        _vpnService.Dispose();
        _pollingService.Dispose();
        Application.Current.Shutdown();
    }
}
