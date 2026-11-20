using System.Diagnostics;
using System.Text.RegularExpressions;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Safe Game Mode: switches Windows power plan to High Performance and lowers priority
/// of known background apps to BelowNormal, restoring everything on disable or app exit.
/// </summary>
public sealed partial class GameModeTool : IOptimizerTool, IDisposable
{
    private const string HighPerformancePowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static readonly HashSet<string> BackgroundApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "firefox",
        "msedge",
        "opera",
        "brave",
        "discord",
        "telegram",
        "qbittorrent",
        "steamwebhelper"
    };

    private readonly record struct SavedProcessPriority(int Pid, string ProcessName, ProcessPriorityClass OriginalPriority);

    private readonly object _syncLock = new();
    private readonly List<SavedProcessPriority> _savedPriorities = new();
    private string? _previousPowerPlanGuid;
    private bool _isActive;
    private bool _disposed;

    public string Id => "game-mode";
    public string Name => "Игровой режим (Game Mode)";
    public string Description => "Переключает схему питания на «Высокую производительность» и понижает приоритет фоновых приложений.";
    public bool CanExecute => true;
    public bool IsActive => _isActive;

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

    public Task<OptimizationResult> EnableAsync()
    {
        return Task.Run(() =>
        {
            lock (_syncLock)
            {
                if (_isActive)
                {
                    return new OptimizationResult(true, "Игровой режим уже включён.", 0, _savedPriorities.Count);
                }

                _previousPowerPlanGuid = GetActivePowerPlanGuid();
                var powerPlanSwitched = SetPowerPlan(HighPerformancePowerPlanGuid);

                _savedPriorities.Clear();
                var loweredCount = LowerBackgroundAppPriorities();
                _isActive = true;

                var powerNote = powerPlanSwitched
                    ? "схема питания «Высокая производительность» активна"
                    : "схема питания не изменена";

                var message = $"Игровой режим включён: {powerNote}, понижен приоритет у {loweredCount} процессов.";
                return new OptimizationResult(true, message, 0, loweredCount);
            }
        });
    }

    public Task<OptimizationResult> DisableAsync()
    {
        return Task.Run(RestoreState);
    }

    private OptimizationResult RestoreState()
    {
        lock (_syncLock)
        {
            if (!_isActive)
            {
                return new OptimizationResult(true, "Игровой режим отключён.", 0, 0);
            }

            var powerRestored = false;
            if (!string.IsNullOrWhiteSpace(_previousPowerPlanGuid))
            {
                powerRestored = SetPowerPlan(_previousPowerPlanGuid);
                _previousPowerPlanGuid = null;
            }

            var restoredCount = RestoreBackgroundAppPriorities();
            _isActive = false;

            var powerNote = powerRestored ? "исходная схема питания восстановлена" : "схема питания проверена";
            var message = $"Игровой режим выключен: {powerNote}, восстановлен приоритет у {restoredCount} процессов.";
            return new OptimizationResult(true, message, 0, restoredCount);
        }
    }

    private int LowerBackgroundAppPriorities()
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
    }
}

