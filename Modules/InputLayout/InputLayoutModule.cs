using System.Globalization;
using PulseGuard.Core.Interfaces;

namespace PulseGuard.Modules.InputLayout;

/// <summary>
/// Polls the active foreground window's keyboard layout every 250 ms via Win32 API
/// and reports two-letter ISO language codes (e.g. RU, EN, PL) with zero steady-state allocations.
/// </summary>
public sealed class InputLayoutModule : IMonitorModule
{
    private const int PollIntervalMs = 250;

    private readonly Dictionary<int, string> _langCodeCache = new();
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _loopCts;
    private Task? _pollTask;
    private int _lastLangId = -1;
    private string _currentLayoutCode = "EN";
    private bool _isPaused = true;
    private bool _disposed;

    public string Id => "input-layout";
    public string Name => "Индикатор раскладки клавиатуры";
    public string Description => "Определяет текущую раскладку клавиатуры активного окна (RU, EN и др.).";
    public bool IsEnabled { get; set; } = true;

    public string CurrentLayoutCode => _currentLayoutCode;

    /// <summary>
    /// Raised when the keyboard layout changes. Second parameter is true when switched from a previous layout.
    /// </summary>
    public event Action<string, bool>? LayoutChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        PollOnce(raiseSwitchHighlight: false);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        PollOnce(raiseSwitchHighlight: true);
        return Task.CompletedTask;
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
                PollOnce(raiseSwitchHighlight: false);
                StartLoopLocked();
            }
        }
    }

    private void StartLoopLocked()
    {
        if (_loopCts is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _pollTask = Task.Run(() => RunLoopAsync(token), token);
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
        _pollTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled || _isPaused)
                {
                    continue;
                }

                PollOnce(raiseSwitchHighlight: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PollOnce(bool raiseSwitchHighlight)
    {
        try
        {
            IntPtr hwnd = InputLayoutNativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            uint threadId = InputLayoutNativeMethods.GetWindowThreadProcessId(hwnd, out _);
            if (threadId == 0)
            {
                return;
            }

            IntPtr hkl = InputLayoutNativeMethods.GetKeyboardLayout(threadId);
            int langId = unchecked((int)(hkl.ToInt64() & 0xFFFF));
            if (langId <= 0 || langId == _lastLangId)
            {
                return;
            }

            if (!_langCodeCache.TryGetValue(langId, out string? code))
            {
                try
                {
                    code = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant();
                }
                catch (CultureNotFoundException)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    return;
                }

                _langCodeCache[langId] = code;
            }

            bool isSwitch = raiseSwitchHighlight && _lastLangId != -1 && !string.Equals(_currentLayoutCode, code, StringComparison.Ordinal);
            _lastLangId = langId;
            _currentLayoutCode = code;

            LayoutChanged?.Invoke(code, isSwitch);
        }
        catch
        {
            // Ignore transient Win32 errors during window/desktop transitions.
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
        }
    }
}

