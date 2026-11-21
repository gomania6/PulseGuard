using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PulseGuard.Modules.BatteryDevices;
using PulseGuard.Modules.BatteryDevices.Models;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Monitoring.Models;
using PulseGuard.Modules.Network;
using PulseGuard.Modules.Network.Models;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Processes;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;
using PulseGuard.Modules.Vpn.Models;
using PulseGuard.Services;
using SkiaSharp;

namespace PulseGuard.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private const int HistoryLength = 60;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PacketLossAlertCooldown = TimeSpan.FromMinutes(3);

    private readonly SettingsService _settingsService;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly VpnService _vpnService;
    private readonly BatteryDeviceMonitorModule _batteryModule;
    private readonly NetworkRadarModule _networkRadarModule;
    private readonly ProcessMonitorModule _processMonitorModule;
    private readonly StutterMonitorModule _stutterMonitorModule;
    private readonly HashSet<string> _notifiedLowBatteryDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<double> _cpuHistory = new();
    private readonly ObservableCollection<double> _gpuHistory = new();
    private readonly ObservableCollection<double> _downloadHistory = new();
    private readonly ObservableCollection<double> _uploadHistory = new();

    private DateTime _lastCpuTempAlert = DateTime.MinValue;
    private DateTime _lastGpuTempAlert = DateTime.MinValue;
    private DateTime _lastRamAlert = DateTime.MinValue;
    private DateTime _lastPacketLossAlert = DateTime.MinValue;

    [ObservableProperty]
    private string _title = "Мониторинг";

    [ObservableProperty]
    private string _cpuName = "CPU";

    [ObservableProperty]
    private double _cpuLoadPercent;

    [ObservableProperty]
    private string _cpuLoadText = "0 %";

    [ObservableProperty]
    private string _cpuTempText = "— °C";

    [ObservableProperty]
    private string? _cpuTempTooltip;

    [ObservableProperty]
    private string? _cpuCardTooltip;

    [ObservableProperty]
    private bool _isCpuTempBlocked;

    [ObservableProperty]
    private string _cpuClockText = "— МГц";

    [ObservableProperty]
    private string _gpuName = "GPU";

    [ObservableProperty]
    private double _gpuLoadPercent;

    [ObservableProperty]
    private string _gpuLoadText = "— %";

    [ObservableProperty]
    private string _gpuTempText = "— °C";

    [ObservableProperty]
    private string _gpuVramText = "VRAM: —";

    [ObservableProperty]
    private double _ramLoadPercent;

    [ObservableProperty]
    private string _ramLoadText = "0 %";

    [ObservableProperty]
    private string _ramUsageText = "0.0 / 0.0 ГБ";

    [ObservableProperty]
    private string _networkDownloadText = "0.00 Мбит/с";

    [ObservableProperty]
    private string _networkUploadText = "0.00 Мбит/с";

    [ObservableProperty]
    private string _networkPacketLossText = "0%";

    [ObservableProperty]
    private string _networkJitterText = "0.0 мс";

    [ObservableProperty]
    private Brush _networkQualityBrush = Brushes.LightGreen;

    [ObservableProperty]
    private string _vpnStatusText = "Выключен";

    [ObservableProperty]
    private Brush _vpnStatusBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);

    [ObservableProperty]
    private string _vpnServerText = "—";

    [ObservableProperty]
    private string _vpnIpText = "—";

    [ObservableProperty]
    private string _vpnPingText = "—";

    [ObservableProperty]
    private string _vpnToggleButtonText = "Подключить";

    [ObservableProperty]
    private bool _isVpnBusy;

    [ObservableProperty]
    private string _vpnActionMessage = string.Empty;

    [ObservableProperty]
    private bool _isBatteryRefreshing;

    [ObservableProperty]
    private bool _hasNoBatteryDevices = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTopNetworkProcesses))]
    private bool _hasTopNetworkProcesses;

    [ObservableProperty]
    private string _stutterCountText = "0 фризов";

    [ObservableProperty]
    private string _stutterMaxDelayText = "Макс. задержка: 0 мс";

    [ObservableProperty]
    private string _stutterDetailsText = "DPC: 0.0% • Очередь CPU: 0";

    [ObservableProperty]
    private Brush _stutterStabilityBrush = CreateFrozenBrush(0x00, 0xE5, 0xA0);

    public bool HasNoTopNetworkProcesses => !HasTopNetworkProcesses;

    public ObservableCollection<BatteryDeviceItemViewModel> BatteryDevices { get; } = new();
    public ObservableCollection<NetworkConsumerItemViewModel> TopNetworkProcesses { get; } = new();

    public ObservableCollection<string> AlertLogs { get; } = new();

    public ISeries[] CpuGpuSeries { get; }
    public ISeries[] NetworkSeries { get; }

    public Axis[] HiddenXAxes { get; } =
    [
        new Axis
        {
            IsVisible = false,
            Padding = new LiveChartsCore.Drawing.Padding(0)
        }
    ];

    public Axis[] CpuGpuYAxes { get; } =
    [
        new Axis
        {
            MinLimit = 0,
            MaxLimit = 100,
            Labeler = val => $"{val:0}%",
            LabelsPaint = new SolidColorPaint(new SKColor(0xA0, 0xA0, 0xA0)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 0x26)),
            Padding = new LiveChartsCore.Drawing.Padding(0, 0, 6, 0)
        }
    ];

    public Axis[] NetworkYAxes { get; } =
    [
        new Axis
        {
            MinLimit = 0,
            Labeler = val => $"{val:0.#}",
            LabelsPaint = new SolidColorPaint(new SKColor(0xA0, 0xA0, 0xA0)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 0x26)),
            Padding = new LiveChartsCore.Drawing.Padding(0, 0, 6, 0)
        }
    ];

    public SolidColorPaint ChartLegendTextPaint { get; } = new(new SKColor(0xA0, 0xA0, 0xA0));
    public SolidColorPaint ChartTooltipTextPaint { get; } = new(SKColors.White);
    public SolidColorPaint ChartTooltipBackgroundPaint { get; } = new(new SKColor(32, 36, 46));
    public DrawMarginFrame? ChartDrawMarginFrame { get; } = null;

    public MonitoringViewModel(
        MonitoringPollingService pollingService,
        SettingsService settingsService,
        TrayNotificationService trayNotificationService,
        VpnService vpnService,
        BatteryDeviceMonitorModule batteryModule,
        NetworkRadarModule networkRadarModule,
        ProcessMonitorModule processMonitorModule,
        StutterMonitorModule stutterMonitorModule,
        GameModeTool gameModeTool,
        RamAutoCleanSchedulerModule ramAutoCleanScheduler)
    {
        _settingsService = settingsService;
        _trayNotificationService = trayNotificationService;
        _vpnService = vpnService;
        _batteryModule = batteryModule;
        _networkRadarModule = networkRadarModule;
        _processMonitorModule = processMonitorModule;
        _stutterMonitorModule = stutterMonitorModule;
        _gameModeTool = gameModeTool;
        ramAutoCleanScheduler.AutoCleanCompleted += msg =>
            Application.Current?.Dispatcher.BeginInvoke(() => AddAlert(msg));
        for (int i = 0; i < HistoryLength; i++)
        {
            _cpuHistory.Add(0);
            _gpuHistory.Add(0);
            _downloadHistory.Add(0);
            _uploadHistory.Add(0);
        }

        CpuGpuSeries =
        [
            new LineSeries<double>
            {
                Name = "CPU %",
                Values = _cpuHistory,
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(0, 178, 255), 1.5f),
                GeometrySize = 0,
                LineSmoothness = 0
            },
            new LineSeries<double>
            {
                Name = "GPU %",
                Values = _gpuHistory,
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(0, 229, 160), 1.5f),
                GeometrySize = 0,
                LineSmoothness = 0
            }
        ];

        NetworkSeries =
        [
            new LineSeries<double>
            {
                Name = "Download (Мбит/с)",
                Values = _downloadHistory,
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(124, 92, 255), 1.5f),
                GeometrySize = 0,
                LineSmoothness = 0
            },
            new LineSeries<double>
            {
                Name = "Upload (Мбит/с)",
                Values = _uploadHistory,
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(255, 166, 0), 1.5f),
                GeometrySize = 0,
                LineSmoothness = 0
            }
        ];

        pollingService.MetricsUpdated += OnMetricsUpdated;
        _vpnService.StatusChanged += OnVpnStatusChanged;
        _batteryModule.DevicesUpdated += OnBatteryDevicesUpdated;
        _networkRadarModule.RadarUpdated += OnNetworkRadarUpdated;
        _stutterMonitorModule.SnapshotUpdated += OnStutterSnapshotUpdated;
        _stutterMonitorModule.StutterDetected += OnStutterDetected;
        ApplyVpnStatus(_vpnService.CurrentStatus);
        ApplyBatteryDevices(_batteryModule.LatestDevices);
        ApplyNetworkRadar(_networkRadarModule.LatestSnapshot);
        ApplyStutterSnapshot(_stutterMonitorModule.LatestSnapshot);
    }

    private void OnStutterSnapshotUpdated(StutterSnapshot snapshot)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => ApplyStutterSnapshot(snapshot));
    }

    private void OnStutterDetected(StutterEvent ev, StutterSnapshot snapshot)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplyStutterSnapshot(snapshot);
            var localTime = ev.TimestampUtc.ToLocalTime();
            string culpritPart = string.IsNullOrWhiteSpace(ev.CulpritProcess)
                ? string.Empty
                : $" — всплеск активности: {ev.CulpritProcess}";
            AddAlert($"[{localTime:HH:mm:ss}] Микрофриз (задержка {ev.DelayMs:0} мс){culpritPart}");
        });
    }

    private void ApplyStutterSnapshot(StutterSnapshot snapshot)
    {
        StutterCountText = snapshot.StutterCount switch
        {
            0 => "Стабильно (0 фризов)",
            1 => "1 фриз за сессию",
            >= 2 and <= 4 => $"{snapshot.StutterCount} фриза за сессию",
            _ => $"{snapshot.StutterCount} фризов за сессию"
        };

        StutterMaxDelayText = $"Макс. задержка: {snapshot.MaxDelayMs:0} мс";
        StutterDetailsText = $"DPC: {snapshot.DpcPercent:0.0}% • Очередь CPU: {snapshot.ProcessorQueueLength}";

        StutterStabilityBrush = snapshot.StutterCount == 0
            ? CreateFrozenBrush(0x00, 0xE5, 0xA0)
            : (snapshot.StutterCount >= 5 || snapshot.MaxDelayMs >= 120)
                ? CreateFrozenBrush(0xFF, 0x5C, 0x5C)
                : CreateFrozenBrush(0xFF, 0xB9, 0x00);
    }

    private void OnNetworkRadarUpdated(NetworkRadarSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyNetworkRadar(snapshot));
    }

    private void ApplyNetworkRadar(NetworkRadarSnapshot snapshot)
    {
        TopNetworkProcesses.Clear();
        foreach (var item in snapshot.TopProcesses)
        {
            TopNetworkProcesses.Add(new NetworkConsumerItemViewModel(item, _processMonitorModule));
        }

        HasTopNetworkProcesses = TopNetworkProcesses.Count > 0;
    }

    private void OnVpnStatusChanged(VpnStatusSnapshot status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyVpnStatus(status));
    }

    private void ApplyVpnStatus(VpnStatusSnapshot status)
    {
        VpnStatusText = status.StatusText;
        VpnStatusBrush = status.State switch
        {
            VpnConnectionState.Connected => CreateFrozenBrush(0x00, 0xE5, 0xA0),
            VpnConnectionState.Error => CreateFrozenBrush(0xFF, 0x5C, 0x5C),
            _ => CreateFrozenBrush(0x8A, 0x8F, 0x98)
        };

        VpnServerText = string.IsNullOrWhiteSpace(status.CountryCode)
            ? status.ServerName
            : status.ServerName == "—"
                ? status.CountryCode
                : $"{status.CountryCode} · {status.ServerName}";

        VpnIpText = status.ExternalIp;
        VpnPingText = status.State == VpnConnectionState.Connected && status.PingMs.HasValue
            ? $"{status.PingMs.Value} мс"
            : "—";
        VpnToggleButtonText = status.State != VpnConnectionState.Disconnected
            ? "Отключить"
            : "Подключить";

        NetworkPacketLossText = $"{status.PacketLossPercent:0}%";
        NetworkJitterText = $"{status.JitterMs:0.0} мс";
        NetworkQualityBrush = status.Quality switch
        {
            ConnectionQuality.Bad => CreateFrozenBrush(0xFF, 0x5C, 0x5C),
            ConnectionQuality.Warning => CreateFrozenBrush(0xFF, 0xB9, 0x00),
            _ => CreateFrozenBrush(0x00, 0xE5, 0xA0)
        };

        EvaluatePacketLossAlert(status);
    }

    private void EvaluatePacketLossAlert(VpnStatusSnapshot status)
    {
        var settings = _settingsService.Current;
        if (status.PacketLossPercent <= settings.PacketLossAlertThreshold || status.PacketLossPercent >= 100)
        {
            return;
        }

        var now = DateTime.Now;
        if ((now - _lastPacketLossAlert) < PacketLossAlertCooldown)
        {
            return;
        }

        _lastPacketLossAlert = now;
        string channelText = status.State == VpnConnectionState.Connected
            ? $"через VPN ({(status.ServerName != "—" ? status.ServerName : status.CountryCode)})"
            : "(канал интернета)";

        string message = $"Потеря пакетов {status.PacketLossPercent:0}% {channelText} — возможны лаги";
        AddAlert($"[{now:HH:mm:ss}] {message}");

        if (ShouldShowNotificationBalloon(settings))
        {
            _trayNotificationService.TryShowWarningBalloon("PulseGuard — Потеря пакетов", message);
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
            var (_, message) = await _vpnService.ToggleAsync();
            VpnActionMessage = message;
        }
        finally
        {
            IsVpnBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartVpnAsync()
    {
        if (IsVpnBusy)
        {
            return;
        }

        IsVpnBusy = true;
        try
        {
            var (_, message) = await _vpnService.RestartAsync();
            VpnActionMessage = message;
        }
        finally
        {
            IsVpnBusy = false;
        }
    }

    private void OnMetricsUpdated(SystemMetricsSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(SystemMetricsSnapshot s)
    {
        CpuName = s.CpuName;
        CpuLoadPercent = Math.Clamp(s.CpuLoadPercent, 0, 100);
        CpuLoadText = $"{CpuLoadPercent:0} %";
        if (s.CpuTemperatureC.HasValue)
        {
            CpuTempText = $"{s.CpuTemperatureC.Value:0} °C";
            CpuTempTooltip = string.IsNullOrWhiteSpace(s.CpuDiagnosticError) ? null : s.CpuDiagnosticError;
            IsCpuTempBlocked = false;
        }
        else
        {
            string reason = !string.IsNullOrWhiteSpace(s.CpuTemperatureUnavailableReason)
                ? s.CpuTemperatureUnavailableReason
                : "Датчик не найден";

            CpuTempText = reason == "Отключено в настройках"
                ? "Н/Д (откл.)"
                : "Н/Д";
            CpuTempTooltip = reason;
            IsCpuTempBlocked = reason.Contains("HVCI", StringComparison.OrdinalIgnoreCase)
                               || reason.Contains("Defender", StringComparison.OrdinalIgnoreCase);
        }

        CpuClockText = s.CpuClockMhz.HasValue
            ? s.CpuClockIsBaseOnly
                ? $"{s.CpuClockMhz.Value:0} МГц (базовая)"
                : $"{s.CpuClockMhz.Value:0} МГц"
            : "— МГц";
        CpuCardTooltip = !string.IsNullOrWhiteSpace(s.CpuTemperatureUnavailableReason)
            ? $"Температура CPU недоступна: {s.CpuTemperatureUnavailableReason}"
            : string.IsNullOrWhiteSpace(s.CpuDiagnosticError)
                ? null
                : s.CpuDiagnosticError;

        GpuName = s.GpuName;
        GpuLoadPercent = Math.Clamp(s.GpuLoadPercent ?? 0, 0, 100);
        GpuLoadText = s.GpuLoadPercent.HasValue ? $"{GpuLoadPercent:0} %" : "— %";
        GpuTempText = s.GpuTemperatureC.HasValue ? $"{s.GpuTemperatureC.Value:0} °C" : "— °C";
        GpuVramText = (s.GpuVramUsedMb.HasValue && s.GpuVramTotalMb.HasValue)
            ? $"VRAM: {s.GpuVramUsedMb.Value / 1024.0:0.0} / {s.GpuVramTotalMb.Value / 1024.0:0.0} ГБ"
            : s.GpuVramUsedMb.HasValue
                ? $"VRAM: {s.GpuVramUsedMb.Value:0} МБ"
                : "VRAM: —";

        RamLoadPercent = Math.Clamp(s.RamLoadPercent, 0, 100);
        RamLoadText = $"{RamLoadPercent:0} %";
        RamUsageText = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} ГБ";

        NetworkDownloadText = FormatNetworkSpeed(s.DownloadBytesPerSec);
        NetworkUploadText = FormatNetworkSpeed(s.UploadBytesPerSec);

        PushPoint(_cpuHistory, CpuLoadPercent);
        PushPoint(_gpuHistory, GpuLoadPercent);
        PushPoint(_downloadHistory, (s.DownloadBytesPerSec * 8.0) / 1_000_000.0);
        PushPoint(_uploadHistory, (s.UploadBytesPerSec * 8.0) / 1_000_000.0);

        EvaluateAlerts(s);
    }

    private static void PushPoint(ObservableCollection<double> series, double value)
    {
        if (series.Count >= HistoryLength)
        {
            series.RemoveAt(0);
        }

        series.Add(Math.Round(value, 2));
    }

    private static string FormatNetworkSpeed(double bytesPerSec)
    {
        const double bytesPerMegabyte = 1024.0 * 1024.0;
        if (bytesPerSec >= bytesPerMegabyte)
        {
            return $"{bytesPerSec / bytesPerMegabyte:0.00} МБ/с";
        }

        double mbps = (bytesPerSec * 8.0) / 1_000_000.0;
        return $"{mbps:0.00} Мбит/с";
    }

    private void EvaluateAlerts(SystemMetricsSnapshot s)
    {
        var now = DateTime.Now;
        var settings = _settingsService.Current;
        double tempThreshold = settings.CpuGpuTempThresholdC;
        double ramThreshold = settings.RamThresholdPercent;
        bool showBalloon = ShouldShowNotificationBalloon(settings);

        if (s.CpuTemperatureC is double cpuTemp && cpuTemp > tempThreshold && (now - _lastCpuTempAlert) >= AlertCooldown)
        {
            _lastCpuTempAlert = now;
            AddAlert($"[{now:HH:mm:ss}] Высокая температура CPU: {cpuTemp:0} °C (> {tempThreshold:0} °C)");
            if (showBalloon)
            {
                _trayNotificationService.TryShowWarningBalloon(
                    "PulseGuard — Высокая температура CPU",
                    $"Температура CPU достигла {cpuTemp:0} °C (порог {tempThreshold:0} °C).");
            }
        }

        if (s.GpuTemperatureC is double gpuTemp && gpuTemp > tempThreshold && (now - _lastGpuTempAlert) >= AlertCooldown)
        {
            _lastGpuTempAlert = now;
            AddAlert($"[{now:HH:mm:ss}] Высокая температура GPU: {gpuTemp:0} °C (> {tempThreshold:0} °C)");
            if (showBalloon)
            {
                _trayNotificationService.TryShowWarningBalloon(
                    "PulseGuard — Высокая температура GPU",
                    $"Температура GPU достигла {gpuTemp:0} °C (порог {tempThreshold:0} °C).");
            }
        }

        if (s.RamLoadPercent > ramThreshold && (now - _lastRamAlert) >= AlertCooldown)
        {
            _lastRamAlert = now;
            AddAlert($"[{now:HH:mm:ss}] Критическая загрузка ОЗУ: {s.RamLoadPercent:0}% (> {ramThreshold:0}%)");
            if (showBalloon)
            {
                _trayNotificationService.TryShowWarningBalloon(
                    "PulseGuard — Загрузка ОЗУ",
                    $"Использование ОЗУ достигло {s.RamLoadPercent:0}% (порог {ramThreshold:0}%).");
            }
        }
    }

    private readonly GameModeTool _gameModeTool;

    private bool ShouldShowNotificationBalloon(AppSettings settings)
    {
        if (!settings.ShowThresholdNotifications)
        {
            return false;
        }

        return settings.NotificationProfile switch
        {
            NotificationProfile.Silent => false,
            NotificationProfile.GamingFocus => !_gameModeTool.IsActive,
            _ => true
        };
    }

    private void AddAlert(string message)
    {
        AlertLogs.Insert(0, message);
        if (AlertLogs.Count > 50)
        {
            AlertLogs.RemoveAt(AlertLogs.Count - 1);
        }
    }

    [RelayCommand]
    private void ClearAlerts()
    {
        AlertLogs.Clear();
    }

    [RelayCommand]
    private async Task RefreshBatteryDevicesAsync()
    {
        if (IsBatteryRefreshing)
        {
            return;
        }

        IsBatteryRefreshing = true;
        try
        {
            await _batteryModule.RefreshAsync(forceHidScan: true);
        }
        finally
        {
            IsBatteryRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task ExportHidDiagnosticsAsync()
    {
        try
        {
            string filePath = await _batteryModule.ExportHidDiagnosticsAsync();
            AddAlert($"[{DateTime.Now:HH:mm:ss}] Список USB HID-устройств сохранён: {filePath}");

            try
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch
            {
                _trayNotificationService.TryShowWarningBalloon(
                    "PulseGuard — Диагностика HID",
                    $"Список HID-устройств сохранён в {filePath}");
            }
        }
        catch
        {
            // Ignore export errors if AppData is unavailable.
        }
    }

    private void OnBatteryDevicesUpdated(IReadOnlyList<BatteryDeviceInfo> devices)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyBatteryDevices(devices));
    }

    private void ApplyBatteryDevices(IReadOnlyList<BatteryDeviceInfo> devices)
    {
        var pinnedIds = _settingsService.Current.PinnedOverlayDeviceIds;

        BatteryDevices.Clear();
        foreach (var device in devices)
        {
            bool isPinned = pinnedIds.Contains(device.DeviceId, StringComparer.OrdinalIgnoreCase);
            BatteryDevices.Add(new BatteryDeviceItemViewModel(device, isPinned, OnDeviceOverlayPinChanged));

            if (isPinned
                && device.IsConnected
                && device.BatteryPercent < 15
                && _notifiedLowBatteryDeviceIds.Add(device.DeviceId))
            {
                AddAlert($"[{DateTime.Now:HH:mm:ss}] Низкий заряд устройства «{device.Name}»: {device.BatteryPercent}% (< 15%)");
                _trayNotificationService.TryShowWarningBalloon(
                    "PulseGuard — Низкий заряд устройства",
                    $"{device.Name}: осталось {device.BatteryPercent}% заряда.");
            }
        }

        HasNoBatteryDevices = BatteryDevices.Count == 0;
    }

    private void OnDeviceOverlayPinChanged(string deviceId, bool isPinned)
    {
        _settingsService.Update(settings =>
        {
            if (isPinned)
            {
                if (!settings.PinnedOverlayDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
                {
                    settings.PinnedOverlayDeviceIds.Add(deviceId);
                }
            }
            else
            {
                settings.PinnedOverlayDeviceIds.RemoveAll(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
            }
        });
    }

    [RelayCommand]
    private async Task ShowDriverHelpAsync()
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Доступ к датчикам CPU в Windows 11",
            Content =
                "Драйвер чтения аппаратных датчиков (WinRing0) заблокирован системой безопасности Windows 11.\n\n" +
                "Чтобы отображались температура и точная частота ядер CPU:\n" +
                "1. Добавьте папку с PulseGuard в исключения Защитника Windows (Безопасность Windows → Защита от вирусов и угроз → Управление настройками → Исключения).\n" +
                "2. Или отключите «Целостность памяти» (Безопасность Windows → Безопасность устройства → Изоляция ядра → Целостность памяти) и перезагрузите ПК.",
            CloseButtonText = "Понятно"
        };

        await box.ShowDialogAsync();
    }
}

public sealed partial class BatteryDeviceItemViewModel : ObservableObject
{
    private readonly Action<string, bool> _onPinChanged;
    private bool _suppressPinCallback;

    public string DeviceId { get; }
    public string Name { get; }
    public int BatteryPercent { get; }
    public string BatteryPercentText { get; }
    public bool IsConnected { get; }
    public string StatusText { get; }
    public double ItemOpacity { get; }
    public Wpf.Ui.Controls.SymbolRegular DeviceIcon { get; }
    public Brush AccentBrush { get; }

    [ObservableProperty]
    private bool _isPinnedToOverlay;

    public BatteryDeviceItemViewModel(
        BatteryDeviceInfo info,
        bool isPinned,
        Action<string, bool> onPinChanged)
    {
        _onPinChanged = onPinChanged;
        DeviceId = info.DeviceId;
        Name = info.Name;
        BatteryPercent = info.BatteryPercent;
        BatteryPercentText = $"{info.BatteryPercent}%";
        IsConnected = info.IsConnected;
        StatusText = !info.IsConnected
            ? "Не в сети"
            : info.IsSleeping
                ? "Спит (последний известный заряд)"
                : "Активна";
        ItemOpacity = !info.IsConnected
            ? 0.55
            : info.IsSleeping
                ? 0.78
                : 1.0;
        DeviceIcon = BatteryDevicePresentation.GetIcon(info.DeviceType);
        AccentBrush = BatteryDevicePresentation.GetCardBrush(info.BatteryPercent, info.IsConnected);

        _suppressPinCallback = true;
        _isPinnedToOverlay = isPinned;
        _suppressPinCallback = false;
    }

    partial void OnIsPinnedToOverlayChanged(bool value)
    {
        if (!_suppressPinCallback)
        {
            _onPinChanged(DeviceId, value);
        }
    }
}

public sealed partial class NetworkConsumerItemViewModel : ObservableObject
{
    private readonly ProcessMonitorModule _processMonitorModule;
    private readonly IReadOnlyList<int> _pids;
    private readonly bool _isSuspended;

    public string ProcessName { get; }
    public string DownloadText { get; }
    public string UploadText { get; }
    public string TooltipText { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;
    public bool IsProtected { get; }
    public bool IsSuspended => _isSuspended;
    public bool CanToggleSuspend => !IsProtected;
    public string ActionButtonText => _isSuspended ? "Продолжить" : "Пауза";
    public Wpf.Ui.Controls.SymbolRegular ActionButtonIcon =>
        _isSuspended ? Wpf.Ui.Controls.SymbolRegular.Play16 : Wpf.Ui.Controls.SymbolRegular.Pause16;
    public string ActionTooltip => IsProtected
        ? "Системный процесс или VPN-клиент защищён от заморозки"
        : _isSuspended
            ? $"Возобновить процесс «{ProcessName}» (NtResumeProcess)"
            : $"Приостановить загрузку «{ProcessName}» (NtSuspendProcess)";

    public NetworkConsumerItemViewModel(
        NetworkProcessTraffic traffic,
        ProcessMonitorModule processMonitorModule)
    {
        _processMonitorModule = processMonitorModule;
        _pids = traffic.Pids;
        _isSuspended = traffic.IsSuspended;
        ProcessName = traffic.ProcessName;
        IsProtected = traffic.IsProtected;
        Icon = ProcessIconResolver.GetSmallIcon(traffic.ProcessName, traffic.ExecutablePath);
        DownloadText = traffic.IsSuspended
            ? "На паузе"
            : $"↓ {traffic.DownloadMbps:0.0} Мбит/с";
        UploadText = traffic.IsSuspended
            ? "—"
            : $"↑ {traffic.UploadMbps:0.0} Мбит/с";
        TooltipText = $"PID: {string.Join(", ", traffic.Pids)} • ↓ {traffic.DownloadMbps:0.00} Мбит/с • ↑ {traffic.UploadMbps:0.00} Мбит/с";
    }

    [RelayCommand]
    private void ToggleSuspend()
    {
        if (IsProtected)
        {
            return;
        }

        if (_isSuspended)
        {
            _processMonitorModule.ResumeGroup(_pids, ProcessName, out _);
        }
        else
        {
            _processMonitorModule.SuspendGroup(_pids, ProcessName, out _);
        }
    }
}
