using System.Windows.Media;
using PulseGuard.Modules.BatteryDevices.Models;
using Wpf.Ui.Controls;

namespace PulseGuard.Modules.BatteryDevices;

/// <summary>
/// Shared UI presentation helpers (icons and charge-level brushes) for battery devices.
/// </summary>
public static class BatteryDevicePresentation
{
    public static readonly Brush NormalBrush = CreateFrozenBrush(0x00, 0xE5, 0xA0);
    public static readonly Brush WarningBrush = CreateFrozenBrush(0xFF, 0xA6, 0x00);
    public static readonly Brush CriticalBrush = CreateFrozenBrush(0xFF, 0x5C, 0x5C);
    public static readonly Brush OverlayNormalBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
    public static readonly Brush OfflineBrush = CreateFrozenBrush(0x8A, 0x8F, 0x98);

    public static SymbolRegular GetIcon(BatteryDeviceType deviceType) => deviceType switch
    {
        BatteryDeviceType.Mouse => SymbolRegular.Cursor20,
        BatteryDeviceType.Keyboard => SymbolRegular.Keyboard20,
        BatteryDeviceType.Headset => SymbolRegular.Headphones20,
        BatteryDeviceType.Gamepad => SymbolRegular.Games20,
        _ => SymbolRegular.Battery520
    };

    public static Brush GetCardBrush(int batteryPercent, bool isConnected)
    {
        if (!isConnected)
        {
            return OfflineBrush;
        }

        if (batteryPercent < 10)
        {
            return CriticalBrush;
        }

        if (batteryPercent < 25)
        {
            return WarningBrush;
        }

        return NormalBrush;
    }

    public static Brush GetOverlayTextBrush(int batteryPercent)
    {
        if (batteryPercent < 10)
        {
            return CriticalBrush;
        }

        if (batteryPercent < 25)
        {
            return WarningBrush;
        }

        return OverlayNormalBrush;
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

