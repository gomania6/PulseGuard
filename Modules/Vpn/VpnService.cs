using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn.Models;

namespace PulseGuard.Modules.Vpn;

/// <summary>
/// Monitors external VPN client status (%AppData%\PulseGuard\vpn_status.json + process + 1.1.1.1 ping)
/// and provides safe Connect / Disconnect / Restart control.
/// </summary>
public sealed class VpnService : IMonitorModule
{
    private const string DefaultPingTargetHost = "1.1.1.1";
    private static readonly string[] PingCandidateHosts = ["1.1.1.1", "8.8.8.8", "9.9.9.9", "77.88.8.8"];
    private const int PingTimeoutMs = 800;
    private const int PingWindowSize = 30;
    private const double PingFailureErrorThresholdSeconds = 10.0;
    private const int PollIntervalMs = 1000;

    private static readonly byte[] PingBuffer = new byte[32];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settingsService;
    private readonly string _statusFilePath;
    private readonly Ping _ping = new();
    private readonly Queue<long?> _pingWindow = new(PingWindowSize);
    private readonly object _windowLock = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private FileSystemWatcher? _watcher;
    private Task? _pollingTask;
    private DateTime? _firstPingFailureUtc;
    private string _activePingHost = DefaultPingTargetHost;
    private VpnConnectionState? _lastStateForServerSelection;
    private bool _disposed;

    public string Id => "vpn-monitor";
    public string Name => "Монитор VPN";
    public string Description => "Мониторинг и управление внешним VPN-клиентом.";
    public bool IsEnabled { get; set; } = true;

    public VpnStatusSnapshot CurrentStatus { get; private set; } = VpnStatusSnapshot.CreateDefault();

    public event Action<VpnStatusSnapshot>? StatusChanged;

    public VpnService(SettingsService settingsService)
    {
        _settingsService = settingsService;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "PulseGuard");
        _statusFilePath = Path.Combine(appDir, "vpn_status.json");

        ProcessBlacklist.SetVpnExclusion(_settingsService.Current.VpnExecutablePath);
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await RefreshNearestServerAsync(cancellationToken).ConfigureAwait(false);
        StartFileWatcher();
        StartBackgroundPolling();
        await UpdateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes known low-latency Anycast/DNS servers in parallel and selects the closest one
    /// for ongoing Ping/Jitter/Loss monitoring.
    /// </summary>
    public async Task<string> RefreshNearestServerAsync(CancellationToken cancellationToken = default)
    {
        var probeTasks = PingCandidateHosts.Select(async host =>
        {
            try
            {
                using var probe = new Ping();
                var reply = await probe.SendPingAsync(host, PingTimeoutMs, PingBuffer).ConfigureAwait(false);
                long? rtt = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
                return (Host: host, Rtt: rtt);
            }
            catch
            {
                return (Host: host, Rtt: (long?)null);
            }
        });

        var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        string bestHost = results
            .Where(r => r.Rtt.HasValue)
            .OrderBy(r => r.Rtt!.Value)
            .Select(r => r.Host)
            .FirstOrDefault() ?? DefaultPingTargetHost;

        if (!string.Equals(_activePingHost, bestHost, StringComparison.Ordinal))
        {
            _activePingHost = bestHost;
            lock (_windowLock)
            {
                _pingWindow.Clear();
            }
        }

        return _activePingHost;
    }

    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var settings = _settingsService.Current;
            string exePath = settings.VpnExecutablePath?.Trim() ?? string.Empty;
            bool isConfigured = !string.IsNullOrWhiteSpace(exePath);
            string processName = isConfigured ? Path.GetFileNameWithoutExtension(exePath) : string.Empty;

            bool isProcessRunning = !string.IsNullOrEmpty(processName) && IsProcessAlive(processName);
            VpnStatusFileModel? fileModel = ReadStatusFile();

            string fileState = fileModel?.State?.Trim().ToLowerInvariant() ?? string.Empty;
            string countryCode = fileModel?.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
            string serverName = !string.IsNullOrWhiteSpace(fileModel?.ServerName)
                ? fileModel.ServerName.Trim()
                : !string.IsNullOrWhiteSpace(fileModel?.Server)
                    ? fileModel.Server.Trim()
                    : "—";
            string externalIp = !string.IsNullOrWhiteSpace(fileModel?.ExternalIp)
                ? fileModel.ExternalIp.Trim()
                : !string.IsNullOrWhiteSpace(fileModel?.Ip)
                    ? fileModel.Ip.Trim()
                    : "—";

            VpnConnectionState state;
            string statusText;
            long? pingMs = await MeasurePingAsync().ConfigureAwait(false);
            var (lossPercent, jitterMs, quality) = RecordAndComputeQuality(pingMs);

            if (!isProcessRunning || fileState == "disconnected")
            {
                _firstPingFailureUtc = null;
                state = VpnConnectionState.Disconnected;
                statusText = "Выключен";
            }
            else if (fileState == "error")
            {
                _firstPingFailureUtc ??= DateTime.UtcNow;
                state = VpnConnectionState.Error;
                statusText = "Сбой / Завис";
            }
            else
            {
                if (pingMs.HasValue)
                {
                    _firstPingFailureUtc = null;
                    state = VpnConnectionState.Connected;
                    statusText = "Работает";
                }
                else
                {
                    _firstPingFailureUtc ??= DateTime.UtcNow;
                    double failedSeconds = (DateTime.UtcNow - _firstPingFailureUtc.Value).TotalSeconds;
                    if (failedSeconds >= PingFailureErrorThresholdSeconds)
                    {
                        state = VpnConnectionState.Error;
                        statusText = "Сбой / Завис";
                    }
                    else
                    {
                        state = VpnConnectionState.Disconnected;
                        statusText = "Подключение...";
                    }
                }
            }

            var next = new VpnStatusSnapshot(
                state,
                statusText,
                countryCode,
                serverName,
                externalIp,
                pingMs,
                lossPercent,
                jitterMs,
                quality,
                isProcessRunning,
                isConfigured,
                _activePingHost);

            if (_lastStateForServerSelection.HasValue && _lastStateForServerSelection.Value != state)
            {
                _lastStateForServerSelection = state;
                _ = RefreshNearestServerAsync(_disposeCts.Token);
            }
            else
            {
                _lastStateForServerSelection ??= state;
            }

            if (next != CurrentStatus)
            {
                CurrentStatus = next;
                StatusChanged?.Invoke(next);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep service resilient to transient file or network errors.
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<(bool Success, string Message)> ConnectAsync()
    {
        string exePath = _settingsService.Current.VpnExecutablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return (false, "Укажите корректный путь к .exe файлу VPN-клиента в Настройках.");
        }

        string processName = Path.GetFileNameWithoutExtension(exePath);
        if (IsProcessAlive(processName) && CurrentStatus.State == VpnConnectionState.Connected)
        {
            await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);
            return (true, "VPN-клиент уже подключён.");
        }

        try
        {
            string arguments = _settingsService.Current.VpnStartMinimized
                ? "--connect --minimized"
                : "--connect";

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            _firstPingFailureUtc = null;
            await Task.Delay(500).ConfigureAwait(false);
            await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);

            return (true, "VPN-клиент запущен.");
        }
        catch (Exception ex)
        {
            return (false, $"Не удалось запустить VPN-клиент: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> DisconnectAsync()
    {
        string exePath = _settingsService.Current.VpnExecutablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return (false, "Путь к VPN-клиенту не задан.");
        }

        string processName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(processName) || ProcessBlacklist.ProtectedProcesses.Contains(processName))
        {
            return (false, "Некорректное имя процесса VPN.");
        }

        try
        {
            if (File.Exists(exePath))
            {
                try
                {
                    using var disconnectCmd = Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--disconnect",
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });

                    await Task.Delay(1200).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore if client does not support --disconnect CLI argument.
                }
            }

            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0 || string.Equals(ReadStatusFile()?.State?.Trim(), "disconnected", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var proc in processes)
                {
                    proc.Dispose();
                }

                _firstPingFailureUtc = null;
                await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);
                return (true, "VPN-клиент отключён.");
            }

            bool anyStillRunning = false;
            foreach (var proc in processes)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        bool sent = proc.CloseMainWindow();
                        if (!sent || proc.MainWindowHandle == IntPtr.Zero)
                        {
                            VpnNativeMethods.PostCloseToProcessWindows(proc.Id);
                        }

                        if (!proc.WaitForExit(2500))
                        {
                            anyStillRunning = true;
                        }
                    }
                }
                catch
                {
                    // Ignore per-process access or exit race errors.
                }
                finally
                {
                    proc.Dispose();
                }
            }

            bool isDisconnectedByFile = string.Equals(ReadStatusFile()?.State?.Trim(), "disconnected", StringComparison.OrdinalIgnoreCase);
            _firstPingFailureUtc = null;
            await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);

            return anyStillRunning && !isDisconnectedByFile
                ? (false, "VPN-клиент остался в трее и не закрылся. Отключите его вручную через иконку в трее.")
                : (true, "VPN-клиент отключён.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка при остановке VPN: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> RestartAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await Task.Delay(400).ConfigureAwait(false);
        return await ConnectAsync().ConfigureAwait(false);
    }

    public Task<(bool Success, string Message)> ToggleAsync()
    {
        return CurrentStatus.State != VpnConnectionState.Disconnected ? DisconnectAsync() : ConnectAsync();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        ProcessBlacklist.SetVpnExclusion(settings.VpnExecutablePath);
        _ = UpdateAsync(_disposeCts.Token);
    }

    private void StartFileWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_statusFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _watcher = new FileSystemWatcher(dir, "vpn_status.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnStatusFileChanged;
                _watcher.Created += OnStatusFileChanged;
                _watcher.Deleted += OnStatusFileChanged;
                _watcher.Renamed += OnStatusFileChanged;
            }
        }
        catch
        {
            // Fallback to PeriodicTimer polling if FileSystemWatcher cannot be attached.
        }
    }

    private void OnStatusFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!_disposed)
        {
            _ = UpdateAsync(_disposeCts.Token);
        }
    }

    private void StartBackgroundPolling()
    {
        if (_pollingTask is not null)
        {
            return;
        }

        var token = _disposeCts.Token;
        _pollingTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await UpdateAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private VpnStatusFileModel? ReadStatusFile()
    {
        try
        {
            if (!File.Exists(_statusFilePath))
            {
                return null;
            }

            using var stream = new FileStream(_statusFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<VpnStatusFileModel>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private (double LossPercent, double JitterMs, ConnectionQuality Quality) RecordAndComputeQuality(long? latestPingMs)
    {
        lock (_windowLock)
        {
            if (_pingWindow.Count >= PingWindowSize)
            {
                _pingWindow.Dequeue();
            }

            _pingWindow.Enqueue(latestPingMs);

            int total = _pingWindow.Count;
            if (total == 0)
            {
                return (0, 0, ConnectionQuality.Good);
            }

            int failed = 0;
            long? prevSuccess = null;
            long jitterSum = 0;
            int jitterPairs = 0;

            foreach (long? sample in _pingWindow)
            {
                if (!sample.HasValue)
                {
                    failed++;
                    prevSuccess = null;
                    continue;
                }

                if (prevSuccess.HasValue)
                {
                    jitterSum += Math.Abs(sample.Value - prevSuccess.Value);
                    jitterPairs++;
                }

                prevSuccess = sample.Value;
            }

            double lossPercent = Math.Round((failed * 100.0) / total, 0);
            double jitterMs = jitterPairs > 0 ? Math.Round((double)jitterSum / jitterPairs, 1) : 0;

            ConnectionQuality quality;
            if (lossPercent > 15.0 || (latestPingMs.HasValue && latestPingMs.Value > 150))
            {
                quality = ConnectionQuality.Bad;
            }
            else if (lossPercent >= 5.0 || jitterMs >= 20.0 || lossPercent > 0.0 || jitterMs >= 15.0)
            {
                quality = ConnectionQuality.Warning;
            }
            else
            {
                quality = ConnectionQuality.Good;
            }

            return (lossPercent, jitterMs, quality);
        }
    }

    private async Task<long?> MeasurePingAsync()
    {
        try
        {
            PingReply reply = await _ping.SendPingAsync(_activePingHost, PingTimeoutMs, PingBuffer).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessAlive(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        bool anyAlive = false;
        foreach (var proc in processes)
        {
            try
            {
                if (!proc.HasExited)
                {
                    anyAlive = true;
                }
            }
            catch
            {
                anyAlive = true;
            }
            finally
            {
                proc.Dispose();
            }
        }

        return anyAlive;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnStatusFileChanged;
            _watcher.Created -= OnStatusFileChanged;
            _watcher.Deleted -= OnStatusFileChanged;
            _watcher.Renamed -= OnStatusFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch
        {
        }

        _disposeCts.Dispose();
        _ping.Dispose();
        _refreshLock.Dispose();
    }
}
