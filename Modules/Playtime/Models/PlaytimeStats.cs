namespace PulseGuard.Modules.Playtime.Models;

public sealed class GamePlaytimeEntry
{
    public string ProcessName { get; set; } = string.Empty;
    public long TodaySeconds { get; set; }
    public long WeekSeconds { get; set; }
    public long TotalSeconds { get; set; }
    public DateTime LastPlayedUtc { get; set; }
    public string TodayDate { get; set; } = string.Empty;
    public string WeekKey { get; set; } = string.Empty;
}

public sealed class DailyGamePlaytimeEntry
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long SecondsPlayed { get; set; }
}

public sealed class PlaytimeStatsFile
{
    public List<GamePlaytimeEntry> Games { get; set; } = [];
    public Dictionary<string, List<DailyGamePlaytimeEntry>> DailyHistory { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PlaytimeGameSnapshot(
    string ProcessName,
    long TodaySeconds,
    long WeekSeconds,
    long TotalSeconds,
    DateTime LastPlayedUtc);

public sealed record PlaytimeDailyGameSnapshot(
    string ProcessName,
    string DisplayName,
    long SecondsPlayed);

public sealed record PlaytimeSnapshot(
    IReadOnlyList<PlaytimeGameSnapshot> Games,
    IReadOnlyDictionary<string, IReadOnlyList<PlaytimeDailyGameSnapshot>> DailyHistory,
    string? ActiveGameName,
    TimeSpan ContinuousSessionDuration,
    bool IsBreakReminderDue);

