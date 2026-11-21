namespace PulseGuard.Core.Security;

/// <summary>
/// Critical Windows, anti-cheat, platform, and user-configured VPN processes
/// that MUST NEVER be touched, suspended, reprioritized, or killed by any optimization action.
/// </summary>
public static class ProcessBlacklist
{
    private static readonly object DynamicLock = new();
    private static string? _dynamicVpnProcessName;
    private static string? _activeGameProcessName;

    public static IReadOnlySet<string> ProtectedProcesses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Idle",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
        "dwm",
        "explorer",
        "smss",
        "audiodg",
        "PulseGuard",
        // Anti-cheat & game platform processes (strictly never touch)
        "vgc",
        "vgtray",
        "BEService",
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "steam",
        "steamwebhelper"
    };

    /// <summary>
    /// Registers or clears the user-configured VPN client executable name in the protected exclusion list
    /// so Game Mode and RAM optimization never touch it.
    /// </summary>
    public static void SetVpnExclusion(string? executablePathOrName)
    {
        string? normalized = NormalizeName(executablePathOrName);

        lock (DynamicLock)
        {
            _dynamicVpnProcessName = normalized;
        }
    }

    /// <summary>
    /// Registers or clears the currently active game process name so optimization tools
    /// never apply OpenProcess, SetPriorityClass, ProcessorAffinity, or NtSuspendProcess to it.
    /// </summary>
    public static void SetActiveGameExclusion(string? executablePathOrName)
    {
        string? normalized = NormalizeName(executablePathOrName);

        lock (DynamicLock)
        {
            _activeGameProcessName = normalized;
        }
    }

    /// <summary>
    /// Returns true if the given process name is protected (system, anti-cheat, Steam, VPN, or active game)
    /// and must be skipped by process-manipulation APIs.
    /// </summary>
    public static bool IsProtected(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        var normalized = NormalizeName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return true;
        }

        if (ProtectedProcesses.Contains(normalized))
        {
            return true;
        }

        lock (DynamicLock)
        {
            if (!string.IsNullOrEmpty(_dynamicVpnProcessName)
                && string.Equals(normalized, _dynamicVpnProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(_activeGameProcessName)
                && string.Equals(normalized, _activeGameProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeName(string? executablePathOrName)
    {
        if (string.IsNullOrWhiteSpace(executablePathOrName))
        {
            return null;
        }

        string fileName = System.IO.Path.GetFileNameWithoutExtension(executablePathOrName.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
}
