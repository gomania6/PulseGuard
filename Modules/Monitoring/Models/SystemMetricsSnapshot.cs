namespace PulseGuard.Modules.Monitoring.Models;

/// <summary>
/// Snapshot of CPU, GPU, RAM, and Network metrics collected in a single polling cycle.
/// </summary>
public sealed class SystemMetricsSnapshot
{
    public string CpuName { get; set; } = "CPU";
    public double CpuLoadPercent { get; set; }
    public double? CpuTemperatureC { get; set; }
    public double? CpuClockMhz { get; set; }
    public bool CpuClockIsBaseOnly { get; set; }
    public string? CpuDiagnosticError { get; set; }

    public string GpuName { get; set; } = "GPU";
    public double? GpuLoadPercent { get; set; }
    public double? GpuTemperatureC { get; set; }
    public double? GpuVramUsedMb { get; set; }
    public double? GpuVramTotalMb { get; set; }

    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double RamLoadPercent { get; set; }

    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;
}

