using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Playtime.Models;
using PulseGuard.Modules.Settings;
using PulseGuard.Services;

namespace PulseGuard.Modules.Playtime;

/// <summary>
/// Tracks game playtime using AutoGameModeService's detector, persists stats atomically to
/// %AppData%\PulseGuard\playtime_stats.json every minute and on game close, and monitors
/// continuous PC session duration for eye-break reminders.
/// </summary>
public sealed class PlaytimeTrackerModule : IMonitorModule
{
    private const int PollIntervalSeconds = 3;
    private const int SaveIntervalSeconds = 30;
    private const int BreakResetIdleMinutes = 5;
    private static readonly TimeSpan NotificationRepeatInterval = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly AutoGameModeService _autoGameModeService;
    private readonly GameModeTool _gameModeTool;
    private readonly SettingsService _settingsService;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly string _statsFilePath;
    private readonly string _backupFilePath;
    private readonly object _syncLock = new();

    private PlaytimeStatsFile _stats = new();
    private CancellationTokenSource? _loopCts;
    private Task? _watchTask;

    private string? _activeGameName;
    private DateTime _lastTickUtc;
    private DateTime _lastSaveUtc;
    private DateTime _sessionStartUtc;
    private DateTime _lastBreakNotificationUtc = DateTime.MinValue;
    private bool _hasUnsavedChanges;
    private bool _loadFailed;
    private bool _disposed;

    public string Id => "playtime-tracker";
    public string Name => "Трекер игрового времени";
    public string Description => "Учитывает время в играх и напоминает о перерыве для глаз.";
    public bool IsEnabled { get; set; } = true;

    public PlaytimeSnapshot LatestSnapshot { get; private set; } = new(
        [],
        new Dictionary<string, IReadOnlyList<PlaytimeDailyGameSnapshot>>(StringComparer.Ordinal),
        null,
        TimeSpan.Zero,
        false);

    public event Action<PlaytimeSnapshot>? StatsUpdated;

    public PlaytimeTrackerModule(
        AutoGameModeService autoGameModeService,
        GameModeTool gameModeTool,
        SettingsService settingsService,
        TrayNotificationService trayNotificationService)
    {
        _autoGameModeService = autoGameModeService;
        _gameModeTool = gameModeTool;
        _settingsService = settingsService;
        _trayNotificationService = trayNotificationService;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _statsFilePath = Path.Combine(appData, "PulseGuard", "playtime_stats.json");
        _backupFilePath = _statsFilePath + ".bak";

        _lastTickUtc = DateTime.UtcNow;
        _lastSaveUtc = _lastTickUtc;
        _sessionStartUtc = _lastTickUtc;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncLock)
        {
            _stats = LoadFromDiskLocked();
            NormalizePeriodsLocked(DateTime.Now);
            LatestSnapshot = BuildSnapshotLocked(DateTime.UtcNow);
        }

        StatsUpdated?.Invoke(LatestSnapshot);
        StartLoop();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        Tick();
        return Task.CompletedTask;
    }

    private void StartLoop()
    {
        lock (_syncLock)
        {
            if (_disposed || _loopCts is not null)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _watchTask = Task.Run(() => RunLoopAsync(token), token);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollIntervalSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled || _disposed)
                {
                    continue;
                }

                Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick()
    {
        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;

        string? detectedGame = null;
        if (_gameModeTool.IsActive && !string.IsNullOrWhiteSpace(_gameModeTool.ActiveGameName))
        {
            detectedGame = _gameModeTool.ActiveGameName;
        }
        else
        {
            var settings = _settingsService.Current;
            detectedGame = _autoGameModeService.DetectCurrentGame(settings.DetectFullscreenApps);
        }

        PlaytimeSnapshot snapshot;
        bool shouldNotifyBreak = false;
        TimeSpan sessionDuration;

        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            double deltaSeconds = Math.Clamp((nowUtc - _lastTickUtc).TotalSeconds, 0, PollIntervalSeconds * 3);
            _lastTickUtc = nowUtc;

            // Reset continuous session if user has been idle for >= 5 minutes (took a break).
            if (GetIdleDuration() >= TimeSpan.FromMinutes(BreakResetIdleMinutes))
            {
                _sessionStartUtc = nowUtc;
                _lastBreakNotificationUtc = DateTime.MinValue;
            }

            NormalizePeriodsLocked(nowLocal);

            string? previousGame = _activeGameName;
            bool gameClosed = !string.IsNullOrEmpty(previousGame)
                && !string.Equals(previousGame, detectedGame, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(previousGame))
            {
                long addSec = (long)Math.Round(deltaSeconds);
                if (addSec > 0)
                {
                    AddPlaytimeLocked(previousGame, addSec, nowUtc, nowLocal);
                    _hasUnsavedChanges = true;
                }
            }

            _activeGameName = detectedGame;

            if (gameClosed && _hasUnsavedChanges)
            {
                SaveToDiskLocked(_stats);
                _hasUnsavedChanges = false;
                _lastSaveUtc = nowUtc;
            }
            else if (_hasUnsavedChanges && (nowUtc - _lastSaveUtc).TotalSeconds >= SaveIntervalSeconds)
            {
                SaveToDiskLocked(_stats);
                _hasUnsavedChanges = false;
                _lastSaveUtc = nowUtc;
            }

            sessionDuration = nowUtc - _sessionStartUtc;
            if (sessionDuration < TimeSpan.Zero)
            {
                sessionDuration = TimeSpan.Zero;
            }

            int reminderMinutes = _settingsService.Current.BreakReminderMinutes;
            bool isBreakDue = reminderMinutes > 0 && sessionDuration.TotalMinutes >= reminderMinutes;

            if (isBreakDue && (nowUtc - _lastBreakNotificationUtc) >= NotificationRepeatInterval)
            {
                _lastBreakNotificationUtc = nowUtc;
                shouldNotifyBreak = true;
            }

            snapshot = BuildSnapshotLocked(nowUtc, isBreakDue, sessionDuration);
            LatestSnapshot = snapshot;
        }

        if (shouldNotifyBreak)
        {
            string formatted = FormatDurationRussian(sessionDuration);
            _trayNotificationService.TryShowInfoBalloon(
                "PulseGuard — Перерыв для глаз",
                $"Вы за ПК уже {formatted} — сделайте 5-минутный перерыв для глаз.");
        }

        StatsUpdated?.Invoke(snapshot);
    }

    private void AddPlaytimeLocked(string processName, long seconds, DateTime nowUtc, DateTime nowLocal)
    {
        string todayKey = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string weekKey = GetWeekKey(nowLocal);

        var entry = _stats.Games.FirstOrDefault(g =>
            string.Equals(g.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            entry = new GamePlaytimeEntry
            {
                ProcessName = processName,
                TodayDate = todayKey,
                WeekKey = weekKey
            };
            _stats.Games.Add(entry);
        }

        if (!string.Equals(entry.TodayDate, todayKey, StringComparison.Ordinal))
        {
            entry.TodaySeconds = 0;
            entry.TodayDate = todayKey;
        }

        if (!string.Equals(entry.WeekKey, weekKey, StringComparison.Ordinal))
        {
            entry.WeekSeconds = 0;
            entry.WeekKey = weekKey;
        }

        entry.TodaySeconds += seconds;
        entry.WeekSeconds += seconds;
        entry.TotalSeconds += seconds;
        entry.LastPlayedUtc = nowUtc;

        if (!_stats.DailyHistory.TryGetValue(todayKey, out var dailyList))
        {
            dailyList = [];
            _stats.DailyHistory[todayKey] = dailyList;
        }

        var dailyEntry = dailyList.FirstOrDefault(d =>
            string.Equals(d.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

        if (dailyEntry is null)
        {
            dailyEntry = new DailyGamePlaytimeEntry
            {
                ProcessName = processName,
                DisplayName = FormatDisplayName(processName),
                SecondsPlayed = 0
            };
            dailyList.Add(dailyEntry);
        }
        else if (string.IsNullOrWhiteSpace(dailyEntry.DisplayName))
        {
            dailyEntry.DisplayName = FormatDisplayName(processName);
        }

        dailyEntry.SecondsPlayed += seconds;
    }

    private static string FormatDisplayName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
    }

    private void NormalizePeriodsLocked(DateTime nowLocal)
    {
        string todayKey = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string weekKey = GetWeekKey(nowLocal);

        foreach (var entry in _stats.Games)
        {
            if (string.Equals(entry.TodayDate, todayKey, StringComparison.Ordinal) && entry.TodaySeconds > 0)
            {
                if (!_stats.DailyHistory.TryGetValue(todayKey, out var dailyList))
                {
                    dailyList = [];
                    _stats.DailyHistory[todayKey] = dailyList;
                }

                if (!dailyList.Any(d => string.Equals(d.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    dailyList.Add(new DailyGamePlaytimeEntry
                    {
                        ProcessName = entry.ProcessName,
                        DisplayName = FormatDisplayName(entry.ProcessName),
                        SecondsPlayed = entry.TodaySeconds
                    });
                }
            }

            if (!string.Equals(entry.TodayDate, todayKey, StringComparison.Ordinal))
            {
                entry.TodaySeconds = 0;
                entry.TodayDate = todayKey;
            }

            if (!string.Equals(entry.WeekKey, weekKey, StringComparison.Ordinal))
            {
                entry.WeekSeconds = 0;
                entry.WeekKey = weekKey;
            }
        }
    }

    private PlaytimeSnapshot BuildSnapshotLocked(DateTime nowUtc, bool? isBreakDueOverride = null, TimeSpan? sessionOverride = null)
    {
        var session = sessionOverride ?? (nowUtc - _sessionStartUtc);
        if (session < TimeSpan.Zero)
        {
            session = TimeSpan.Zero;
        }

        int reminderMinutes = _settingsService.Current.BreakReminderMinutes;
        bool isBreakDue = isBreakDueOverride ?? (reminderMinutes > 0 && session.TotalMinutes >= reminderMinutes);

        var games = _stats.Games
            .OrderByDescending(g => g.LastPlayedUtc)
            .ThenByDescending(g => g.TotalSeconds)
            .Select(g => new PlaytimeGameSnapshot(
                g.ProcessName,
                g.TodaySeconds,
                g.WeekSeconds,
                g.TotalSeconds,
                g.LastPlayedUtc))
            .ToList();

        var dailyHistory = new Dictionary<string, IReadOnlyList<PlaytimeDailyGameSnapshot>>(
            _stats.DailyHistory.Count,
            StringComparer.Ordinal);

        foreach (var (dateKey, entries) in _stats.DailyHistory)
        {
            if (entries is null || entries.Count == 0)
            {
                continue;
            }

            var ordered = entries
                .Where(e => e.SecondsPlayed > 0 && !string.IsNullOrWhiteSpace(e.ProcessName))
                .OrderByDescending(e => e.SecondsPlayed)
                .Select(e => new PlaytimeDailyGameSnapshot(
                    e.ProcessName,
                    string.IsNullOrWhiteSpace(e.DisplayName) ? FormatDisplayName(e.ProcessName) : e.DisplayName,
                    e.SecondsPlayed))
                .ToArray();

            if (ordered.Length > 0)
            {
                dailyHistory[dateKey] = ordered;
            }
        }

        return new PlaytimeSnapshot(games, dailyHistory, _activeGameName, session, isBreakDue);
    }

    /// <summary>
    /// Flushes any unsaved playtime changes atomically to disk.
    /// </summary>
    public void FlushSave()
    {
        lock (_syncLock)
        {
            if (_disposed || !_hasUnsavedChanges)
            {
                return;
            }

            SaveToDiskLocked(_stats);
            _hasUnsavedChanges = false;
            _lastSaveUtc = DateTime.UtcNow;
        }
    }

    private PlaytimeStatsFile LoadFromDiskLocked()
    {
        bool primaryExists = File.Exists(_statsFilePath);
        if (primaryExists && TryReadStatsFile(_statsFilePath, out var primaryStats))
        {
            _loadFailed = false;
            return primaryStats;
        }

        bool backupExists = File.Exists(_backupFilePath);
        if (backupExists && TryReadStatsFile(_backupFilePath, out var backupStats))
        {
            _loadFailed = false;
            return backupStats;
        }

        // Never overwrite existing stats files with an empty object if reading/parsing failed.
        _loadFailed = primaryExists || backupExists;
        return new PlaytimeStatsFile();
    }

    private static bool TryReadStatsFile(string path, out PlaytimeStatsFile stats)
    {
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                stats = default!;
                return false;
            }

            var loaded = JsonSerializer.Deserialize<PlaytimeStatsFile>(json, JsonOptions);
            if (loaded is null)
            {
                stats = default!;
                return false;
            }

            loaded.Games ??= [];
            loaded.DailyHistory = loaded.DailyHistory is null
                ? new Dictionary<string, List<DailyGamePlaytimeEntry>>(StringComparer.Ordinal)
                : new Dictionary<string, List<DailyGamePlaytimeEntry>>(loaded.DailyHistory, StringComparer.Ordinal);
            stats = loaded;
            return true;
        }
        catch
        {
            stats = default!;
            return false;
        }
    }

    private void SaveToDiskLocked(PlaytimeStatsFile stats)
    {
        if (_loadFailed)
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_statsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(stats, JsonOptions);
            string tempPath = _statsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(_statsFilePath))
            {
                try
                {
                    File.Replace(tempPath, _statsFilePath, _backupFilePath, ignoreMetadataErrors: true);
                    return;
                }
                catch
                {
                    try
                    {
                        File.Copy(_statsFilePath, _backupFilePath, overwrite: true);
                    }
                    catch
                    {
                        // Best-effort backup before atomic move.
                    }
                }
            }

            File.Move(tempPath, _statsFilePath, overwrite: true);
        }
        catch
        {
            // Ignore disk write errors if %AppData% is temporarily unavailable.
        }
    }

    private static string GetWeekKey(DateTime localTime)
    {
        int year = ISOWeek.GetYear(localTime);
        int week = ISOWeek.GetWeekOfYear(localTime);
        return $"{year}-W{week:D2}";
    }

    private static TimeSpan GetIdleDuration()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii))
            {
                return TimeSpan.Zero;
            }

            uint idleMs = unchecked((uint)Environment.TickCount - lii.dwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public static string FormatDurationRussian(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return $"{Math.Max(0, (int)duration.TotalSeconds)} сек";
        }

        int totalMinutes = (int)duration.TotalMinutes;
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;

        if (hours > 0 && minutes > 0)
        {
            return $"{hours} ч {minutes} мин";
        }

        if (hours > 0)
        {
            return $"{hours} ч";
        }

        return $"{minutes} мин";
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_hasUnsavedChanges)
            {
                SaveToDiskLocked(_stats);
                _hasUnsavedChanges = false;
            }

            try
            {
                _loopCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _loopCts?.Dispose();
            _loopCts = null;
            _watchTask = null;
        }
    }
}

