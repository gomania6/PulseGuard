using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulseGuard.Core.Security;
using PulseGuard.Modules.Optimization.Models;
using PulseGuard.Modules.Processes;
using PulseGuard.Modules.Settings;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Manages the 3 safe optimization profiles (Esports Anti-Cheat Ready, Max FPS Single-Player, Normal Reset),
/// mandatory registry backup/rollback, Standby List purge, and whitelist-only background app suspension.
/// </summary>
public sealed partial class OptimizationProfileService : IDisposable
{
    public const string BalancedPowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformancePowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimatePerformancePowerPlanGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static IReadOnlyList<string> AllowedSuspendWhitelist { get; } =
    [
        "chrome.exe",
        "msedge.exe",
        "firefox.exe",
        "opera.exe",
        "OneDrive.exe",
        "EpicGamesLauncher.exe",
        "Battle.net.exe"
    ];

    private static readonly HashSet<string> AllowedSuspendSet = new(AllowedSuspendWhitelist, StringComparer.OrdinalIgnoreCase);

    private readonly RegistryBackupService _backupService;
    private readonly AntiCheatDetectorService _antiCheatDetector;
    private readonly SettingsService _settingsService;
    private readonly ProcessMonitorModule _processMonitorModule;

    private readonly object _syncLock = new();
    private readonly Dictionary<int, string> _suspendedPids = new();
    private readonly List<string> _stoppedServices = new();
    private bool _disposed;

    public OptimizationProfileType ActiveProfile => _settingsService.Current.ActiveOptimizationProfile;

    public event Action<OptimizationProfileType, string>? ProfileStateChanged;

    public OptimizationProfileService(
        RegistryBackupService backupService,
        AntiCheatDetectorService antiCheatDetector,
        SettingsService settingsService,
        ProcessMonitorModule processMonitorModule)
    {
        _backupService = backupService;
        _antiCheatDetector = antiCheatDetector;
        _settingsService = settingsService;
        _processMonitorModule = processMonitorModule;
    }

    public Task<(bool Success, string Message)> ApplyProfileAsync(
        OptimizationProfileType profile,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            return profile switch
            {
                OptimizationProfileType.EsportsAntiCheat => ApplyEsportsProfile(cancellationToken),
                OptimizationProfileType.MaxFpsSinglePlayer => ApplyMaxFpsProfile(cancellationToken),
                OptimizationProfileType.NormalReset => ApplyNormalResetProfile(),
                _ => (false, "Неизвестный профиль оптимизации.")
            };
        }, cancellationToken);
    }

    public Task<(bool Success, string Message)> FullRollbackAsync()
    {
        return Task.Run(() =>
        {
            var resumedCount = ResumeAllProfileSuspendedProcesses();
            _processMonitorModule.ResumeAllSuspendedProcesses();

            var startedServices = RestoreStoppedServices();
            var (restoredReg, failedReg) = _backupService.RollbackAll();
            var powerRestored = SetPowerPlan(BalancedPowerPlanGuid) || SetPowerPlan("SCHEME_BALANCED");

            _settingsService.Update(s => s.ActiveOptimizationProfile = OptimizationProfileType.NormalReset);

            var powerText = powerRestored ? "схема питания «Сбалансированная»" : "схема питания проверена";
            var serviceText = startedServices > 0 ? $", запущено служб: {startedServices}" : string.Empty;
            var failText = failedReg > 0 ? $" (не удалось откатить ключей: {failedReg})" : string.Empty;
            var message = $"Полный откат выполнен: восстановлено ключей реестра: {restoredReg}{failText}, {powerText}, разморожено процессов: {resumedCount}{serviceText}.";

            ProfileStateChanged?.Invoke(OptimizationProfileType.NormalReset, message);
            return (failedReg == 0, message);
        });
    }

    public int SuspendWhitelistedBackgroundApps()
    {
        var selected = _settingsService.Current.FreezeWhitelistApps;
        if (selected is null || selected.Count == 0)
        {
            return 0;
        }

        var allowedToSuspend = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in selected)
        {
            var exe = EnsureExeSuffix(raw);
            if (AllowedSuspendSet.Contains(exe))
            {
                allowedToSuspend.Add(PathWithoutExe(exe));
            }
        }

        if (allowedToSuspend.Count == 0)
        {
            return 0;
        }

        int suspendedCount = 0;
        int currentPid = Environment.ProcessId;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return 0;
        }

        foreach (var proc in processes)
        {
            using (proc)
            {
                try
                {
                    int pid = proc.Id;
                    string name = proc.ProcessName;

                    if (pid <= 4 || pid == currentPid || ProcessBlacklist.IsProtected(name))
                    {
                        continue;
                    }

                    if (!allowedToSuspend.Contains(name))
                    {
                        continue;
                    }

                    lock (_syncLock)
                    {
                        if (_suspendedPids.ContainsKey(pid))
                        {
                            continue;
                        }
                    }

                    IntPtr handle = OptimizationNativeMethods.OpenProcess(
                        OptimizationNativeMethods.PROCESS_SUSPEND_RESUME,
                        false,
                        pid);

                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (OptimizationNativeMethods.NtSuspendProcess(handle) == 0)
                        {
                            lock (_syncLock)
                            {
                                _suspendedPids[pid] = name;
                            }

                            suspendedCount++;
                        }
                    }
                    finally
                    {
                        OptimizationNativeMethods.CloseHandle(handle);
                    }
                }
                catch
                {
                    // Skip inaccessible or exited processes safely.
                }
            }
        }

        return suspendedCount;
    }

    public int ResumeAllProfileSuspendedProcesses()
    {
        List<KeyValuePair<int, string>> snapshot;
        lock (_syncLock)
        {
            if (_suspendedPids.Count == 0)
            {
                return 0;
            }

            snapshot = _suspendedPids.ToList();
            _suspendedPids.Clear();
        }

        int resumed = 0;
        foreach (var (pid, expectedName) in snapshot)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!string.Equals(proc.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IntPtr handle = OptimizationNativeMethods.OpenProcess(
                    OptimizationNativeMethods.PROCESS_SUSPEND_RESUME,
                    false,
                    pid);

                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (OptimizationNativeMethods.NtResumeProcess(handle) == 0)
                    {
                        resumed++;
                    }
                }
                finally
                {
                    OptimizationNativeMethods.CloseHandle(handle);
                }
            }
            catch
            {
                // Process may have exited.
            }
        }

        return resumed;
    }

    public static bool TryPurgeStandbyList(out string detail)
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            detail = "привилегия SeProfileSingleProcessPrivilege недоступна";
            return false;
        }

        int command = OptimizationNativeMethods.MemoryPurgeStandbyList;
        int status = OptimizationNativeMethods.NtSetSystemInformation(
            OptimizationNativeMethods.SystemMemoryListInformation,
            ref command,
            sizeof(int));

        if (status == 0)
        {
            detail = "Standby List очищен";
            return true;
        }

        detail = $"NtSetSystemInformation вернул 0x{status:X8}";
        return false;
    }

    private (bool Success, string Message) ApplyEsportsProfile(CancellationToken cancellationToken)
    {
        _antiCheatDetector.Refresh();

        var powerOk = ActivateUltimatePerformanceAndUnparkCores();
        int regApplied = ApplyCommonEsportsRegistryTweaks();
        int ifeoCount = ApplyIfeoGamePriorities();

        bool standbyOk = TryPurgeStandbyList(out var standbyNote);

        _settingsService.Update(s => s.ActiveOptimizationProfile = OptimizationProfileType.EsportsAntiCheat);

        var powerText = powerOk ? "Макс. производительность + парковка ядер выкл (CPMINCORES 100)" : "схема питания проверена";
        var msg = $"Профиль «Киберспорт (Anti-Cheat Ready)» применён: {powerText}, ключей реестра: {regApplied}, IFEO для {ifeoCount} игр, {standbyNote}.";

        ProfileStateChanged?.Invoke(OptimizationProfileType.EsportsAntiCheat, msg);
        return (true, msg);
    }

    private (bool Success, string Message) ApplyMaxFpsProfile(CancellationToken cancellationToken)
    {
        var antiCheat = _antiCheatDetector.Refresh();

        var powerOk = ActivateUltimatePerformanceAndUnparkCores();
        int regApplied = ApplyCommonEsportsRegistryTweaks();
        int ifeoCount = ApplyIfeoGamePriorities();

        // Profile 2 extras: UI animations, transparency, UWP background apps, Nagle & NetworkThrottlingIndex
        if (_backupService.BackupAndSetValue(
                RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency",
                0,
                RegistryValueKind.DWord))
        {
            regApplied++;
        }

        if (_backupService.BackupAndSetValue(
                RegistryHive.CurrentUser,
                @"Control Panel\Desktop\WindowMetrics",
                "MinAnimate",
                "0",
                RegistryValueKind.String))
        {
            regApplied++;
        }

        if (_backupService.BackupAndSetValue(
                RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
                "GlobalUserDisabled",
                1,
                RegistryValueKind.DWord))
        {
            regApplied++;
        }

        if (_backupService.BackupAndSetValue(
                RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "NetworkThrottlingIndex",
                unchecked((int)0xFFFFFFFF),
                RegistryValueKind.DWord))
        {
            regApplied++;
        }

        regApplied += ApplyDisableNagleOnInterfaces();

        // Optional HVCI disable — STRICTLY blocked when Riot Vanguard is detected.
        string hvciNote = string.Empty;
        if (_settingsService.Current.DisableHvciInMaxFps)
        {
            if (antiCheat.IsVanguardDetected)
            {
                hvciNote = ", HVCI заблокирован (Riot Vanguard)";
            }
            else if (_backupService.BackupAndSetValue(
                         RegistryHive.LocalMachine,
                         @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                         "Enabled",
                         0,
                         RegistryValueKind.DWord))
            {
                regApplied++;
                hvciNote = ", HVCI отключён (требуется перезагрузка)";
            }
        }

        // Optional SysMain / WSearch stop
        int stoppedServices = 0;
        if (_settingsService.Current.StopSysMainAndWSearchInMaxFps)
        {
            if (TryStopService("SysMain")) stoppedServices++;
            if (TryStopService("WSearch")) stoppedServices++;
        }

        _ = TryPurgeStandbyList(out var standbyNote);

        _settingsService.Update(s => s.ActiveOptimizationProfile = OptimizationProfileType.MaxFpsSinglePlayer);

        var serviceText = stoppedServices > 0 ? $", остановлено служб: {stoppedServices}" : string.Empty;
        var powerText = powerOk ? "Макс. производительность + CPMINCORES 100" : "схема питания проверена";
        var msg = $"Профиль «Максимальный FPS (Single-Player)» применён: {powerText}, ключей реестра: {regApplied}, IFEO для {ifeoCount} игр, {standbyNote}{serviceText}{hvciNote}.";

        ProfileStateChanged?.Invoke(OptimizationProfileType.MaxFpsSinglePlayer, msg);
        return (true, msg);
    }

    private (bool Success, string Message) ApplyNormalResetProfile()
    {
        return FullRollbackAsync().GetAwaiter().GetResult();
    }

    private int ApplyCommonEsportsRegistryTweaks()
    {
        int count = 0;

        // Game Mode
        if (_backupService.BackupAndSetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord)) count++;
        if (_backupService.BackupAndSetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord)) count++;

        // GameDVR Off
        if (_backupService.BackupAndSetValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord)) count++;
        if (_backupService.BackupAndSetValue(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord)) count++;

        // MMCSS\Tasks\Games
        const string mmcssGamesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        if (_backupService.BackupAndSetValue(RegistryHive.LocalMachine, mmcssGamesKey, "GPU Priority", 8, RegistryValueKind.DWord)) count++;
        if (_backupService.BackupAndSetValue(RegistryHive.LocalMachine, mmcssGamesKey, "Priority", 6, RegistryValueKind.DWord)) count++;
        if (_backupService.BackupAndSetValue(RegistryHive.LocalMachine, mmcssGamesKey, "Scheduling Category", "High", RegistryValueKind.String)) count++;

        // SystemResponsiveness
        if (_backupService.BackupAndSetValue(
                RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness",
                10,
                RegistryValueKind.DWord))
        {
            count++;
        }

        // Windows 11 (Build >= 22621) SwapEffectUpgradeEnable=1;
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            const string dxKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
            const string dxValueName = "DirectXUserGlobalSettings";
            string currentDx = _backupService.ReadCurrentStringValue(RegistryHive.CurrentUser, dxKey, dxValueName) ?? string.Empty;
            string updatedDx = SetDirectXGlobalToken(currentDx, "SwapEffectUpgradeEnable", "1");

            if (_backupService.BackupAndSetValue(RegistryHive.CurrentUser, dxKey, dxValueName, updatedDx, RegistryValueKind.String))
            {
                count++;
            }
        }

        return count;
    }

    private int ApplyIfeoGamePriorities()
    {
        int count = 0;
        var games = _settingsService.Current.GameProcessNames;
        if (games is null || games.Count == 0)
        {
            return 0;
        }

        foreach (var raw in games)
        {
            var exe = EnsureExeSuffix(raw);
            if (string.IsNullOrWhiteSpace(exe) || ProcessBlacklist.IsProtected(exe))
            {
                continue;
            }

            string subKey = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}\PerfOptions";
            if (_backupService.BackupAndSetValue(RegistryHive.LocalMachine, subKey, "CpuPriorityClass", 3, RegistryValueKind.DWord))
            {
                count++;
            }
        }

        return count;
    }

    private int ApplyDisableNagleOnInterfaces()
    {
        int count = 0;
        try
        {
            const string interfacesRoot = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var rootKey = baseKey.OpenSubKey(interfacesRoot, writable: false);
            if (rootKey is null)
            {
                return 0;
            }

            foreach (var subName in rootKey.GetSubKeyNames())
            {
                using var ifaceKey = rootKey.OpenSubKey(subName, writable: false);
                if (ifaceKey is null)
                {
                    continue;
                }

                string? dhcpIp = ifaceKey.GetValue("DhcpIPAddress")?.ToString();
                object? staticIp = ifaceKey.GetValue("IPAddress");
                bool hasIp = (!string.IsNullOrWhiteSpace(dhcpIp) && dhcpIp != "0.0.0.0")
                    || (staticIp is string[] arr && arr.Any(ip => !string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0"));

                if (!hasIp)
                {
                    continue;
                }

                string fullSubKey = $@"{interfacesRoot}\{subName}";
                if (_backupService.BackupAndSetValue(RegistryHive.LocalMachine, fullSubKey, "TcpAckFrequency", 1, RegistryValueKind.DWord)) count++;
                if (_backupService.BackupAndSetValue(RegistryHive.LocalMachine, fullSubKey, "TCPNoDelay", 1, RegistryValueKind.DWord)) count++;
            }
        }
        catch
        {
            // Ignore enumeration errors.
        }

        return count;
    }

    private static string SetDirectXGlobalToken(string existing, string key, string value)
    {
        var tokens = existing
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        tokens.Add($"{key}={value}");
        return string.Join(';', tokens) + ";";
    }

    private static bool ActivateUltimatePerformanceAndUnparkCores()
    {
        bool activated = SetPowerPlan(UltimatePerformancePowerPlanGuid);
        if (!activated)
        {
            var duplicatedGuid = DuplicatePowerScheme(UltimatePerformancePowerPlanGuid);
            if (!string.IsNullOrEmpty(duplicatedGuid))
            {
                activated = SetPowerPlan(duplicatedGuid);
            }
        }

        if (!activated)
        {
            activated = SetPowerPlan(HighPerformancePowerPlanGuid) || SetPowerPlan("SCHEME_MIN");
        }

        // Disable CPU core parking (CPMINCORES = 100%) on the active power plan
        RunPowerCfg("/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 100");
        RunPowerCfg("/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 100");
        RunPowerCfg("/setactive SCHEME_CURRENT");
        return activated;
    }

    private static string? DuplicatePowerScheme(string sourceGuid)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"-duplicatescheme {sourceGuid}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc is null)
            {
                return null;
            }

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var match = GuidRegex().Match(output);
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SetPowerPlan(string guid) => RunPowerCfg($"/setactive {guid}");

    private static bool RunPowerCfg(string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc is null)
            {
                return false;
            }

            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryStopService(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            scm = OptimizationNativeMethods.OpenSCManagerW(null, null, OptimizationNativeMethods.SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;

            service = OptimizationNativeMethods.OpenServiceW(
                scm,
                serviceName,
                OptimizationNativeMethods.SERVICE_QUERY_STATUS | OptimizationNativeMethods.SERVICE_STOP);
            if (service == IntPtr.Zero) return false;

            if (OptimizationNativeMethods.QueryServiceStatus(service, out var currentStatus)
                && currentStatus.dwCurrentState == OptimizationNativeMethods.SERVICE_STOPPED)
            {
                return false;
            }

            if (OptimizationNativeMethods.ControlService(service, OptimizationNativeMethods.SERVICE_CONTROL_STOP, out _))
            {
                lock (_syncLock)
                {
                    if (!_stoppedServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
                    {
                        _stoppedServices.Add(serviceName);
                    }
                }

                return true;
            }
        }
        catch
        {
        }
        finally
        {
            if (service != IntPtr.Zero) OptimizationNativeMethods.CloseServiceHandle(service);
            if (scm != IntPtr.Zero) OptimizationNativeMethods.CloseServiceHandle(scm);
        }

        return false;
    }

    private int RestoreStoppedServices()
    {
        List<string> toStart;
        lock (_syncLock)
        {
            if (_stoppedServices.Count == 0)
            {
                return 0;
            }

            toStart = _stoppedServices.ToList();
            _stoppedServices.Clear();
        }

        int started = 0;
        foreach (var serviceName in toStart)
        {
            IntPtr scm = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            try
            {
                scm = OptimizationNativeMethods.OpenSCManagerW(null, null, OptimizationNativeMethods.SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) continue;

                service = OptimizationNativeMethods.OpenServiceW(scm, serviceName, OptimizationNativeMethods.SERVICE_START);
                if (service == IntPtr.Zero) continue;

                if (OptimizationNativeMethods.StartServiceW(service, 0, IntPtr.Zero))
                {
                    started++;
                }
            }
            catch
            {
            }
            finally
            {
                if (service != IntPtr.Zero) OptimizationNativeMethods.CloseServiceHandle(service);
                if (scm != IntPtr.Zero) OptimizationNativeMethods.CloseServiceHandle(scm);
            }
        }

        return started;
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!OptimizationNativeMethods.OpenProcessToken(
                    Process.GetCurrentProcess().Handle,
                    OptimizationNativeMethods.TOKEN_ADJUST_PRIVILEGES | OptimizationNativeMethods.TOKEN_QUERY,
                    out tokenHandle))
            {
                return false;
            }

            if (!OptimizationNativeMethods.LookupPrivilegeValueW(null, privilegeName, out var luid))
            {
                return false;
            }

            var tp = new OptimizationNativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = OptimizationNativeMethods.SE_PRIVILEGE_ENABLED
            };

            return OptimizationNativeMethods.AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                OptimizationNativeMethods.CloseHandle(tokenHandle);
            }
        }
    }

    private static string EnsureExeSuffix(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".exe";
    }

    private static string PathWithoutExe(string exeName) =>
        exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;

    [GeneratedRegex("[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResumeAllProfileSuspendedProcesses();
        RestoreStoppedServices();
    }
}

