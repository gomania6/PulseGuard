using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;
using PulseGuard.Modules.Vpn.Models;

namespace PulseGuard.ViewModels;

public partial class VpnViewModel : ObservableObject
{
    private static readonly Brush DisconnectedBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);
    private static readonly Brush GoodBrush = CreateFrozenBrush(0x00, 0xE5, 0xA0);
    private static readonly Brush WarningBrush = CreateFrozenBrush(0xFF, 0xB9, 0x00);
    private static readonly Brush ErrorBrush = CreateFrozenBrush(0xFF, 0x5C, 0x5C);

    private readonly VpnService _vpnService;
    private readonly SettingsService _settingsService;
    private bool _isLoadingSettings;

    [ObservableProperty]
    private string _title = "VPN-туннель и сеть";

    [ObservableProperty]
    private string _statusText = "Выключен";

    [ObservableProperty]
    private Brush _statusBrush = DisconnectedBrush;

    [ObservableProperty]
    private string _serverText = "—";

    [ObservableProperty]
    private string _externalIpText = "—";

    [ObservableProperty]
    private string _pingText = "—";

    [ObservableProperty]
    private string _packetLossText = "0%";

    [ObservableProperty]
    private string _jitterText = "0.0 мс";

    [ObservableProperty]
    private string _qualityText = "Хорошее";

    [ObservableProperty]
    private Brush _qualityBrush = GoodBrush;

    [ObservableProperty]
    private string _processStateText = "Не запущен";

    [ObservableProperty]
    private string _configStateText = "Не настроен";

    [ObservableProperty]
    private string _toggleButtonText = "Подключить";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControlVpn))]
    private bool _isBusy;

    [ObservableProperty]
    private string _actionMessage = "Настройте путь к VPN-клиенту ниже или в разделе «Настройки».";

    // Quick settings synced with AppSettings
    [ObservableProperty]
    private string _vpnExecutablePath = string.Empty;

    [ObservableProperty]
    private bool _vpnAutoStart;

    [ObservableProperty]
    private bool _vpnStartMinimized = true;

    [ObservableProperty]
    private bool _overlayShowVpn = true;

    public bool CanControlVpn => !IsBusy;

    public VpnViewModel(VpnService vpnService, SettingsService settingsService)
    {
        _vpnService = vpnService;
        _settingsService = settingsService;

        _vpnService.StatusChanged += OnVpnStatusChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        LoadSettings(_settingsService.Current);
        ApplyStatus(_vpnService.CurrentStatus);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => LoadSettings(settings));
    }

    private void LoadSettings(AppSettings settings)
    {
        _isLoadingSettings = true;
        try
        {
            VpnExecutablePath = settings.VpnExecutablePath;
            VpnAutoStart = settings.VpnAutoStart;
            VpnStartMinimized = settings.VpnStartMinimized;
            OverlayShowVpn = settings.OverlayShowVpn;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    partial void OnVpnExecutablePathChanged(string value)
    {
        if (_isLoadingSettings) return;
        _settingsService.Update(s => s.VpnExecutablePath = value ?? string.Empty);
    }

    partial void OnVpnAutoStartChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _settingsService.Update(s => s.VpnAutoStart = value);
    }

    partial void OnVpnStartMinimizedChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _settingsService.Update(s => s.VpnStartMinimized = value);
    }

    partial void OnOverlayShowVpnChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _settingsService.Update(s => s.OverlayShowVpn = value);
    }

    private void OnVpnStatusChanged(VpnStatusSnapshot status)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => ApplyStatus(status));
    }

    private void ApplyStatus(VpnStatusSnapshot status)
    {
        StatusText = status.StatusText;
        StatusBrush = status.State switch
        {
            VpnConnectionState.Connected => GoodBrush,
            VpnConnectionState.Error => ErrorBrush,
            _ => DisconnectedBrush
        };

        ServerText = string.IsNullOrWhiteSpace(status.CountryCode)
            ? status.ServerName
            : status.ServerName == "—"
                ? status.CountryCode
                : $"{status.CountryCode} · {status.ServerName}";

        ExternalIpText = status.ExternalIp;
        PingText = status.State == VpnConnectionState.Connected && status.PingMs.HasValue
            ? $"{status.PingMs.Value} мс"
            : "—";

        PacketLossText = $"{status.PacketLossPercent:0}%";
        JitterText = $"{status.JitterMs:0.0} мс";

        (QualityText, QualityBrush) = status.Quality switch
        {
            ConnectionQuality.Bad => ("Плохое", ErrorBrush),
            ConnectionQuality.Warning => ("Нестабильное", WarningBrush),
            _ => ("Хорошее", GoodBrush)
        };

        ProcessStateText = status.IsProcessRunning ? "Запущен" : "Не запущен";
        ConfigStateText = status.IsConfigured ? "Настроен" : "Не указан .exe";
        ToggleButtonText = status.State != VpnConnectionState.Disconnected ? "Отключить" : "Подключить";
    }

    [RelayCommand]
    private async Task ToggleVpnAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            var (_, message) = await _vpnService.ToggleAsync();
            ActionMessage = message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartVpnAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            var (_, message) = await _vpnService.RestartAsync();
            ActionMessage = message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseVpnExecutable()
    {
        var dialog = new OpenFileDialog
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

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

