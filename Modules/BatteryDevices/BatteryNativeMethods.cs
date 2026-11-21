using System.Runtime.InteropServices;

namespace PulseGuard.Modules.BatteryDevices;

/// <summary>
/// Win32 SetupAPI and Configuration Manager (cfgmgr32) P/Invoke declarations
/// for reading PnP/wireless device battery properties without extra WinRT dependencies.
/// </summary>
internal static class BatteryNativeMethods
{
    public static readonly IntPtr InvalidHandleValue = new(-1);

    public const uint DigcfPresent = 0x00000002;
    public const uint DigcfAllClasses = 0x00000004;

    public const uint DevPropTypeByte = 0x00000003;
    public const uint DevPropTypeInt32 = 0x00000006;
    public const uint DevPropTypeUInt32 = 0x00000007;
    public const uint DevPropTypeGuid = 0x0000000D;
    public const uint DevPropTypeBoolean = 0x00000011;
    public const uint DevPropTypeString = 0x00000012;

    public const uint CrSuccess = 0x00000000;

    // DEVPKEY_Device_BatteryPlusCharging {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2
    public static readonly DEVPROPKEY DevPKeyBatteryPlusCharging = new()
    {
        fmtid = new Guid(0x104EA319, 0x6EE2, 0x4701, 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5),
        pid = 2
    };

    // DEVPKEY_Device_IsConnected {83DA6326-97A6-4088-9453-A1923F573B29}, 15
    public static readonly DEVPROPKEY DevPKeyIsConnected = new()
    {
        fmtid = new Guid(0x83DA6326, 0x97A6, 0x4088, 0x94, 0x53, 0xA1, 0x92, 0x3F, 0x57, 0x3B, 0x29),
        pid = 15
    };

    // DEVPKEY_Device_FriendlyName {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 14
    public static readonly DEVPROPKEY DevPKeyFriendlyName = new()
    {
        fmtid = new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0),
        pid = 14
    };

    // DEVPKEY_Device_DeviceDesc {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 2
    public static readonly DEVPROPKEY DevPKeyDeviceDesc = new()
    {
        fmtid = new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0),
        pid = 2
    };

    // DEVPKEY_Device_Class {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 9
    public static readonly DEVPROPKEY DevPKeyClass = new()
    {
        fmtid = new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0),
        pid = 9
    };

    // DEVPKEY_Device_ContainerId {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}, 2
    public static readonly DEVPROPKEY DevPKeyContainerId = new()
    {
        fmtid = new Guid(0x8C7ED206, 0x3F8A, 0x4827, 0xB3, 0xAB, 0xAE, 0x9E, 0x1F, 0xAE, 0xFC, 0x6C),
        pid = 2
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        in DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        char[] deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    public static extern uint CM_Get_Parent(
        out uint pdnDevInst,
        uint dnDevInst,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    public static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        in DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}

