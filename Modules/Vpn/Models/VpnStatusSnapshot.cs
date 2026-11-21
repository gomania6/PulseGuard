using System.Text.Json.Serialization;

namespace PulseGuard.Modules.Vpn.Models;

public enum VpnConnectionState
{
    Disconnected,
    Connected,
    Error
}

public enum ConnectionQuality
{
    Good,
    Warning,
    Bad
}

/// <summary>
/// Deserialization model for %AppData%\PulseGuard\vpn_status.json written by external VPN clients.
/// </summary>
public sealed class VpnStatusFileModel
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("externalIp")]
    public string? ExternalIp { get; set; }

    [JsonPropertyName("pingMs")]
    public long? PingMs { get; set; }
}

/// <summary>
/// Evaluated VPN tunnel state combining process presence, vpn_status.json, and 1.1.1.1 ping verification.
/// </summary>
public sealed record VpnStatusSnapshot(
    VpnConnectionState State,
    string StatusText,
    string CountryCode,
    string ServerName,
    string ExternalIp,
    long? PingMs,
    double PacketLossPercent,
    double JitterMs,
    ConnectionQuality Quality,
    bool IsProcessRunning,
    bool IsConfigured,
    string PingHost = "1.1.1.1")
{
    public static VpnStatusSnapshot CreateDefault(bool isConfigured = false) => new(
        VpnConnectionState.Disconnected,
        "Выключен",
        string.Empty,
        "—",
        "—",
        null,
        0,
        0,
        ConnectionQuality.Good,
        false,
        isConfigured);
}

