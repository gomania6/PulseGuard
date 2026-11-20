using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;

namespace PulseGuard.Modules.Optimization;

public readonly record struct MemoryStatusInfo(double UsedGb, double FreeGb, double TotalGb, double UsedPercent);

/// <summary>
/// Safely trims working sets of non-blacklisted user processes using Win32 EmptyWorkingSet.
/// Never kills processes and strictly skips ProcessBlacklist and PulseGuard.
/// </summary>
public sealed class SafeRamCleanerTool : IOptimizerTool
{
    public string Id => "safe-ram-cleaner";
    public string Name => "Безопасная очистка ОЗУ";
    public string Description => "Вызывает EmptyWorkingSet для пользовательских процессов, не затрагивая системный чёрный список.";
    public bool CanExecute => true;

    public static MemoryStatusInfo GetMemoryStatus()
    {
        var mem = new OptimizationNativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<OptimizationNativeMethods.MEMORYSTATUSEX>()
        };

        if (!OptimizationNativeMethods.GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0)
        {
            return new MemoryStatusInfo(0, 0, 0, 0);
        }

        const double gbDivisor = 1024.0 * 1024.0 * 1024.0;
        var totalGb = mem.ullTotalPhys / gbDivisor;
        var freeGb = mem.ullAvailPhys / gbDivisor;
        var usedGb = Math.Max(0, totalGb - freeGb);
        var usedPercent = (usedGb / totalGb) * 100.0;

        return new MemoryStatusInfo(usedGb, freeGb, totalGb, usedPercent);
    }

    public Task<OptimizationPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var status = GetMemoryStatus();
        return Task.FromResult(new OptimizationPreview(
            $"Занято {status.UsedGb:F1} ГБ из {status.TotalGb:F1} ГБ",
            0,
            0));
    }

    public Task<OptimizationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var beforeBytes = GetUsedMemoryBytes();
            var currentPid = Environment.ProcessId;
            var processedCount = 0;

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                return new OptimizationResult(false, $"Не удалось получить список процессов: {ex.Message}", 0, 0);
            }

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

                    if (pid <= 4 || pid == currentPid || ProcessBlacklist.IsProtected(name))
                    {
                        continue;
                    }

                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        handle = OptimizationNativeMethods.OpenProcess(
                            OptimizationNativeMethods.PROCESS_QUERY_INFORMATION | OptimizationNativeMethods.PROCESS_SET_QUOTA,
                            false,
                            pid);

                        if (handle == IntPtr.Zero)
                        {
                            handle = OptimizationNativeMethods.OpenProcess(
                                OptimizationNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | OptimizationNativeMethods.PROCESS_SET_QUOTA,
                                false,
                                pid);
                        }

                        if (handle != IntPtr.Zero && OptimizationNativeMethods.EmptyWorkingSet(handle))
                        {
                            processedCount++;
                        }
                    }
                    catch
                    {
                        // Silently skip inaccessible or exited processes.
                    }
                    finally
                    {
                        if (handle != IntPtr.Zero)
                        {
                            OptimizationNativeMethods.CloseHandle(handle);
                        }
                    }
                }
            }

            // Allow Windows memory manager a brief moment to update working set counters.
            Thread.Sleep(150);

            var afterBytes = GetUsedMemoryBytes();
            var reclaimedBytes = beforeBytes > afterBytes ? (long)(beforeBytes - afterBytes) : 0L;

            var message = reclaimedBytes > 0
                ? $"Освобождено: {FormatBytes(reclaimedBytes)} ОЗУ (обработано процессов: {processedCount})"
                : $"Очистка выполнена (обработано процессов: {processedCount})";

            return new OptimizationResult(true, message, reclaimedBytes, processedCount);
        }, cancellationToken);
    }

    private static ulong GetUsedMemoryBytes()
    {
        var mem = new OptimizationNativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<OptimizationNativeMethods.MEMORYSTATUSEX>()
        };

        return OptimizationNativeMethods.GlobalMemoryStatusEx(ref mem)
            ? mem.ullTotalPhys - mem.ullAvailPhys
            : 0UL;
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024.0 * 1024.0;
        const double gb = mb * 1024.0;
        return bytes >= gb
            ? $"{bytes / gb:F1} ГБ"
            : $"{bytes / mb:F0} МБ";
    }
}

