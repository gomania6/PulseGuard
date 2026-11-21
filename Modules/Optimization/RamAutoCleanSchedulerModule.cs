using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Services;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Background scheduler that automatically runs SafeRamCleanerTool when RAM usage exceeds
/// the configured threshold or on a periodic interval, respecting cooldowns and notification profiles.
/// </summary>
public sealed class RamAutoCleanSchedulerModule : IMonitorModule, IDisposable
{
    private const int CheckIntervalSeconds = 15;

    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly SystemMonitorModule _systemMonitorModule;
    private readonly SettingsService _settingsService;
    private readonly GameModeTool _gameModeTool;
    private readonly TrayNotificationService _trayNotificationService;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTime _lastAutoCleanUtc = DateTime.MinValue;
    private bool _disposed;

    public string Id => "ram-autoclean-scheduler";
    public string Name => "Планировщик авто-очистки ОЗУ";
    public string Description => "Автоматически запускает безопасную очистку ОЗУ при превышении порога или по расписанию.";
    public bool IsEnabled { get; set; } = true;

    public string LastStatusMessage { get; private set; } = "Авто-очистка не выполнялась";

    public event Action<string>? AutoCleanCompleted;

    public RamAutoCleanSchedulerModule(
        SafeRamCleanerTool ramCleanerTool,
        SystemMonitorModule systemMonitorModule,
        SettingsService settingsService,
        GameModeTool gameModeTool,
        TrayNotificationService trayNotificationService)
    {
        _ramCleanerTool = ramCleanerTool;
        _systemMonitorModule = systemMonitorModule;
        _settingsService = settingsService;
        _gameModeTool = gameModeTool;
        _trayNotificationService = trayNotificationService;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _cts is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token), token);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        return EvaluateAsync(cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled || _disposed)
                {
                    continue;
                }

                await EvaluateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Current;
        if (!settings.IsAutoRamCleanEnabled || _disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, settings.AutoRamCleanCooldownMinutes));
        if ((nowUtc - _lastAutoCleanUtc) < cooldown)
        {
            return;
        }

        double currentRamPercent = _systemMonitorModule.LatestSnapshot.RamLoadPercent;
        bool thresholdExceeded = currentRamPercent >= settings.AutoRamCleanThresholdPercent;
        bool intervalDue = settings.AutoRamCleanIntervalMinutes > 0
            && _lastAutoCleanUtc != DateTime.MinValue
            && (nowUtc - _lastAutoCleanUtc) >= TimeSpan.FromMinutes(settings.AutoRamCleanIntervalMinutes);

        if (!thresholdExceeded && !intervalDue)
        {
            return;
        }

        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _lastAutoCleanUtc = DateTime.UtcNow;
            var result = await _ramCleanerTool.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            string reason = thresholdExceeded
                ? $"порог ОЗУ {currentRamPercent:0}% ≥ {settings.AutoRamCleanThresholdPercent:0}%"
                : $"расписание ({settings.AutoRamCleanIntervalMinutes} мин)";

            string status = $"[{DateTime.Now:HH:mm:ss}] Авто-очистка ({reason}): {result.Message}";
            LastStatusMessage = status;
            AutoCleanCompleted?.Invoke(status);

            if (ShouldShowBalloon(settings))
            {
                _trayNotificationService.TryShowInfoBalloon(
                    "PulseGuard — Авто-очистка ОЗУ",
                    $"{result.Message} (Причина: {reason})");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runLock.Release();
        }
    }

    private bool ShouldShowBalloon(AppSettings settings)
    {
        if (!settings.ShowThresholdNotifications)
        {
            return false;
        }

        return settings.NotificationProfile switch
        {
            NotificationProfile.Silent => false,
            NotificationProfile.GamingFocus => !_gameModeTool.IsActive,
            _ => true
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts?.Dispose();
        _cts = null;
        _runLock.Dispose();
    }
}

