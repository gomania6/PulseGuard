using System.Diagnostics;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.Monitoring.Models;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Processes;

namespace PulseGuard.Modules.Monitoring;

/// <summary>
/// Detects system scheduler latency / micro-stutters by measuring Stopwatch.GetTimestamp()
/// wakeup deviation against a 50 ms PeriodicTimer, sampling DPC time and Processor Queue Length,
/// and identifying any background process spiking CPU or disk I/O at the moment of the stutter.
/// </summary>
public sealed class StutterMonitorModule : IMonitorModule
{
    private const int ExpectedIntervalMs = 50;
    private const double StutterThresholdDeviationMs = 45.0;
    private const double SleepIgnoreThresholdMs = 5000.0;
    private const int PerfSampleTickInterval = 10;     // ~500 ms
    private const int BaselineTickInterval = 40;       // ~2000 ms
    private const int CulpritCooldownMs = 1500;

    private static readonly Dictionary<string, string> FriendlyProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng"] = "Windows Defender",
        ["TiWorker"] = "Windows Update",
        ["TrustedInstaller"] = "Windows Installer",
        ["SearchIndexer"] = "Windows Search",
        ["CompatTelRunner"] = "Windows Telemetry"
    };

    private readonly record struct ProcessBaseline(long CpuTicks, ulong IoBytes);

    private readonly GameModeTool _gameModeTool;
    private readonly object _lifecycleLock = new();
    private readonly Dictionary<int, ProcessBaseline> _processBaselines = new();

    private PerformanceCounter? _dpcCounter;
    private PerformanceCounter? _queueCounter;
    private bool _countersInitialized;

    private CancellationTokenSource? _loopCts;
    private Task? _pollTask;
    private bool _isPaused;
    private bool _disposed;

    private int _stutterCount;
    private double _maxDelayMs;
    private double _currentDpcPercent;
    private int _currentQueueLength;
    private StutterEvent? _lastEvent;
    private long _lastCulpritCheckTicks;

    public string Id => "stutter-monitor";
    public string Name => "Монитор микрофризов (Stutter Monitor)";
    public string Description => "Отслеживает задержки планировщика ОС, DPC и всплески активности фоновых процессов.";
    public bool IsEnabled { get; set; } = true;

    public StutterSnapshot LatestSnapshot { get; private set; } = new(0, 0, 0, 0, 0, null);

    public event Action<StutterEvent, StutterSnapshot>? StutterDetected;
    public event Action<StutterSnapshot>? SnapshotUpdated;

    public StutterMonitorModule(GameModeTool gameModeTool)
    {
        _gameModeTool = gameModeTool;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCounters();
        RefreshProcessBaselines();
        StartLoop();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public void SetPaused(bool paused)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _isPaused == paused)
            {
                return;
            }

            _isPaused = paused;
            if (paused)
            {
                StopLoopLocked();
            }
            else
            {
                RefreshProcessBaselines();
                StartLoopLocked();
            }
        }
    }

    private void InitializeCounters()
    {
        if (_countersInitialized)
        {
            return;
        }

        _countersInitialized = true;
        try
        {
            _dpcCounter = new PerformanceCounter("Processor", "% DPC Time", "_Total", readOnly: true);
            _ = _dpcCounter.NextValue();
        }
        catch
        {
            _dpcCounter?.Dispose();
            _dpcCounter = null;
        }

        try
        {
            _queueCounter = new PerformanceCounter("System", "Processor Queue Length", readOnly: true);
            _ = _queueCounter.NextValue();
        }
        catch
        {
            _queueCounter?.Dispose();
            _queueCounter = null;
        }
    }

    private void StartLoop()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _isPaused)
            {
                return;
            }

            StartLoopLocked();
        }
    }

    private void StartLoopLocked()
    {
        if (_loopCts is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _pollTask = Task.Run(() => RunLoopAsync(token), token);
    }

    private void StopLoopLocked()
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
        _pollTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ExpectedIntervalMs));
            long prevTimestamp = Stopwatch.GetTimestamp();
            int tickCount = 0;

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long nowTimestamp = Stopwatch.GetTimestamp();
                double actualMs = (nowTimestamp - prevTimestamp) * 1000.0 / Stopwatch.Frequency;
                prevTimestamp = nowTimestamp;

                if (!IsEnabled || _isPaused)
                {
                    continue;
                }

                // Ignore system sleep/hibernate transitions.
                if (actualMs > SleepIgnoreThresholdMs)
                {
                    RefreshProcessBaselines();
                    continue;
                }

                tickCount++;
                if (tickCount % PerfSampleTickInterval == 0)
                {
                    SamplePerfCounters();
                }

                double deviationMs = Math.Max(0, actualMs - ExpectedIntervalMs);
                if (deviationMs > StutterThresholdDeviationMs)
                {
                    SamplePerfCounters();
                    string? culprit = FindCulpritIfAllowed(nowTimestamp);

                    _stutterCount++;
                    if (deviationMs > _maxDelayMs)
                    {
                        _maxDelayMs = deviationMs;
                    }

                    var ev = new StutterEvent(
                        DateTime.UtcNow,
                        deviationMs,
                        _currentDpcPercent,
                        _currentQueueLength,
                        culprit);

                    _lastEvent = ev;
                    var snapshot = new StutterSnapshot(
                        _stutterCount,
                        _maxDelayMs,
                        deviationMs,
                        _currentDpcPercent,
                        _currentQueueLength,
                        ev);
                    LatestSnapshot = snapshot;

                    StutterDetected?.Invoke(ev, snapshot);
                    SnapshotUpdated?.Invoke(snapshot);
                    continue;
                }

                if (tickCount % BaselineTickInterval == 0)
                {
                    RefreshProcessBaselines();
                    var snapshot = new StutterSnapshot(
                        _stutterCount,
                        _maxDelayMs,
                        deviationMs,
                        _currentDpcPercent,
                        _currentQueueLength,
                        _lastEvent);
                    LatestSnapshot = snapshot;
                    SnapshotUpdated?.Invoke(snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SamplePerfCounters()
    {
        try
        {
            if (_dpcCounter is not null)
            {
                _currentDpcPercent = Math.Clamp(_dpcCounter.NextValue(), 0, 100);
            }
        }
        catch
        {
        }

        try
        {
            if (_queueCounter is not null)
            {
                _currentQueueLength = Math.Max(0, (int)_queueCounter.NextValue());
            }
        }
        catch
        {
        }
    }

    private string? FindCulpritIfAllowed(long nowTimestamp)
    {
        double elapsedSinceLastCheckMs = (nowTimestamp - _lastCulpritCheckTicks) * 1000.0 / Stopwatch.Frequency;
        if (_lastCulpritCheckTicks != 0 && elapsedSinceLastCheckMs < CulpritCooldownMs)
        {
            return null;
        }

        _lastCulpritCheckTicks = nowTimestamp;
        return FindCulpritProcess();
    }

    private string? FindCulpritProcess()
    {
        string? activeGame = _gameModeTool.IsActive ? _gameModeTool.ActiveGameName : null;
        int selfPid = Environment.ProcessId;

        string? bestCpuProcess = null;
        long bestCpuDeltaTicks = 0;

        string? bestIoProcess = null;
        ulong bestIoDeltaBytes = 0;

        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                using (proc)
                {
                    try
                    {
                        int pid = proc.Id;
                        if (pid <= 4 || pid == selfPid)
                        {
                            continue;
                        }

                        string name = proc.ProcessName;
                        if (string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "System", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "PulseGuard", StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(activeGame) && string.Equals(name, activeGame, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        long cpuTicks = proc.TotalProcessorTime.Ticks;
                        ulong ioBytes = ReadIoBytes(proc);

                        if (_processBaselines.TryGetValue(pid, out var prev))
                        {
                            long cpuDelta = cpuTicks - prev.CpuTicks;
                            ulong ioDelta = ioBytes >= prev.IoBytes ? ioBytes - prev.IoBytes : 0;

                            if (cpuDelta > bestCpuDeltaTicks)
                            {
                                bestCpuDeltaTicks = cpuDelta;
                                bestCpuProcess = name;
                            }

                            if (ioDelta > bestIoDeltaBytes)
                            {
                                bestIoDeltaBytes = ioDelta;
                                bestIoProcess = name;
                            }
                        }

                        _processBaselines[pid] = new ProcessBaseline(cpuTicks, ioBytes);
                    }
                    catch
                    {
                        // Skip protected/exited processes.
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        // Minimum threshold: >= 15 ms CPU burst or >= 2 MB Disk/IO burst
        bool hasCpuSpike = bestCpuProcess is not null && bestCpuDeltaTicks >= TimeSpan.FromMilliseconds(15).Ticks;
        bool hasIoSpike = bestIoProcess is not null && bestIoDeltaBytes >= 2UL * 1024 * 1024;

        if (hasCpuSpike && hasIoSpike && !string.Equals(bestCpuProcess, bestIoProcess, StringComparison.OrdinalIgnoreCase))
        {
            return $"{FormatCulpritName(bestCpuProcess!)} / {FormatCulpritName(bestIoProcess!)}";
        }

        if (hasCpuSpike)
        {
            return FormatCulpritName(bestCpuProcess!);
        }

        if (hasIoSpike)
        {
            return FormatCulpritName(bestIoProcess!);
        }

        return null;
    }

    private void RefreshProcessBaselines()
    {
        try
        {
            var processes = Process.GetProcesses();
            var activePids = new HashSet<int>();

            foreach (var proc in processes)
            {
                using (proc)
                {
                    try
                    {
                        int pid = proc.Id;
                        if (pid <= 4)
                        {
                            continue;
                        }

                        activePids.Add(pid);
                        long cpuTicks = proc.TotalProcessorTime.Ticks;
                        ulong ioBytes = ReadIoBytes(proc);
                        _processBaselines[pid] = new ProcessBaseline(cpuTicks, ioBytes);
                    }
                    catch
                    {
                    }
                }
            }

            if (_processBaselines.Count > activePids.Count + 32)
            {
                var stalePids = _processBaselines.Keys.Where(pid => !activePids.Contains(pid)).ToList();
                foreach (int pid in stalePids)
                {
                    _processBaselines.Remove(pid);
                }
            }
        }
        catch
        {
        }
    }

    private static ulong ReadIoBytes(Process proc)
    {
        IntPtr handle = ProcessNativeMethods.OpenProcess(
            ProcessNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            proc.Id);

        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            if (ProcessNativeMethods.GetProcessIoCounters(handle, out var io))
            {
                return io.ReadTransferCount + io.WriteTransferCount;
            }
        }
        finally
        {
            ProcessNativeMethods.CloseHandle(handle);
        }

        return 0;
    }

    private static string FormatCulpritName(string processName)
    {
        return FriendlyProcessNames.TryGetValue(processName, out string? friendly)
            ? $"{processName}.exe ({friendly})"
            : $"{processName}.exe";
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLoopLocked();
        }

        _dpcCounter?.Dispose();
        _dpcCounter = null;
        _queueCounter?.Dispose();
        _queueCounter = null;
    }
}

