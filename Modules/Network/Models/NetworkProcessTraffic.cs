namespace PulseGuard.Modules.Network.Models;

/// <summary>
/// Real-time network throughput for a grouped process (by ProcessName).
/// </summary>
public sealed record NetworkProcessTraffic(
    string ProcessName,
    IReadOnlyList<int> Pids,
    double DownloadMbps,
    double UploadMbps,
    bool IsProtected,
    bool IsSuspended,
    string? ExecutablePath);

/// <summary>
/// Aggregated 1-second snapshot of top network-consuming processes.
/// </summary>
public sealed class NetworkRadarSnapshot
{
    public static NetworkRadarSnapshot Empty { get; } = new();

    public IReadOnlyList<NetworkProcessTraffic> TopProcesses { get; init; } = Array.Empty<NetworkProcessTraffic>();
    public NetworkProcessTraffic? TopConsumer { get; init; }
    public double TotalTrackedDownloadMbps { get; init; }
    public double TotalTrackedUploadMbps { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

