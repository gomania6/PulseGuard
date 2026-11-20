using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Overlay;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.ViewModels;

public sealed record PollingIntervalOption(int IntervalMs, string DisplayText);
public sealed record OverlayCornerOption(OverlayCorner Corner, string DisplayText);
public sealed record OverlayLayoutOption(OverlayLayoutMode Mode, string DisplayText);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly StartupTaskSchedulerService _startupTaskScheduler;
    private readonly MonitoringPollingService _monitoringPollingService;
    private readonly OverlayService _overlayService;

    private bool _isSyncing;

    [ObservableProperty]
    private string _title = "Настройки";

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _startMinimizedToTray;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = true;

    [ObservableProperty]
    private PollingIntervalOption _selectedPollingOption;

    [ObservableProperty]
    private bool _pauseChartsWhenMinimized = true;

    [ObservableProperty]
    private double _cpuGpuTempThresholdC = AppSettings.DefaultCpuGpuTempThresholdC;

    [ObservableProperty]
    private double _ramThresholdPercent = AppSettings.DefaultRamThresholdPercent;

    [ObservableProperty]
    private bool _showThresholdNotifications = true;

    [ObservableProperty]
    private bool _isOverlayEnabled;

    [ObservableProperty]
    private double _overlayOpacityPercent = AppSettings.DefaultOverlayOpacityPercent;

    [ObservableProperty]
    private OverlayCornerOption _selectedOverlayCorner;

    [ObservableProperty]
    private OverlayLayoutOption _selectedOverlayLayout;

    [ObservableProperty]
    private bool _overlayLockPosition = true;

    [ObservableProperty]
    private bool _overlayShowCpu = true;

    [ObservableProperty]
    private bool _overlayShowGpu = true;

    [ObservableProperty]
    private bool _overlayShowRam = true;

    [ObservableProperty]
    private bool _overlayShowNetwork = true;

    [ObservableProperty]
    private bool _overlayShowTemperature = true;

    [ObservableProperty]
    private bool _overlayShowIcons = true;

    [ObservableProperty]
    private bool _overlayShowHoverButtons = true;

    [ObservableProperty]
    private bool _overlayShowVpn = true;

    [ObservableProperty]
    private string _vpnExecutablePath = string.Empty;

    [ObservableProperty]
    private bool _vpnAutoStart;

    [ObservableProperty]
    private bool _vpnStartMinimized = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public IReadOnlyList<PollingIntervalOption> PollingIntervalOptions { get; } =
    [
        new(500, "500 мс (плавно)"),
        new(1000, "1000 мс (рекомендуется)"),
        new(2000, "2000 мс (экономно)"),
        new(5000, "5000 мс (минимум нагрузки)")
    ];

    public IReadOnlyList<OverlayCornerOption> OverlayCornerOptions { get; } =
    [
        new(OverlayCorner.TopLeft, "Верхний левый угол"),
        new(OverlayCorner.TopRight, "Верхний правый угол"),
        new(OverlayCorner.BottomLeft, "Нижний левый угол"),
        new(OverlayCorner.BottomRight, "Нижний правый угол"),
        new(OverlayCorner.Custom, "Вручную (перетаскиванием)")
    ];

    public IReadOnlyList<OverlayLayoutOption> OverlayLayoutOptions { get; } =
    [
        new(OverlayLayoutMode.Horizontal, "В одну строку (горизонтально)"),
        new(OverlayLayoutMode.Vertical, "В столбик (построчно)")
    ];

    public string SettingsFilePath => _settingsService.SettingsFilePath;

    public SettingsViewModel(
        SettingsService settingsService,
        StartupTaskSchedulerService startupTaskScheduler,
        MonitoringPollingService monitoringPollingService,
        OverlayService overlayService)
    {
        _settingsService = settingsService;
        _startupTaskScheduler = startupTaskScheduler;
        _monitoringPollingService = monitoringPollingService;
        _overlayService = overlayService;

        _selectedPollingOption = PollingIntervalOptions[1];
        _selectedOverlayCorner = OverlayCornerOptions[1];
        _selectedOverlayLayout = OverlayLayoutOptions[0];
        LoadFromSettings(_settingsService.Current);

        _overlayService.VisibilityChanged += OnOverlayVisibilityChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_isSyncing)
        {
            return;
        }

        _isSyncing = true;
        try
        {
            SelectedOverlayCorner = OverlayCornerOptions.FirstOrDefault(o => o.Corner == settings.OverlayPosition)
                                    ?? OverlayCornerOptions[1];
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void OnOverlayVisibilityChanged(bool isVisible)
    {
        if (IsOverlayEnabled == isVisible)
        {
            return;
        }

        _isSyncing = true;
        try
        {
            IsOverlayEnabled = isVisible;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void LoadFromSettings(AppSettings settings)
    {
        _isSyncing = true;
        try
        {
            RunAtStartup = settings.RunAtStartup;
            StartMinimizedToTray = settings.StartMinimizedToTray;
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
            SelectedPollingOption = PollingIntervalOptions.FirstOrDefault(o => o.IntervalMs == settings.PollingIntervalMs)
                                    ?? PollingIntervalOptions[1];
            PauseChartsWhenMinimized = settings.PauseChartsWhenMinimized;
            CpuGpuTempThresholdC = settings.CpuGpuTempThresholdC;
            RamThresholdPercent = settings.RamThresholdPercent;
            ShowThresholdNotifications = settings.ShowThresholdNotifications;
            IsOverlayEnabled = _overlayService.IsVisible;
            OverlayOpacityPercent = settings.OverlayOpacityPercent;
            SelectedOverlayCorner = OverlayCornerOptions.FirstOrDefault(o => o.Corner == settings.OverlayPosition)
                                    ?? OverlayCornerOptions[1];
            SelectedOverlayLayout = OverlayLayoutOptions.FirstOrDefault(o => o.Mode == settings.OverlayLayout)
                                    ?? OverlayLayoutOptions[0];
            OverlayLockPosition = settings.OverlayLockPosition;
            OverlayShowCpu = settings.OverlayShowCpu;
            OverlayShowGpu = settings.OverlayShowGpu;
            OverlayShowRam = settings.OverlayShowRam;
            OverlayShowNetwork = settings.OverlayShowNetwork;
            OverlayShowTemperature = settings.OverlayShowTemperature;
            OverlayShowIcons = settings.OverlayShowIcons;
            OverlayShowHoverButtons = settings.OverlayShowHoverButtons;
            OverlayShowVpn = settings.OverlayShowVpn;
            VpnExecutablePath = settings.VpnExecutablePath;
            VpnAutoStart = settings.VpnAutoStart;
            VpnStartMinimized = settings.VpnStartMinimized;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _ = ApplyRunAtStartupAsync(value);
    }

    private async Task ApplyRunAtStartupAsync(bool enabled)
    {
        var (success, message) = await _startupTaskScheduler.SetStartupEnabledAsync(enabled);
        StatusMessage = message;

        if (!success)
        {
            _isSyncing = true;
            try
            {
                RunAtStartup = !enabled;
            }
            finally
            {
                _isSyncing = false;
            }

            return;
        }

        _settingsService.Update(s => s.RunAtStartup = enabled);
    }

    partial void OnStartMinimizedToTrayChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _settingsService.Update(s => s.StartMinimizedToTray = value);
        StatusMessage = "Настройка запуска в трей сохранена.";
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _settingsService.Update(s => s.MinimizeToTrayOnClose = value);
        StatusMessage = "Поведение при закрытии окна сохранено.";
    }

    partial void OnSelectedPollingOptionChanged(PollingIntervalOption value)
    {
        if (_isSyncing || value is null)
        {
            return;
        }

        _settingsService.Update(s => s.PollingIntervalMs = value.IntervalMs);
        _monitoringPollingService.SetPollingInterval(value.IntervalMs);
        StatusMessage = $"Интервал обновления датчиков: {value.DisplayText}.";
    }

    partial void OnPauseChartsWhenMinimizedChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _settingsService.Update(s => s.PauseChartsWhenMinimized = value);
        StatusMessage = value
            ? "При сворачивании в трей графики и опрос будут ставиться на паузу."
            : "Мониторинг продолжится даже при сворачивании в трей.";
    }

    partial void OnCpuGpuTempThresholdCChanged(double value)
    {
        if (_isSyncing)
        {
            return;
        }

        double rounded = Math.Clamp(Math.Round(value), 60.0, 100.0);
        _settingsService.Update(s => s.CpuGpuTempThresholdC = rounded);
    }

    partial void OnRamThresholdPercentChanged(double value)
    {
        if (_isSyncing)
        {
            return;
        }

        double rounded = Math.Clamp(Math.Round(value), 70.0, 98.0);
        _settingsService.Update(s => s.RamThresholdPercent = rounded);
    }

    partial void OnShowThresholdNotificationsChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _settingsService.Update(s => s.ShowThresholdNotifications = value);
        StatusMessage = value
            ? "Всплывающие уведомления в трее включены."
            : "Всплывающие уведомления в трее отключены.";
    }

    partial void OnIsOverlayEnabledChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _overlayService.SetVisible(value);
        StatusMessage = value
            ? "Компактный мини-виджет включён."
            : "Компактный мини-виджет скрыт.";
    }

    partial void OnOverlayOpacityPercentChanged(double value)
    {
        if (_isSyncing)
        {
            return;
        }

        double rounded = Math.Clamp(Math.Round(value), 40.0, 95.0);
        _settingsService.Update(s => s.OverlayOpacityPercent = rounded);
    }

    partial void OnSelectedOverlayCornerChanged(OverlayCornerOption value)
    {
        if (_isSyncing || value is null)
        {
            return;
        }

        _settingsService.Update(s => s.OverlayPosition = value.Corner);
        StatusMessage = $"Положение мини-виджета: {value.DisplayText}.";
    }

    partial void OnSelectedOverlayLayoutChanged(OverlayLayoutOption value)
    {
        if (_isSyncing || value is null)
        {
            return;
        }

        _settingsService.Update(s => s.OverlayLayout = value.Mode);
        StatusMessage = $"Формат мини-виджета: {value.DisplayText}.";
    }

    partial void OnOverlayLockPositionChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        _settingsService.Update(s => s.OverlayLockPosition = value);
        StatusMessage = value
            ? "Положение мини-виджета закреплено (перетаскивание отключено)."
            : "Мини-виджет можно перетаскивать мышкой.";
    }

    partial void OnOverlayShowCpuChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowCpu = value);
    }

    partial void OnOverlayShowGpuChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowGpu = value);
    }

    partial void OnOverlayShowRamChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowRam = value);
    }

    partial void OnOverlayShowNetworkChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowNetwork = value);
    }

    partial void OnOverlayShowTemperatureChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowTemperature = value);
    }

    partial void OnOverlayShowIconsChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowIcons = value);
    }

    partial void OnOverlayShowHoverButtonsChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowHoverButtons = value);
    }

    partial void OnOverlayShowVpnChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.OverlayShowVpn = value);
    }

    partial void OnVpnExecutablePathChanged(string value)
    {
        if (_isSyncing) return;
        string trimmed = value?.Trim() ?? string.Empty;
        _settingsService.Update(s => s.VpnExecutablePath = trimmed);
        StatusMessage = string.IsNullOrEmpty(trimmed)
            ? "Путь к VPN-клиенту очищен."
            : "Путь к VPN-клиенту сохранён и добавлен в список исключений.";
    }

    partial void OnVpnAutoStartChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.VpnAutoStart = value);
        StatusMessage = value
            ? "VPN будет запускаться при старте PulseGuard."
            : "Автозапуск VPN при старте PulseGuard отключён.";
    }

    partial void OnVpnStartMinimizedChanged(bool value)
    {
        if (_isSyncing) return;
        _settingsService.Update(s => s.VpnStartMinimized = value);
    }

    [RelayCommand]
    private void BrowseVpnExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите исполняемый файл VPN-клиента",
            Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            VpnExecutablePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        if (_settingsService.Current.RunAtStartup)
        {
            await _startupTaskScheduler.SetStartupEnabledAsync(false);
        }

        _settingsService.ResetToDefaults();
        _overlayService.SetVisible(_settingsService.Current.IsOverlayEnabled);
        LoadFromSettings(_settingsService.Current);
        _monitoringPollingService.SetPollingInterval(_settingsService.Current.PollingIntervalMs);
        StatusMessage = "Настройки сброшены по умолчанию.";
    }
}
