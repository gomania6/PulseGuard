using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Overlay;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;
using PulseGuard.Modules.Vpn;
using PulseGuard.ViewModels;

namespace PulseGuard.Services;

/// <summary>
/// Registers and handles global Windows hotkeys via user32.dll RegisterHotKey:
/// Alt+Home / configurable hotkey (Toggle MainWindow), Alt+F1 (Overlay), Alt+F2 (Quick RAM Clean),
/// Alt+F3 (VPN Toggle), Alt+F5 (Game Mode Toggle). Alt+F4 is intentionally untouched.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint VK_HOME = 0x24;
    private const uint VK_F1 = 0x70;
    private const uint VK_F2 = 0x71;
    private const uint VK_F3 = 0x72;
    private const uint VK_F5 = 0x74;

    private const int HotkeyIdOverlay = 1;
    private const int HotkeyIdRamClean = 2;
    private const int HotkeyIdVpn = 3;
    private const int HotkeyIdGameMode = 5;
    private const int HotkeyIdToggleMainWindow = 6;
    private const int HotkeyIdToggleMainWindowCustom = 7;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly SettingsService _settingsService;
    private readonly OverlayService _overlayService;
    private readonly OverlayViewModel _overlayViewModel;
    private readonly SafeRamCleanerTool _ramCleanerTool;
    private readonly VpnService _vpnService;
    private readonly GameModeTool _gameModeTool;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly HashSet<int> _registeredIds = new();

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private string _registeredCustomHotkey = string.Empty;
    private bool _disposed;

    public GlobalHotkeyService(
        SettingsService settingsService,
        OverlayService overlayService,
        OverlayViewModel overlayViewModel,
        SafeRamCleanerTool ramCleanerTool,
        VpnService vpnService,
        GameModeTool gameModeTool,
        MainWindowViewModel mainWindowViewModel)
    {
        _settingsService = settingsService;
        _overlayService = overlayService;
        _overlayViewModel = overlayViewModel;
        _ramCleanerTool = ramCleanerTool;
        _vpnService = vpnService;
        _gameModeTool = gameModeTool;
        _mainWindowViewModel = mainWindowViewModel;

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Attach(Window window)
    {
        if (_disposed)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (handle == IntPtr.Zero || _hwnd == handle)
        {
            return;
        }

        DetachHook();
        _hwnd = handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        if (_settingsService.Current.IsGlobalHotkeysEnabled)
        {
            RegisterAll();
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _hwnd == IntPtr.Zero)
            {
                return;
            }

            if (settings.IsGlobalHotkeysEnabled)
            {
                if (!string.Equals(_registeredCustomHotkey, settings.ToggleWindowHotkey, StringComparison.Ordinal))
                {
                    if (_registeredIds.Remove(HotkeyIdToggleMainWindowCustom))
                    {
                        UnregisterHotKey(_hwnd, HotkeyIdToggleMainWindowCustom);
                    }
                    _registeredCustomHotkey = string.Empty;
                }

                RegisterAll();
            }
            else
            {
                UnregisterAll();
            }
        });
    }

    private void RegisterAll()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        TryRegister(HotkeyIdOverlay, MOD_ALT, VK_F1);
        TryRegister(HotkeyIdRamClean, MOD_ALT, VK_F2);
        TryRegister(HotkeyIdVpn, MOD_ALT, VK_F3);
        TryRegister(HotkeyIdGameMode, MOD_ALT, VK_F5);
        TryRegister(HotkeyIdToggleMainWindow, MOD_ALT, VK_HOME);

        string customHotkey = _settingsService.Current.ToggleWindowHotkey;
        if (TryParseHotkey(customHotkey, out uint customModifiers, out uint customVk))
        {
            if (TryRegister(HotkeyIdToggleMainWindowCustom, customModifiers, customVk))
            {
                _registeredCustomHotkey = customHotkey;
            }
        }
    }

    private bool TryRegister(int id, uint modifiers, uint vk)
    {
        if (_registeredIds.Contains(id))
        {
            return true;
        }

        if (RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
        {
            _registeredIds.Add(id);
            return true;
        }

        return false;
    }

    private static bool TryParseHotkey(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "Отключено", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Key? parsedKey = null;

        foreach (string part in parts)
        {
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_CONTROL;
            }
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_ALT;
            }
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_SHIFT;
            }
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_WIN;
            }
            else if (Enum.TryParse<Key>(part, ignoreCase: true, out var key))
            {
                parsedKey = key;
            }
        }

        if (parsedKey is null || modifiers == 0)
        {
            return false;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(parsedKey.Value);
        if (virtualKey <= 0)
        {
            return false;
        }

        vk = (uint)virtualKey;
        return true;
    }

    private void UnregisterAll()
    {
        _registeredCustomHotkey = string.Empty;

        if (_hwnd == IntPtr.Zero)
        {
            _registeredIds.Clear();
            return;
        }

        foreach (int id in _registeredIds)
        {
            UnregisterHotKey(_hwnd, id);
        }

        _registeredIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && !_disposed)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case HotkeyIdOverlay:
                    _overlayService.Toggle();
                    handled = true;
                    break;
                case HotkeyIdRamClean:
                    _ = HandleQuickRamCleanAsync();
                    handled = true;
                    break;
                case HotkeyIdVpn:
                    _ = _vpnService.ToggleAsync();
                    handled = true;
                    break;
                case HotkeyIdGameMode:
                    _ = _gameModeTool.ExecuteAsync();
                    handled = true;
                    break;
                case HotkeyIdToggleMainWindow:
                case HotkeyIdToggleMainWindowCustom:
                    Application.Current?.Dispatcher.BeginInvoke(() => _mainWindowViewModel.ToggleWindowCommand.Execute(null));
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private async Task HandleQuickRamCleanAsync()
    {
        var result = await _ramCleanerTool.ExecuteAsync().ConfigureAwait(false);
        string statusText = FormatReclaimedRamStatus(result);

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _overlayViewModel.ShowTransientStatus(statusText, 3000);
        });
    }

    private static string FormatReclaimedRamStatus(Core.Interfaces.OptimizationResult result)
    {
        const double gbBytes = 1024.0 * 1024.0 * 1024.0;
        const double mbBytes = 1024.0 * 1024.0;

        if (result.ReclaimedBytes >= gbBytes)
        {
            return $"Очищено: {result.ReclaimedBytes / gbBytes:0.0} ГБ";
        }

        if (result.ReclaimedBytes >= mbBytes)
        {
            return $"Очищено: {result.ReclaimedBytes / mbBytes:0} МБ";
        }

        return "ОЗУ оптимизировано";
    }

    private void DetachHook()
    {
        UnregisterAll();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                try
                {
                    dispatcher.Invoke(DetachHook);
                }
                catch
                {
                }
            }
        }
        else
        {
            DetachHook();
        }
    }
}

