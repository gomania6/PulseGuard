using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Monitoring.Models;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Vpn;
using PulseGuard.Modules.Vpn.Models;
using PulseGuard.Services;
using SkiaSharp;

namespace PulseGuard.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private const int HistoryLength = 60;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(1);

    private readonly SettingsService _settingsService;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly VpnService _vpnService;
    private readonly ObservableCollection<double> _cpuHistory = new();
    private readonly ObservableCollection<double> _gpuHistory = new();
    private readonly ObservableCollection<double> _downloadHistory = new();
    private readonly ObservableCollection<double> _uploadHistory = new();

    private DateTime _lastCpuTempAlert = DateTime.MinValue;
    private DateTime _lastGpuTempAlert = DateTime.MinValue;
    private DateTime _lastRamAlert = DateTime.MinValue;

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
        VpnService vpnService)
    {
        _settingsService = settingsService;
        _trayNotificationService = trayNotificationService;
        _vpnService = vpnService;
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
        ApplyVpnStatus(_vpnService.CurrentStatus);
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
        VpnPingText = status.PingMs.HasValue ? $"{status.PingMs.Value} мс" : "—";
        VpnToggleButtonText = status.IsProcessRunning ? "Отключить" : "Подключить";
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
            CpuTempTooltip = null;
            IsCpuTempBlocked = false;
        }
        else
        {
            CpuTempText = "Н/Д (блок драйвера)";
            CpuTempTooltip = "Драйвер датчиков (WinRing0) заблокирован функцией «Целостность памяти» (Memory Integrity) в Windows 11.";
            IsCpuTempBlocked = true;
        }

        CpuClockText = s.CpuClockMhz.HasValue
            ? s.CpuClockIsBaseOnly
                ? $"{s.CpuClockMhz.Value:0} МГц (базовая)"
                : $"{s.CpuClockMhz.Value:0} МГц"
            : "— МГц";
        CpuCardTooltip = string.IsNullOrWhiteSpace(s.CpuDiagnosticError)
            ? null
            : $"Диагностика CPU: {s.CpuDiagnosticError}";

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
        bool showBalloon = settings.ShowThresholdNotifications;

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
