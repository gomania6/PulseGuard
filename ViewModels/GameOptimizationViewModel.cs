using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Optimization.Models;
using PulseGuard.Modules.Playtime;
using PulseGuard.Modules.Playtime.Models;
using PulseGuard.Modules.Settings;

namespace PulseGuard.ViewModels;

public partial class GameOptimizationViewModel : ObservableObject
{
    private readonly GameModeTool _gameModeTool;
    private readonly SettingsService _settingsService;
    private readonly PlaytimeTrackerModule _playtimeTrackerModule;
    private readonly OptimizationProfileService _profileService;
    private readonly RegistryBackupService _backupService;
    private readonly AntiCheatDetectorService _antiCheatDetector;

    private bool _isUpdatingGameMode;
    private bool _isSyncingSettings;

    [ObservableProperty]
    private string _title = "Игровые оптимизации и античиты";

    // Anti-Cheat & Registry Backup Card
    [ObservableProperty]
    private string _antiCheatSummary = "Проверка служб античитов...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHvciOptionEnabled))]
    [NotifyPropertyChangedFor(nameof(HvciOptionTooltip))]
    private bool _isVanguardDetected;

    [ObservableProperty]
    private string _backupStatusText = "Резервная копия реестра: пусто";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyProfile))]
    [NotifyPropertyChangedFor(nameof(CanRollback))]
    private bool _isApplyingProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEsportsProfileActive))]
    [NotifyPropertyChangedFor(nameof(IsMaxFpsProfileActive))]
    [NotifyPropertyChangedFor(nameof(IsNormalProfileActive))]
    private OptimizationProfileType _activeProfile = OptimizationProfileType.NormalReset;

    [ObservableProperty]
    private string _profileActionStatus = "Выберите профиль оптимизации. Перед любым изменением реестра создаётся бэкап в %AppData%\\PulseGuard\\registry_backup.json.";

    [ObservableProperty]
    private bool _stopSysMainAndWSearchInMaxFps;

    [ObservableProperty]
    private bool _disableHvciInMaxFps;

    public ObservableCollection<WhitelistSuspendItemViewModel> WhitelistSuspendApps { get; } = new();

    public bool IsEsportsProfileActive => ActiveProfile == OptimizationProfileType.EsportsAntiCheat;
    public bool IsMaxFpsProfileActive => ActiveProfile == OptimizationProfileType.MaxFpsSinglePlayer;
    public bool IsNormalProfileActive => ActiveProfile == OptimizationProfileType.NormalReset;
    public bool IsHvciOptionEnabled => !IsVanguardDetected;
    public string HvciOptionTooltip => IsVanguardDetected
        ? AntiCheatDetectorService.VanguardRestrictionTooltip
        : "Отключение HVCI (Memory Integrity) может повысить FPS, но снижает защиту ядра и требует перезагрузки.";

    public bool CanApplyProfile => !IsApplyingProfile;
    public bool CanRollback => !IsApplyingProfile;

    // Game Mode
    [ObservableProperty]
    private bool _isGameModeEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleGameMode))]
    private bool _isTogglingGameMode;

    [ObservableProperty]
    private string _gameModeStatus = "Игровой режим отключён. Исходная схема питания и приоритеты не изменены.";

    [ObservableProperty]
    private bool _isAutoGameModeEnabled = true;

    [ObservableProperty]
    private bool _detectFullscreenApps = true;

    [ObservableProperty]
    private string _newGameProcessName = string.Empty;

    public ObservableCollection<string> GameProcessNames { get; } = new();
    public bool CanToggleGameMode => !IsTogglingGameMode;

    // Playtime Tracker
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Brush CalendarEmptyBrush = CreateFrozenBrush(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly Brush CalendarLevel1Brush = CreateFrozenBrush(0x38, 0x38, 0xBD, 0xF8);
    private static readonly Brush CalendarLevel2Brush = CreateFrozenBrush(0x66, 0x38, 0xBD, 0xF8);
    private static readonly Brush CalendarLevel3Brush = CreateFrozenBrush(0x99, 0x00, 0xB2, 0xFF);
    private static readonly Brush CalendarLevel4Brush = CreateFrozenBrush(0xCC, 0x00, 0xE5, 0xA0);
    private static readonly Brush CalendarBorderDefaultBrush = CreateFrozenBrush(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly Brush CalendarBorderTodayBrush = CreateFrozenBrush(0xAA, 0x38, 0xBD, 0xF8);
    private static readonly Brush CalendarBorderSelectedBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0xFF);

    private PlaytimeSnapshot _latestPlaytimeSnapshot = new(
        [],
        new Dictionary<string, IReadOnlyList<PlaytimeDailyGameSnapshot>>(StringComparer.Ordinal),
        null,
        TimeSpan.Zero,
        false);
    private DateTime _currentCalendarMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private DateTime _selectedCalendarDate = DateTime.Today;

    [ObservableProperty]
    private string _continuousSessionText = "Сессия за ПК: 0 сек";

    [ObservableProperty]
    private bool _hasPlaytimeGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummaryToday))]
    [NotifyPropertyChangedFor(nameof(IsSummaryYesterday))]
    [NotifyPropertyChangedFor(nameof(IsSummaryLast7Days))]
    private PlaytimeSummaryPeriod _selectedSummaryPeriod = PlaytimeSummaryPeriod.Today;

    [ObservableProperty]
    private string _dailySummaryTotalText = "Всего: 0 мин";

    [ObservableProperty]
    private bool _hasDailySummaryGames;

    [ObservableProperty]
    private string _calendarMonthTitle = string.Empty;

    [ObservableProperty]
    private string _selectedCalendarDateTitle = string.Empty;

    [ObservableProperty]
    private bool _hasSelectedCalendarGames;

    public bool IsSummaryToday => SelectedSummaryPeriod == PlaytimeSummaryPeriod.Today;
    public bool IsSummaryYesterday => SelectedSummaryPeriod == PlaytimeSummaryPeriod.Yesterday;
    public bool IsSummaryLast7Days => SelectedSummaryPeriod == PlaytimeSummaryPeriod.Last7Days;

    public ObservableCollection<PlaytimeItemViewModel> PlaytimeGames { get; } = new();
    public ObservableCollection<DailySummaryGameItemViewModel> DailySummaryGames { get; } = new();
    public ObservableCollection<PlaytimeCalendarDayViewModel> CalendarDays { get; } = new();
    public ObservableCollection<DailySummaryGameItemViewModel> SelectedCalendarGames { get; } = new();

    public GameOptimizationViewModel(
        GameModeTool gameModeTool,
        SettingsService settingsService,
        PlaytimeTrackerModule playtimeTrackerModule,
        OptimizationProfileService profileService,
        RegistryBackupService backupService,
        AntiCheatDetectorService antiCheatDetector)
    {
        _gameModeTool = gameModeTool;
        _settingsService = settingsService;
        _playtimeTrackerModule = playtimeTrackerModule;
        _profileService = profileService;
        _backupService = backupService;
        _antiCheatDetector = antiCheatDetector;

        _gameModeTool.StateChanged += OnGameModeToolStateChanged;
        _playtimeTrackerModule.StatsUpdated += OnPlaytimeStatsUpdated;
        _backupService.BackupChanged += OnBackupChanged;
        _antiCheatDetector.StatusChanged += OnAntiCheatStatusChanged;

        LoadSettings();
        ApplyAntiCheatStatus(_antiCheatDetector.Refresh());
        UpdateBackupStatus();
        SyncGameModeState(_gameModeTool.IsActive, _gameModeTool.ActiveGameName);
        ApplyPlaytimeSnapshot(_playtimeTrackerModule.LatestSnapshot);
    }

    private void OnBackupChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke(UpdateBackupStatus);
    }

    private void OnAntiCheatStatusChanged(AntiCheatStatus status)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => ApplyAntiCheatStatus(status));
    }

    private void ApplyAntiCheatStatus(AntiCheatStatus status)
    {
        AntiCheatSummary = status.SummaryText;
        IsVanguardDetected = status.IsVanguardDetected;

        if (status.IsVanguardDetected && DisableHvciInMaxFps)
        {
            _isSyncingSettings = true;
            DisableHvciInMaxFps = false;
            _isSyncingSettings = false;
            _settingsService.Update(s => s.DisableHvciInMaxFps = false);
        }
    }

    private void UpdateBackupStatus()
    {
        int count = _backupService.BackupCount;
        BackupStatusText = count > 0
            ? $"В бэкапе сохранено ключей реестра: {count}"
            : "Резервная копия реестра: изменений пока нет";
    }

    private void LoadSettings()
    {
        _isSyncingSettings = true;
        try
        {
            var s = _settingsService.Current;
            IsAutoGameModeEnabled = s.IsAutoGameModeEnabled;
            DetectFullscreenApps = s.DetectFullscreenApps;
            GameProcessNames.Clear();
            foreach (var name in s.GameProcessNames)
            {
                GameProcessNames.Add(name);
            }

            ActiveProfile = s.ActiveOptimizationProfile;
            StopSysMainAndWSearchInMaxFps = s.StopSysMainAndWSearchInMaxFps;
            DisableHvciInMaxFps = !IsVanguardDetected && s.DisableHvciInMaxFps;

            var selectedSet = new HashSet<string>(s.FreezeWhitelistApps, StringComparer.OrdinalIgnoreCase);
            WhitelistSuspendApps.Clear();
            foreach (var exe in OptimizationProfileService.AllowedSuspendWhitelist)
            {
                WhitelistSuspendApps.Add(new WhitelistSuspendItemViewModel(
                    exe,
                    selectedSet.Contains(exe),
                    OnWhitelistSelectionChanged));
            }
        }
        finally
        {
            _isSyncingSettings = false;
        }
    }

    private void OnWhitelistSelectionChanged()
    {
        if (_isSyncingSettings) return;
        var selected = WhitelistSuspendApps
            .Where(x => x.IsSelected)
            .Select(x => x.ExecutableName)
            .ToList();
        _settingsService.Update(s => s.FreezeWhitelistApps = selected);
    }

    partial void OnStopSysMainAndWSearchInMaxFpsChanged(bool value)
    {
        if (_isSyncingSettings) return;
        _settingsService.Update(s => s.StopSysMainAndWSearchInMaxFps = value);
    }

    partial void OnDisableHvciInMaxFpsChanged(bool value)
    {
        if (_isSyncingSettings) return;
        if (IsVanguardDetected && value)
        {
            _isSyncingSettings = true;
            DisableHvciInMaxFps = false;
            _isSyncingSettings = false;
            return;
        }

        _settingsService.Update(s => s.DisableHvciInMaxFps = value);
    }

    partial void OnIsAutoGameModeEnabledChanged(bool value)
    {
        if (_isSyncingSettings) return;
        _settingsService.Update(s => s.IsAutoGameModeEnabled = value);
    }

    partial void OnDetectFullscreenAppsChanged(bool value)
    {
        if (_isSyncingSettings) return;
        _settingsService.Update(s => s.DetectFullscreenApps = value);
    }

    partial void OnIsGameModeEnabledChanged(bool value)
    {
        if (_isUpdatingGameMode) return;
        _ = ApplyGameModeAsync(value);
    }

    [RelayCommand]
    private void RefreshAntiCheat()
    {
        ApplyAntiCheatStatus(_antiCheatDetector.Refresh());
    }

    [RelayCommand]
    private Task ApplyEsportsProfileAsync() => ApplyProfileInternalAsync(OptimizationProfileType.EsportsAntiCheat);

    [RelayCommand]
    private Task ApplyMaxFpsProfileAsync() => ApplyProfileInternalAsync(OptimizationProfileType.MaxFpsSinglePlayer);

    [RelayCommand]
    private Task ApplyNormalResetProfileAsync() => ApplyProfileInternalAsync(OptimizationProfileType.NormalReset);

    [RelayCommand]
    private async Task FullRollbackAsync()
    {
        if (IsApplyingProfile) return;

        IsApplyingProfile = true;
        ProfileActionStatus = "Выполняется полный откат реестра, схемы питания и разморозка процессов...";
        try
        {
            if (_gameModeTool.IsActive)
            {
                await _gameModeTool.DisableAsync();
            }

            var (_, message) = await _profileService.FullRollbackAsync();
            ActiveProfile = _profileService.ActiveProfile;
            ProfileActionStatus = message;
            UpdateBackupStatus();
            ApplyAntiCheatStatus(_antiCheatDetector.Refresh());
        }
        catch (Exception ex)
        {
            ProfileActionStatus = $"Ошибка полного отката: {ex.Message}";
        }
        finally
        {
            IsApplyingProfile = false;
        }
    }

    private async Task ApplyProfileInternalAsync(OptimizationProfileType profile)
    {
        if (IsApplyingProfile) return;

        IsApplyingProfile = true;
        ProfileActionStatus = "Применение профиля оптимизации с резервным копированием...";
        try
        {
            var (_, message) = await _profileService.ApplyProfileAsync(profile);
            ActiveProfile = _profileService.ActiveProfile;
            ProfileActionStatus = message;
            UpdateBackupStatus();
            ApplyAntiCheatStatus(_antiCheatDetector.Refresh());
        }
        catch (Exception ex)
        {
            ProfileActionStatus = $"Ошибка применения профиля: {ex.Message}";
        }
        finally
        {
            IsApplyingProfile = false;
        }
    }

    [RelayCommand]
    private void AddGameProcess()
    {
        var raw = (NewGameProcessName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (!raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            raw += ".exe";
        }

        if (!GameProcessNames.Any(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase)))
        {
            GameProcessNames.Add(raw);
            _settingsService.Update(s => s.GameProcessNames = [.. GameProcessNames]);
        }

        NewGameProcessName = string.Empty;
    }

    [RelayCommand]
    private void RemoveGameProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        if (GameProcessNames.Remove(processName))
        {
            _settingsService.Update(s => s.GameProcessNames = [.. GameProcessNames]);
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

    private void OnGameModeToolStateChanged(bool isActive, string? gameName)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => SyncGameModeState(isActive, gameName));
    }

    private void SyncGameModeState(bool isActive, string? gameName)
    {
        _isUpdatingGameMode = true;
        IsGameModeEnabled = isActive;
        _isUpdatingGameMode = false;

        GameModeStatus = isActive
            ? string.IsNullOrWhiteSpace(gameName)
                ? "Игровой режим активен: схема «Максимальная производительность»."
                : $"Игровой режим активен (авто: {gameName})."
            : "Игровой режим отключён. Исходная схема питания и приоритеты не изменены.";
    }

    private void OnPlaytimeStatsUpdated(PlaytimeSnapshot snapshot)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => ApplyPlaytimeSnapshot(snapshot));
    }

    private void ApplyPlaytimeSnapshot(PlaytimeSnapshot snapshot)
    {
        _latestPlaytimeSnapshot = snapshot;

        string formattedSession = PlaytimeTrackerModule.FormatDurationRussian(snapshot.ContinuousSessionDuration);
        ContinuousSessionText = snapshot.IsBreakReminderDue
            ? $"Сессия за ПК: {formattedSession} (пора сделать перерыв)"
            : $"Сессия за ПК: {formattedSession}";

        PlaytimeGames.Clear();
        foreach (var game in snapshot.Games)
        {
            PlaytimeGames.Add(new PlaytimeItemViewModel(game));
        }

        HasPlaytimeGames = PlaytimeGames.Count > 0;
        RefreshDailySummary();
        RefreshCalendar();
        RefreshSelectedCalendarDayDetails();
    }

    [RelayCommand]
    private void SelectSummaryToday() => SetSummaryPeriod(PlaytimeSummaryPeriod.Today);

    [RelayCommand]
    private void SelectSummaryYesterday() => SetSummaryPeriod(PlaytimeSummaryPeriod.Yesterday);

    [RelayCommand]
    private void SelectSummaryLast7Days() => SetSummaryPeriod(PlaytimeSummaryPeriod.Last7Days);

    private void SetSummaryPeriod(PlaytimeSummaryPeriod period)
    {
        if (SelectedSummaryPeriod == period)
        {
            return;
        }

        SelectedSummaryPeriod = period;
        RefreshDailySummary();
    }

    [RelayCommand]
    private void PreviousCalendarMonth()
    {
        _currentCalendarMonth = _currentCalendarMonth.AddMonths(-1);
        RefreshCalendar();
    }

    [RelayCommand]
    private void NextCalendarMonth()
    {
        _currentCalendarMonth = _currentCalendarMonth.AddMonths(1);
        RefreshCalendar();
    }

    [RelayCommand]
    private void SelectCalendarDay(PlaytimeCalendarDayViewModel? day)
    {
        if (day is null || day.IsPlaceholder)
        {
            return;
        }

        _selectedCalendarDate = day.Date;
        RefreshCalendar();
        RefreshSelectedCalendarDayDetails();
    }

    private void RefreshDailySummary()
    {
        DailySummaryGames.Clear();
        var today = DateTime.Today;
        var aggregated = new Dictionary<string, (string DisplayName, long Seconds)>(StringComparer.OrdinalIgnoreCase);

        int daysToInclude = SelectedSummaryPeriod switch
        {
            PlaytimeSummaryPeriod.Today => 1,
            PlaytimeSummaryPeriod.Yesterday => 1,
            PlaytimeSummaryPeriod.Last7Days => 7,
            _ => 1
        };

        DateTime startDate = SelectedSummaryPeriod == PlaytimeSummaryPeriod.Yesterday
            ? today.AddDays(-1)
            : today.AddDays(-(daysToInclude - 1));

        for (int i = 0; i < daysToInclude; i++)
        {
            string key = startDate.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!_latestPlaytimeSnapshot.DailyHistory.TryGetValue(key, out var entries))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (aggregated.TryGetValue(entry.ProcessName, out var current))
                {
                    aggregated[entry.ProcessName] = (current.DisplayName, current.Seconds + entry.SecondsPlayed);
                }
                else
                {
                    aggregated[entry.ProcessName] = (entry.DisplayName, entry.SecondsPlayed);
                }
            }
        }

        long totalSeconds = 0;
        foreach (var (processName, info) in aggregated.OrderByDescending(kv => kv.Value.Seconds))
        {
            totalSeconds += info.Seconds;
            DailySummaryGames.Add(new DailySummaryGameItemViewModel(
                info.DisplayName,
                processName,
                PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(info.Seconds))));
        }

        HasDailySummaryGames = DailySummaryGames.Count > 0;
        DailySummaryTotalText = totalSeconds > 0
            ? $"Итого: {PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(totalSeconds))}"
            : "Итого: 0 мин";
    }

    private void RefreshCalendar()
    {
        string monthName = RussianCulture.DateTimeFormat.GetMonthName(_currentCalendarMonth.Month);
        if (!string.IsNullOrEmpty(monthName))
        {
            monthName = char.ToUpper(monthName[0], RussianCulture) + monthName[1..];
        }
        CalendarMonthTitle = $"{monthName} {_currentCalendarMonth.Year}";

        CalendarDays.Clear();

        int firstDayOffset = ((int)_currentCalendarMonth.DayOfWeek + 6) % 7;
        for (int i = 0; i < firstDayOffset; i++)
        {
            CalendarDays.Add(PlaytimeCalendarDayViewModel.CreatePlaceholder());
        }

        int daysInMonth = DateTime.DaysInMonth(_currentCalendarMonth.Year, _currentCalendarMonth.Month);
        DateTime today = DateTime.Today;

        for (int day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(_currentCalendarMonth.Year, _currentCalendarMonth.Month, day);
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            long daySeconds = 0;
            if (_latestPlaytimeSnapshot.DailyHistory.TryGetValue(key, out var entries))
            {
                foreach (var entry in entries)
                {
                    daySeconds += entry.SecondsPlayed;
                }
            }

            bool isToday = date == today;
            bool isSelected = date == _selectedCalendarDate;
            Brush bgBrush = GetHeatmapBrush(daySeconds);
            Brush borderBrush = isSelected
                ? CalendarBorderSelectedBrush
                : isToday
                    ? CalendarBorderTodayBrush
                    : CalendarBorderDefaultBrush;

            string tooltip = daySeconds > 0
                ? $"{date.ToString("d MMMM yyyy", RussianCulture)}: {PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(daySeconds))}"
                : $"{date.ToString("d MMMM yyyy", RussianCulture)}: нет игровых сессий";

            CalendarDays.Add(new PlaytimeCalendarDayViewModel(
                date,
                day.ToString(CultureInfo.InvariantCulture),
                daySeconds,
                isPlaceholder: false,
                isToday,
                isSelected,
                bgBrush,
                borderBrush,
                tooltip));
        }

        // Always pad to 6 rows (42 cells) so the calendar height stays constant across months.
        while (CalendarDays.Count < 42)
        {
            CalendarDays.Add(PlaytimeCalendarDayViewModel.CreatePlaceholder());
        }
    }

    private void RefreshSelectedCalendarDayDetails()
    {
        SelectedCalendarGames.Clear();
        string key = _selectedCalendarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string dateText = _selectedCalendarDate.ToString("d MMMM yyyy", RussianCulture);

        if (_latestPlaytimeSnapshot.DailyHistory.TryGetValue(key, out var entries) && entries.Count > 0)
        {
            long totalSeconds = 0;
            foreach (var entry in entries)
            {
                totalSeconds += entry.SecondsPlayed;
                SelectedCalendarGames.Add(new DailySummaryGameItemViewModel(
                    entry.DisplayName,
                    entry.ProcessName,
                    PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(entry.SecondsPlayed))));
            }

            HasSelectedCalendarGames = true;
            SelectedCalendarDateTitle = $"{dateText} — всего {PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(totalSeconds))}";
        }
        else
        {
            HasSelectedCalendarGames = false;
            SelectedCalendarDateTitle = $"{dateText} — игровых сессий не было";
        }
    }

    private static Brush GetHeatmapBrush(long seconds)
    {
        if (seconds <= 0) return CalendarEmptyBrush;
        if (seconds < 3600) return CalendarLevel1Brush;
        if (seconds < 3 * 3600) return CalendarLevel2Brush;
        if (seconds < 6 * 3600) return CalendarLevel3Brush;
        return CalendarLevel4Brush;
    }

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

public enum PlaytimeSummaryPeriod
{
    Today,
    Yesterday,
    Last7Days
}

public sealed record DailySummaryGameItemViewModel(
    string DisplayName,
    string ProcessName,
    string DurationText);

public sealed class PlaytimeCalendarDayViewModel
{
    public DateTime Date { get; }
    public string DayNumberText { get; }
    public long TotalSeconds { get; }
    public bool IsPlaceholder { get; }
    public bool IsToday { get; }
    public bool IsSelected { get; }
    public Brush BackgroundBrush { get; }
    public Brush BorderBrush { get; }
    public string TooltipText { get; }
    public bool IsInteractive => !IsPlaceholder;
    public bool HasActivity => TotalSeconds > 0;

    public PlaytimeCalendarDayViewModel(
        DateTime date,
        string dayNumberText,
        long totalSeconds,
        bool isPlaceholder,
        bool isToday,
        bool isSelected,
        Brush backgroundBrush,
        Brush borderBrush,
        string tooltipText)
    {
        Date = date;
        DayNumberText = dayNumberText;
        TotalSeconds = totalSeconds;
        IsPlaceholder = isPlaceholder;
        IsToday = isToday;
        IsSelected = isSelected;
        BackgroundBrush = backgroundBrush;
        BorderBrush = borderBrush;
        TooltipText = tooltipText;
    }

    public static PlaytimeCalendarDayViewModel CreatePlaceholder() =>
        new(
            DateTime.MinValue,
            string.Empty,
            0,
            isPlaceholder: true,
            isToday: false,
            isSelected: false,
            Brushes.Transparent,
            Brushes.Transparent,
            string.Empty);
}

public sealed partial class WhitelistSuspendItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public string ExecutableName { get; }

    [ObservableProperty]
    private bool _isSelected;

    public WhitelistSuspendItemViewModel(string executableName, bool isSelected, Action onChanged)
    {
        ExecutableName = executableName;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

public sealed class PlaytimeItemViewModel
{
    public string ProcessName { get; }
    public string TodayText { get; }
    public string WeekText { get; }
    public string TotalText { get; }
    public string LastPlayedText { get; }

    public PlaytimeItemViewModel(PlaytimeGameSnapshot snapshot)
    {
        ProcessName = snapshot.ProcessName;
        TodayText = $"Сегодня: {PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(snapshot.TodaySeconds))}";
        WeekText = $"Неделя: {PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(snapshot.WeekSeconds))}";
        TotalText = $"Всего: {PlaytimeTrackerModule.FormatDurationRussian(TimeSpan.FromSeconds(snapshot.TotalSeconds))}";
        var localLast = snapshot.LastPlayedUtc.ToLocalTime();
        LastPlayedText = $"Последний запуск: {localLast:dd.MM.yyyy HH:mm}";
    }
}

