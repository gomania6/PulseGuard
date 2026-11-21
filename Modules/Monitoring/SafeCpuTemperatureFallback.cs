using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Text;

namespace PulseGuard.Modules.Monitoring;

/// <summary>
/// Safe user-mode CPU temperature fallback cascade (Sources 2-5):
/// 2) HWiNFO64 Shared Memory (Global\HWiNFO_SENS_SM2)
/// 3) WMI namespaces root\LibreHardwareMonitor and root\OpenHardwareMonitor
/// 4) WMI MSAcpi_ThermalZoneTemperature in root\WMI
/// 5) PDH Thermal Zone Information (High Precision Temperature and Temperature)
/// </summary>
internal sealed class SafeCpuTemperatureFallback : IDisposable
{
    private const string HwInfoSharedMemoryName = @"Global\HWiNFO_SENS_SM2";
    private const uint HwInfoSignature = 0x4E495748; // "HWiN"
    private const int SensorTypeTemp = 1;

    private static readonly string[] WmiMonitorNamespaces =
    [
        @"root\LibreHardwareMonitor",
        @"root\OpenHardwareMonitor"
    ];

    private readonly byte[] _hwInfoOrigLabelBuf = new byte[128];
    private readonly byte[] _hwInfoUserLabelBuf = new byte[128];
    private readonly List<PerformanceCounter> _highPrecisionThermalCounters = [];
    private readonly List<PerformanceCounter> _standardThermalCounters = [];

    private bool _thermalCountersInitialized;
    private DateTime _nextHwInfoProbeUtc = DateTime.MinValue;
    private DateTime _nextWmiMonitorProbeUtc = DateTime.MinValue;
    private DateTime _nextAcpiWmiProbeUtc = DateTime.MinValue;
    private string? _preferredWmiNamespace;
    private bool _disposed;

    public (double? TemperatureC, string? SourceLabel) TryReadTemperature()
    {
        if (_disposed)
        {
            return (null, null);
        }

        // Source 2: HWiNFO64 Shared Memory
        if (TryReadFromHwInfo(out double hwInfoTemp))
        {
            return (hwInfoTemp, "HWiNFO64");
        }

        // Source 3: WMI LibreHardwareMonitor / OpenHardwareMonitor
        if (TryReadFromWmiMonitor(out double wmiTemp, out string? wmiSource))
        {
            return (wmiTemp, wmiSource);
        }

        // Source 4: WMI MSAcpi_ThermalZoneTemperature (root\WMI)
        if (TryReadFromAcpiWmi(out double acpiWmiTemp))
        {
            return (acpiWmiTemp, "WMI (ACPI Thermal Zone)");
        }

        // Source 5: PDH Thermal Zone Information
        if (TryReadFromPdhThermalZone(out double pdhTemp))
        {
            return (pdhTemp, "PDH (Thermal Zone)");
        }

        return (null, null);
    }

    public void AppendDiagnostics(StringBuilder sb)
    {
        // Source 2: HWiNFO64
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(HwInfoSharedMemoryName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            uint signature = accessor.Capacity >= 4 ? accessor.ReadUInt32(0) : 0;
            bool ok = TryReadFromHwInfoInternal(accessor, out double hwVal, out string? hwSensor);
            sb.AppendLine(ok
                ? $"[Source 2] HWiNFO64 Shared Memory: OK -> {hwVal:0.0} C (sensor: {hwSensor})"
                : $"[Source 2] HWiNFO64 Shared Memory: opened (sig=0x{signature:X8}, cap={accessor.Capacity}), no matching CPU temp sensor");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Source 2] HWiNFO64 Shared Memory: unavailable ({ex.GetType().Name}: {ex.Message})");
        }

        // Source 3: WMI LibreHardwareMonitor / OpenHardwareMonitor
        foreach (string ns in WmiMonitorNamespaces)
        {
            if (TryQueryWmiMonitorNamespace(ns, out double wmiVal, out string? wmiSensor, out string? wmiErr))
            {
                sb.AppendLine($"[Source 3] WMI {ns}: OK -> {wmiVal:0.0} C (sensor: {wmiSensor})");
            }
            else
            {
                sb.AppendLine($"[Source 3] WMI {ns}: {wmiErr ?? "no CPU temp sensors"}");
            }
        }

        // Source 4: WMI MSAcpi_ThermalZoneTemperature
        if (TryReadFromAcpiWmiInternal(out double acpiWmiVal, out string? acpiWmiDetails))
        {
            sb.AppendLine($"[Source 4] WMI root\\WMI (MSAcpi_ThermalZoneTemperature): OK -> {acpiWmiVal:0.0} C ({acpiWmiDetails})");
        }
        else
        {
            sb.AppendLine($"[Source 4] WMI root\\WMI (MSAcpi_ThermalZoneTemperature): {acpiWmiDetails}");
        }

        // Source 5: PDH Thermal Zone Information
        EnsureThermalCounters();
        sb.AppendLine($"[Source 5] PDH Thermal Zone Information: HighPrecisionCounters={_highPrecisionThermalCounters.Count}, StandardCounters={_standardThermalCounters.Count}");
        LogPdhCounterValues(sb, "High Precision Temperature", _highPrecisionThermalCounters);
        LogPdhCounterValues(sb, "Temperature", _standardThermalCounters);
    }

    private static void LogPdhCounterValues(StringBuilder sb, string counterName, List<PerformanceCounter> counters)
    {
        for (int i = 0; i < counters.Count; i++)
        {
            try
            {
                float raw = counters[i].NextValue();
                double c = NormalizeThermalZoneKelvinToCelsius(raw);
                bool accepted = IsValidThermalZoneCelsius(c);
                sb.AppendLine($"  - {counterName} [{counters[i].InstanceName}]: raw={raw.ToString("0.##", CultureInfo.InvariantCulture)}, C={c:0.00} C, accepted={accepted}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  - {counterName} [{counters[i].InstanceName}]: error ({ex.Message})");
            }
        }
    }

    private bool TryReadFromHwInfo(out double temperatureC)
    {
        temperatureC = 0;

        var now = DateTime.UtcNow;
        if (now < _nextHwInfoProbeUtc)
        {
            return false;
        }

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(HwInfoSharedMemoryName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return TryReadFromHwInfoInternal(accessor, out temperatureC, out _);
        }
        catch
        {
            _nextHwInfoProbeUtc = now.AddSeconds(5);
            return false;
        }
    }

    private bool TryReadFromHwInfoInternal(MemoryMappedViewAccessor accessor, out double temperatureC, out string? matchedLabel)
    {
        temperatureC = 0;
        matchedLabel = null;

        if (accessor.Capacity < 44)
        {
            return false;
        }

        uint signature = accessor.ReadUInt32(0);
        if (signature != HwInfoSignature)
        {
            return false;
        }

        uint readingOffset = accessor.ReadUInt32(32);
        uint readingElementSize = accessor.ReadUInt32(36);
        uint readingCount = accessor.ReadUInt32(40);

        if (readingElementSize < 292 || readingCount == 0 || readingCount > 4096)
        {
            return false;
        }

        long requiredBytes = readingOffset + (long)readingElementSize * readingCount;
        if (requiredBytes > accessor.Capacity)
        {
            return false;
        }

        double? bestTemp = null;
        int bestPriority = 0;

        for (uint i = 0; i < readingCount; i++)
        {
            long baseOffset = readingOffset + (long)i * readingElementSize;
            int readingType = accessor.ReadInt32(baseOffset);
            if (readingType != SensorTypeTemp)
            {
                continue;
            }

            double val = accessor.ReadDouble(baseOffset + 284);
            if (double.IsNaN(val) || double.IsInfinity(val) || val < 20.0 || val > 125.0)
            {
                continue;
            }

            accessor.ReadArray(baseOffset + 12, _hwInfoOrigLabelBuf, 0, _hwInfoOrigLabelBuf.Length);
            string origLabel = ReadNullTerminatedAscii(_hwInfoOrigLabelBuf);
            string activeLabel = origLabel;

            int priority = GetCpuTempSensorPriority(origLabel);
            if (priority == 0 && readingElementSize >= 268)
            {
                accessor.ReadArray(baseOffset + 140, _hwInfoUserLabelBuf, 0, _hwInfoUserLabelBuf.Length);
                string userLabel = ReadNullTerminatedAscii(_hwInfoUserLabelBuf);
                if (!string.IsNullOrWhiteSpace(userLabel))
                {
                    priority = GetCpuTempSensorPriority(userLabel);
                    if (priority > 0)
                    {
                        activeLabel = userLabel;
                    }
                }
            }

            if (priority > bestPriority || (priority == bestPriority && priority > 0 && (!bestTemp.HasValue || val > bestTemp.Value)))
            {
                bestPriority = priority;
                bestTemp = val;
                matchedLabel = activeLabel;
            }
        }

        if (bestTemp.HasValue)
        {
            temperatureC = bestTemp.Value;
            return true;
        }

        return false;
    }

    private bool TryReadFromWmiMonitor(out double temperatureC, out string? sourceLabel)
    {
        temperatureC = 0;
        sourceLabel = null;

        var now = DateTime.UtcNow;
        if (now < _nextWmiMonitorProbeUtc)
        {
            return false;
        }

        if (_preferredWmiNamespace is not null)
        {
            if (TryQueryWmiMonitorNamespace(_preferredWmiNamespace, out temperatureC, out _, out _))
            {
                sourceLabel = FormatWmiMonitorLabel(_preferredWmiNamespace);
                return true;
            }

            _preferredWmiNamespace = null;
        }

        for (int i = 0; i < WmiMonitorNamespaces.Length; i++)
        {
            string ns = WmiMonitorNamespaces[i];
            if (TryQueryWmiMonitorNamespace(ns, out temperatureC, out _, out _))
            {
                _preferredWmiNamespace = ns;
                sourceLabel = FormatWmiMonitorLabel(ns);
                return true;
            }
        }

        _nextWmiMonitorProbeUtc = now.AddSeconds(10);
        return false;
    }

    private static bool TryQueryWmiMonitorNamespace(string wmiNamespace, out double temperatureC, out string? matchedName, out string? error)
    {
        temperatureC = 0;
        matchedName = null;
        error = null;

        try
        {
            var scope = new ManagementScope(wmiNamespace);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT Name, Value FROM Sensor WHERE SensorType = 'Temperature'"));
            using var results = searcher.Get();

            double? bestTemp = null;
            int bestPriority = 0;

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    string? name = obj["Name"] as string;
                    object? rawValue = obj["Value"];
                    if (string.IsNullOrWhiteSpace(name) || rawValue is null)
                    {
                        continue;
                    }

                    double val = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                    if (double.IsNaN(val) || double.IsInfinity(val) || val < 20.0 || val > 125.0)
                    {
                        continue;
                    }

                    int priority = GetCpuTempSensorPriority(name);
                    if (priority > bestPriority || (priority == bestPriority && priority > 0 && (!bestTemp.HasValue || val > bestTemp.Value)))
                    {
                        bestPriority = priority;
                        bestTemp = val;
                        matchedName = name;
                    }
                }
            }

            if (bestTemp.HasValue)
            {
                temperatureC = bestTemp.Value;
                return true;
            }

            error = "connected, no CPU temperature sensors in range 20..125 C";
        }
        catch (Exception ex)
        {
            error = $"unavailable ({ex.GetType().Name}: {ex.Message})";
        }

        return false;
    }

    private static string FormatWmiMonitorLabel(string wmiNamespace) =>
        wmiNamespace.Contains("OpenHardwareMonitor", StringComparison.OrdinalIgnoreCase)
            ? "WMI (OpenHardwareMonitor)"
            : "WMI (LibreHardwareMonitor)";

    private bool TryReadFromAcpiWmi(out double temperatureC)
    {
        temperatureC = 0;
        var now = DateTime.UtcNow;
        if (now < _nextAcpiWmiProbeUtc)
        {
            return false;
        }

        if (TryReadFromAcpiWmiInternal(out temperatureC, out _))
        {
            return true;
        }

        _nextAcpiWmiProbeUtc = now.AddSeconds(10);
        return false;
    }

    private static bool TryReadFromAcpiWmiInternal(out double temperatureC, out string details)
    {
        temperatureC = 0;
        details = string.Empty;

        try
        {
            var scope = new ManagementScope(@"root\WMI");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
            using var results = searcher.Get();

            double? maxValid = null;
            int totalZones = 0;
            var zoneSummary = new StringBuilder();

            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    totalZones++;
                    object? raw = obj["CurrentTemperature"];
                    if (raw is null)
                    {
                        continue;
                    }

                    double tenthKelvin = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    double celsius = (tenthKelvin / 10.0) - 273.15;
                    bool valid = IsValidThermalZoneCelsius(celsius);

                    if (zoneSummary.Length > 0)
                    {
                        zoneSummary.Append(", ");
                    }
                    zoneSummary.Append($"{celsius:0.00}C({(valid ? "ok" : "rejected")})");

                    if (valid && (!maxValid.HasValue || celsius > maxValid.Value))
                    {
                        maxValid = celsius;
                    }
                }
            }

            details = totalZones == 0 ? "0 zones returned" : $"zones={totalZones}: {zoneSummary}";
            if (maxValid.HasValue)
            {
                temperatureC = maxValid.Value;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            details = $"unavailable ({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }

    private bool TryReadFromPdhThermalZone(out double temperatureC)
    {
        temperatureC = 0;
        EnsureThermalCounters();

        if (TryReadBestFromCounters(_highPrecisionThermalCounters, out temperatureC))
        {
            return true;
        }

        if (TryReadBestFromCounters(_standardThermalCounters, out temperatureC))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadBestFromCounters(List<PerformanceCounter> counters, out double temperatureC)
    {
        temperatureC = 0;
        double? maxTemp = null;

        for (int i = 0; i < counters.Count; i++)
        {
            try
            {
                float raw = counters[i].NextValue();
                double celsius = NormalizeThermalZoneKelvinToCelsius(raw);
                if (!IsValidThermalZoneCelsius(celsius))
                {
                    continue;
                }

                if (!maxTemp.HasValue || celsius > maxTemp.Value)
                {
                    maxTemp = celsius;
                }
            }
            catch
            {
            }
        }

        if (maxTemp.HasValue)
        {
            temperatureC = maxTemp.Value;
            return true;
        }

        return false;
    }

    private static double NormalizeThermalZoneKelvinToCelsius(double rawValue)
    {
        double kelvin = rawValue > 1000.0 ? rawValue / 10.0 : rawValue;
        return kelvin - 273.15;
    }

    private static bool IsValidThermalZoneCelsius(double celsius)
    {
        if (double.IsNaN(celsius) || double.IsInfinity(celsius) || celsius < 22.0 || celsius > 115.0)
        {
            return false;
        }

        // Filter known static BIOS ACPI stubs (27.85 C = 301 K, 29.85 C = 303 K).
        if (Math.Abs(celsius - 27.85) < 0.25 || Math.Abs(celsius - 29.85) < 0.25)
        {
            return false;
        }

        return true;
    }

    private static int GetCpuTempSensorPriority(string sensorName)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return 0;
        }

        if (sensorName.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Drive", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("VRM", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("PCH", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("DIMM", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Ambient", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Water", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Liquid", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (sensorName.Contains("CPU Package", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("CPU (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("CPU CCD1", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Core Max", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("CPU DTS", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (sensorName.Contains("CPU CCD", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Core Average", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (sensorName.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private void EnsureThermalCounters()
    {
        if (_thermalCountersInitialized)
        {
            return;
        }

        _thermalCountersInitialized = true;
        try
        {
            var category = new PerformanceCounterCategory("Thermal Zone Information");
            string[] instances = category.GetInstanceNames();
            foreach (string instance in instances)
            {
                try
                {
                    if (category.CounterExists("High Precision Temperature"))
                    {
                        var hpCounter = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", instance, readOnly: true);
                        _ = hpCounter.NextValue();
                        _highPrecisionThermalCounters.Add(hpCounter);
                    }
                }
                catch
                {
                }

                try
                {
                    if (category.CounterExists("Temperature"))
                    {
                        var stdCounter = new PerformanceCounter("Thermal Zone Information", "Temperature", instance, readOnly: true);
                        _ = stdCounter.NextValue();
                        _standardThermalCounters.Add(stdCounter);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static string ReadNullTerminatedAscii(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0)
        {
            len = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer, 0, len);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var counter in _highPrecisionThermalCounters)
        {
            counter.Dispose();
        }
        _highPrecisionThermalCounters.Clear();

        foreach (var counter in _standardThermalCounters)
        {
            counter.Dispose();
        }
        _standardThermalCounters.Clear();
    }
}

