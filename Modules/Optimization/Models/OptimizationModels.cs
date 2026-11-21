namespace PulseGuard.Modules.Optimization.Models;

public enum OptimizationProfileType
{
    EsportsAntiCheat,
    MaxFpsSinglePlayer,
    NormalReset
}

public sealed class RegistryBackupEntry
{
    public string Hive { get; set; } = string.Empty;
    public string SubKey { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public bool ValueExisted { get; set; }
    public string ValueKind { get; set; } = string.Empty;
    public string? OriginalValue { get; set; }
}

public sealed class RegistryBackupFile
{
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<RegistryBackupEntry> Entries { get; set; } = [];
}

public sealed record AntiCheatStatus(
    bool IsVanguardDetected,
    bool IsBattlEyeDetected,
    bool IsEacDetected,
    bool IsSteamRunning,
    string SummaryText);

public sealed class WhitelistSuspendAppOption
{
    public string ExecutableName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsSelected { get; set; }
}

