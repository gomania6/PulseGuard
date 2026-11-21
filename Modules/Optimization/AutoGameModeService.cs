using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Watches for configured game processes and fullscreen apps (excluding Explorer, browsers, and media players)
/// to automatically enable/disable Game Mode on a lightweight 3-second PeriodicTimer.
/// </summary>
public sealed class AutoGameModeService : IMonitorModule
{
    private const int PollIntervalMs = 3000;
    private const int FullscreenTolerancePx = 2;
    private const int DisableGraceTicks = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static readonly HashSet<string> FullscreenExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ApplicationFrameHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "LockApp",
        "ScreenClippingHost",
        "SnippingTool",
        "TextInputHost",
        "Photos",
        "Microsoft.Photos",
        "WindowsTerminal",
        "cmd",
        "powershell",
        "pwsh",
        // Browsers
        "chrome",
        "firefox",
        "msedge",
        "opera",
        "brave",
        "vivaldi",
        "yandex",
        "browser",
        // Chat / Launchers / Capture
        "Telegram",
        "Discord",
        "obs64",
        "obs32",
        "steam",
        "steamwebhelper",
        "epicgameslauncher",
        "Battle.net",
        "RiotClientServices",
        // Media players
        "vlc",
        "mpc-hc",
        "mpc-hc64",
        "mpv",
        "potplayermini",
        "potplayermini64",
        "wmplayer",
        "spotify",
        "aimp",
        "foobar2000"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly GameModeTool _gameModeTool;
    private readonly SettingsService _settingsService;
    private readonly object _lifecycleLock = new();
    private readonly HashSet<string> _configuredGamesCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loopCts;
    private Task? _watchTask;
    private bool _autoActivatedByWatcher;
    private int _missedDetectionTicks;
    private int? _autoActivatedFullscreenPid;
    private string? _autoActivatedFullscreenName;
    private bool _disposed;

    public string Id => "auto-game-mode";
    public string Name => "Авто-Игровой режим";
    public string Description => "Автоматически включает Игровой режим при запуске игр или полноэкранных приложений.";
    public bool IsEnabled { get; set; } = true;

    public AutoGameModeService(GameModeTool gameModeTool, SettingsService settingsService)
    {
        _gameModeTool = gameModeTool;
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _gameModeTool.StateChanged += OnGameModeStateChanged;
        RebuildGameNamesCache(_settingsService.Current);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ApplySettings(_settingsService.Current);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        return EvaluateAsync(cancellationToken);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        RebuildGameNamesCache(settings);
        ApplySettings(settings);
    }

    private void OnGameModeStateChanged(bool isActive, string? _)
    {
        if (!isActive)
        {
            _autoActivatedByWatcher = false;
            _missedDetectionTicks = 0;
            _autoActivatedFullscreenPid = null;
            _autoActivatedFullscreenName = null;
        }
    }

    private void RebuildGameNamesCache(AppSettings settings)
    {
        lock (_lifecycleLock)
        {
            _configuredGamesCache.Clear();
            foreach (string raw in settings.GameProcessNames)
            {
                string normalized = NormalizeProcessName(raw);
                if (!string.IsNullOrEmpty(normalized) && !ProcessBlacklist.IsProtected(normalized))
                {
                    _configuredGamesCache.Add(normalized);
                }
            }
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            if (settings.IsAutoGameModeEnabled && IsEnabled)
            {
                StartWatcherLocked();
            }
            else
            {
                StopWatcherLocked();
                if (_autoActivatedByWatcher && _gameModeTool.IsActive)
                {
                    _autoActivatedByWatcher = false;
                    _ = _gameModeTool.DisableAsync();
                }
            }
        }
    }

    private void StartWatcherLocked()
    {
        if (_loopCts is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _watchTask = Task.Run(() => RunLoopAsync(token), token);
    }

    private void StopWatcherLocked()
    {
        if (_loopCts is null)
        {
            return;
        }

        try
        {
            _loopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _loopCts.Dispose();
        _loopCts = null;
        _watchTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Current;
        if (!settings.IsAutoGameModeEnabled || _disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        string? detectedGame = DetectRunningGame(settings.DetectFullscreenApps);

        if (!string.IsNullOrEmpty(detectedGame))
        {
            _missedDetectionTicks = 0;
            if (!_gameModeTool.IsActive)
            {
                _autoActivatedByWatcher = true;
                await _gameModeTool.EnableAsync(detectedGame).ConfigureAwait(false);
            }
        }
        else if (_autoActivatedByWatcher && _gameModeTool.IsActive)
        {
            _missedDetectionTicks++;
            if (_missedDetectionTicks >= DisableGraceTicks)
            {
                _missedDetectionTicks = 0;
                _autoActivatedByWatcher = false;
                await _gameModeTool.DisableAsync().ConfigureAwait(false);
            }
        }
        else
        {
            _missedDetectionTicks = 0;
        }
    }

    public string? DetectCurrentGame(bool detectFullscreen) => DetectRunningGame(detectFullscreen);

    private string? DetectRunningGame(bool detectFullscreen)
    {
        bool hasConfiguredGames;
        lock (_lifecycleLock)
        {
            hasConfiguredGames = _configuredGamesCache.Count > 0;
        }

        if (hasConfiguredGames)
        {
            try
            {
                var processes = Process.GetProcesses();
                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            string name = process.ProcessName;
                            lock (_lifecycleLock)
                            {
                                if (_configuredGamesCache.Contains(name))
                                {
                                    return name;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        if (detectFullscreen)
        {
            if (_autoActivatedByWatcher
                && _autoActivatedFullscreenPid is int trackedPid
                && !string.IsNullOrEmpty(_autoActivatedFullscreenName)
                && IsProcessAlive(trackedPid, _autoActivatedFullscreenName))
            {
                return _autoActivatedFullscreenName;
            }

            _autoActivatedFullscreenPid = null;
            _autoActivatedFullscreenName = null;

            var fullscreenApp = DetectFullscreenForegroundApp();
            if (fullscreenApp is { } detected)
            {
                _autoActivatedFullscreenPid = detected.Pid;
                _autoActivatedFullscreenName = detected.Name;
                return detected.Name;
            }
        }

        return null;
    }

    private static bool IsProcessAlive(int pid, string expectedName)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited
                && string.Equals(proc.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static (string Name, int Pid)? DetectFullscreenForegroundApp()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return null;
            }

            if (!GetWindowRect(hwnd, out var winRect))
            {
                return null;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref mi))
            {
                return null;
            }

            int winWidth = winRect.Right - winRect.Left;
            int winHeight = winRect.Bottom - winRect.Top;
            int monWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int monHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

            if (monWidth <= 0 || monHeight <= 0)
            {
                return null;
            }

            bool coversMonitor = winWidth >= (monWidth - FullscreenTolerancePx)
                && winHeight >= (monHeight - FullscreenTolerancePx)
                && winRect.Left <= (mi.rcMonitor.Left + FullscreenTolerancePx)
                && winRect.Top <= (mi.rcMonitor.Top + FullscreenTolerancePx);

            if (!coversMonitor)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid <= 4 || pid == (uint)Environment.ProcessId)
            {
                return null;
            }

            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;

            if (ProcessBlacklist.IsProtected(name) || FullscreenExclusions.Contains(name))
            {
                return null;
            }

            return (name, (int)pid);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _gameModeTool.StateChanged -= OnGameModeStateChanged;

        lock (_lifecycleLock)
        {
            StopWatcherLocked();
        }
    }
}

