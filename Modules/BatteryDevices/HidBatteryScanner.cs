using System.IO;
using System.Text;
using HidSharp;
using HidSharp.Reports;
using PulseGuard.Modules.BatteryDevices.Models;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.Modules.BatteryDevices;

/// <summary>
/// Scans USB/wireless HID interfaces via HidSharp for battery levels on 2.4 GHz receivers
/// (Logitech HID++ 2.0, Razer 90-byte Feature Report, and Standard HID Battery UsagePage 0x84/0x85).
/// </summary>
internal sealed class HidBatteryScanner
{
    private const int MicrosoftVendorId = 0x045E;
    private const int LogitechVendorId = 0x046D;
    private const int RazerVendorId = 0x1532;
    private static readonly HashSet<int> RedragonVendorIds = [0x258A, 0x04D9, 0x320F, 0x248A];
    private const int HidTimeoutMs = 300;
    private const int MaxReadAttempts = 5;
    private const int MaxAllowedSingleDropPercent = 15;

    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, BatteryDeviceInfo> _lastKnownLogitechDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pendingDropReadings = new(StringComparer.OrdinalIgnoreCase);

    public HidBatteryScanner(SettingsService settingsService)
    {
        _settingsService = settingsService;
        foreach (var cached in _settingsService.Current.CachedBatteryDevices)
        {
            if (cached.DeviceId.StartsWith("hid:logitech:", StringComparison.OrdinalIgnoreCase))
            {
                _lastKnownLogitechDevices[cached.DeviceId] = new BatteryDeviceInfo(
                    cached.DeviceId,
                    string.IsNullOrWhiteSpace(cached.Name) ? "Logitech Wireless Device" : cached.Name,
                    Math.Clamp(cached.LastKnownBatteryPercent, 0, 100),
                    IsConnected: false,
                    cached.DeviceType,
                    IsSleeping: false);
            }
        }
    }

    public List<BatteryDeviceInfo> Scan()
    {
        var results = new List<BatteryDeviceInfo>();
        var seenDeviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HidDevice[] allDevices;
        try
        {
            allDevices = DeviceList.Local.GetHidDevices().ToArray();
        }
        catch
        {
            return results;
        }

        // 1. Logitech HID++ 2.0 receivers & mice (VID 0x046D)
        var logitechResults = ScanLogitechDevices(allDevices, seenDeviceKeys);
        results.AddRange(logitechResults);

        // 2. Razer 2.4 GHz receivers / mice (VID 0x1532)
        foreach (var device in allDevices)
        {
            if (device.VendorID != RazerVendorId)
            {
                continue;
            }

            try
            {
                string key = $"hid:razer:{device.ProductID:X4}:{GetSafeSerial(device)}";
                if (seenDeviceKeys.Contains(key))
                {
                    continue;
                }

                if (TryReadRazerBattery(device, out int percent))
                {
                    seenDeviceKeys.Add(key);
                    string name = GetFriendlyHidName(device, "Razer Wireless Device");
                    var type = BatteryDeviceMonitorModule.ClassifyDeviceType(name, null, key);
                    if (type == BatteryDeviceType.Unknown)
                    {
                        type = BatteryDeviceType.Mouse;
                    }

                    results.Add(new BatteryDeviceInfo(key, name, percent, IsConnected: true, type));
                }
            }
            catch
            {
                // Ignore inaccessible or busy HID interfaces.
            }
        }

        // 3. Standard HID Battery (UsagePage 0x84 / 0x85, Battery Strength 0x66)
        foreach (var device in allDevices)
        {
            try
            {
                string key = $"hid:std:{device.VendorID:X4}:{device.ProductID:X4}:{GetSafeSerial(device)}";
                if (seenDeviceKeys.Contains(key))
                {
                    continue;
                }

                if (TryReadStandardHidBattery(device, out int percent))
                {
                    seenDeviceKeys.Add(key);
                    string name = GetFriendlyHidName(device, $"HID Device ({device.VendorID:X4}:{device.ProductID:X4})");
                    var type = BatteryDeviceMonitorModule.ClassifyDeviceType(name, null, key);
                    results.Add(new BatteryDeviceInfo(key, name, percent, IsConnected: true, type));
                }
            }
            catch
            {
                // Ignore inaccessible or busy HID interfaces.
            }
        }

        return results;
    }

    public string ExportHidDiagnostics()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "PulseGuard");
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "hid_devices.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"PulseGuard HID Diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 80));

        HidDevice[] devices = DeviceList.Local.GetHidDevices().ToArray();
        var priorityLines = new List<string>();
        var otherLines = new List<string>();

        foreach (var dev in devices)
        {
            string productName = GetFriendlyHidName(dev, "(Unknown)");
            string manufacturer = GetSafeManufacturer(dev);
            uint usagePageVal = 0;
            uint usageVal = 0;
            bool hasUsage = false;
            ReportDescriptor? descriptor = null;

            try
            {
                descriptor = dev.GetReportDescriptor();
                var topItem = descriptor.DeviceItems.FirstOrDefault();
                if (topItem is not null)
                {
                    uint rawUsage = topItem.Usages.GetAllValues().FirstOrDefault();
                    usagePageVal = rawUsage >> 16;
                    usageVal = rawUsage & 0xFFFF;
                    hasUsage = true;
                }
            }
            catch
            {
                // Some restricted system HIDs deny descriptor reads.
            }

            string usagePageHex = hasUsage ? $"0x{usagePageVal:X4}" : "N/A";
            string usageHex = hasUsage ? $"0x{usageVal:X4}" : "N/A";
            int maxIn = SafeGetLength(() => dev.GetMaxInputReportLength());
            int maxOut = SafeGetLength(() => dev.GetMaxOutputReportLength());
            int maxFeat = SafeGetLength(() => dev.GetMaxFeatureReportLength());
            string redragonTag = RedragonVendorIds.Contains(dev.VendorID) ? " [Redragon/Controller]" : string.Empty;

            string line =
                $"ProductName: {productName}{redragonTag} | Manufacturer: {manufacturer} | " +
                $"VID: 0x{dev.VendorID:X4} | PID: 0x{dev.ProductID:X4} | " +
                $"UsagePage: {usagePageHex} | Usage: {usageHex} | " +
                $"MaxFeatureReportLength: {maxFeat} | MaxOutputReportLength: {maxOut} | MaxInputReportLength: {maxIn}";

            bool isPriority = (hasUsage && (usagePageVal >= 0xFF00 || usagePageVal == 0x84 || usagePageVal == 0x85))
                || RedragonVendorIds.Contains(dev.VendorID)
                || (dev.VendorID != MicrosoftVendorId && dev.VendorID != LogitechVendorId);

            if (isPriority)
            {
                priorityLines.Add(line);
                if (maxFeat > 0 && hasUsage && usagePageVal >= 0xFF00 && dev.VendorID != MicrosoftVendorId)
                {
                    AppendFeatureReportSamples(dev, descriptor, maxFeat, priorityLines);
                }
            }
            else
            {
                otherLines.Add(line);
            }
        }

        sb.AppendLine();
        sb.AppendLine("[Priority HID Devices (Non-MS/Logitech, Redragon, UsagePage >= 0xFF00, Battery 0x84/0x85)]");
        if (priorityLines.Count == 0)
        {
            sb.AppendLine("(None found)");
        }
        else
        {
            foreach (string line in priorityLines)
            {
                sb.AppendLine(line);
            }
        }

        sb.AppendLine();
        sb.AppendLine("[Microsoft / Logitech / Other HID Devices]");
        foreach (string line in otherLines)
        {
            sb.AppendLine(line);
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return filePath;
    }

    private static void AppendFeatureReportSamples(
        HidDevice dev,
        ReportDescriptor? descriptor,
        int maxFeatureLength,
        List<string> outputLines)
    {
        try
        {
            var openConfig = new OpenConfiguration();
            openConfig.SetOption(OpenOption.Priority, OpenPriority.Low);

            using var stream = dev.Open(openConfig);
            stream.ReadTimeout = 150;

            var reportIds = descriptor?.FeatureReports.Select(r => r.ReportID).Distinct().Take(6).ToList()
                ?? [0x00];

            if (reportIds.Count == 0)
            {
                reportIds.Add(0x00);
            }

            foreach (byte reportId in reportIds)
            {
                try
                {
                    var buf = new byte[maxFeatureLength];
                    buf[0] = reportId;
                    stream.GetFeature(buf);
                    outputLines.Add($"    -> FeatureReport[0x{reportId:X2}]: {BitConverter.ToString(buf)}");
                }
                catch
                {
                    // Skip unreadable feature report IDs.
                }
            }
        }
        catch
        {
            // Ignore devices that deny opening feature streams.
        }
    }

    private static readonly byte[] LogitechDeviceIndices = [0x01, 0x02, 0xFF];

    private List<BatteryDeviceInfo> ScanLogitechDevices(HidDevice[] allDevices, HashSet<string> seenDeviceKeys)
    {
        var found = new List<BatteryDeviceInfo>();
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentLogitechPids = new HashSet<int>();

        foreach (var device in allDevices)
        {
            if (device.VendorID != LogitechVendorId)
            {
                continue;
            }

            presentLogitechPids.Add(device.ProductID);

            try
            {
                if (!IsLogitechHidPlusPlusInterface(device, out int maxOut, out int maxIn))
                {
                    continue;
                }

                bool useLongReport = maxOut >= 20 && maxIn >= 20;
                if (!useLongReport)
                {
                    continue;
                }

                const byte reportId = 0x11;
                const int reportLength = 20;
                const byte swId = 0x0A;

                var openConfig = new OpenConfiguration();
                openConfig.SetOption(OpenOption.Priority, OpenPriority.Low);

                using var stream = device.Open(openConfig);
                stream.ReadTimeout = HidTimeoutMs;
                stream.WriteTimeout = HidTimeoutMs;

                string serial = GetSafeSerial(device);
                string fallbackName = GetFriendlyHidName(device, "Logitech Wireless Device");
                bool foundReceiverSubDevice = false;

                foreach (byte deviceIndex in LogitechDeviceIndices)
                {
                    // If receiver already responded on slot 0x01/0x02, skip direct 0xFF probe on the same interface.
                    if (deviceIndex == 0xFF && foundReceiverSubDevice)
                    {
                        continue;
                    }

                    string key = $"hid:logitech:{device.ProductID:X4}:{serial}:idx{deviceIndex:X2}";
                    if (seenDeviceKeys.Contains(key))
                    {
                        continue;
                    }

                    int? previousValidPercent = _lastKnownLogitechDevices.TryGetValue(key, out var prevInfo)
                        ? prevInfo.BatteryPercent
                        : null;

                    if (TryReadLogitechDeviceAtIndex(stream, reportId, reportLength, deviceIndex, swId, previousValidPercent, out int percent, out bool isCharging, out string? realName))
                    {
                        if (deviceIndex is 0x01 or 0x02)
                        {
                            foundReceiverSubDevice = true;
                        }

                        percent = ResolveSuddenDrop(key, percent, previousValidPercent, isCharging);

                        string baseName = !string.IsNullOrWhiteSpace(realName) ? realName : fallbackName;
                        string displayName = isCharging ? $"{baseName} ⚡" : baseName;
                        var type = BatteryDeviceMonitorModule.ClassifyDeviceType(baseName, null, key);
                        if (type == BatteryDeviceType.Unknown)
                        {
                            type = BatteryDeviceType.Mouse;
                        }

                        var info = new BatteryDeviceInfo(key, displayName, percent, IsConnected: true, type, IsSleeping: false);
                        UpdateCachedLogitechDevice(key, info);
                        seenDeviceKeys.Add(key);
                        activeKeys.Add(key);
                        found.Add(info);
                    }
                }
            }
            catch
            {
                // Ignore inaccessible or sleeping HID interfaces to avoid cursor micro-stutters.
            }
        }

        // Preserve sleeping Logitech devices if their USB receiver is still plugged in;
        // only mark disconnected when the receiver itself is physically unplugged.
        foreach (var (key, lastInfo) in _lastKnownLogitechDevices)
        {
            if (!activeKeys.Contains(key) && !seenDeviceKeys.Contains(key))
            {
                seenDeviceKeys.Add(key);
                bool receiverPresent = TryExtractLogitechPid(key, out int pid) && presentLogitechPids.Contains(pid);
                found.Add(receiverPresent
                    ? lastInfo with { IsConnected = true, IsSleeping = true }
                    : lastInfo with { IsConnected = false, IsSleeping = false });
            }
        }

        return found;
    }

    private void UpdateCachedLogitechDevice(string key, BatteryDeviceInfo info)
    {
        bool changed = !_lastKnownLogitechDevices.TryGetValue(key, out var prev)
            || prev.BatteryPercent != info.BatteryPercent
            || !string.Equals(prev.Name, info.Name, StringComparison.Ordinal)
            || prev.DeviceType != info.DeviceType;

        _lastKnownLogitechDevices[key] = info;

        if (!changed)
        {
            return;
        }

        _settingsService.Update(settings =>
        {
            var existing = settings.CachedBatteryDevices
                .FirstOrDefault(c => string.Equals(c.DeviceId, key, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                settings.CachedBatteryDevices.Add(new CachedBatteryDevice
                {
                    DeviceId = key,
                    Name = info.Name,
                    LastKnownBatteryPercent = info.BatteryPercent,
                    DeviceType = info.DeviceType
                });
            }
            else
            {
                existing.Name = info.Name;
                existing.LastKnownBatteryPercent = info.BatteryPercent;
                existing.DeviceType = info.DeviceType;
            }
        });
    }

    private static bool TryExtractLogitechPid(string key, out int pid)
    {
        pid = 0;
        const string prefix = "hid:logitech:";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int start = prefix.Length;
        int colonIdx = key.IndexOf(':', start);
        if (colonIdx <= start)
        {
            return false;
        }

        return int.TryParse(key.AsSpan(start, colonIdx - start), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static bool IsLogitechHidPlusPlusInterface(HidDevice device, out int maxOut, out int maxIn)
    {
        maxOut = SafeGetLength(() => device.GetMaxOutputReportLength());
        maxIn = SafeGetLength(() => device.GetMaxInputReportLength());
        if (maxOut < 7 || maxIn < 7)
        {
            return false;
        }

        try
        {
            var desc = device.GetReportDescriptor();
            foreach (var item in desc.DeviceItems)
            {
                foreach (uint u in item.Usages.GetAllValues())
                {
                    uint page = u >> 16;
                    if (page is 0xFF00 or 0xFF43)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryReadLogitechDeviceAtIndex(
        HidStream stream,
        byte reportId,
        int reportLength,
        byte deviceIndex,
        byte swId,
        int? previousValidPercent,
        out int batteryPercent,
        out bool isCharging,
        out string? deviceName)
    {
        batteryPercent = 0;
        isCharging = false;
        deviceName = null;

        // Step A & B: Try Unified Battery (0x1004) first.
        if (TryQueryLogitechFeatureIndex(stream, reportId, reportLength, deviceIndex, swId, 0x1004, out byte unifiedIdx)
            && TryLogitechCommand(stream, reportId, reportLength, deviceIndex, unifiedIdx, (byte)(0x10 | swId), 0, 0, out byte[] unifiedResp))
        {
            int soc = unifiedResp[4];
            byte batteryLevel = unifiedResp[5]; // 1 = Critical, 2 = Low, 4 = Good, 8 = Full
            if (IsValidUnifiedBatteryReading(soc, batteryLevel, previousValidPercent))
            {
                batteryPercent = soc;
                isCharging = unifiedResp[6] is >= 1 and <= 3;
                deviceName = TryReadLogitechDeviceName(stream, reportId, reportLength, deviceIndex, swId);
                return true;
            }
        }

        // Fallback to Battery Level Status (0x1000).
        if (TryQueryLogitechFeatureIndex(stream, reportId, reportLength, deviceIndex, swId, 0x1000, out byte legacyIdx)
            && TryLogitechCommand(stream, reportId, reportLength, deviceIndex, legacyIdx, swId, 0, 0, out byte[] legacyResp))
        {
            int soc = legacyResp[4];
            if (soc is >= 1 and <= 100)
            {
                batteryPercent = soc;
                deviceName = TryReadLogitechDeviceName(stream, reportId, reportLength, deviceIndex, swId);
                return true;
            }
        }

        return false;
    }

    private int ResolveSuddenDrop(string key, int newPercent, int? previousValidPercent, bool isCharging)
    {
        if (!previousValidPercent.HasValue || isCharging)
        {
            _pendingDropReadings.Remove(key);
            return newPercent;
        }

        int prev = previousValidPercent.Value;
        if ((prev - newPercent) < MaxAllowedSingleDropPercent)
        {
            _pendingDropReadings.Remove(key);
            return newPercent;
        }

        // Sharp drop (e.g. 40% -> 15%): require two consecutive consistent readings
        // before accepting; otherwise keep the saved battery percentage.
        if (_pendingDropReadings.TryGetValue(key, out int pending) && Math.Abs(pending - newPercent) <= 3)
        {
            _pendingDropReadings.Remove(key);
            return newPercent;
        }

        _pendingDropReadings[key] = newPercent;
        return prev;
    }

    private static bool IsValidUnifiedBatteryReading(int soc, byte batteryLevel, int? previousValidPercent)
    {
        if (soc is < 1 or > 100)
        {
            return false;
        }

        // Reject contradictory 15% (0x0F) readings when batteryLevel says Good (4) or Full (8).
        if (soc == 15 && batteryLevel is 4 or 8)
        {
            return false;
        }

        if (batteryLevel is 1 or 2 or 4 or 8)
        {
            return true;
        }

        return previousValidPercent is <= 25 || soc != 15;
    }

    private static string? TryReadLogitechDeviceName(
        HidStream stream,
        byte reportId,
        int reportLength,
        byte deviceIndex,
        byte swId)
    {
        try
        {
            if (!TryQueryLogitechFeatureIndex(stream, reportId, reportLength, deviceIndex, swId, 0x0005, out byte nameFeatureIdx))
            {
                return null;
            }

            // Function 0: GetDeviceNameCount -> resp[4] = length
            if (!TryLogitechCommand(stream, reportId, reportLength, deviceIndex, nameFeatureIdx, swId, 0, 0, out byte[] countResp))
            {
                return null;
            }

            int totalLength = Math.Clamp((int)countResp[4], 0, 64);
            if (totalLength == 0)
            {
                return null;
            }

            var nameBytes = new List<byte>(totalLength);
            while (nameBytes.Count < totalLength)
            {
                byte charIndex = (byte)nameBytes.Count;
                // Function 1: GetDeviceName(charIndex) -> resp[4..19] (16 bytes max per chunk)
                if (!TryLogitechCommand(stream, reportId, reportLength, deviceIndex, nameFeatureIdx, (byte)(0x10 | swId), charIndex, 0, out byte[] chunkResp))
                {
                    break;
                }

                int remaining = totalLength - nameBytes.Count;
                int toCopy = Math.Min(16, remaining);
                for (int i = 0; i < toCopy; i++)
                {
                    byte b = chunkResp[4 + i];
                    if (b == 0)
                    {
                        break;
                    }

                    nameBytes.Add(b);
                }

                if (toCopy == 0)
                {
                    break;
                }
            }

            return nameBytes.Count > 0 ? Encoding.UTF8.GetString(nameBytes.ToArray()).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryQueryLogitechFeatureIndex(
        HidStream stream,
        byte reportId,
        int reportLength,
        byte deviceIndex,
        byte swId,
        ushort featureId,
        out byte featureIndex)
    {
        featureIndex = 0;
        byte p0 = (byte)(featureId >> 8);
        byte p1 = (byte)(featureId & 0xFF);

        if (!TryLogitechCommand(stream, reportId, reportLength, deviceIndex, 0x00, swId, p0, p1, out byte[] resp))
        {
            return false;
        }

        featureIndex = resp[4];
        return featureIndex > 0;
    }

    private static bool TryLogitechCommand(
        HidStream stream,
        byte reportId,
        int reportLength,
        byte deviceIndex,
        byte featureIndex,
        byte funcSwId,
        byte param0,
        byte param1,
        out byte[] response)
    {
        response = new byte[reportLength];
        var request = new byte[reportLength];
        request[0] = reportId;
        request[1] = deviceIndex;
        request[2] = featureIndex;
        request[3] = funcSwId;
        request[4] = param0;
        request[5] = param1;

        try
        {
            stream.Write(request, 0, request.Length);

            var deadline = Environment.TickCount64 + HidTimeoutMs;
            for (int attempt = 0; attempt < MaxReadAttempts && Environment.TickCount64 < deadline; attempt++)
            {
                int read = stream.Read(response, 0, response.Length);
                if (read < 20 || response[0] != 0x11)
                {
                    // Ignore short 7-byte ReportID 0x10 packets and incomplete reads.
                    continue;
                }

                // Filter out HID++ protocol error responses (0xFF or 0x8F)
                if (response[1] == deviceIndex && (response[2] == 0xFF || response[2] == 0x8F))
                {
                    return false;
                }

                // Strictly match our deviceIndex, featureIndex, and SoftwareID (0x0A) to ignore G HUB traffic.
                if (response[1] == deviceIndex
                    && response[2] == featureIndex
                    && (response[3] & 0x0F) == 0x0A)
                {
                    return true;
                }
            }
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    private static bool TryReadRazerBattery(HidDevice device, out int batteryPercent)
    {
        batteryPercent = 0;

        int maxFeature = SafeGetLength(() => device.GetMaxFeatureReportLength());
        if (maxFeature < 90)
        {
            return false;
        }

        var openConfig = new OpenConfiguration();
        openConfig.SetOption(OpenOption.Priority, OpenPriority.Low);

        using var stream = device.Open(openConfig);
        stream.ReadTimeout = HidTimeoutMs;
        stream.WriteTimeout = HidTimeoutMs;

        var buf = new byte[maxFeature];
        int offset = maxFeature > 90 ? 1 : 0; // Byte 0 is Report ID (0x00) when length is 91.

        // Razer 90-byte protocol: Class 0x07 (Power), Command 0x80 (GetBatteryLevel)
        buf[offset + 0] = 0x00; // Status: new command
        buf[offset + 1] = 0x1F; // Transaction ID
        buf[offset + 5] = 0x02; // Data size
        buf[offset + 6] = 0x07; // Command class: Power
        buf[offset + 7] = 0x80; // Command ID: GetBatteryLevel
        buf[offset + 88] = ComputeRazerCrc(buf, offset);

        stream.SetFeature(buf);
        Thread.Sleep(25);

        Array.Clear(buf, 0, buf.Length);
        stream.GetFeature(buf);

        // Status 0x02 = Successful response
        if (buf[offset + 0] != 0x02 || buf[offset + 6] != 0x07 || buf[offset + 7] != 0x80)
        {
            return false;
        }

        byte rawLevel = buf[offset + 9];
        if (rawLevel == 0)
        {
            return false;
        }

        batteryPercent = Math.Clamp((int)Math.Round((rawLevel * 100.0) / 255.0), 0, 100);
        return true;
    }

    private static byte ComputeRazerCrc(byte[] buf, int offset)
    {
        byte crc = 0;
        for (int i = 2; i < 88; i++)
        {
            crc ^= buf[offset + i];
        }

        return crc;
    }

    private static bool TryReadStandardHidBattery(HidDevice device, out int batteryPercent)
    {
        batteryPercent = 0;

        int maxFeature = SafeGetLength(() => device.GetMaxFeatureReportLength());
        if (maxFeature <= 0)
        {
            return false;
        }

        ReportDescriptor descriptor;
        try
        {
            descriptor = device.GetReportDescriptor();
        }
        catch
        {
            return false;
        }

        foreach (var report in descriptor.FeatureReports)
        {
            foreach (var dataItem in report.DataItems)
            {
                bool hasBatteryUsage = false;
                foreach (uint rawUsage in dataItem.Usages.GetAllValues())
                {
                    uint usagePage = rawUsage >> 16;
                    uint usageId = rawUsage & 0xFFFF;
                    if ((usagePage == 0x84 || usagePage == 0x85) && usageId == 0x66)
                    {
                        hasBatteryUsage = true;
                        break;
                    }
                }

                if (!hasBatteryUsage)
                {
                    continue;
                }

                var openConfig = new OpenConfiguration();
                openConfig.SetOption(OpenOption.Priority, OpenPriority.Low);

                using var stream = device.Open(openConfig);
                stream.ReadTimeout = HidTimeoutMs;

                var buf = new byte[maxFeature];
                buf[0] = report.ReportID;
                stream.GetFeature(buf);

                if (dataItem.ElementCount > 0)
                {
                    int val = dataItem.ReadLogical(buf, 8, 0);
                    if (val is >= 0 and <= 100)
                    {
                        batteryPercent = val;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static int SafeGetLength(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return 0;
        }
    }

    private static string GetSafeSerial(HidDevice device)
    {
        try
        {
            string? serial = device.GetSerialNumber();
            return string.IsNullOrWhiteSpace(serial) ? "noserial" : serial.Trim();
        }
        catch
        {
            return "noserial";
        }
    }

    private static string GetSafeManufacturer(HidDevice device)
    {
        try
        {
            string? mfg = device.GetManufacturer();
            return string.IsNullOrWhiteSpace(mfg) ? "(Unknown)" : mfg.Trim();
        }
        catch
        {
            return "(Unknown)";
        }
    }

    private static string GetFriendlyHidName(HidDevice device, string fallback)
    {
        try
        {
            string? product = device.GetProductName();
            if (!string.IsNullOrWhiteSpace(product))
            {
                return product.Trim();
            }

            string? friendly = device.GetFriendlyName();
            if (!string.IsNullOrWhiteSpace(friendly))
            {
                return friendly.Trim();
            }
        }
        catch
        {
            // Ignore descriptor read errors.
        }

        return fallback;
    }
}

