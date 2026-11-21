namespace PulseGuard.Modules.BatteryDevices.Models;

/// <summary>
/// Categorized peripheral device type for icon selection.
/// </summary>
public enum BatteryDeviceType
{
    Unknown,
    Mouse,
    Keyboard,
    Headset,
    Gamepad
}

/// <summary>
/// Immutable snapshot of a PnP/wireless device with a battery sensor.
/// </summary>
public sealed record BatteryDeviceInfo(
    string DeviceId,
    string Name,
    int BatteryPercent,
    bool IsConnected,
    BatteryDeviceType DeviceType,
    bool IsSleeping = false)
{
    public bool IsAwake => IsConnected && !IsSleeping;
}

