using System.Diagnostics;
using PulseGuard.Modules.Optimization.Models;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Detects active/installed anti-cheat drivers and services (Riot Vanguard, BattlEye, EasyAntiCheat)
/// as well as Steam so PulseGuard enforces BYOVD protection and never conflicts with kernel anti-cheats.
/// </summary>
public sealed class AntiCheatDetectorService
{
    public const string VanguardRestrictionTooltip =
        "Обнаружен Riot Vanguard — параметр заблокирован во избежание ошибки VAN: RESTRICTION";

    private readonly object _syncLock = new();
    private AntiCheatStatus _currentStatus;

    public AntiCheatStatus CurrentStatus
    {
        get
        {
            lock (_syncLock)
            {
                return _currentStatus;
            }
        }
    }

    public bool IsVanguardDetected => CurrentStatus.IsVanguardDetected;

    public event Action<AntiCheatStatus>? StatusChanged;

    public AntiCheatDetectorService()
    {
        _currentStatus = ScanEnvironment();
    }

    public AntiCheatStatus Refresh()
    {
        var updated = ScanEnvironment();
        bool changed;
        lock (_syncLock)
        {
            changed = !Equals(_currentStatus, updated);
            _currentStatus = updated;
        }

        if (changed)
        {
            StatusChanged?.Invoke(updated);
        }

        return updated;
    }

    private static AntiCheatStatus ScanEnvironment()
    {
        bool vanguard = IsServicePresent("vgc") || IsProcessRunning("vgc") || IsProcessRunning("vgtray");
        bool battlEye = IsServicePresent("BEService") || IsProcessRunning("BEService");
        bool eac = IsServicePresent("EasyAntiCheat") || IsServicePresent("EasyAntiCheat_EOS") || IsProcessRunning("EasyAntiCheat");
        bool steam = IsProcessRunning("steam");

        var detected = new List<string>(4);
        if (vanguard) detected.Add("Riot Vanguard (vgc)");
        if (battlEye) detected.Add("BattlEye (BEService)");
        if (eac) detected.Add("EasyAntiCheat");
        if (steam) detected.Add("Steam (VAC)");

        string summary = detected.Count > 0
            ? $"Активная защита: {string.Join(", ", detected)}"
            : "Античиты (vgc, BEService, EAC) и Steam не обнаружены";

        return new AntiCheatStatus(vanguard, battlEye, eac, steam, summary);
    }

    public static bool IsServicePresent(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            scm = OptimizationNativeMethods.OpenSCManagerW(null, null, OptimizationNativeMethods.SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                return false;
            }

            service = OptimizationNativeMethods.OpenServiceW(scm, serviceName, OptimizationNativeMethods.SERVICE_QUERY_STATUS);
            return service != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                OptimizationNativeMethods.CloseServiceHandle(service);
            }

            if (scm != IntPtr.Zero)
            {
                OptimizationNativeMethods.CloseServiceHandle(scm);
            }
        }
    }

    public static bool IsServiceRunning(string serviceName)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            scm = OptimizationNativeMethods.OpenSCManagerW(null, null, OptimizationNativeMethods.SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                return false;
            }

            service = OptimizationNativeMethods.OpenServiceW(scm, serviceName, OptimizationNativeMethods.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            return OptimizationNativeMethods.QueryServiceStatus(service, out var status)
                && status.dwCurrentState == OptimizationNativeMethods.SERVICE_RUNNING;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                OptimizationNativeMethods.CloseServiceHandle(service);
            }

            if (scm != IntPtr.Zero)
            {
                OptimizationNativeMethods.CloseServiceHandle(scm);
            }
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            bool any = procs.Length > 0;
            foreach (var p in procs)
            {
                p.Dispose();
            }

            return any;
        }
        catch
        {
            return false;
        }
    }
}

