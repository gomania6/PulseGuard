using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.Optimization;

namespace PulseGuard.ViewModels;

public partial class OptimizationViewModel : ObservableObject
{
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly TempFilesCleanerTool _tempCleanerTool;
    private readonly GameModeTool _gameModeTool;

    private TempAnalysisResult? _lastTempAnalysis;
    private bool _isUpdatingGameMode;

    [ObservableProperty]
    private string _title = "Оптимизация";

    // Card 1: Safe RAM Cleaner
    [ObservableProperty]
    private string _ramStatusText = "Занято: — ГБ • Свободно: — ГБ";

    [ObservableProperty]
    private string _ramActionStatus = "Вызывает Win32 EmptyWorkingSet только для пользовательских процессов.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCleanRam))]
    private bool _isCleaningRam;

    // Card 2: Temporary Files Cleaner
    [ObservableProperty]
    private bool _includeUserTemp = true;

    [ObservableProperty]
    private bool _includeSystemTemp = true;

    [ObservableProperty]
    private bool _includeRecycleBin = false;

    [ObservableProperty]
    private string _userTempInfo = "Не проверено";

    [ObservableProperty]
    private string _systemTempInfo = "Не проверено";

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

    // Card 3: Game Mode
    [ObservableProperty]
    private bool _isGameModeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleGameMode))]
    private bool _isTogglingGameMode;

    [ObservableProperty]
    private string _gameModeStatus = "Игровой режим отключён. Исходная схема питания и приоритеты не изменены.";

    public bool CanCleanRam => !IsCleaningRam;
    public bool CanAnalyzeTemp => !IsAnalyzingTemp && !IsCleaningTemp;
    public bool CanCleanTemp => !IsAnalyzingTemp && !IsCleaningTemp && HasTempAnalysis && SelectedReclaimableBytes > 0;
    public bool CanToggleGameMode => !IsTogglingGameMode;

    public OptimizationViewModel(
        SafeRamCleanerTool ramCleanerTool,
        TempFilesCleanerTool tempCleanerTool,
        GameModeTool gameModeTool)
    {
        _ramCleanerTool = ramCleanerTool;
        _tempCleanerTool = tempCleanerTool;
        _gameModeTool = gameModeTool;

        RefreshRamStatus();
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

    partial void OnIncludeRecycleBinChanged(bool value)
    {
        _tempCleanerTool.IncludeRecycleBin = value;
        RecalculateSelectedTempBytes();
    }

    partial void OnIsGameModeEnabledChanged(bool value)
    {
        if (_isUpdatingGameMode)
        {
            return;
        }

        _ = ApplyGameModeAsync(value);
    }

    [RelayCommand]
    private async Task CleanRamAsync()
    {
        if (IsCleaningRam)
        {
            return;
        }

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
        if (IsAnalyzingTemp || IsCleaningTemp)
        {
            return;
        }

        IsAnalyzingTemp = true;
        TempActionStatus = "Сканирование временных каталогов и Корзины...";

        try
        {
            _lastTempAnalysis = await _tempCleanerTool.AnalyzeAsync();
            UserTempInfo = $"{_lastTempAnalysis.UserTemp.FileCount} файлов • {TempFilesCleanerTool.FormatSize(_lastTempAnalysis.UserTemp.TotalBytes)}";
            SystemTempInfo = $"{_lastTempAnalysis.SystemTemp.FileCount} файлов • {TempFilesCleanerTool.FormatSize(_lastTempAnalysis.SystemTemp.TotalBytes)}";
            RecycleBinInfo = $"{_lastTempAnalysis.RecycleBin.FileCount} объектов • {TempFilesCleanerTool.FormatSize(_lastTempAnalysis.RecycleBin.TotalBytes)}";

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
        if (!CanCleanTemp)
        {
            return;
        }

        IsCleaningTemp = true;
        TempActionStatus = "Безопасное удаление выбранных файлов...";

        try
        {
            _tempCleanerTool.IncludeUserTemp = IncludeUserTemp;
            _tempCleanerTool.IncludeSystemTemp = IncludeSystemTemp;
            _tempCleanerTool.IncludeRecycleBin = IncludeRecycleBin;

            var result = await _tempCleanerTool.ExecuteAsync();
            TempActionStatus = result.Message;

            // Refresh analysis after cleanup so counts reflect remaining locked files.
            _lastTempAnalysis = await _tempCleanerTool.AnalyzeAsync();
            UserTempInfo = $"{_lastTempAnalysis.UserTemp.FileCount} файлов • {TempFilesCleanerTool.FormatSize(_lastTempAnalysis.UserTemp.TotalBytes)}";
            SystemTempInfo = $"{_lastTempAnalysis.SystemTemp.FileCount} файлов • {TempFilesCleanerTool.FormatSize(_lastTempAnalysis.SystemTemp.TotalBytes)}";
            RecycleBinInfo = $"{_lastTempAnalysis.RecycleBin.FileCount} объектов • {TempFilesCleanerTool.FormatSize(_lastTempAnalysis.RecycleBin.TotalBytes)}";
            RecalculateSelectedTempBytes();
        }
        finally
        {
            IsCleaningTemp = false;
        }
    }

    private async Task ApplyGameModeAsync(bool enable)
    {
        IsTogglingGameMode = true;
        GameModeStatus = enable ? "Активация игрового режима..." : "Восстановление исходных параметров...";

        try
        {
            var result = enable
                ? await _gameModeTool.EnableAsync()
                : await _gameModeTool.DisableAsync();

            GameModeStatus = result.Message;
        }
        catch (Exception ex)
        {
            _isUpdatingGameMode = true;
            IsGameModeEnabled = _gameModeTool.IsActive;
            _isUpdatingGameMode = false;
            GameModeStatus = $"Ошибка переключения игрового режима: {ex.Message}";
        }
        finally
        {
            IsTogglingGameMode = false;
        }
    }

    private void RecalculateSelectedTempBytes()
    {
        if (_lastTempAnalysis is null)
        {
            SelectedReclaimableBytes = 0;
            return;
        }

        long total = 0;
        if (IncludeUserTemp)
        {
            total += _lastTempAnalysis.UserTemp.TotalBytes;
        }

        if (IncludeSystemTemp)
        {
            total += _lastTempAnalysis.SystemTemp.TotalBytes;
        }

        if (IncludeRecycleBin)
        {
            total += _lastTempAnalysis.RecycleBin.TotalBytes;
        }

        SelectedReclaimableBytes = total;
    }
}
