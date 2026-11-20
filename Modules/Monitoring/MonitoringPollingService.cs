using PulseGuard.Modules.Monitoring.Models;

namespace PulseGuard.Modules.Monitoring;

/// <summary>
/// Coordinates background 1-second sensor polling and completely stops the PeriodicTimer when minimized to tray.
/// </summary>
public sealed class MonitoringPollingService : IDisposable
{
    private readonly SystemMonitorModule _monitorModule;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _loopCts;
    private Task? _pollingTask;
    private bool _isInitialized;
    private bool _disposed;
    private volatile bool _isPaused;
    private int _intervalMs = 1000;

    public event Action<SystemMetricsSnapshot>? MetricsUpdated;

    public MonitoringPollingService(SystemMonitorModule monitorModule)
    {
        _monitorModule = monitorModule;
    }

    public bool IsPaused => _isPaused;

    public void SetPollingInterval(int intervalMs)
    {
        _intervalMs = Math.Clamp(intervalMs, 250, 10000);
        if (!_isPaused)
        {
            RestartLoop();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _isPaused == paused)
            {
                return;
            }

            _isPaused = paused;
            if (paused)
            {
                StopLoopLocked();
            }
            else
            {
                StartLoopLocked();
            }
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _isPaused)
            {
                return;
            }

            StartLoopLocked();
        }
    }

    private void RestartLoop()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _isPaused)
            {
                return;
            }

            StopLoopLocked();
            StartLoopLocked();
        }
    }

    private void StartLoopLocked()
    {
        if (_pollingTask is not null && !_pollingTask.IsCompleted)
        {
            return;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var token = _loopCts.Token;
        _pollingTask = Task.Run(() => RunLoopAsync(token), token);
    }

    private void StopLoopLocked()
    {
        if (_loopCts is null)
        {
            return;
        }

        try
        {
            _loopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _loopCts.Dispose();
        _loopCts = null;
        _pollingTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (!_isInitialized)
        {
            try
            {
                await _monitorModule.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _isInitialized = true;
            }
            catch
            {
                // Keep service alive even if hardware driver initialization fails partially.
            }
        }

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _monitorModule.UpdateAsync(cancellationToken).ConfigureAwait(false);
                    MetricsUpdated?.Invoke(_monitorModule.LatestSnapshot);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient sensor read errors during polling.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when paused/minimized to tray or disposed.
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLoopLocked();
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _monitorModule.Dispose();
        }
    }
}
