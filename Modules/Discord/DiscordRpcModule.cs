using System.Net.Http;
using System.Text.Json;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.Discord.Models;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.Modules.Discord;

/// <summary>
/// Lightweight Discord local RPC module tracking active voice channel, ping, and speaking participants.
/// Consumes zero CPU when disabled, when Discord is closed, or when not in a voice channel.
/// </summary>
public sealed class DiscordRpcModule : IMonitorModule
{
    private const string DiscordRedirectUri = "http://127.0.0.1";
    private const string DiscordOAuthTokenUrl = "https://discord.com/api/oauth2/token";

    private static readonly HttpClient OAuthHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] GlobalEvents =
    [
        "VOICE_CHANNEL_SELECT",
        "VOICE_CONNECTION_STATUS"
    ];

    private static readonly string[] ChannelEvents =
    [
        "VOICE_STATE_CREATE",
        "VOICE_STATE_UPDATE",
        "VOICE_STATE_DELETE",
        "SPEAKING_START",
        "SPEAKING_STOP"
    ];

    private readonly SettingsService _settingsService;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DiscordVoiceParticipant> _participants = new(StringComparer.Ordinal);

    private DiscordIpcClient? _client;
    private Task? _backgroundTask;
    private bool _isAuthenticated;
    private string _connectedClientId = string.Empty;
    private string? _currentChannelId;
    private string? _subscribedChannelId;
    private string _currentChannelName = string.Empty;
    private int? _currentPingMs;
    private DiscordConnectionState _connectionState = DiscordConnectionState.Disconnected;
    private string _statusMessage = "Не подключено";
    private bool _disposed;

    public string Id => "discord-rpc";
    public string Name => "Интеграция с Discord";
    public string Description => "Отображает активный голосовой канал, пинг и говорящих участников в оверлее.";
    public bool IsEnabled { get; set; } = true;

    public DiscordVoiceSnapshot CurrentSnapshot { get; private set; } = DiscordVoiceSnapshot.CreateDisconnected();

    public event Action<DiscordVoiceSnapshot>? SnapshotChanged;

    public DiscordRpcModule(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _backgroundTask is not null)
        {
            return Task.CompletedTask;
        }

        _backgroundTask = Task.Run(() => ConnectionLoopAsync(_disposeCts.Token), _disposeCts.Token);
        WakeLoop();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Connects to local Discord IPC and triggers the DISPATCH / AUTHORIZE modal with scopes: ["rpc"].
    /// </summary>
    public async Task<(bool Success, string Message)> AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return (false, "Модуль остановлен.");
        }

        string clientId = _settingsService.Current.DiscordClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            const string msg = "Укажите Discord Client ID перед подключением.";
            UpdateStatus(DiscordConnectionState.Error, msg);
            return (false, msg);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, cancellationToken);
        await _connectionLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedAsync(clientId, linkedCts.Token).ConfigureAwait(false))
            {
                const string msg = "Discord не запущен или локальный IPC-канал недоступен.";
                UpdateStatus(DiscordConnectionState.Disconnected, msg);
                return (false, msg);
            }

            UpdateStatus(DiscordConnectionState.Authorizing, "Подтвердите доступ в окне Discord...");

            var client = _client!;
            string nonce = Guid.NewGuid().ToString("N");
            var response = await client.SendCommandAsync(
                new
                {
                    cmd = "AUTHORIZE",
                    args = new
                    {
                        client_id = clientId,
                        scopes = new[] { "rpc" }
                    },
                    nonce
                },
                nonce,
                AuthorizeTimeout,
                linkedCts.Token).ConfigureAwait(false);

            if (response is null)
            {
                const string msg = "Истекло время ожидания ответа от Discord.";
                UpdateStatus(DiscordConnectionState.Error, msg);
                return (false, msg);
            }

            if (TryGetRpcError(response.Value, out string errorMsg))
            {
                string msg = $"Discord отклонил авторизацию: {errorMsg}";
                UpdateStatus(DiscordConnectionState.Error, msg);
                return (false, msg);
            }

            string? accessToken = null;
            string? authCode = null;
            if (response.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("access_token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
                {
                    accessToken = tokenProp.GetString();
                }

                if (data.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
                {
                    authCode = codeProp.GetString();
                }
            }

            string? exchangeError = null;
            if (string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(authCode))
            {
                string clientSecret = _settingsService.Current.DiscordClientSecret.Trim();
                (accessToken, exchangeError) = await ExchangeCodeForTokenAsync(
                    clientId,
                    clientSecret,
                    authCode,
                    linkedCts.Token).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                _settingsService.Update(s => s.DiscordAccessToken = accessToken);
                if (await AuthenticateAndSubscribeAsync(client, accessToken, linkedCts.Token).ConfigureAwait(false))
                {
                    return (true, "Discord успешно подключён.");
                }

                const string authFailMsg = "Не удалось завершить аутентификацию в Discord.";
                UpdateStatus(DiscordConnectionState.Error, authFailMsg);
                return (false, authFailMsg);
            }

            // Standard RPC AUTHORIZE returns an OAuth2 code; attempt session subscription in case the client permits it.
            if (await TrySubscribeSessionAsync(client, linkedCts.Token).ConfigureAwait(false))
            {
                _isAuthenticated = true;
                UpdateStatus(DiscordConnectionState.Connected, "Подключено к Discord.");
                return (true, "Discord подключён на текущую сессию.");
            }

            string codeMsg = !string.IsNullOrWhiteSpace(exchangeError)
                ? exchangeError
                : !string.IsNullOrWhiteSpace(authCode)
                    ? "Discord вернул код авторизации, но не удалось обменять его на access_token."
                    : "Discord не вернул токен доступа.";
            UpdateStatus(DiscordConnectionState.Error, codeMsg);
            return (false, codeMsg);
        }
        catch (OperationCanceledException)
        {
            return (false, "Авторизация отменена.");
        }
        catch (Exception ex)
        {
            string msg = $"Ошибка подключения к Discord: {ex.Message}";
            UpdateStatus(DiscordConnectionState.Error, msg);
            return (false, msg);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private static async Task<(string? AccessToken, string? ErrorMessage)> ExchangeCodeForTokenAsync(
        string clientId,
        string clientSecret,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            return (null, "Discord вернул code вместо access_token. Укажите Discord Client Secret в настройках или включите Public Client / Implicit Grant в Discord Developer Portal.");
        }

        try
        {
            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", DiscordRedirectUri)
            ]);

            using var response = await OAuthHttpClient.PostAsync(DiscordOAuthTokenUrl, content, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (response.IsSuccessStatusCode
                && root.TryGetProperty("access_token", out var tokenProp)
                && tokenProp.ValueKind == JsonValueKind.String)
            {
                return (tokenProp.GetString(), null);
            }

            string errDesc = root.TryGetProperty("error_description", out var descProp) && descProp.ValueKind == JsonValueKind.String
                ? descProp.GetString() ?? string.Empty
                : root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String
                    ? errProp.GetString() ?? response.StatusCode.ToString()
                    : response.StatusCode.ToString();

            return (null, $"Ошибка обмена кода OAuth2: {errDesc}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Ошибка запроса токена Discord OAuth2: {ex.Message}");
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        if (!settings.IsDiscordOverlayEnabled || string.IsNullOrWhiteSpace(settings.DiscordClientId))
        {
            _ = DisconnectAsync("Интеграция с Discord отключена");
            return;
        }

        if (!string.Equals(_connectedClientId, settings.DiscordClientId.Trim(), StringComparison.Ordinal))
        {
            _ = DisconnectAsync("Обновлён Discord Client ID").ContinueWith(_ => WakeLoop());
            return;
        }

        WakeLoop();
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReconnectInterval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = _settingsService.Current;
                bool canAutoConnect = settings.IsDiscordOverlayEnabled
                                      && !string.IsNullOrWhiteSpace(settings.DiscordClientId)
                                      && !string.IsNullOrWhiteSpace(settings.DiscordAccessToken);

                if (canAutoConnect && (_client is null || !_client.IsConnected || !_isAuthenticated))
                {
                    if (DiscordIpcClient.HasActiveDiscordPipe())
                    {
                        await TryAutoConnectAsync(settings.DiscordClientId.Trim(), settings.DiscordAccessToken.Trim(), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        UpdateStatus(DiscordConnectionState.Disconnected, "Discord не запущен");
                    }
                }
                else if (!settings.IsDiscordOverlayEnabled || string.IsNullOrWhiteSpace(settings.DiscordClientId))
                {
                    if (_client is not null)
                    {
                        await DisconnectAsync("Не подключено").ConfigureAwait(false);
                    }
                }

                // Wait for either wake signal or 10s reconnect tick.
                var wakeTask = _wakeSignal.WaitAsync(cancellationToken);
                if (canAutoConnect && (_client is null || !_client.IsConnected || !_isAuthenticated))
                {
                    var tickTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                    await Task.WhenAny(wakeTask, tickTask).ConfigureAwait(false);
                }
                else
                {
                    await wakeTask.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task TryAutoConnectAsync(string clientId, string accessToken, CancellationToken cancellationToken)
    {
        if (!await _connectionLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!await EnsureConnectedAsync(clientId, cancellationToken).ConfigureAwait(false))
            {
                UpdateStatus(DiscordConnectionState.Disconnected, "Discord не запущен");
                return;
            }

            if (await AuthenticateAndSubscribeAsync(_client!, accessToken, cancellationToken).ConfigureAwait(false))
            {
                UpdateStatus(DiscordConnectionState.Connected, "Подключено к Discord");
            }
            else
            {
                _settingsService.Update(s => s.DiscordAccessToken = string.Empty);
                UpdateStatus(DiscordConnectionState.Error, "Токен устарел. Нажмите «Подключить Discord».");
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation.
        }
        catch
        {
            UpdateStatus(DiscordConnectionState.Disconnected, "Ожидание Discord...");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task<bool> EnsureConnectedAsync(string clientId, CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true } && string.Equals(_connectedClientId, clientId, StringComparison.Ordinal))
        {
            return true;
        }

        await DisposeClientOnlyAsync().ConfigureAwait(false);

        UpdateStatus(DiscordConnectionState.Connecting, "Подключение к Discord...");

        var client = new DiscordIpcClient();
        client.DispatchReceived += OnDispatchReceived;
        client.Disconnected += OnClientDisconnected;

        if (!await client.ConnectAsync(clientId, cancellationToken).ConfigureAwait(false))
        {
            client.DispatchReceived -= OnDispatchReceived;
            client.Disconnected -= OnClientDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _client = client;
        _connectedClientId = clientId;
        _isAuthenticated = false;
        return true;
    }

    private async Task<bool> AuthenticateAndSubscribeAsync(
        DiscordIpcClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        string nonce = Guid.NewGuid().ToString("N");
        var authResponse = await client.SendCommandAsync(
            new
            {
                cmd = "AUTHENTICATE",
                args = new { access_token = accessToken },
                nonce
            },
            nonce,
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (authResponse is null || TryGetRpcError(authResponse.Value, out _))
        {
            _isAuthenticated = false;
            return false;
        }

        _isAuthenticated = true;
        UpdateStatus(DiscordConnectionState.Connected, "Подключено к Discord");

        await TrySubscribeSessionAsync(client, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TrySubscribeSessionAsync(DiscordIpcClient client, CancellationToken cancellationToken)
    {
        foreach (string evt in GlobalEvents)
        {
            string nonce = Guid.NewGuid().ToString("N");
            var subResp = await client.SendCommandAsync(
                new { cmd = "SUBSCRIBE", evt, nonce },
                nonce,
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            if (subResp is null || TryGetRpcError(subResp.Value, out _))
            {
                return false;
            }
        }

        await RefreshSelectedVoiceChannelAsync(client, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RefreshSelectedVoiceChannelAsync(DiscordIpcClient client, CancellationToken cancellationToken)
    {
        string nonce = Guid.NewGuid().ToString("N");
        var resp = await client.SendCommandAsync(
            new { cmd = "GET_SELECTED_VOICE_CHANNEL", args = new { }, nonce },
            nonce,
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (resp is null || TryGetRpcError(resp.Value, out _))
        {
            return;
        }

        if (!resp.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            await SetVoiceChannelAsync(client, null, string.Empty, Array.Empty<DiscordVoiceParticipant>(), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string? channelId = data.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
        string channelName = data.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? string.Empty
            : string.Empty;

        var participants = new List<DiscordVoiceParticipant>();
        if (data.TryGetProperty("voice_states", out var statesProp) && statesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in statesProp.EnumerateArray())
            {
                if (TryParseVoiceState(item, out var participant))
                {
                    participants.Add(participant);
                }
            }
        }

        await SetVoiceChannelAsync(client, channelId, channelName, participants, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetVoiceChannelAsync(
        DiscordIpcClient client,
        string? channelId,
        string channelName,
        IReadOnlyList<DiscordVoiceParticipant> initialParticipants,
        CancellationToken cancellationToken)
    {
        string? previousSubscribedChannel;
        lock (_stateLock)
        {
            previousSubscribedChannel = _subscribedChannelId;
            _currentChannelId = channelId;
            _currentChannelName = channelName;
            _participants.Clear();
            foreach (var p in initialParticipants)
            {
                _participants[p.UserId] = p;
            }

            if (string.IsNullOrEmpty(channelId))
            {
                _currentPingMs = null;
                _subscribedChannelId = null;
            }
            else
            {
                _subscribedChannelId = channelId;
            }
        }

        PublishSnapshot();

        if (!string.IsNullOrEmpty(previousSubscribedChannel)
            && !string.Equals(previousSubscribedChannel, channelId, StringComparison.Ordinal))
        {
            await ToggleChannelSubscriptionsAsync(client, "UNSUBSCRIBE", previousSubscribedChannel, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(channelId)
            && !string.Equals(previousSubscribedChannel, channelId, StringComparison.Ordinal))
        {
            await ToggleChannelSubscriptionsAsync(client, "SUBSCRIBE", channelId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ToggleChannelSubscriptionsAsync(
        DiscordIpcClient client,
        string cmd,
        string channelId,
        CancellationToken cancellationToken)
    {
        foreach (string evt in ChannelEvents)
        {
            string nonce = Guid.NewGuid().ToString("N");
            await client.SendCommandAsync(
                new
                {
                    cmd,
                    evt,
                    args = new { channel_id = channelId },
                    nonce
                },
                nonce,
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnDispatchReceived(string evt, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return;
        }

        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                HandleVoiceChannelSelect(data);
                break;

            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                HandleVoiceStateUpsert(data);
                break;

            case "VOICE_STATE_DELETE":
                HandleVoiceStateDelete(data);
                break;

            case "SPEAKING_START":
                HandleSpeakingChanged(data, isSpeaking: true);
                break;

            case "SPEAKING_STOP":
                HandleSpeakingChanged(data, isSpeaking: false);
                break;

            case "VOICE_CONNECTION_STATUS":
                HandleVoiceConnectionStatus(data);
                break;
        }
    }

    private void HandleVoiceChannelSelect(JsonElement data)
    {
        string? channelId = data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("channel_id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;

        var client = _client;
        if (client is null || !client.IsConnected)
        {
            return;
        }

        if (string.IsNullOrEmpty(channelId))
        {
            _ = SetVoiceChannelAsync(client, null, string.Empty, Array.Empty<DiscordVoiceParticipant>(), _disposeCts.Token);
        }
        else
        {
            _ = RefreshSelectedVoiceChannelAsync(client, _disposeCts.Token);
        }
    }

    private void HandleVoiceStateUpsert(JsonElement data)
    {
        if (!TryParseVoiceState(data, out var parsed))
        {
            return;
        }

        lock (_stateLock)
        {
            if (_participants.TryGetValue(parsed.UserId, out var existing))
            {
                parsed = parsed with { IsSpeaking = existing.IsSpeaking };
            }

            _participants[parsed.UserId] = parsed;
        }

        PublishSnapshot();
    }

    private void HandleVoiceStateDelete(JsonElement data)
    {
        string? userId = TryExtractUserId(data);
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        bool removed;
        lock (_stateLock)
        {
            removed = _participants.Remove(userId);
        }

        if (removed)
        {
            PublishSnapshot();
        }
    }

    private void HandleSpeakingChanged(JsonElement data, bool isSpeaking)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("user_id", out var userIdProp)
            || userIdProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? userId = userIdProp.GetString();
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        bool changed = false;
        lock (_stateLock)
        {
            if (_participants.TryGetValue(userId, out var existing) && existing.IsSpeaking != isSpeaking)
            {
                _participants[userId] = existing with { IsSpeaking = isSpeaking };
                changed = true;
            }
        }

        if (changed)
        {
            PublishSnapshot();
        }
    }

    private void HandleVoiceConnectionStatus(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int? pingMs = null;
        if (data.TryGetProperty("average_ping", out var avgProp) && avgProp.TryGetDouble(out double avg))
        {
            pingMs = (int)Math.Round(avg);
        }
        else if (data.TryGetProperty("pings", out var pingsProp) && pingsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var pingItem in pingsProp.EnumerateArray())
            {
                if (pingItem.ValueKind == JsonValueKind.Object
                    && pingItem.TryGetProperty("time", out var timeProp)
                    && timeProp.TryGetInt32(out int timeVal))
                {
                    pingMs = timeVal;
                }
            }
        }

        string? state = data.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.String
            ? stateProp.GetString()
            : null;

        bool changed = false;
        lock (_stateLock)
        {
            if (string.Equals(state, "DISCONNECTED", StringComparison.Ordinal))
            {
                _currentChannelId = null;
                _currentChannelName = string.Empty;
                _currentPingMs = null;
                _participants.Clear();
                changed = true;
            }
            else if (pingMs.HasValue && _currentPingMs != pingMs)
            {
                _currentPingMs = pingMs;
                changed = true;
            }
        }

        if (changed)
        {
            PublishSnapshot();
        }
    }

    private static bool TryParseVoiceState(JsonElement data, out DiscordVoiceParticipant participant)
    {
        participant = default!;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? userId = TryExtractUserId(data);
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        string displayName = string.Empty;
        if (data.TryGetProperty("nick", out var nickProp) && nickProp.ValueKind == JsonValueKind.String)
        {
            displayName = nickProp.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(displayName)
            && data.TryGetProperty("user", out var userProp)
            && userProp.ValueKind == JsonValueKind.Object)
        {
            if (userProp.TryGetProperty("global_name", out var globalProp) && globalProp.ValueKind == JsonValueKind.String)
            {
                displayName = globalProp.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(displayName)
                && userProp.TryGetProperty("username", out var usernameProp)
                && usernameProp.ValueKind == JsonValueKind.String)
            {
                displayName = usernameProp.GetString() ?? string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = userId;
        }

        bool isMuted = false;
        bool isDeafened = false;
        bool isStreaming = false;
        bool isVideoOn = false;
        if (data.TryGetProperty("voice_state", out var vsProp) && vsProp.ValueKind == JsonValueKind.Object)
        {
            isMuted = GetBool(vsProp, "mute") || GetBool(vsProp, "self_mute") || GetBool(vsProp, "suppress");
            isDeafened = GetBool(vsProp, "deaf") || GetBool(vsProp, "self_deaf");
            isStreaming = GetBool(vsProp, "self_stream") || GetBool(vsProp, "stream");
            isVideoOn = GetBool(vsProp, "self_video") || GetBool(vsProp, "video");
        }

        participant = new DiscordVoiceParticipant(
            userId,
            displayName,
            IsSpeaking: false,
            IsMuted: isMuted || isDeafened,
            IsDeafened: isDeafened,
            IsStreaming: isStreaming,
            IsVideoOn: isVideoOn);
        return true;
    }

    private static string? TryExtractUserId(JsonElement data)
    {
        if (data.TryGetProperty("user", out var userProp)
            && userProp.ValueKind == JsonValueKind.Object
            && userProp.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String)
        {
            return idProp.GetString();
        }

        if (data.TryGetProperty("user_id", out var directIdProp) && directIdProp.ValueKind == JsonValueKind.String)
        {
            return directIdProp.GetString();
        }

        return null;
    }

    private static bool GetBool(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static bool TryGetRpcError(JsonElement root, out string message)
    {
        message = string.Empty;
        if (!root.TryGetProperty("evt", out var evtProp)
            || !string.Equals(evtProp.GetString(), "ERROR", StringComparison.Ordinal))
        {
            return false;
        }

        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("message", out var msgProp)
            && msgProp.ValueKind == JsonValueKind.String)
        {
            message = msgProp.GetString() ?? "Неизвестная ошибка Discord RPC";
        }
        else
        {
            message = "Ошибка Discord RPC";
        }

        return true;
    }

    private void OnClientDisconnected(string reason)
    {
        _isAuthenticated = false;
        _ = DisconnectAsync(reason).ContinueWith(_ => WakeLoop());
    }

    private async Task DisconnectAsync(string reason)
    {
        await DisposeClientOnlyAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _isAuthenticated = false;
            _connectedClientId = string.Empty;
            _currentChannelId = null;
            _subscribedChannelId = null;
            _currentChannelName = string.Empty;
            _currentPingMs = null;
            _participants.Clear();
            _connectionState = DiscordConnectionState.Disconnected;
            _statusMessage = reason;
        }

        PublishSnapshot();
    }

    private async Task DisposeClientOnlyAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.DispatchReceived -= OnDispatchReceived;
            client.Disconnected -= OnClientDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void UpdateStatus(DiscordConnectionState state, string message)
    {
        lock (_stateLock)
        {
            _connectionState = state;
            _statusMessage = message;
        }

        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        DiscordVoiceSnapshot snapshot;
        lock (_stateLock)
        {
            bool inVoice = !string.IsNullOrEmpty(_currentChannelId);
            var orderedParticipants = _participants.Values
                .OrderByDescending(p => p.IsSpeaking)
                .ThenBy(p => p.IsMuted)
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            snapshot = new DiscordVoiceSnapshot(
                _connectionState,
                _statusMessage,
                inVoice,
                _currentChannelId,
                _currentChannelName,
                _currentPingMs,
                orderedParticipants);
        }

        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private void WakeLoop()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _disposeCts.Cancel();

        try
        {
            _backgroundTask?.Wait(500);
        }
        catch
        {
            // Ignore shutdown wait errors.
        }

        _ = DisposeClientOnlyAsync();
        _wakeSignal.Dispose();
        _connectionLock.Dispose();
        _disposeCts.Dispose();
    }
}

