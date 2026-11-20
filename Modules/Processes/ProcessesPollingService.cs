using PulseGuard.Modules.Processes.Models;

namespace PulseGuard.Modules.Processes;

/// <summary>
/// Polls running processes every 2 seconds ONLY when the Processes tab is open
/// and the main window is not minimized to the system tray.
/// Completely stops the PeriodicTimer otherwise.
/// </summary>
public sealed class ProcessesPollingService : IDisposable
{
    private const int PollingIntervalMs = 3000;

    private readonly ProcessMonitorModule _processModule;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _loopCts;
    private Task? _pollingTask;
    private bool _started;
    private bool _disposed;
    private bool _isPageActive;
    private bool _isMinimizedToTray;

    public event Action<ProcessesSnapshot>? ProcessesUpdated;

    public ProcessesPollingService(ProcessMonitorModule processModule)
    {
        _processModule = processModule;
    }

    public bool IsPollingEnabled => _isPageActive && !_isMinimizedToTray;

    public void SetPageActive(bool isActive)
    {
        lock (_lifecycleLock)
        {
            _isPageActive = isActive;
            SyncTimerStateLocked();
        }
    }

    public void SetMinimizedToTray(bool isMinimized)
    {
        lock (_lifecycleLock)
        {
            _isMinimizedToTray = isMinimized;
            SyncTimerStateLocked();
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            _started = true;
            SyncTimerStateLocked();
        }
    }

    public void TriggerImmediatePoll()
    {
        CancellationToken token;
        lock (_lifecycleLock)
        {
            if (_disposed || !IsPollingEnabled || _loopCts is null)
            {
                return;
            }

            token = _loopCts.Token;
        }

        _ = Task.Run(() => PollOnceAsync(token), token);
    }

    private void SyncTimerStateLocked()
    {
        if (_disposed || !_started)
        {
            return;
        }

        if (IsPollingEnabled)
        {
            StartLoopLocked();
        }
        else
        {
            StopLoopLocked();
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
        try
        {
            await _processModule.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollingIntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching tabs, minimizing to tray, or shutting down.
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _pollLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!IsPollingEnabled || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await _processModule.UpdateAsync(cancellationToken).ConfigureAwait(false);
            ProcessesUpdated?.Invoke(_processModule.LatestSnapshot);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown or pause.
        }
        catch
        {
            // Ignore transient process enumeration errors.
        }
        finally
        {
            _pollLock.Release();
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
            _pollLock.Dispose();
            _processModule.Dispose();
        }
    }
}
