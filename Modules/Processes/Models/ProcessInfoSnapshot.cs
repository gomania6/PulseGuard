using System.Diagnostics;

namespace PulseGuard.Modules.Processes.Models;

/// <summary>
/// Immutable snapshot of a grouped application (one or more OS processes with the same ProcessName).
/// </summary>
public sealed record ProcessInfoSnapshot(
    string Name,
    IReadOnlyList<int> Pids,
    int InstanceCount,
    double CpuPercent,
    double MemoryMb,
    double DiskMbPerSec,
    int BasePriority,
    ProcessPriorityClass? PriorityClass,
    bool IsSystemProtected,
    bool IsBackgroundSystemHost);

/// <summary>
/// Aggregated snapshot of grouped running processes and top resource consumers.
/// </summary>
public sealed record ProcessesSnapshot(
    IReadOnlyList<ProcessInfoSnapshot> Processes,
    ProcessInfoSnapshot? TopCpuProcess,
    ProcessInfoSnapshot? TopMemoryProcess,
    ProcessInfoSnapshot? TopDiskProcess,
    int TotalRawProcessCount,
    DateTime Timestamp)
{
    public static ProcessesSnapshot Empty { get; } = new(
        Array.Empty<ProcessInfoSnapshot>(),
        null,
        null,
        null,
        0,
        DateTime.MinValue);
}
