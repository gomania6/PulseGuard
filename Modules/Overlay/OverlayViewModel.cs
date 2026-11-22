using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.BatteryDevices;
using PulseGuard.Modules.BatteryDevices.Models;
using PulseGuard.Modules.Discord;
using PulseGuard.Modules.Discord.Models;
using PulseGuard.Modules.InputLayout;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Monitoring.Models;
using PulseGuard.Modules.Network;
using PulseGuard.Modules.Network.Models;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Playtime;
using PulseGuard.Modules.Playtime.Models;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;
using PulseGuard.Modules.Vpn.Models;

namespace PulseGuard.Modules.Overlay;

/// <summary>
/// ViewModel for the always-on-top compact overlay mini-widget.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IDisposable
{
    private static readonly Brush VpnDisconnectedBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);
    private static readonly Brush VpnConnectedBrush = CreateFrozenBrush(0x00, 0xE5, 0xA0);
    private static readonly Brush VpnWarningBrush = CreateFrozenBrush(0xFF, 0xB9, 0x00);
    private static readonly Brush VpnErrorBrush = CreateFrozenBrush(0xFF, 0x5C, 0x5C);

    private static readonly Brush DiscordSpeakingBrush = CreateFrozenBrush(0x00, 0xE5, 0xA0);
    private static readonly Brush DiscordSilentBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);
    private static readonly Brush DiscordNameDefaultBrush = CreateFrozenBrush(0xD8, 0xDC, 0xE2);
    private static readonly Brush DiscordNameMutedBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);
    private static readonly Brush GameSessionNormalBrush = CreateFrozenBrush(0xFF, 0xB9, 0x00);
    private static readonly Brush BreakReminderHighlightBrush = CreateFrozenBrush(0xFF, 0x8C, 0x00);

    private const int MaxDiscordParticipantsDisplayed = 8;
    private const double NetworkTopConsumerThresholdMbps = 10.0;
    private const int InputLayoutHighlightDurationMs = 1500;
    private const int StutterAlertDisplayDurationMs = 3000;

    private readonly MonitoringPollingService _pollingService;
    private readonly SystemMonitorModule _monitorModule;
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly SettingsService _settingsService;
    private readonly VpnService _vpnService;
    private readonly BatteryDeviceMonitorModule _batteryModule;
    private readonly DiscordRpcModule _discordRpcModule;
    private readonly NetworkRadarModule _networkRadarModule;
    private readonly GameModeTool _gameModeTool;
    private readonly InputLayoutModule _inputLayoutModule;
    private readonly PlaytimeTrackerModule _playtimeTrackerModule;
    private readonly StutterMonitorModule _stutterMonitorModule;

    private CancellationTokenSource? _transientStatusCts;
    private CancellationTokenSource? _layoutHighlightCts;
    private CancellationTokenSource? _stutterAlertCts;
    private readonly DispatcherTimer _gameSessionTimer;
    private string? _lastDiscordChannelId;
    private volatile bool _isActive;
    private double _lastOpacityPercent = -1;

    [ObservableProperty]
    private string _cpuText = "0%";

    [ObservableProperty]
    private string _gpuText = "Н/Д";

    [ObservableProperty]
    private string _ramText = "0%";

    [ObservableProperty]
    private string _networkText = "↓ 0.0 Мбит/с  ↑ 0.0 Мбит/с";

    [ObservableProperty]
    private string _vpnText = "VPN Выкл";

    [ObservableProperty]
    private Brush _vpnDotBrush = VpnDisconnectedBrush;

    [ObservableProperty]
    private bool _showVpn = true;

    [ObservableProperty]
    private bool _isVpnBusy;

    [ObservableProperty]
    private Brush _panelBackground = CreateBackgroundBrush(AppSettings.DefaultOverlayOpacityPercent);

    [ObservableProperty]
    private bool _isRamCleaning;

    [ObservableProperty]
    private bool _showCpu = true;

    [ObservableProperty]
    private bool _showGpu = true;

    [ObservableProperty]
    private bool _showRam = true;

    [ObservableProperty]
    private bool _showNetwork = true;

    [ObservableProperty]
    private bool _showNoMetricsFallback;

    [ObservableProperty]
    private bool _showIcons = true;

    [ObservableProperty]
    private bool _showHoverButtons = true;

    [ObservableProperty]
    private bool _isPositionLocked = true;

    [ObservableProperty]
    private bool _isClickThroughEnabled = true;

    [ObservableProperty]
    private Orientation _layoutOrientation = Orientation.Horizontal;

    [ObservableProperty]
    private Thickness _itemMargin = new(0, 0, 14, 0);

    [ObservableProperty]
    private Thickness _hoverButtonsMargin = new(10, 0, 0, 0);

    [ObservableProperty]
    private bool _hasPinnedBatteryDevices;

    [ObservableProperty]
    private bool _showDiscordVoice;

    [ObservableProperty]
    private string _discordChannelText = string.Empty;

    [ObservableProperty]
    private bool _isGameModeActive;

    [ObservableProperty]
    private string _gameSessionText = "00:00:00";

    [ObservableProperty]
    private bool _hasTransientStatus;

    [ObservableProperty]
    private string _transientStatusText = string.Empty;

    [ObservableProperty]
    private bool _showInputLayout = true;

    [ObservableProperty]
    private string _inputLayoutText = "⌨ EN";

    [ObservableProperty]
    private bool _isInputLayoutHighlighted;

    [ObservableProperty]
    private bool _isBreakReminderDue;

    [ObservableProperty]
    private bool _showPcSessionTimer;

    [ObservableProperty]
    private string _pcSessionText = string.Empty;

    [ObservableProperty]
    private Brush _sessionTimerBrush = GameSessionNormalBrush;

    [ObservableProperty]
    private bool _showStutterAlert;

    [ObservableProperty]
    private string _stutterAlertText = string.Empty;

    public ObservableCollection<OverlayBatteryBadgeViewModel> PinnedBatteryDevices { get; } = new();
    public ObservableCollection<OverlayDiscordParticipantViewModel> DiscordParticipants { get; } = new();

    public event Action? CloseRequested;

    public OverlayViewModel(
        MonitoringPollingService pollingService,
        SystemMonitorModule monitorModule,
        SafeRamCleanerTool ramCleanerTool,
        SettingsService settingsService,
        VpnService vpnService,
        BatteryDeviceMonitorModule batteryModule,
        DiscordRpcModule discordRpcModule,
        NetworkRadarModule networkRadarModule,
        GameModeTool gameModeTool,
        InputLayoutModule inputLayoutModule,
        PlaytimeTrackerModule playtimeTrackerModule,
        StutterMonitorModule stutterMonitorModule)
    {
        _pollingService = pollingService;
        _monitorModule = monitorModule;
        _ramCleanerTool = ramCleanerTool;
        _settingsService = settingsService;
        _vpnService = vpnService;
        _batteryModule = batteryModule;
        _discordRpcModule = discordRpcModule;
        _networkRadarModule = networkRadarModule;
        _gameModeTool = gameModeTool;
        _inputLayoutModule = inputLayoutModule;
        _playtimeTrackerModule = playtimeTrackerModule;
        _stutterMonitorModule = stutterMonitorModule;

        _gameSessionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _gameSessionTimer.Tick += (_, _) =>
        {
            if (_isActive && _gameModeTool.IsActive)
            {
                ApplyGameModeState(true);
            }
            else
            {
                _gameSessionTimer.Stop();
            }
        };

        ApplySettings(_settingsService.Current);
        ApplyVpnStatus(_vpnService.CurrentStatus);
        ApplyBatteryDevices(_batteryModule.LatestDevices);
        ApplyDiscordSnapshot(_discordRpcModule.CurrentSnapshot);
        ApplyGameModeState(_gameModeTool.IsActive);
        ApplyInputLayout(_inputLayoutModule.CurrentLayoutCode, isSwitch: false);
        ApplyPlaytimeSnapshot(_playtimeTrackerModule.LatestSnapshot);

        _pollingService.MetricsUpdated += OnMetricsUpdated;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _vpnService.StatusChanged += OnVpnStatusChanged;
        _batteryModule.DevicesUpdated += OnBatteryDevicesUpdated;
        _discordRpcModule.SnapshotChanged += OnDiscordSnapshotChanged;
        _networkRadarModule.RadarUpdated += OnNetworkRadarUpdated;
        _gameModeTool.StateChanged += OnGameModeStateChanged;
        _inputLayoutModule.LayoutChanged += OnInputLayoutChanged;
        _playtimeTrackerModule.StatsUpdated += OnPlaytimeStatsUpdated;
        _stutterMonitorModule.StutterDetected += OnStutterDetected;
    }

    public void SetActive(bool isActive)
    {
        _isActive = isActive;
        _inputLayoutModule.SetPaused(!isActive || !_settingsService.Current.OverlayShowInputLayout);

        if (isActive)
        {
            ApplySettings(_settingsService.Current);
            ApplySnapshot(_monitorModule.LatestSnapshot);
            ApplyVpnStatus(_vpnService.CurrentStatus);
            ApplyBatteryDevices(_batteryModule.LatestDevices);
            ApplyDiscordSnapshot(_discordRpcModule.CurrentSnapshot);
            ApplyGameModeState(_gameModeTool.IsActive);
            ApplyInputLayout(_inputLayoutModule.CurrentLayoutCode, isSwitch: false);
            ApplyPlaytimeSnapshot(_playtimeTrackerModule.LatestSnapshot);
        }
        else
        {
            _gameSessionTimer.Stop();
            foreach (var participant in DiscordParticipants)
            {
                participant.CancelAnimations();
            }
        }
    }

    public void SavePosition(double left, double top)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
        {
            return;
        }

        _settingsService.Update(s =>
        {
            s.OverlayPosition = OverlayCorner.Custom;
            s.OverlayLeft = Math.Round(left);
            s.OverlayTop = Math.Round(top);
        });
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            ApplySettings(settings);
            if (_isActive)
            {
                ApplySnapshot(_monitorModule.LatestSnapshot);
                ApplyBatteryDevices(_batteryModule.LatestDevices);
                ApplyDiscordSnapshot(_discordRpcModule.CurrentSnapshot);
            }
        });
    }

    private void ApplySettings(AppSettings settings)
    {
        UpdateBackground(settings.OverlayOpacityPercent);

        ShowCpu = settings.OverlayShowCpu;
        ShowGpu = settings.OverlayShowGpu;
        ShowRam = settings.OverlayShowRam;
        ShowNetwork = settings.OverlayShowNetwork;
        ShowVpn = settings.OverlayShowVpn;
        ShowInputLayout = settings.OverlayShowInputLayout;
        _inputLayoutModule.SetPaused(!_isActive || !settings.OverlayShowInputLayout);
        UpdateNoMetricsFallback();

        ShowIcons = settings.OverlayShowIcons;
        ShowHoverButtons = settings.OverlayShowHoverButtons;
        IsPositionLocked = settings.OverlayLockPosition;
        IsClickThroughEnabled = settings.IsOverlayClickThrough;

        bool isVertical = settings.OverlayLayout == OverlayLayoutMode.Vertical;
        LayoutOrientation = isVertical ? Orientation.Vertical : Orientation.Horizontal;
        ItemMargin = isVertical ? new Thickness(0, 2, 0, 2) : new Thickness(0, 0, 14, 0);
        HoverButtonsMargin = isVertical ? new Thickness(0, 6, 0, 0) : new Thickness(10, 0, 0, 0);
    }

    private void OnMetricsUpdated(SystemMetricsSnapshot snapshot)
    {
        if (!_isActive)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(SystemMetricsSnapshot s)
    {
        bool showTemp = _settingsService.Current.OverlayShowTemperature;

        if (ShowCpu)
        {
            double cpuLoad = Math.Clamp(s.CpuLoadPercent, 0, 100);
            CpuText = showTemp && s.CpuTemperatureC.HasValue
                ? $"{cpuLoad:0}% · {s.CpuTemperatureC.Value:0}°C"
                : $"{cpuLoad:0}%";
        }

        if (ShowGpu)
        {
            if (s.GpuLoadPercent.HasValue)
            {
                double gpuLoad = Math.Clamp(s.GpuLoadPercent.Value, 0, 100);
                GpuText = showTemp && s.GpuTemperatureC.HasValue
                    ? $"{gpuLoad:0}% · {s.GpuTemperatureC.Value:0}°C"
                    : $"{gpuLoad:0}%";
            }
            else if (showTemp && s.GpuTemperatureC.HasValue)
            {
                GpuText = $"{s.GpuTemperatureC.Value:0}°C";
            }
            else
            {
                GpuText = "Н/Д";
            }
        }

        if (ShowRam)
        {
            double ramLoad = Math.Clamp(s.RamLoadPercent, 0, 100);
            RamText = $"{ramLoad:0}%";
        }

        if (ShowNetwork)
        {
            UpdateNetworkText(s, _networkRadarModule.LatestSnapshot);
        }

        if (_gameModeTool.IsActive)
        {
            ApplyGameModeState(true);
        }
    }

    public void ShowTransientStatus(string text, int durationMs = 3000)
    {
        _transientStatusCts?.Cancel();
        _transientStatusCts?.Dispose();
        _transientStatusCts = new CancellationTokenSource();
        var token = _transientStatusCts.Token;

        TransientStatusText = text;
        HasTransientStatus = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, token).ConfigureAwait(false);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        HasTransientStatus = false;
                        TransientStatusText = string.Empty;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void OnGameModeStateChanged(bool isActive, string? _)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyGameModeState(isActive));
    }

    private void ApplyGameModeState(bool isActive)
    {
        IsGameModeActive = isActive;
        if (!isActive || _gameModeTool.SessionStartUtc is not DateTime startUtc)
        {
            _gameSessionTimer.Stop();
            GameSessionText = "00:00:00";
            return;
        }

        var elapsed = DateTime.UtcNow - startUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        GameSessionText = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

        if (_isActive && !_gameSessionTimer.IsEnabled)
        {
            _gameSessionTimer.Start();
        }
    }

    private void OnNetworkRadarUpdated(NetworkRadarSnapshot radar)
    {
        if (!_isActive || !ShowNetwork)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => UpdateNetworkText(_monitorModule.LatestSnapshot, radar));
    }

    private void UpdateNetworkText(SystemMetricsSnapshot s, NetworkRadarSnapshot radar)
    {
        double downMbps = (s.DownloadBytesPerSec * 8.0) / 1_000_000.0;
        double upMbps = (s.UploadBytesPerSec * 8.0) / 1_000_000.0;

        string? topTag = null;
        if (downMbps > NetworkTopConsumerThresholdMbps
            && radar.TopConsumer is { } top
            && top.DownloadMbps >= 0.1)
        {
            topTag = $" ({top.ProcessName})";
        }

        NetworkText = $"↓ {downMbps:0.0} Мбит/с{topTag}  ↑ {upMbps:0.0} Мбит/с";
    }

    private void UpdateBackground(double opacityPercent)
    {
        double clamped = Math.Clamp(opacityPercent, 40.0, 95.0);
        if (Math.Abs(clamped - _lastOpacityPercent) < 0.5)
        {
            return;
        }

        _lastOpacityPercent = clamped;
        PanelBackground = CreateBackgroundBrush(clamped);
    }

    private static Brush CreateBackgroundBrush(double opacityPercent)
    {
        byte alpha = (byte)Math.Clamp(Math.Round((opacityPercent / 100.0) * 255.0), 102, 243);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x1A, 0x1D, 0x24));
        brush.Freeze();
        return brush;
    }

    private void OnVpnStatusChanged(VpnStatusSnapshot status)
    {
        if (!_isActive)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyVpnStatus(status));
    }

    private void ApplyVpnStatus(VpnStatusSnapshot status)
    {
        switch (status.State)
        {
            case VpnConnectionState.Connected:
                string label = string.IsNullOrWhiteSpace(status.CountryCode) ? "VPN" : status.CountryCode;
                if (status.PacketLossPercent > 0)
                {
                    VpnDotBrush = status.Quality == ConnectionQuality.Bad
                        ? VpnErrorBrush
                        : VpnWarningBrush;
                    VpnText = status.PingMs.HasValue
                        ? $"{label} {status.PingMs.Value} мс • Loss {status.PacketLossPercent:0}%"
                        : $"{label} • Loss {status.PacketLossPercent:0}%";
                }
                else
                {
                    VpnDotBrush = VpnConnectedBrush;
                    VpnText = status.PingMs.HasValue
                        ? $"{label} {status.PingMs.Value} мс"
                        : label;
                }
                break;

            case VpnConnectionState.Error:
                VpnDotBrush = VpnErrorBrush;
                VpnText = "VPN Сбой";
                break;

            default:
                VpnDotBrush = VpnDisconnectedBrush;
                VpnText = "VPN Выкл";
                break;
        }
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    [RelayCommand]
    private async Task ToggleVpnAsync()
    {
        if (IsVpnBusy)
        {
            return;
        }

        IsVpnBusy = true;
        try
        {
            await _vpnService.ToggleAsync();
        }
        finally
        {
            IsVpnBusy = false;
        }
    }

    [RelayCommand]
    private async Task QuickRamCleanAsync()
    {
        if (IsRamCleaning)
        {
            return;
        }

        IsRamCleaning = true;
        try
        {
            await _ramCleanerTool.ExecuteAsync();
        }
        finally
        {
            IsRamCleaning = false;
        }
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        CloseRequested?.Invoke();
    }

    private void OnBatteryDevicesUpdated(IReadOnlyList<BatteryDeviceInfo> devices)
    {
        if (!_isActive)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyBatteryDevices(devices));
    }

    private void ApplyBatteryDevices(IReadOnlyList<BatteryDeviceInfo> devices)
    {
        var pinnedIds = _settingsService.Current.PinnedOverlayDeviceIds;

        PinnedBatteryDevices.Clear();
        foreach (var device in devices)
        {
            if (!device.IsConnected || !pinnedIds.Contains(device.DeviceId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            PinnedBatteryDevices.Add(new OverlayBatteryBadgeViewModel(device));
        }

        HasPinnedBatteryDevices = PinnedBatteryDevices.Count > 0;
        UpdateNoMetricsFallback();
    }

    private void OnDiscordSnapshotChanged(DiscordVoiceSnapshot snapshot)
    {
        if (!_isActive)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyDiscordSnapshot(snapshot));
    }

    private void ApplyDiscordSnapshot(DiscordVoiceSnapshot snapshot)
    {
        bool enabled = _settingsService.Current.IsDiscordOverlayEnabled;
        if (!enabled || !snapshot.IsInVoiceChannel)
        {
            ShowDiscordVoice = false;
            DiscordChannelText = string.Empty;
            _lastDiscordChannelId = null;
            foreach (var participant in DiscordParticipants)
            {
                participant.CancelAnimations();
            }
            DiscordParticipants.Clear();
            UpdateNoMetricsFallback();
            return;
        }

        bool isSameActiveChannel = string.Equals(_lastDiscordChannelId, snapshot.ChannelId, StringComparison.Ordinal);
        _lastDiscordChannelId = snapshot.ChannelId;

        ShowDiscordVoice = true;
        string channelName = string.IsNullOrWhiteSpace(snapshot.ChannelName) ? "Голосовой канал" : snapshot.ChannelName;
        DiscordChannelText = snapshot.PingMs.HasValue
            ? $"🔊 {channelName} • {snapshot.PingMs.Value} мс"
            : $"🔊 {channelName}";

        int count = Math.Min(snapshot.Participants.Count, MaxDiscordParticipantsDisplayed);
        var activeUserIds = new HashSet<string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            activeUserIds.Add(snapshot.Participants[i].UserId);
        }

        // Trigger leave animation (400 ms red tint + 220 ms fade out) before removing departed participants.
        for (int i = DiscordParticipants.Count - 1; i >= 0; i--)
        {
            var existing = DiscordParticipants[i];
            if (!activeUserIds.Contains(existing.UserId) && !existing.IsLeaving)
            {
                var leavingItem = existing;
                leavingItem.StartLeave(() => DiscordParticipants.Remove(leavingItem));
            }
        }

        for (int i = 0; i < count; i++)
        {
            var p = snapshot.Participants[i];
            Brush indicatorBrush = p.IsSpeaking ? DiscordSpeakingBrush : DiscordSilentBrush;
            Brush nameBrush = p.IsSpeaking
                ? DiscordSpeakingBrush
                : (p.IsMuted || p.IsDeafened ? DiscordNameMutedBrush : DiscordNameDefaultBrush);
            bool isMutedOnly = p.IsMuted && !p.IsDeafened;

            int existingIndex = -1;
            for (int j = 0; j < DiscordParticipants.Count; j++)
            {
                if (string.Equals(DiscordParticipants[j].UserId, p.UserId, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = DiscordParticipants[existingIndex];
                if (existing.IsLeaving)
                {
                    existing.CancelLeave();
                    existing.TriggerJoinHighlight();
                }

                existing.UpdateFrom(
                    p.DisplayName,
                    p.IsSpeaking,
                    isMutedOnly,
                    p.IsDeafened,
                    p.IsStreaming,
                    p.IsVideoOn,
                    indicatorBrush,
                    nameBrush);
            }
            else
            {
                var item = new OverlayDiscordParticipantViewModel(
                    p.UserId,
                    p.DisplayName,
                    p.IsSpeaking,
                    isMutedOnly,
                    p.IsDeafened,
                    p.IsStreaming,
                    p.IsVideoOn,
                    indicatorBrush,
                    nameBrush);

                if (isSameActiveChannel)
                {
                    item.TriggerJoinHighlight();
                }

                int insertIndex = Math.Min(i, DiscordParticipants.Count);
                DiscordParticipants.Insert(insertIndex, item);
            }
        }

        UpdateNoMetricsFallback();
    }

    private void UpdateNoMetricsFallback()
    {
        ShowNoMetricsFallback = !ShowCpu && !ShowGpu && !ShowRam && !ShowNetwork && !ShowVpn && !ShowInputLayout && !HasPinnedBatteryDevices && !ShowDiscordVoice;
    }

    private void OnInputLayoutChanged(string code, bool isSwitch)
    {
        if (!_isActive || !ShowInputLayout)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => ApplyInputLayout(code, isSwitch));
    }

    private void ApplyInputLayout(string code, bool isSwitch)
    {
        InputLayoutText = $"⌨ {code}";
        if (!isSwitch)
        {
            return;
        }

        _layoutHighlightCts?.Cancel();
        _layoutHighlightCts?.Dispose();
        _layoutHighlightCts = new CancellationTokenSource();
        var token = _layoutHighlightCts.Token;

        IsInputLayoutHighlighted = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(InputLayoutHighlightDurationMs, token).ConfigureAwait(false);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        IsInputLayoutHighlighted = false;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void OnPlaytimeStatsUpdated(PlaytimeSnapshot snapshot)
    {
        if (!_isActive)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => ApplyPlaytimeSnapshot(snapshot));
    }

    private void OnStutterDetected(StutterEvent ev, StutterSnapshot _)
    {
        if (!_isActive)
        {
            return;
        }

        if ((DateTime.UtcNow - ev.TimestampUtc).TotalSeconds > 10.0)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => ShowStutterAlertBadge(ev.DelayMs));
    }

    private void ShowStutterAlertBadge(double delayMs)
    {
        _stutterAlertCts?.Cancel();
        _stutterAlertCts?.Dispose();
        _stutterAlertCts = new CancellationTokenSource();
        var token = _stutterAlertCts.Token;

        StutterAlertText = $"⚡ Фриз {delayMs:0} мс";
        ShowStutterAlert = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StutterAlertDisplayDurationMs, token).ConfigureAwait(false);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        ShowStutterAlert = false;
                        StutterAlertText = string.Empty;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void ApplyPlaytimeSnapshot(PlaytimeSnapshot snapshot)
    {
        IsBreakReminderDue = snapshot.IsBreakReminderDue;
        SessionTimerBrush = snapshot.IsBreakReminderDue ? BreakReminderHighlightBrush : GameSessionNormalBrush;
        ShowPcSessionTimer = !IsGameModeActive && snapshot.IsBreakReminderDue;
        PcSessionText = snapshot.IsBreakReminderDue
            ? $"⏱ {PlaytimeTrackerModule.FormatDurationRussian(snapshot.ContinuousSessionDuration)}"
            : string.Empty;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollingService.MetricsUpdated -= OnMetricsUpdated;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _vpnService.StatusChanged -= OnVpnStatusChanged;
        _batteryModule.DevicesUpdated -= OnBatteryDevicesUpdated;
        _discordRpcModule.SnapshotChanged -= OnDiscordSnapshotChanged;
        _networkRadarModule.RadarUpdated -= OnNetworkRadarUpdated;
        _gameModeTool.StateChanged -= OnGameModeStateChanged;
        _inputLayoutModule.LayoutChanged -= OnInputLayoutChanged;
        _playtimeTrackerModule.StatsUpdated -= OnPlaytimeStatsUpdated;
        _stutterMonitorModule.StutterDetected -= OnStutterDetected;
        CancelAndDisposeCts(ref _transientStatusCts);
        CancelAndDisposeCts(ref _layoutHighlightCts);
        CancelAndDisposeCts(ref _stutterAlertCts);
        _gameSessionTimer.Stop();
        foreach (var participant in DiscordParticipants)
        {
            participant.CancelAnimations();
        }
    }

    private static void CancelAndDisposeCts(ref CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
        cts = null;
    }
}

public sealed partial class OverlayDiscordParticipantViewModel : ObservableObject
{
    private const int JoinHighlightDurationMs = 1200;
    private const int LeaveTotalDurationMs = 640;

    private CancellationTokenSource? _joinCts;
    private CancellationTokenSource? _leaveCts;

    public string UserId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isMutedOnly;

    [ObservableProperty]
    private bool _isDeafened;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isVideoOn;

    [ObservableProperty]
    private Brush _indicatorBrush;

    [ObservableProperty]
    private Brush _nameBrush;

    [ObservableProperty]
    private bool _isJoining;

    [ObservableProperty]
    private bool _isLeaving;

    public OverlayDiscordParticipantViewModel(
        string userId,
        string displayName,
        bool isSpeaking,
        bool isMutedOnly,
        bool isDeafened,
        bool isStreaming,
        bool isVideoOn,
        Brush indicatorBrush,
        Brush nameBrush)
    {
        UserId = userId;
        _displayName = displayName;
        _isSpeaking = isSpeaking;
        _isMutedOnly = isMutedOnly;
        _isDeafened = isDeafened;
        _isStreaming = isStreaming;
        _isVideoOn = isVideoOn;
        _indicatorBrush = indicatorBrush;
        _nameBrush = nameBrush;
    }

    public void UpdateFrom(
        string displayName,
        bool isSpeaking,
        bool isMutedOnly,
        bool isDeafened,
        bool isStreaming,
        bool isVideoOn,
        Brush indicatorBrush,
        Brush nameBrush)
    {
        DisplayName = displayName;
        IsSpeaking = isSpeaking;
        IsMutedOnly = isMutedOnly;
        IsDeafened = isDeafened;
        IsStreaming = isStreaming;
        IsVideoOn = isVideoOn;
        IndicatorBrush = indicatorBrush;
        NameBrush = nameBrush;
    }

    public void TriggerJoinHighlight()
    {
        _joinCts?.Cancel();
        _joinCts?.Dispose();
        _joinCts = new CancellationTokenSource();
        var token = _joinCts.Token;

        IsJoining = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(JoinHighlightDurationMs, token).ConfigureAwait(false);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        IsJoining = false;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    public void StartLeave(Action onLeaveCompleted)
    {
        _joinCts?.Cancel();
        _joinCts?.Dispose();
        _joinCts = null;
        IsJoining = false;

        _leaveCts?.Cancel();
        _leaveCts?.Dispose();
        _leaveCts = new CancellationTokenSource();
        var token = _leaveCts.Token;

        IsLeaving = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LeaveTotalDurationMs, token).ConfigureAwait(false);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        onLeaveCompleted();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    public void CancelLeave()
    {
        _leaveCts?.Cancel();
        _leaveCts?.Dispose();
        _leaveCts = null;
        IsLeaving = false;
    }

    public void CancelAnimations()
    {
        _joinCts?.Cancel();
        _joinCts?.Dispose();
        _joinCts = null;
        _leaveCts?.Cancel();
        _leaveCts?.Dispose();
        _leaveCts = null;
        IsJoining = false;
        IsLeaving = false;
    }
}

public sealed class OverlayBatteryBadgeViewModel
{
    public Wpf.Ui.Controls.SymbolRegular DeviceIcon { get; }
    public string PercentText { get; }
    public string TooltipText { get; }
    public Brush TextBrush { get; }
    public Brush StatusDotBrush { get; }
    public bool IsSleeping { get; }

    public OverlayBatteryBadgeViewModel(BatteryDeviceInfo info)
    {
        DeviceIcon = BatteryDevicePresentation.GetIcon(info.DeviceType);
        PercentText = $"{info.BatteryPercent}%";
        IsSleeping = info.IsSleeping;
        StatusDotBrush = info.IsAwake
            ? BatteryDevicePresentation.NormalBrush
            : BatteryDevicePresentation.OfflineBrush;
        TooltipText = info.IsSleeping
            ? $"{info.Name}: {info.BatteryPercent}% — Мышь в спящем режиме (показан последний известный заряд)"
            : $"{info.Name}: {info.BatteryPercent}%";
        TextBrush = info.IsSleeping
            ? BatteryDevicePresentation.OfflineBrush
            : BatteryDevicePresentation.GetOverlayTextBrush(info.BatteryPercent);
    }
}
