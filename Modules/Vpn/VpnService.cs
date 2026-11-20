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
    private const string PingTargetHost = "1.1.1.1";
    private const int PingTimeoutMs = 1500;
    private const double PingFailureErrorThresholdSeconds = 10.0;
    private const int PollIntervalMs = 2500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settingsService;
    private readonly string _statusFilePath;
    private readonly Ping _ping = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private FileSystemWatcher? _watcher;
    private Task? _pollingTask;
    private DateTime? _firstPingFailureUtc;
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

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartFileWatcher();
        StartBackgroundPolling();
        return UpdateAsync(cancellationToken);
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
            long? pingMs = null;

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
                // Process is running and not explicitly disconnected: verify tunnel with real ping to 1.1.1.1
                pingMs = await MeasurePingAsync().ConfigureAwait(false);

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
                isProcessRunning,
                isConfigured);

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
        if (IsProcessAlive(processName))
        {
            await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);
            return (true, "VPN-клиент уже запущен.");
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
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
            {
                await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);
                return (true, "VPN-клиент уже остановлен.");
            }

            foreach (var proc in processes)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(1500))
                        {
                            // Explicit user-requested VPN stop when tray-only client ignores WM_CLOSE.
                            proc.Kill();
                            proc.WaitForExit(1000);
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

            _firstPingFailureUtc = null;
            await UpdateAsync(_disposeCts.Token).ConfigureAwait(false);
            return (true, "VPN-клиент отключён.");
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
        return CurrentStatus.IsProcessRunning ? DisconnectAsync() : ConnectAsync();
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

    private async Task<long?> MeasurePingAsync()
    {
        try
        {
            PingReply reply = await _ping.SendPingAsync(PingTargetHost, PingTimeoutMs).ConfigureAwait(false);
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
