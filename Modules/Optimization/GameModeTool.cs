using System.Diagnostics;
using System.Text.RegularExpressions;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;
using PulseGuard.Modules.Optimization.Models;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Safe Game Mode (Anti-Cheat Ready): switches Windows power plan to Ultimate/High Performance,
/// sets 0.5 ms timer resolution, trims background RAM, purges Standby List, suspends strictly
/// whitelisted background apps, and lowers priority of safe non-protected background apps.
/// Never calls OpenProcess/SetPriorityClass/ProcessorAffinity/NtSuspendProcess on games,
/// vgc.exe, vgtray.exe, steam.exe, steamwebhelper.exe, or VPN processes.
/// </summary>
public sealed partial class GameModeTool : IOptimizerTool, IDisposable
{
    private const uint DesiredTimerResolution100Ns = 5000; // 0.5 ms

    private static readonly HashSet<string> BackgroundApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "firefox",
        "msedge",
        "opera",
        "brave",
        "discord",
        "telegram",
        "qbittorrent"
    };

    private readonly record struct SavedProcessPriority(int Pid, string ProcessName, ProcessPriorityClass OriginalPriority);

    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly OptimizationProfileService _profileService;
    private readonly object _syncLock = new();
    private readonly List<SavedProcessPriority> _savedPriorities = new();
    private string? _previousPowerPlanGuid;
    private bool _timerResolutionBoosted;
    private bool _isActive;
    private DateTime? _sessionStartUtc;
    private string? _activeGameName;
    private bool _disposed;

    public GameModeTool(SafeRamCleanerTool ramCleanerTool, OptimizationProfileService profileService)
    {
        _ramCleanerTool = ramCleanerTool;
        _profileService = profileService;
    }

    public string Id => "game-mode";
    public string Name => "Игровой режим (Game Mode)";
    public string Description => "Переключает схему питания, устанавливает таймер 0.5 мс, очищает Standby List и безопасно приостанавливает выбранные фоновые программы.";
    public bool CanExecute => true;
    public bool IsActive => _isActive;
    public DateTime? SessionStartUtc => _sessionStartUtc;
    public string? ActiveGameName => _activeGameName;

    public event Action<bool, string?>? StateChanged;

    public Task<OptimizationPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OptimizationPreview(
            _isActive ? "Игровой режим активен" : "Игровой режим отключён",
            0,
            _savedPriorities.Count));
    }

    public Task<OptimizationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        return _isActive ? DisableAsync() : EnableAsync();
    }

    public Task<OptimizationResult> EnableAsync(string? gameName = null)
    {
        return Task.Run(() =>
        {
            OptimizationResult result;
            bool raised = false;

            lock (_syncLock)
            {
                if (_isActive)
                {
                    return new OptimizationResult(true, "Игровой режим уже включён.", 0, _savedPriorities.Count);
                }

                ProcessBlacklist.SetActiveGameExclusion(gameName);

                _previousPowerPlanGuid = GetActivePowerPlanGuid();
                var powerPlanSwitched = SetPowerPlan(OptimizationProfileService.UltimatePerformancePowerPlanGuid)
                    || SetPowerPlan(OptimizationProfileService.HighPerformancePowerPlanGuid)
                    || SetPowerPlan("SCHEME_MIN");

                _timerResolutionBoosted = OptimizationNativeMethods.NtSetTimerResolution(DesiredTimerResolution100Ns, true, out _) >= 0;

                _savedPriorities.Clear();
                var loweredCount = LowerBackgroundAppPriorities(gameName);
                var suspendedCount = _profileService.ActiveProfile != OptimizationProfileType.NormalReset
                    ? _profileService.SuspendWhitelistedBackgroundApps()
                    : 0;
                _ = OptimizationProfileService.TryPurgeStandbyList(out _);

                _isActive = true;
                _sessionStartUtc = DateTime.UtcNow;
                _activeGameName = gameName;
                raised = true;

                var powerNote = powerPlanSwitched
                    ? "схема «Макс. производительность»"
                    : "схема питания не изменена";
                var timerNote = _timerResolutionBoosted ? ", таймер 0.5 мс" : string.Empty;
                var suspendNote = suspendedCount > 0 ? $", заморожено по WhiteList: {suspendedCount}" : string.Empty;

                var prefix = string.IsNullOrWhiteSpace(gameName)
                    ? "Игровой режим включён"
                    : $"Авто-режим ({gameName}) включён";
                var message = $"{prefix}: {powerNote}{timerNote}{suspendNote}, понижен приоритет у {loweredCount} фоновых процессов.";
                result = new OptimizationResult(true, message, 0, loweredCount + suspendedCount);
            }

            if (raised)
            {
                _ = _ramCleanerTool.ExecuteAsync();
                StateChanged?.Invoke(true, gameName);
            }

            return result;
        });
    }

    public Task<OptimizationResult> DisableAsync()
    {
        return Task.Run(RestoreState);
    }

    private OptimizationResult RestoreState()
    {
        OptimizationResult result;
        bool raised = false;

        lock (_syncLock)
        {
            if (!_isActive)
            {
                ProcessBlacklist.SetActiveGameExclusion(null);
                _profileService.ResumeAllProfileSuspendedProcesses();
                return new OptimizationResult(true, "Игровой режим отключён.", 0, 0);
            }

            var powerRestored = false;
            if (!string.IsNullOrWhiteSpace(_previousPowerPlanGuid))
            {
                powerRestored = SetPowerPlan(_previousPowerPlanGuid);
                _previousPowerPlanGuid = null;
            }

            if (_timerResolutionBoosted)
            {
                _ = OptimizationNativeMethods.NtSetTimerResolution(DesiredTimerResolution100Ns, false, out _);
                _timerResolutionBoosted = false;
            }

            var resumedCount = _profileService.ResumeAllProfileSuspendedProcesses();
            var restoredCount = RestoreBackgroundAppPriorities();
            ProcessBlacklist.SetActiveGameExclusion(null);

            _isActive = false;
            _sessionStartUtc = null;
            _activeGameName = null;
            raised = true;

            var powerNote = powerRestored ? "исходная схема питания восстановлена" : "схема питания проверена";
            var resumeNote = resumedCount > 0 ? $", разморожено процессов: {resumedCount}" : string.Empty;
            var message = $"Игровой режим выключен: {powerNote}, восстановлен приоритет у {restoredCount} процессов{resumeNote}.";
            result = new OptimizationResult(true, message, 0, restoredCount + resumedCount);
        }

        if (raised)
        {
            StateChanged?.Invoke(false, null);
        }

        return result;
    }

    private int LowerBackgroundAppPriorities(string? activeGameName)
    {
        var count = 0;
        var currentPid = Environment.ProcessId;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return 0;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    var name = process.ProcessName;

                    if (pid <= 4 || pid == currentPid || ProcessBlacklist.IsProtected(name))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(activeGameName)
                        && string.Equals(name, activeGameName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!BackgroundApps.Contains(name))
                    {
                        continue;
                    }

                    var original = process.PriorityClass;
                    if (original == ProcessPriorityClass.BelowNormal)
                    {
                        continue;
                    }

                    _savedPriorities.Add(new SavedProcessPriority(pid, name, original));
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    count++;
                }
                catch
                {
                    // Skip exited or inaccessible processes silently.
                }
            }
        }

        return count;
    }

    private int RestoreBackgroundAppPriorities()
    {
        var restored = 0;

        foreach (var saved in _savedPriorities)
        {
            if (ProcessBlacklist.IsProtected(saved.ProcessName))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(saved.Pid);
                if (!string.Equals(process.ProcessName, saved.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.PriorityClass = saved.OriginalPriority;
                restored++;
            }
            catch
            {
                // Process may have exited while Game Mode was active.
            }
        }

        _savedPriorities.Clear();
        return restored;
    }

    private static string? GetActivePowerPlanGuid()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = "/getactivescheme",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc is null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            var match = GuidRegex().Match(output);
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SetPowerPlan(string guid)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"/setactive {guid}",
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

    [GeneratedRegex("[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isActive)
        {
            RestoreState();
        }
        else
        {
            ProcessBlacklist.SetActiveGameExclusion(null);
            _profileService.ResumeAllProfileSuspendedProcesses();
        }
    }
}
