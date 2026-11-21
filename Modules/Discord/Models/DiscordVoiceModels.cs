namespace PulseGuard.Modules.Discord.Models;

public enum DiscordConnectionState
{
    Disconnected,
    Connecting,
    Authorizing,
    Connected,
    Error
}

public sealed record DiscordVoiceParticipant(
    string UserId,
    string DisplayName,
    bool IsSpeaking,
    bool IsMuted,
    bool IsDeafened,
    bool IsStreaming = false,
    bool IsVideoOn = false);

public sealed record DiscordVoiceSnapshot(
    DiscordConnectionState ConnectionState,
    string StatusMessage,
    bool IsInVoiceChannel,
    string? ChannelId,
    string ChannelName,
    int? PingMs,
    IReadOnlyList<DiscordVoiceParticipant> Participants)
{
    public static DiscordVoiceSnapshot CreateDisconnected(string message = "Не подключено") =>
        new(
            DiscordConnectionState.Disconnected,
            message,
            IsInVoiceChannel: false,
            ChannelId: null,
            ChannelName: string.Empty,
            PingMs: null,
            Participants: Array.Empty<DiscordVoiceParticipant>());
}

