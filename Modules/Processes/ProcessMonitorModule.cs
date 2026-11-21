using System.Diagnostics;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;
using PulseGuard.Modules.Processes.Models;

namespace PulseGuard.Modules.Processes;

/// <summary>
/// Modular process monitor grouping processes by ProcessName, collecting CPU %, RAM (MB),
/// Disk I/O (MB/s), and priority with strict ProcessBlacklist protection and safe group actions.
/// </summary>
public sealed class ProcessMonitorModule : IMonitorModule
{
    private readonly record struct ProcessSample(
        string ProcessName,
        TimeSpan TotalProcessorTime,
        ulong TotalIoBytes,
        long TimestampTicks);

    private sealed class GroupAccumulator
    {
        public string Name = string.Empty;
        public readonly List<int> Pids = new(8);
        public double CpuPercent;
        public double MemoryMb;
        public double DiskMbPerSec;
        public int BasePriority;
        public ProcessPriorityClass? PriorityClass;
        public bool HasNormalOrHigherPriority;
        public bool IsSystemProtected;
        public bool IsBackgroundSystemHost;

        public void Reset(string name, bool isProtected, bool isBackgroundHost)
        {
            Name = name;
            Pids.Clear();
            CpuPercent = 0;
            MemoryMb = 0;
            DiskMbPerSec = 0;
            BasePriority = 0;
            PriorityClass = null;
            HasNormalOrHigherPriority = false;
            IsSystemProtected = isProtected;
            IsBackgroundSystemHost = isBackgroundHost;
        }
    }

    private static readonly HashSet<string> BackgroundSystemHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "RuntimeBroker",
        "fontdrvhost",
        "conhost",
        "ctfmon",
        "dllhost",
        "SearchHost",
        "StartMenuExperienceHost",
        "TextInputHost",
        "sihost",
        "taskhostw",
        "WmiPrvSE"
    };

    private readonly object _syncLock = new();
    private readonly object _suspendLock = new();
    private readonly HashSet<int> _suspendedProcessIds = new();
    private Dictionary<int, ProcessSample> _previousSamples = new(256);
    private Dictionary<int, ProcessSample> _nextSamples = new(256);
    private readonly Dictionary<string, GroupAccumulator> _groups = new(128, StringComparer.OrdinalIgnoreCase);
    private readonly Stack<GroupAccumulator> _accumulatorPool = new(128);
    private readonly List<ProcessInfoSnapshot> _snapshotBuffer = new(128);
    private bool _disposed;

    public string Id => "process-monitor";
    public string Name => "Монитор процессов";
    public string Description => "Групповой мониторинг CPU, ОЗУ, дискового ввода-вывода и приоритетов процессов с защитой ProcessBlacklist.";
    public bool IsEnabled { get; set; } = true;

    public ProcessesSnapshot LatestSnapshot { get; private set; } = ProcessesSnapshot.Empty;

    public IReadOnlySet<int> SuspendedProcessIds
    {
        get
        {
            lock (_suspendLock)
            {
                return new HashSet<int>(_suspendedProcessIds);
            }
        }
    }

    public event Action? SuspendedProcessesChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _disposed)
        {
            return Task.CompletedTask;
        }

        var nowTicks = Stopwatch.GetTimestamp();
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var currentProcessId = Environment.ProcessId;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return Task.CompletedTask;
        }

        lock (_syncLock)
        {
            _nextSamples.Clear();
            _snapshotBuffer.Clear();

            foreach (var acc in _groups.Values)
            {
                _accumulatorPool.Push(acc);
            }
            _groups.Clear();

            var totalRawCount = processes.Length;

            foreach (var process in processes)
            {
                using (process)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    int pid;
                    string name;
                    try
                    {
                        pid = process.Id;
                        name = process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    var isProtected = pid == 0
                        || pid == 4
                        || pid == currentProcessId
                        || ProcessBlacklist.IsProtected(name);

                    var isBackgroundHost = !isProtected && BackgroundSystemHosts.Contains(name);

                    double memoryMb = 0;
                    try
                    {
                        memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
                    }
                    catch
                    {
                        memoryMb = 0;
                    }

                    var basePriority = 0;
                    try
                    {
                        basePriority = process.BasePriority;
                    }
                    catch
                    {
                        basePriority = 0;
                    }

                    ProcessPriorityClass? priorityClass = null;
                    try
                    {
                        priorityClass = process.PriorityClass;
                    }
                    catch
                    {
                        priorityClass = null;
                    }

                    var hasCpuTime = false;
                    var totalCpuTime = TimeSpan.Zero;
                    try
                    {
                        totalCpuTime = process.TotalProcessorTime;
                        hasCpuTime = true;
                    }
                    catch
                    {
                        hasCpuTime = false;
                    }

                    var hasIoCounters = TryGetProcessIoBytes(pid, out var totalIoBytes);

                    double cpuPercent = 0;
                    double diskMbPerSec = 0;

                    if (_previousSamples.TryGetValue(pid, out var prev)
                        && string.Equals(prev.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        var elapsedSeconds = Stopwatch.GetElapsedTime(prev.TimestampTicks, nowTicks).TotalSeconds;
                        if (elapsedSeconds > 0.05)
                        {
                            if (hasCpuTime && prev.TotalProcessorTime >= TimeSpan.Zero)
                            {
                                var deltaCpuSeconds = (totalCpuTime - prev.TotalProcessorTime).TotalSeconds;
                                if (deltaCpuSeconds > 0)
                                {
                                    cpuPercent = Math.Clamp((deltaCpuSeconds / (elapsedSeconds * processorCount)) * 100.0, 0.0, 100.0);
                                }
                            }

                            if (hasIoCounters && totalIoBytes >= prev.TotalIoBytes)
                            {
                                var deltaIoBytes = totalIoBytes - prev.TotalIoBytes;
                                diskMbPerSec = (deltaIoBytes / (1024.0 * 1024.0)) / elapsedSeconds;
                            }
                        }
                    }

                    _nextSamples[pid] = new ProcessSample(
                        name,
                        hasCpuTime ? totalCpuTime : TimeSpan.Zero,
                        hasIoCounters ? totalIoBytes : 0UL,
                        nowTicks);

                    if (!_groups.TryGetValue(name, out var group))
                    {
                        group = _accumulatorPool.Count > 0 ? _accumulatorPool.Pop() : new GroupAccumulator();
                        group.Reset(name, isProtected, isBackgroundHost);
                        _groups[name] = group;
                    }

                    group.Pids.Add(pid);
                    group.CpuPercent = Math.Min(100.0, group.CpuPercent + cpuPercent);
                    group.MemoryMb += memoryMb;
                    group.DiskMbPerSec += diskMbPerSec;
                    if (isProtected)
                    {
                        group.IsSystemProtected = true;
                    }

                    if (basePriority > group.BasePriority)
                    {
                        group.BasePriority = basePriority;
                    }

                    if (priorityClass.HasValue)
                    {
                        if (priorityClass.Value is not (ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle))
                        {
                            group.HasNormalOrHigherPriority = true;
                            group.PriorityClass = priorityClass.Value;
                        }
                        else if (!group.HasNormalOrHigherPriority)
                        {
                            group.PriorityClass = priorityClass.Value;
                        }
                    }
                }
            }

            ProcessInfoSnapshot? topCpu = null;
            ProcessInfoSnapshot? topMemory = null;
            ProcessInfoSnapshot? topDisk = null;

            foreach (var group in _groups.Values)
            {
                var snapshot = new ProcessInfoSnapshot(
                    group.Name,
                    group.Pids.ToArray(),
                    group.Pids.Count,
                    group.CpuPercent,
                    group.MemoryMb,
                    group.DiskMbPerSec,
                    group.BasePriority,
                    group.PriorityClass,
                    group.IsSystemProtected,
                    group.IsBackgroundSystemHost);

                _snapshotBuffer.Add(snapshot);

                if (!snapshot.IsSystemProtected && !snapshot.IsBackgroundSystemHost)
                {
                    if (topCpu is null || snapshot.CpuPercent > topCpu.CpuPercent)
                    {
                        topCpu = snapshot;
                    }

                    if (topMemory is null || snapshot.MemoryMb > topMemory.MemoryMb)
                    {
                        topMemory = snapshot;
                    }

                    if (topDisk is null || snapshot.DiskMbPerSec > topDisk.DiskMbPerSec)
                    {
                        topDisk = snapshot;
                    }
                }
            }

            // Swap sample dictionaries to minimize allocations on the next tick.
            (_previousSamples, _nextSamples) = (_nextSamples, _previousSamples);

            LatestSnapshot = new ProcessesSnapshot(
                _snapshotBuffer.ToArray(),
                topCpu,
                topMemory,
                topDisk,
                totalRawCount,
                DateTime.Now);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Safely toggles priority between BelowNormal and Normal for all non-protected PIDs in a process group.
    /// </summary>
    public bool ToggleGroupPriority(
        IReadOnlyList<int> pids,
        string processName,
        bool currentlyLowPriority,
        out ProcessPriorityClass? newPriority,
        out string message)
    {
        newPriority = null;

        if (ProcessBlacklist.IsProtected(processName))
        {
            message = "Защищённый системный процесс Windows — изменение приоритета запрещено.";
            return false;
        }

        var target = currentlyLowPriority ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        var updatedCount = 0;

        foreach (var pid in pids)
        {
            if (IsProtectedPid(pid))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.PriorityClass = target;
                updatedCount++;
            }
            catch
            {
                // Skip exited or access-denied child instances silently.
            }
        }

        if (updatedCount == 0)
        {
            message = $"Не удалось изменить приоритет для «{processName}».";
            return false;
        }

        newPriority = target;
        var targetLabel = target == ProcessPriorityClass.BelowNormal ? "Ниже среднего" : "Обычный";
        message = pids.Count > 1
            ? $"Приоритет «{processName}» ({updatedCount} процессов) изменён на «{targetLabel}»."
            : $"Приоритет «{processName}» изменён на «{targetLabel}».";
        return true;
    }

    /// <summary>
    /// Safely trims the working set of all non-protected PIDs in a process group using Win32 EmptyWorkingSet.
    /// </summary>
    public bool TrimGroupWorkingSet(IReadOnlyList<int> pids, string processName, out string message)
    {
        if (ProcessBlacklist.IsProtected(processName))
        {
            message = "Защищённый системный процесс Windows — очистка ОЗУ запрещена.";
            return false;
        }

        var trimmedCount = 0;

        foreach (var pid in pids)
        {
            if (IsProtectedPid(pid))
            {
                continue;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = ProcessNativeMethods.OpenProcess(
                    ProcessNativeMethods.PROCESS_QUERY_INFORMATION | ProcessNativeMethods.PROCESS_SET_QUOTA,
                    false,
                    pid);

                if (handle == IntPtr.Zero)
                {
                    handle = ProcessNativeMethods.OpenProcess(
                        ProcessNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | ProcessNativeMethods.PROCESS_SET_QUOTA,
                        false,
                        pid);
                }

                if (handle != IntPtr.Zero && ProcessNativeMethods.EmptyWorkingSet(handle))
                {
                    trimmedCount++;
                }
            }
            catch
            {
                // Skip exited or inaccessible processes silently.
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    ProcessNativeMethods.CloseHandle(handle);
                }
            }
        }

        if (trimmedCount == 0)
        {
            message = $"Не удалось освободить ОЗУ для «{processName}».";
            return false;
        }

        message = pids.Count > 1
            ? $"ОЗУ освобождено для «{processName}» ({trimmedCount} процессов)."
            : $"ОЗУ освобождено для «{processName}» (EmptyWorkingSet).";
        return true;
    }

    public bool IsGroupSuspended(IEnumerable<int> pids)
    {
        lock (_suspendLock)
        {
            foreach (int pid in pids)
            {
                if (_suspendedProcessIds.Contains(pid))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Safely suspends all non-protected PIDs in a process group using ntdll.dll NtSuspendProcess.
    /// Strictly blocks System, Idle, explorer, dwm, svchost, PulseGuard, and the active VPN client.
    /// </summary>
    public bool SuspendGroup(IReadOnlyList<int> pids, string processName, out string message)
    {
        if (ProcessBlacklist.IsProtected(processName))
        {
            message = "Защищённый системный процесс или VPN-клиент — заморозка запрещена.";
            return false;
        }

        int suspendedCount = 0;

        foreach (int pid in pids)
        {
            if (IsProtectedPid(pid) || IsProtectedLiveProcess(pid))
            {
                continue;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = ProcessNativeMethods.OpenProcess(
                    ProcessNativeMethods.PROCESS_SUSPEND_RESUME,
                    false,
                    pid);

                if (handle != IntPtr.Zero && ProcessNativeMethods.NtSuspendProcess(handle) >= 0)
                {
                    lock (_suspendLock)
                    {
                        _suspendedProcessIds.Add(pid);
                    }
                    suspendedCount++;
                }
            }
            catch
            {
                // Skip exited or inaccessible processes silently.
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    ProcessNativeMethods.CloseHandle(handle);
                }
            }
        }

        if (suspendedCount == 0)
        {
            message = $"Не удалось заморозить «{processName}».";
            return false;
        }

        SuspendedProcessesChanged?.Invoke();
        message = pids.Count > 1
            ? $"Процесс «{processName}» ({suspendedCount} экз.) заморожен."
            : $"Процесс «{processName}» заморожен.";
        return true;
    }

    /// <summary>
    /// Resumes all suspended PIDs in a process group using ntdll.dll NtResumeProcess.
    /// </summary>
    public bool ResumeGroup(IReadOnlyList<int> pids, string processName, out string message)
    {
        int resumedCount = 0;

        foreach (int pid in pids)
        {
            if (IsProtectedPid(pid))
            {
                continue;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = ProcessNativeMethods.OpenProcess(
                    ProcessNativeMethods.PROCESS_SUSPEND_RESUME,
                    false,
                    pid);

                if (handle != IntPtr.Zero && ProcessNativeMethods.NtResumeProcess(handle) >= 0)
                {
                    resumedCount++;
                }
            }
            catch
            {
                // Skip exited or inaccessible processes silently.
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    ProcessNativeMethods.CloseHandle(handle);
                }

                lock (_suspendLock)
                {
                    _suspendedProcessIds.Remove(pid);
                }
            }
        }

        SuspendedProcessesChanged?.Invoke();

        if (resumedCount == 0)
        {
            message = $"Не удалось возобновить «{processName}» (возможно, процесс уже завершён).";
            return false;
        }

        message = pids.Count > 1
            ? $"Процесс «{processName}» ({resumedCount} экз.) возобновлён."
            : $"Процесс «{processName}» возобновлён.";
        return true;
    }

    /// <summary>
    /// Automatically resumes all processes suspended by PulseGuard (must be called on app exit).
    /// </summary>
    public void ResumeAllSuspendedProcesses()
    {
        int[] pidsToResume;
        lock (_suspendLock)
        {
            if (_suspendedProcessIds.Count == 0)
            {
                return;
            }

            pidsToResume = new int[_suspendedProcessIds.Count];
            _suspendedProcessIds.CopyTo(pidsToResume);
            _suspendedProcessIds.Clear();
        }

        foreach (int pid in pidsToResume)
        {
            if (IsProtectedPid(pid))
            {
                continue;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = ProcessNativeMethods.OpenProcess(
                    ProcessNativeMethods.PROCESS_SUSPEND_RESUME,
                    false,
                    pid);

                if (handle != IntPtr.Zero)
                {
                    ProcessNativeMethods.NtResumeProcess(handle);
                }
            }
            catch
            {
                // Best-effort resume on shutdown.
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    ProcessNativeMethods.CloseHandle(handle);
                }
            }
        }

        SuspendedProcessesChanged?.Invoke();
    }

    private static bool IsProtectedLiveProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return ProcessBlacklist.IsProtected(proc.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProtectedPid(int pid) => pid <= 4 || pid == Environment.ProcessId;

    private static bool TryGetProcessIoBytes(int pid, out ulong totalIoBytes)
    {
        totalIoBytes = 0;
        if (pid <= 4)
        {
            return false;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = ProcessNativeMethods.OpenProcess(
                ProcessNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                pid);

            if (handle == IntPtr.Zero)
            {
                return false;
            }

            if (ProcessNativeMethods.GetProcessIoCounters(handle, out var counters))
            {
                totalIoBytes = counters.ReadTransferCount + counters.WriteTransferCount;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                ProcessNativeMethods.CloseHandle(handle);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResumeAllSuspendedProcesses();
        lock (_syncLock)
        {
            _previousSamples.Clear();
            _nextSamples.Clear();
            _groups.Clear();
            _accumulatorPool.Clear();
            _snapshotBuffer.Clear();
        }
    }
}
