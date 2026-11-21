using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.Network;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Vpn;

namespace PulseGuard.ViewModels;

public sealed record AutoCleanIntervalOption(int Minutes, string DisplayText);

public partial class OptimizationViewModel : ObservableObject
{
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly TempFilesCleanerTool _tempCleanerTool;
    private readonly NetworkOptimizerTool _networkOptimizerTool;
    private readonly VpnService _vpnService;
    private readonly SettingsService _settingsService;
    private readonly RamAutoCleanSchedulerModule _ramAutoCleanScheduler;
    private bool _isSyncingRamScheduler;

    private TempAnalysisResult? _lastTempAnalysis;

    [ObservableProperty]
    private string _title = "Оптимизация системы";

    // Card 1: Safe RAM Cleaner
    [ObservableProperty]
    private string _ramStatusText = "Занято: — ГБ • Свободно: — ГБ";

    [ObservableProperty]
    private string _ramActionStatus = "Вызывает Win32 EmptyWorkingSet только для пользовательских процессов.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanRam))]
    private bool _isCleaningRam;

    // Card 1.1: RAM Auto-Clean Scheduler
    [ObservableProperty]
    private bool _isAutoRamCleanEnabled;

    [ObservableProperty]
    private double _autoRamCleanThresholdPercent = 85.0;

    [ObservableProperty]
    private AutoCleanIntervalOption _selectedAutoCleanIntervalOption;

    [ObservableProperty]
    private string _autoRamCleanStatus = "Авто-очистка отключена.";

    public IReadOnlyList<AutoCleanIntervalOption> AutoCleanIntervalOptions { get; } =
    [
        new(0, "Только по порогу ОЗУ"),
        new(30, "Порог + каждые 30 мин"),
        new(60, "Порог + каждый 1 час"),
        new(120, "Порог + каждые 2 часа")
    ];

    // Card 2: Temporary Files & Cache Cleaner
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _includeUserTemp = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _includeSystemTemp = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _includeD3dShaderCache = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _includeRecycleBin = false;

    [ObservableProperty]
    private string _userTempInfo = "Не проверено";

    [ObservableProperty]
    private string _systemTempInfo = "Не проверено";

    [ObservableProperty]
    private string _d3dShaderCacheInfo = "Не проверено";

    [ObservableProperty]
    private string _recycleBinInfo = "Не проверено";

    [ObservableProperty]
    private string _tempActionStatus = "Нажмите «Анализировать» для оценки освобождаемого объёма.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnalyzeTemp))]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _isAnalyzingTemp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnalyzeTemp))]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _isCleaningTemp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private bool _hasTempAnalysis;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanTemp))]
    private long _selectedReclaimableBytes;

    // Card 3: Network & DNS Optimization
    [ObservableProperty]
    private string _networkAdapterInfo = "Нажмите «Проверить DNS» для замера отклика серверов.";

    [ObservableProperty]
    private string _networkBenchmarkSummary = string.Empty;

    [ObservableProperty]
    private string _networkActionStatus = "Очищает кэш DNS, выбирает быстрейший DNS и нормализует TCP Auto-Tuning через штатный netsh (без правки реестра).";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunNetworkAction))]
    private bool _isNetworkBusy;

    public bool CanRunNetworkAction => !IsNetworkBusy;
    public bool CanCleanRam => !IsCleaningRam;
    public bool CanAnalyzeTemp => !IsAnalyzingTemp && !IsCleaningTemp;
    public bool CanCleanTemp => !IsAnalyzingTemp
        && !IsCleaningTemp
        && (IncludeUserTemp || IncludeSystemTemp || IncludeD3dShaderCache || IncludeRecycleBin);

    public OptimizationViewModel(
        SafeRamCleanerTool ramCleanerTool,
        TempFilesCleanerTool tempCleanerTool,
        NetworkOptimizerTool networkOptimizerTool,
        VpnService vpnService,
        SettingsService settingsService,
        RamAutoCleanSchedulerModule ramAutoCleanScheduler)
    {
        _ramCleanerTool = ramCleanerTool;
        _tempCleanerTool = tempCleanerTool;
        _networkOptimizerTool = networkOptimizerTool;
        _vpnService = vpnService;
        _settingsService = settingsService;
        _ramAutoCleanScheduler = ramAutoCleanScheduler;

        _selectedAutoCleanIntervalOption = AutoCleanIntervalOptions[0];
        LoadRamSchedulerSettings();
        _ramAutoCleanScheduler.AutoCleanCompleted += status =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => AutoRamCleanStatus = status);
        };
        RefreshRamStatus();
    }

    private void LoadRamSchedulerSettings()
    {
        _isSyncingRamScheduler = true;
        try
        {
            var s = _settingsService.Current;
            IsAutoRamCleanEnabled = s.IsAutoRamCleanEnabled;
            AutoRamCleanThresholdPercent = s.AutoRamCleanThresholdPercent;
            SelectedAutoCleanIntervalOption = AutoCleanIntervalOptions.FirstOrDefault(o => o.Minutes == s.AutoRamCleanIntervalMinutes)
                                              ?? AutoCleanIntervalOptions[0];
            AutoRamCleanStatus = s.IsAutoRamCleanEnabled
                ? $"Планировщик активен: порог {s.AutoRamCleanThresholdPercent:0}% ({SelectedAutoCleanIntervalOption.DisplayText})."
                : "Авто-очистка отключена.";
        }
        finally
        {
            _isSyncingRamScheduler = false;
        }
    }

    partial void OnIsAutoRamCleanEnabledChanged(bool value)
    {
        if (_isSyncingRamScheduler) return;
        _settingsService.Update(s => s.IsAutoRamCleanEnabled = value);
        AutoRamCleanStatus = value
            ? $"Планировщик включён: порог {AutoRamCleanThresholdPercent:0}%."
            : "Авто-очистка отключена.";
    }

    partial void OnAutoRamCleanThresholdPercentChanged(double value)
    {
        if (_isSyncingRamScheduler) return;
        double rounded = Math.Clamp(Math.Round(value), 65.0, 95.0);
        _settingsService.Update(s => s.AutoRamCleanThresholdPercent = rounded);
    }

    partial void OnSelectedAutoCleanIntervalOptionChanged(AutoCleanIntervalOption value)
    {
        if (_isSyncingRamScheduler || value is null) return;
        _settingsService.Update(s => s.AutoRamCleanIntervalMinutes = value.Minutes);
    }

    public void RefreshRamStatus()
    {
        var mem = SafeRamCleanerTool.GetMemoryStatus();
        RamStatusText = $"Занято: {mem.UsedGb:F1} ГБ • Свободно: {mem.FreeGb:F1} ГБ (из {mem.TotalGb:F1} ГБ)";
    }

    partial void OnIncludeUserTempChanged(bool value)
    {
        _tempCleanerTool.IncludeUserTemp = value;
        RecalculateSelectedTempBytes();
    }

    partial void OnIncludeSystemTempChanged(bool value)
    {
        _tempCleanerTool.IncludeSystemTemp = value;
        RecalculateSelectedTempBytes();
    }

    partial void OnIncludeD3dShaderCacheChanged(bool value)
    {
        _tempCleanerTool.IncludeD3dShaderCache = value;
        RecalculateSelectedTempBytes();
    }

    partial void OnIncludeRecycleBinChanged(bool value)
    {
        _tempCleanerTool.IncludeRecycleBin = value;
        RecalculateSelectedTempBytes();
    }

    [RelayCommand]
    private async Task CleanRamAsync()
    {
        if (IsCleaningRam) return;

        IsCleaningRam = true;
        RamActionStatus = "Оптимизация рабочей памяти процессов...";

        try
        {
            var result = await _ramCleanerTool.ExecuteAsync();
            RamActionStatus = result.Message;
            RefreshRamStatus();
        }
        finally
        {
            IsCleaningRam = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeTempAsync()
    {
        if (IsAnalyzingTemp || IsCleaningTemp) return;

        IsAnalyzingTemp = true;
        TempActionStatus = "Сканирование временных каталогов, D3DSCache и Корзины...";

        try
        {
            _lastTempAnalysis = await _tempCleanerTool.AnalyzeAsync();
            UpdateTempInfos(_lastTempAnalysis);

            HasTempAnalysis = true;
            RecalculateSelectedTempBytes();

            TempActionStatus = SelectedReclaimableBytes > 0
                ? $"Готово к очистке: {TempFilesCleanerTool.FormatSize(SelectedReclaimableBytes)}"
                : "В выбранных категориях нечего удалять.";
        }
        finally
        {
            IsAnalyzingTemp = false;
        }
    }

    [RelayCommand]
    private async Task CleanTempAsync()
    {
        if (!CanCleanTemp) return;

        IsCleaningTemp = true;
        TempActionStatus = "Безопасное удаление выбранных файлов...";

        try
        {
            _tempCleanerTool.IncludeUserTemp = IncludeUserTemp;
            _tempCleanerTool.IncludeSystemTemp = IncludeSystemTemp;
            _tempCleanerTool.IncludeD3dShaderCache = IncludeD3dShaderCache;
            _tempCleanerTool.IncludeRecycleBin = IncludeRecycleBin;

            var result = await _tempCleanerTool.ExecuteAsync();
            TempActionStatus = result.Message;

            _lastTempAnalysis = await _tempCleanerTool.AnalyzeAsync();
            UpdateTempInfos(_lastTempAnalysis);
            HasTempAnalysis = true;
            RecalculateSelectedTempBytes();
        }
        finally
        {
            IsCleaningTemp = false;
        }
    }

    private void UpdateTempInfos(TempAnalysisResult analysis)
    {
        UserTempInfo = $"{analysis.UserTemp.FileCount} файлов • {TempFilesCleanerTool.FormatSize(analysis.UserTemp.TotalBytes)}";
        SystemTempInfo = $"{analysis.SystemTemp.FileCount} файлов • {TempFilesCleanerTool.FormatSize(analysis.SystemTemp.TotalBytes)}";
        D3dShaderCacheInfo = $"{analysis.D3dShaderCache.FileCount} файлов • {TempFilesCleanerTool.FormatSize(analysis.D3dShaderCache.TotalBytes)}";
        RecycleBinInfo = $"{analysis.RecycleBin.FileCount} объектов • {TempFilesCleanerTool.FormatSize(analysis.RecycleBin.TotalBytes)}";
    }

    private void RecalculateSelectedTempBytes()
    {
        if (_lastTempAnalysis is null)
        {
            SelectedReclaimableBytes = 0;
            return;
        }

        long total = 0;
        if (IncludeUserTemp) total += _lastTempAnalysis.UserTemp.TotalBytes;
        if (IncludeSystemTemp) total += _lastTempAnalysis.SystemTemp.TotalBytes;
        if (IncludeD3dShaderCache) total += _lastTempAnalysis.D3dShaderCache.TotalBytes;
        if (IncludeRecycleBin) total += _lastTempAnalysis.RecycleBin.TotalBytes;

        SelectedReclaimableBytes = total;
    }

    [RelayCommand]
    private async Task DiagnoseNetworkAsync()
    {
        if (IsNetworkBusy) return;

        IsNetworkBusy = true;
        NetworkActionStatus = "Замер отклика DNS-серверов (Cloudflare, Google, Quad9, Yandex)...";
        try
        {
            var diag = await _networkOptimizerTool.DiagnoseAsync();
            string bestServer = await _vpnService.RefreshNearestServerAsync();
            NetworkAdapterInfo = string.IsNullOrWhiteSpace(diag.AdapterName)
                ? "Активный адаптер не найден"
                : $"Адаптер: {diag.AdapterName} • Текущий DNS: {diag.CurrentDns} • Сервер проверки Loss/Jitter: {bestServer}";

            NetworkBenchmarkSummary = string.Join("  |  ",
                diag.Candidates.Select(c => c.PingMs.HasValue
                    ? $"{c.Name} ({c.PrimaryIp}): {c.PingMs} мс"
                    : $"{c.Name} ({c.PrimaryIp}): таймаут"));

            NetworkActionStatus = diag.FastestCandidate is { } best
                ? $"Рекомендуемый ближайший DNS: {best.Name} ({best.PrimaryIp}, {best.PingMs} мс)."
                : "Не удалось измерить отклик DNS-серверов.";
        }
        finally
        {
            IsNetworkBusy = false;
        }
    }

    [RelayCommand]
    private async Task OptimizeNetworkAsync()
    {
        if (IsNetworkBusy) return;

        IsNetworkBusy = true;
        NetworkActionStatus = "Применение быстрого DNS, очистка кэша и настройка TCP...";
        try
        {
            var result = await _networkOptimizerTool.ExecuteAsync();
            _ = await _vpnService.RefreshNearestServerAsync();
            NetworkActionStatus = result.Message;

            var diag = await _networkOptimizerTool.DiagnoseAsync();
            if (!string.IsNullOrWhiteSpace(diag.AdapterName))
            {
                NetworkAdapterInfo = $"Адаптер: {diag.AdapterName} • Текущий DNS: {diag.CurrentDns}";
            }
        }
        finally
        {
            IsNetworkBusy = false;
        }
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        if (IsNetworkBusy) return;

        IsNetworkBusy = true;
        try
        {
            var result = await _networkOptimizerTool.FlushDnsAsync();
            NetworkActionStatus = result.Message;
        }
        finally
        {
            IsNetworkBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetDnsToDhcpAsync()
    {
        if (IsNetworkBusy) return;

        IsNetworkBusy = true;
        try
        {
            var result = await _networkOptimizerTool.ResetDnsToDhcpAsync();
            NetworkActionStatus = result.Message;

            var diag = await _networkOptimizerTool.DiagnoseAsync();
            if (!string.IsNullOrWhiteSpace(diag.AdapterName))
            {
                NetworkAdapterInfo = $"Адаптер: {diag.AdapterName} • Текущий DNS: {diag.CurrentDns}";
            }
        }
        finally
        {
            IsNetworkBusy = false;
        }
    }
}
