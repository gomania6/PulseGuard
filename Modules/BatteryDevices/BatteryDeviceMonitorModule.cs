using System.Runtime.InteropServices;
using System.Text;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.BatteryDevices.Models;
using PulseGuard.Modules.Settings;

namespace PulseGuard.Modules.BatteryDevices;

/// <summary>
/// Polls Windows PnP / Bluetooth / HID devices for battery levels via SetupAPI
/// (<c>DEVPKEY_Device_BatteryPlusCharging</c>) every 30 seconds and USB HID++ receivers every 60 seconds.
/// </summary>
public sealed class BatteryDeviceMonitorModule : IMonitorModule
{
    private static readonly TimeSpan WakeCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HidPollingInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WakePollCooldown = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly byte[] _propBuffer = new byte[1024];
    private readonly char[] _instanceIdBuffer = new char[512];
    private readonly Dictionary<string, BatteryDeviceInfo> _dedupMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly HidBatteryScanner _hidScanner;

    private List<BatteryDeviceInfo> _lastHidDevices = [];
    private DateTime _lastHidScanUtc = DateTime.MinValue;
    private DateTime _lastWakePollUtc = DateTime.MinValue;
    private volatile bool _hasSleepingDevice;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _disposed;

    public BatteryDeviceMonitorModule(SettingsService settingsService)
    {
        _hidScanner = new HidBatteryScanner(settingsService);
    }

    public string Id => "battery-devices";
    public string Name => "Заряд устройств";
    public string Description => "Мониторинг уровня заряда беспроводных и PnP-устройств (SetupAPI).";
    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<BatteryDeviceInfo> LatestDevices { get; private set; } = [];

    public event Action<IReadOnlyList<BatteryDeviceInfo>>? DevicesUpdated;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await RefreshAsync(forceHidScan: true, cancellationToken).ConfigureAwait(false);
        StartBackgroundPolling();
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
        => RefreshAsync(forceHidScan: false, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshAsync(forceHidScan: false, cancellationToken);

    public async Task RefreshAsync(bool forceHidScan, CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsEnabled)
        {
            return;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = await Task.Run(() => ScanBatteryDevices(forceHidScan), cancellationToken).ConfigureAwait(false);
            _hasSleepingDevice = devices.Any(d => d.IsSleeping);
            LatestDevices = devices;
            DevicesUpdated?.Invoke(devices);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation on shutdown.
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public Task<string> ExportHidDiagnosticsAsync(CancellationToken cancellationToken = default)
        => Task.Run(_hidScanner.ExportHidDiagnostics, cancellationToken);

    private void StartBackgroundPolling()
    {
        if (_pollingTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollingTask = Task.Run(() => PollLoopAsync(token), token);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(WakeCheckInterval);
        var lastFullPollUtc = DateTime.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!IsEnabled)
                {
                    continue;
                }

                var nowUtc = DateTime.UtcNow;
                if (_hasSleepingDevice
                    && (nowUtc - _lastWakePollUtc) >= WakePollCooldown
                    && GetUserIdleTimeMs() < 2000)
                {
                    _lastWakePollUtc = nowUtc;
                    lastFullPollUtc = nowUtc;
                    await RefreshAsync(forceHidScan: true, token).ConfigureAwait(false);
                    continue;
                }

                if ((nowUtc - lastFullPollUtc) >= PollingInterval)
                {
                    lastFullPollUtc = nowUtc;
                    await RefreshAsync(forceHidScan: false, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static uint GetUserIdleTimeMs()
    {
        var info = new BatteryNativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<BatteryNativeMethods.LASTINPUTINFO>()
        };

        if (!BatteryNativeMethods.GetLastInputInfo(ref info))
        {
            return uint.MaxValue;
        }

        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    private IReadOnlyList<BatteryDeviceInfo> ScanBatteryDevices(bool forceHidScan)
    {
        _dedupMap.Clear();

        IntPtr devInfoSet = BatteryNativeMethods.SetupDiGetClassDevsW(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            BatteryNativeMethods.DigcfAllClasses | BatteryNativeMethods.DigcfPresent);

        if (devInfoSet == BatteryNativeMethods.InvalidHandleValue)
        {
            return Array.Empty<BatteryDeviceInfo>();
        }

        try
        {
            var devInfoData = new BatteryNativeMethods.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<BatteryNativeMethods.SP_DEVINFO_DATA>()
            };

            uint index = 0;
            while (BatteryNativeMethods.SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData))
            {
                index++;

                int? batteryPercent = TryReadBatteryPercent(devInfoSet, ref devInfoData);
                if (!batteryPercent.HasValue)
                {
                    continue;
                }

                string instanceId = TryReadInstanceId(devInfoSet, ref devInfoData);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                string? friendlyName = TryReadStringProperty(devInfoSet, ref devInfoData, BatteryNativeMethods.DevPKeyFriendlyName);
                string? deviceDesc = TryReadStringProperty(devInfoSet, ref devInfoData, BatteryNativeMethods.DevPKeyDeviceDesc);
                string? deviceClass = TryReadStringProperty(devInfoSet, ref devInfoData, BatteryNativeMethods.DevPKeyClass);
                bool isConnected = TryReadIsConnected(devInfoSet, ref devInfoData);

                // If child HID/BTH/GATT devnode has a generic or empty name, try parent devnode for the real device name/connection state.
                bool isGenericName = string.IsNullOrWhiteSpace(friendlyName)
                    || friendlyName.Contains("Battery", StringComparison.OrdinalIgnoreCase)
                    || friendlyName.StartsWith("HID", StringComparison.OrdinalIgnoreCase);

                if (isGenericName
                    && BatteryNativeMethods.CM_Get_Parent(out uint parentDevInst, devInfoData.DevInst, 0) == BatteryNativeMethods.CrSuccess)
                {
                    string? parentName = TryReadDevNodeStringProperty(parentDevInst, BatteryNativeMethods.DevPKeyFriendlyName);
                    if (!string.IsNullOrWhiteSpace(parentName))
                    {
                        friendlyName = parentName;
                    }

                    if (isConnected)
                    {
                        bool? parentConnected = TryReadDevNodeBoolProperty(parentDevInst, BatteryNativeMethods.DevPKeyIsConnected);
                        if (parentConnected.HasValue)
                        {
                            isConnected = parentConnected.Value;
                        }
                    }
                }

                string displayName = !string.IsNullOrWhiteSpace(friendlyName)
                    ? friendlyName.Trim()
                    : !string.IsNullOrWhiteSpace(deviceDesc)
                        ? deviceDesc.Trim()
                        : instanceId;

                // Use ContainerId for deduplication when multiple devnodes represent the same physical device,
                // excluding the local computer container ({00000000-0000-0000-FFFF-FFFFFFFFFFFF}).
                Guid? containerId = TryReadGuidProperty(devInfoSet, ref devInfoData, BatteryNativeMethods.DevPKeyContainerId);
                string dedupKey = containerId.HasValue && containerId.Value != Guid.Empty && !IsRootContainer(containerId.Value)
                    ? $"container:{containerId.Value:D}"
                    : $"inst:{instanceId}";

                var deviceType = ClassifyDeviceType(displayName, deviceClass, instanceId);
                int clampedPercent = Math.Clamp(batteryPercent.Value, 0, 100);

                var candidate = new BatteryDeviceInfo(
                    dedupKey,
                    displayName,
                    clampedPercent,
                    isConnected,
                    deviceType);

                if (_dedupMap.TryGetValue(dedupKey, out var existing))
                {
                    // Prefer connected entry, more specific device type, or longer/friendlier name.
                    bool takeCandidate =
                        (!existing.IsConnected && candidate.IsConnected)
                        || (existing.IsConnected == candidate.IsConnected
                            && existing.DeviceType == BatteryDeviceType.Unknown
                            && candidate.DeviceType != BatteryDeviceType.Unknown)
                        || (existing.IsConnected == candidate.IsConnected
                            && existing.Name.StartsWith("HID", StringComparison.OrdinalIgnoreCase)
                            && !candidate.Name.StartsWith("HID", StringComparison.OrdinalIgnoreCase));

                    if (takeCandidate)
                    {
                        _dedupMap[dedupKey] = candidate;
                    }
                }
                else
                {
                    _dedupMap[dedupKey] = candidate;
                }
            }
        }
        finally
        {
            BatteryNativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        try
        {
            var nowUtc = DateTime.UtcNow;
            if (forceHidScan || (nowUtc - _lastHidScanUtc) >= HidPollingInterval)
            {
                _lastHidDevices = _hidScanner.Scan();
                _lastHidScanUtc = nowUtc;
            }

            foreach (var hidDev in _lastHidDevices)
            {
                bool alreadyPresent = _dedupMap.Values.Any(existing =>
                    string.Equals(existing.Name, hidDev.Name, StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent && !_dedupMap.ContainsKey(hidDev.DeviceId))
                {
                    _dedupMap[hidDev.DeviceId] = hidDev;
                }
            }
        }
        catch
        {
            // Isolate HID scan failures from PnP battery results.
        }

        if (_dedupMap.Count == 0)
        {
            return Array.Empty<BatteryDeviceInfo>();
        }

        return _dedupMap.Values
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int? TryReadBatteryPercent(IntPtr devInfoSet, ref BatteryNativeMethods.SP_DEVINFO_DATA devInfoData)
    {
        if (!BatteryNativeMethods.SetupDiGetDevicePropertyW(
                devInfoSet,
                ref devInfoData,
                BatteryNativeMethods.DevPKeyBatteryPlusCharging,
                out uint propType,
                _propBuffer,
                (uint)_propBuffer.Length,
                out uint requiredSize,
                0))
        {
            return null;
        }

        if (propType == BatteryNativeMethods.DevPropTypeByte && requiredSize >= 1)
        {
            return _propBuffer[0];
        }

        if ((propType == BatteryNativeMethods.DevPropTypeUInt32 || propType == BatteryNativeMethods.DevPropTypeInt32)
            && requiredSize >= 4)
        {
            return (int)BitConverter.ToUInt32(_propBuffer, 0);
        }

        return null;
    }

    private bool TryReadIsConnected(IntPtr devInfoSet, ref BatteryNativeMethods.SP_DEVINFO_DATA devInfoData)
    {
        if (!BatteryNativeMethods.SetupDiGetDevicePropertyW(
                devInfoSet,
                ref devInfoData,
                BatteryNativeMethods.DevPKeyIsConnected,
                out uint propType,
                _propBuffer,
                (uint)_propBuffer.Length,
                out uint requiredSize,
                0))
        {
            // DIGCF_PRESENT already filters to present devices; if IsConnected property is absent, treat as connected.
            return true;
        }

        if (propType == BatteryNativeMethods.DevPropTypeBoolean && requiredSize >= 1)
        {
            return _propBuffer[0] != 0;
        }

        return true;
    }

    private string? TryReadStringProperty(
        IntPtr devInfoSet,
        ref BatteryNativeMethods.SP_DEVINFO_DATA devInfoData,
        in BatteryNativeMethods.DEVPROPKEY key)
    {
        if (!BatteryNativeMethods.SetupDiGetDevicePropertyW(
                devInfoSet,
                ref devInfoData,
                key,
                out uint propType,
                _propBuffer,
                (uint)_propBuffer.Length,
                out uint requiredSize,
                0))
        {
            return null;
        }

        if (propType != BatteryNativeMethods.DevPropTypeString || requiredSize < 2)
        {
            return null;
        }

        int byteCount = (int)Math.Min(requiredSize, (uint)_propBuffer.Length);
        return Encoding.Unicode.GetString(_propBuffer, 0, byteCount).TrimEnd('\0');
    }

    private Guid? TryReadGuidProperty(
        IntPtr devInfoSet,
        ref BatteryNativeMethods.SP_DEVINFO_DATA devInfoData,
        in BatteryNativeMethods.DEVPROPKEY key)
    {
        if (!BatteryNativeMethods.SetupDiGetDevicePropertyW(
                devInfoSet,
                ref devInfoData,
                key,
                out uint propType,
                _propBuffer,
                (uint)_propBuffer.Length,
                out uint requiredSize,
                0))
        {
            return null;
        }

        if (propType != BatteryNativeMethods.DevPropTypeGuid || requiredSize < 16)
        {
            return null;
        }

        return new Guid(_propBuffer.AsSpan(0, 16));
    }

    private string TryReadInstanceId(IntPtr devInfoSet, ref BatteryNativeMethods.SP_DEVINFO_DATA devInfoData)
    {
        if (!BatteryNativeMethods.SetupDiGetDeviceInstanceIdW(
                devInfoSet,
                ref devInfoData,
                _instanceIdBuffer,
                (uint)_instanceIdBuffer.Length,
                out uint requiredSize))
        {
            return string.Empty;
        }

        int length = (int)Math.Min(requiredSize, (uint)_instanceIdBuffer.Length);
        return new string(_instanceIdBuffer, 0, length).TrimEnd('\0');
    }

    private string? TryReadDevNodeStringProperty(uint devInst, in BatteryNativeMethods.DEVPROPKEY key)
    {
        uint size = (uint)_propBuffer.Length;
        if (BatteryNativeMethods.CM_Get_DevNode_PropertyW(
                devInst,
                key,
                out uint propType,
                _propBuffer,
                ref size,
                0) != BatteryNativeMethods.CrSuccess)
        {
            return null;
        }

        if (propType != BatteryNativeMethods.DevPropTypeString || size < 2)
        {
            return null;
        }

        int byteCount = (int)Math.Min(size, (uint)_propBuffer.Length);
        return Encoding.Unicode.GetString(_propBuffer, 0, byteCount).TrimEnd('\0');
    }

    private bool? TryReadDevNodeBoolProperty(uint devInst, in BatteryNativeMethods.DEVPROPKEY key)
    {
        uint size = (uint)_propBuffer.Length;
        if (BatteryNativeMethods.CM_Get_DevNode_PropertyW(
                devInst,
                key,
                out uint propType,
                _propBuffer,
                ref size,
                0) != BatteryNativeMethods.CrSuccess)
        {
            return null;
        }

        if (propType == BatteryNativeMethods.DevPropTypeBoolean && size >= 1)
        {
            return _propBuffer[0] != 0;
        }

        return null;
    }

    private static bool IsRootContainer(Guid guid)
    {
        // GUID_CONTAINER_ID_BASE: {00000000-0000-0000-FFFF-FFFFFFFFFFFF}
        return guid == new Guid(0x00000000, 0x0000, 0x0000, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
    }

    internal static BatteryDeviceType ClassifyDeviceType(string name, string? deviceClass, string instanceId)
    {
        if (ContainsAny(name, "mouse", "мышь", "g pro", "superlight", "viper", "deathadder", "basilisk", "mx master", "mx anywhere", "aerox", "rival", "trackball")
            || string.Equals(deviceClass, "Mouse", StringComparison.OrdinalIgnoreCase))
        {
            return BatteryDeviceType.Mouse;
        }

        if (ContainsAny(name, "keyboard", "клавиатура", "redragon", "k5", "k6", "draconic", "fizz", "deimos", "keychron", "blackwidow", "huntsman", "apex pro", "g915", "mx keys", "kbd")
            || string.Equals(deviceClass, "Keyboard", StringComparison.OrdinalIgnoreCase))
        {
            return BatteryDeviceType.Keyboard;
        }

        if (ContainsAny(name, "headset", "headphone", "earbuds", "earphone", "airpods", "buds", "arctis", "cloud", "g733", "kraken", "barracuda", "wh-1000", "wf-1000", "наушники", "гарнитура", "hands-free", "audio")
            || string.Equals(deviceClass, "AudioEndpoint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceClass, "MEDIA", StringComparison.OrdinalIgnoreCase))
        {
            return BatteryDeviceType.Headset;
        }

        if (ContainsAny(name, "controller", "gamepad", "xbox", "dualsense", "dualshock", "joy-con", "геймпад", "контроллер")
            || ContainsAny(instanceId, "IG_00", "XINPUT", "045E&PID_02"))
        {
            return BatteryDeviceType.Gamepad;
        }

        return BatteryDeviceType.Unknown;
    }

    private static bool ContainsAny(string source, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (source.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _refreshLock.Dispose();
    }
}

