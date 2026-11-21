using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.Monitoring.Models;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.Modules.Monitoring;

/// <summary>
/// Modular hardware monitor using LibreHardwareMonitorLib (CPU/Motherboard/GPU),
/// native Win32 APIs (GetSystemTimes, CallNtPowerInformation, GlobalMemoryStatusEx),
/// NetworkInterface for network throughput, and SafeCpuTemperatureFallback cascade.
/// </summary>
public sealed class SystemMonitorModule : IMonitorModule
{
    private readonly SettingsService _settingsService;
    private readonly SafeCpuTemperatureFallback _safeCpuTemperatureFallback = new();
    private Computer? _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly SystemMetricsSnapshot _snapshot = new();
    private readonly List<IHardware> _cpuHardware = new(2);
    private readonly List<IHardware> _motherboardHardware = new(2);
    private readonly List<IHardware> _gpuHardware = new(2);
    private readonly PROCESSOR_POWER_INFORMATION[] _powerInfoBuffer = new PROCESSOR_POWER_INFORMATION[Math.Max(1, Environment.ProcessorCount)];

    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;
    private bool _hasSystemTimesBaseline;

    private IntPtr _pdhQuery;
    private IntPtr _pdhPerfCounter;
    private bool _pdhInitialized;

    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastNetworkSampleUtc;
    private bool _hasNetworkBaseline;
    private bool _initialized;
    private bool _activeDisableCpuDriverSetting;
    private volatile bool _reinitComputerRequested;
    private bool _cpuDriverOpenFailed;
    private string? _lastLhmInitError;

    public string Id => "system-monitor";
    public string Name => "Системный мониторинг";
    public string Description => "Сбор показателей CPU, GPU, ОЗУ и сетевого трафика.";
    public bool IsEnabled { get; set; } = true;

    public SystemMetricsSnapshot LatestSnapshot => _snapshot;

    public SystemMonitorModule(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_initialized && settings.DisableCpuKernelDriver != _activeDisableCpuDriverSetting)
        {
            _reinitComputerRequested = true;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        InitializeComputer();

        if (TryGetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
            _hasSystemTimesBaseline = true;
        }

        InitializePdhCpuPerformanceCounter();

        try
        {
            SampleNetworkBytes(out _lastBytesReceived, out _lastBytesSent);
            _lastNetworkSampleUtc = DateTime.UtcNow;
            _hasNetworkBaseline = true;
        }
        catch
        {
            _hasNetworkBaseline = false;
        }

        _initialized = true;
        WriteSensorDiagnosticsFile();
        return Task.CompletedTask;
    }

    private void InitializeComputer()
    {
        _computer?.Close();
        _computer = null;
        _cpuHardware.Clear();
        _motherboardHardware.Clear();
        _gpuHardware.Clear();
        _cpuDriverOpenFailed = false;
        _lastLhmInitError = null;

        bool disableCpuDriver = _settingsService.Current.DisableCpuKernelDriver;
        _activeDisableCpuDriverSetting = disableCpuDriver;
        string? initError = null;

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = !disableCpuDriver,
                IsGpuEnabled = true,
                IsMotherboardEnabled = !disableCpuDriver,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsStorageEnabled = false
            };

            _computer.Open();
            _computer.Accept(_updateVisitor);
            CollectHardware(_computer);
        }
        catch (Exception ex)
        {
            initError = $"LibreHardwareMonitor: {ex.Message}";
            _lastLhmInitError = initError;
            if (!disableCpuDriver)
            {
                _cpuDriverOpenFailed = true;
                try
                {
                    _computer?.Close();
                    _computer = new Computer
                    {
                        IsCpuEnabled = false,
                        IsGpuEnabled = true,
                        IsMotherboardEnabled = false,
                        IsMemoryEnabled = false,
                        IsNetworkEnabled = false,
                        IsControllerEnabled = false,
                        IsStorageEnabled = false
                    };
                    _computer.Open();
                    _computer.Accept(_updateVisitor);
                    CollectHardware(_computer);
                }
                catch
                {
                    // GPU-only fallback also failed; keep going with native APIs and safe CPU fallbacks.
                }
            }
        }

        _snapshot.CpuDiagnosticError = initError;
    }

    private void CollectHardware(Computer computer)
    {
        _cpuHardware.Clear();
        _motherboardHardware.Clear();
        _gpuHardware.Clear();

        foreach (var hardware in computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                _cpuHardware.Add(hardware);
                _snapshot.CpuName = hardware.Name;
            }
            else if (hardware.HardwareType == HardwareType.Motherboard)
            {
                _motherboardHardware.Add(hardware);
            }
            else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                _gpuHardware.Add(hardware);
                if (_gpuHardware.Count == 1 || hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                {
                    _snapshot.GpuName = hardware.Name;
                }
            }
        }
    }

    private void WriteSensorDiagnosticsFile()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PulseGuard");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cpu_sensor_diag.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"PulseGuard CPU Sensor Diagnostics ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine($"DisableCpuKernelDriver setting: {_activeDisableCpuDriverSetting}");
            sb.AppendLine($"[Source 1] LibreHardwareMonitorLib: cpuHardware={_cpuHardware.Count}, motherboardHardware={_motherboardHardware.Count}, gpuHardware={_gpuHardware.Count}, openFailed={_cpuDriverOpenFailed}");
            if (!string.IsNullOrWhiteSpace(_lastLhmInitError))
            {
                sb.AppendLine($"  InitError: {_lastLhmInitError}");
            }

            AppendLhmHardwareDiagnostics(sb, "CPU", _cpuHardware);
            AppendLhmHardwareDiagnostics(sb, "Motherboard", _motherboardHardware);

            _safeCpuTemperatureFallback.AppendDiagnostics(sb);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Never let diagnostic logging disrupt monitoring.
        }
    }

    private static void AppendLhmHardwareDiagnostics(StringBuilder sb, string label, List<IHardware> hardwareList)
    {
        for (int i = 0; i < hardwareList.Count; i++)
        {
            var hw = hardwareList[i];
            sb.AppendLine($"  [{label}] {hw.Name} ({hw.HardwareType})");
            AppendLhmTempSensors(sb, hw.Sensors);
            for (int sub = 0; sub < hw.SubHardware.Length; sub++)
            {
                var subHw = hw.SubHardware[sub];
                sb.AppendLine($"    [SubHardware] {subHw.Name} ({subHw.HardwareType})");
                AppendLhmTempSensors(sb, subHw.Sensors);
            }
        }
    }

    private static void AppendLhmTempSensors(StringBuilder sb, ISensor[] sensors)
    {
        for (int s = 0; s < sensors.Length; s++)
        {
            if (sensors[s].SensorType == SensorType.Temperature)
            {
                string valStr = sensors[s].Value.HasValue ? $"{sensors[s].Value.Value:0.0} °C" : "null";
                sb.AppendLine($"      TempSensor: {sensors[s].Name} = {valStr}");
            }
        }
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || !_initialized)
        {
            return Task.CompletedTask;
        }

        if (_reinitComputerRequested)
        {
            _reinitComputerRequested = false;
            InitializeComputer();
            WriteSensorDiagnosticsFile();
        }

        try
        {
            UpdateCpuMetrics();
        }
        catch (Exception ex)
        {
            _snapshot.CpuDiagnosticError = $"CPU: {ex.Message}";
        }

        try
        {
            UpdateGpuMetrics();
        }
        catch
        {
            // Isolate GPU errors from RAM and Network.
        }

        try
        {
            UpdateRamMetrics();
        }
        catch
        {
            // Isolate RAM errors from Network.
        }

        try
        {
            UpdateNetworkMetrics();
        }
        catch
        {
            // Isolate Network errors.
        }

        _snapshot.Timestamp = DateTime.Now;
        return Task.CompletedTask;
    }

    private void UpdateCpuMetrics()
    {
        // 1. CPU Load (%) via native GetSystemTimes
        if (TryReadCpuLoadFromSystemTimes(out double nativeLoad))
        {
            _snapshot.CpuLoadPercent = nativeLoad;
        }

        // 2. CPU Temperature (°C) & Clock (MHz) via Source 1: LibreHardwareMonitorLib (CPU + Motherboard)
        double? preferredTemp = null;
        double? maxTemp = null;
        double? motherboardCpuTemp = null;
        bool hasAnyLhmTempSensor = false;
        double lhmClockSum = 0;
        int lhmClockCount = 0;
        string? lhmError = null;

        for (int i = 0; i < _cpuHardware.Count; i++)
        {
            try
            {
                var cpu = _cpuHardware[i];
                cpu.Update();
                ReadCpuSensors(cpu.Sensors, ref preferredTemp, ref maxTemp, ref hasAnyLhmTempSensor, ref lhmClockSum, ref lhmClockCount);

                for (int sub = 0; sub < cpu.SubHardware.Length; sub++)
                {
                    var subHardware = cpu.SubHardware[sub];
                    subHardware.Update();
                    ReadCpuSensors(subHardware.Sensors, ref preferredTemp, ref maxTemp, ref hasAnyLhmTempSensor, ref lhmClockSum, ref lhmClockCount);
                }
            }
            catch (Exception ex)
            {
                lhmError = $"LHM CPU: {ex.Message}";
            }
        }

        if (!preferredTemp.HasValue && !maxTemp.HasValue)
        {
            for (int i = 0; i < _motherboardHardware.Count; i++)
            {
                try
                {
                    var mb = _motherboardHardware[i];
                    mb.Update();
                    ReadMotherboardCpuTempSensors(mb.Sensors, ref motherboardCpuTemp, ref hasAnyLhmTempSensor);

                    for (int sub = 0; sub < mb.SubHardware.Length; sub++)
                    {
                        var subHardware = mb.SubHardware[sub];
                        subHardware.Update();
                        ReadMotherboardCpuTempSensors(subHardware.Sensors, ref motherboardCpuTemp, ref hasAnyLhmTempSensor);
                    }
                }
                catch
                {
                }
            }
        }

        // 3. Real-time CPU Clock priority:
        //    (a) LibreHardwareMonitorLib SensorType.Clock (average of CPU cores, excluding Bus)
        //    (b) PDH % Processor Performance * Base MHz from CallNtPowerInformation
        //    (c) Base MHz from CallNtPowerInformation marked as base-only
        if (lhmClockCount > 0)
        {
            _snapshot.CpuClockMhz = lhmClockSum / lhmClockCount;
            _snapshot.CpuClockIsBaseOnly = false;
        }
        else if (TryReadBaseCpuClockFromPowerInfo(out double baseMhz))
        {
            if (TryReadPdhProcessorPerformance(out double perfPercent))
            {
                _snapshot.CpuClockMhz = baseMhz * (perfPercent / 100.0);
                _snapshot.CpuClockIsBaseOnly = false;
            }
            else
            {
                _snapshot.CpuClockMhz = baseMhz;
                _snapshot.CpuClockIsBaseOnly = true;
            }
        }
        else
        {
            _snapshot.CpuClockMhz = null;
            _snapshot.CpuClockIsBaseOnly = false;
        }

        double? cpuTemp = preferredTemp ?? maxTemp ?? motherboardCpuTemp;
        string? activeSource = cpuTemp.HasValue ? "LibreHardwareMonitor" : null;
        if (!cpuTemp.HasValue)
        {
            (cpuTemp, activeSource) = _safeCpuTemperatureFallback.TryReadTemperature();
        }

        _snapshot.CpuTemperatureC = cpuTemp;
        if (cpuTemp.HasValue)
        {
            _snapshot.CpuTemperatureUnavailableReason = null;
            _snapshot.CpuDiagnosticError = $"Источник: {activeSource}";
        }
        else
        {
            string unavailableReason = _activeDisableCpuDriverSetting
                ? "Отключено в настройках"
                : (_cpuDriverOpenFailed || !hasAnyLhmTempSensor)
                    ? "Драйвер заблокирован Windows 11 Memory Integrity (HVCI) / Defender (запустите HWiNFO64 Shared Memory)"
                    : "Датчик не найден";

            _snapshot.CpuTemperatureUnavailableReason = unavailableReason;
            _snapshot.CpuDiagnosticError = lhmError ?? unavailableReason;
        }
    }

    private static void ReadMotherboardCpuTempSensors(
        ISensor[] sensors,
        ref double? motherboardCpuTemp,
        ref bool hasAnyTempSensor)
    {
        for (int s = 0; s < sensors.Length; s++)
        {
            var sensor = sensors[s];
            if (sensor.SensorType != SensorType.Temperature)
            {
                continue;
            }

            if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            {
                hasAnyTempSensor = true;
                if (!sensor.Value.HasValue)
                {
                    continue;
                }

                float val = sensor.Value.Value;
                if (val is >= 20 and <= 125 && (!motherboardCpuTemp.HasValue || val > motherboardCpuTemp.Value))
                {
                    motherboardCpuTemp = val;
                }
            }
        }
    }

    private static void ReadCpuSensors(
        ISensor[] sensors,
        ref double? preferredTemp,
        ref double? maxTemp,
        ref bool hasAnyTempSensor,
        ref double clockSum,
        ref int clockCount)
    {
        for (int s = 0; s < sensors.Length; s++)
        {
            var sensor = sensors[s];
            if (sensor.SensorType == SensorType.Temperature)
            {
                hasAnyTempSensor = true;
            }

            if (!sensor.Value.HasValue)
            {
                continue;
            }

            float val = sensor.Value.Value;

            if (sensor.SensorType == SensorType.Temperature && val is >= 20 and <= 125)
            {
                if (maxTemp is null || val > maxTemp.Value)
                {
                    maxTemp = val;
                }

                if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase))
                {
                    preferredTemp = val;
                }
            }
            else if (sensor.SensorType == SensorType.Clock &&
                     !sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase) &&
                     val > 0)
            {
                clockSum += val;
                clockCount++;
            }
        }
    }

    private bool TryReadCpuLoadFromSystemTimes(out double loadPercent)
    {
        loadPercent = 0;
        if (!TryGetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            return false;
        }

        if (!_hasSystemTimesBaseline)
        {
            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
            _hasSystemTimesBaseline = true;
            return false;
        }

        ulong deltaIdle = idle - _prevIdleTime;
        ulong deltaKernel = kernel - _prevKernelTime;
        ulong deltaUser = user - _prevUserTime;

        _prevIdleTime = idle;
        _prevKernelTime = kernel;
        _prevUserTime = user;

        ulong total = deltaKernel + deltaUser;
        if (total == 0)
        {
            return false;
        }

        double busy = total >= deltaIdle ? total - deltaIdle : 0;
        loadPercent = Math.Clamp((busy / total) * 100.0, 0.0, 100.0);
        return true;
    }

    private bool TryReadBaseCpuClockFromPowerInfo(out double mhz)
    {
        mhz = 0;
        int size = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>() * _powerInfoBuffer.Length;
        if (CallNtPowerInformation(11 /* ProcessorInformation */, IntPtr.Zero, 0, _powerInfoBuffer, size) != 0)
        {
            return false;
        }

        double sum = 0;
        int count = 0;
        for (int i = 0; i < _powerInfoBuffer.Length; i++)
        {
            uint freq = _powerInfoBuffer[i].MaxMhz > 0
                ? _powerInfoBuffer[i].MaxMhz
                : _powerInfoBuffer[i].CurrentMhz;
            if (freq > 0)
            {
                sum += freq;
                count++;
            }
        }

        if (count == 0)
        {
            return false;
        }

        mhz = sum / count;
        return true;
    }

    private void InitializePdhCpuPerformanceCounter()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0)
            {
                return;
            }

            const string counterPath = @"\Processor Information(_Total)\% Processor Performance";
            if (PdhAddEnglishCounterW(_pdhQuery, counterPath, IntPtr.Zero, out _pdhPerfCounter) != 0)
            {
                PdhCloseQuery(_pdhQuery);
                _pdhQuery = IntPtr.Zero;
                return;
            }

            PdhCollectQueryData(_pdhQuery);
            _pdhInitialized = true;
        }
        catch
        {
            if (_pdhQuery != IntPtr.Zero)
            {
                PdhCloseQuery(_pdhQuery);
                _pdhQuery = IntPtr.Zero;
            }

            _pdhInitialized = false;
        }
    }

    private bool TryReadPdhProcessorPerformance(out double percentPerformance)
    {
        percentPerformance = 0;
        if (!_pdhInitialized || _pdhQuery == IntPtr.Zero || _pdhPerfCounter == IntPtr.Zero)
        {
            return false;
        }

        if (PdhCollectQueryData(_pdhQuery) != 0)
        {
            return false;
        }

        const uint pdhFmtDouble = 0x00000200;
        if (PdhGetFormattedCounterValue(_pdhPerfCounter, pdhFmtDouble, IntPtr.Zero, out var value) != 0)
        {
            return false;
        }

        if (value.CStatus != 0 || value.DoubleValue <= 0)
        {
            return false;
        }

        percentPerformance = value.DoubleValue;
        return true;
    }

    private void UpdateGpuMetrics()
    {
        if (_gpuHardware.Count == 0)
        {
            _snapshot.GpuLoadPercent = null;
            _snapshot.GpuTemperatureC = null;
            _snapshot.GpuVramUsedMb = null;
            _snapshot.GpuVramTotalMb = null;
            return;
        }

        IHardware? selectedGpu = null;
        for (int i = 0; i < _gpuHardware.Count; i++)
        {
            var candidate = _gpuHardware[i];
            try
            {
                candidate.Update();
            }
            catch
            {
                continue;
            }

            if (selectedGpu is null || candidate.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
            {
                selectedGpu = candidate;
            }
        }

        if (selectedGpu is null)
        {
            return;
        }

        _snapshot.GpuName = selectedGpu.Name;

        double? coreLoad = null;
        int coreLoadPriority = 0;
        double? coreTemp = null;
        int coreTempPriority = 0;
        double? vramUsed = null;
        double? vramTotal = null;

        for (int s = 0; s < selectedGpu.Sensors.Length; s++)
        {
            var sensor = selectedGpu.Sensors[s];
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            float val = sensor.Value.Value;

            if (sensor.SensorType == SensorType.Load)
            {
                int priority = GetGpuLoadPriority(sensor.Name);
                if (priority > coreLoadPriority)
                {
                    coreLoad = val;
                    coreLoadPriority = priority;
                }
            }
            else if (sensor.SensorType == SensorType.Temperature && val is > 0 and < 150)
            {
                int priority = GetGpuTempPriority(sensor.Name);
                if (priority > coreTempPriority)
                {
                    coreTemp = val;
                    coreTempPriority = priority;
                }
            }
            else if (sensor.SensorType == SensorType.SmallData)
            {
                if (sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
                {
                    vramUsed = val;
                }
                else if (sensor.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase))
                {
                    vramTotal = val;
                }
            }
        }

        _snapshot.GpuLoadPercent = coreLoad;
        _snapshot.GpuTemperatureC = coreTemp;
        _snapshot.GpuVramUsedMb = vramUsed;
        _snapshot.GpuVramTotalMb = vramTotal;
    }

    private static int GetGpuTempPriority(string sensorName)
    {
        bool isSecondary =
            sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("Junction", StringComparison.OrdinalIgnoreCase) ||
            sensorName.Contains("VR", StringComparison.OrdinalIgnoreCase);

        if (!isSecondary && sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (!isSecondary && (string.Equals(sensorName, "GPU", StringComparison.OrdinalIgnoreCase) ||
                             sensorName.Contains("Edge", StringComparison.OrdinalIgnoreCase)))
        {
            return 3;
        }

        if (!isSecondary)
        {
            return 2;
        }

        return 1;
    }

    private static int GetGpuLoadPriority(string sensorName)
    {
        if (sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
            !sensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (sensorName.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private void UpdateRamMetrics()
    {
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus) && memStatus.ullTotalPhys > 0)
        {
            const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
            double totalGb = memStatus.ullTotalPhys / bytesPerGb;
            double availGb = memStatus.ullAvailPhys / bytesPerGb;
            double usedGb = Math.Max(0, totalGb - availGb);

            _snapshot.RamTotalGb = totalGb;
            _snapshot.RamUsedGb = usedGb;
            _snapshot.RamLoadPercent = (usedGb / totalGb) * 100.0;
        }
    }

    private void UpdateNetworkMetrics()
    {
        var nowUtc = DateTime.UtcNow;
        SampleNetworkBytes(out long rx, out long tx);

        if (!_hasNetworkBaseline)
        {
            _lastBytesReceived = rx;
            _lastBytesSent = tx;
            _lastNetworkSampleUtc = nowUtc;
            _hasNetworkBaseline = true;
            _snapshot.DownloadBytesPerSec = 0;
            _snapshot.UploadBytesPerSec = 0;
            return;
        }

        double elapsedSeconds = (nowUtc - _lastNetworkSampleUtc).TotalSeconds;
        if (elapsedSeconds > 0.1)
        {
            long deltaRx = Math.Max(0, rx - _lastBytesReceived);
            long deltaTx = Math.Max(0, tx - _lastBytesSent);

            _snapshot.DownloadBytesPerSec = deltaRx / elapsedSeconds;
            _snapshot.UploadBytesPerSec = deltaTx / elapsedSeconds;

            _lastBytesReceived = rx;
            _lastBytesSent = tx;
            _lastNetworkSampleUtc = nowUtc;
        }
    }

    private static void SampleNetworkBytes(out long totalReceived, out long totalSent)
    {
        totalReceived = 0;
        totalSent = 0;

        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int i = 0; i < interfaces.Length; i++)
        {
            var ni = interfaces[i];
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (IsVirtualAdapter(ni.Description, ni.Name))
            {
                var stats = ni.GetIPv4Statistics();
                if (stats.BytesReceived == 0 && stats.BytesSent == 0)
                {
                    continue;
                }
            }

            var ipStats = ni.GetIPv4Statistics();
            totalReceived += ipStats.BytesReceived;
            totalSent += ipStats.BytesSent;
        }
    }

    private static bool IsVirtualAdapter(string description, string name)
    {
        return description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime)
    {
        idleTime = 0;
        kernelTime = 0;
        userTime = 0;

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return false;
        }

        idleTime = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
        kernelTime = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
        userTime = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
        return true;
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;

        if (_pdhQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
            _pdhPerfCounter = IntPtr.Zero;
            _pdhInitialized = false;
        }

        _safeCpuTemperatureFallback.Dispose();
        _computer?.Close();
        _computer = null;
        _initialized = false;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr lpInputBuffer,
        int nInputBufferSize,
        [Out] PROCESSOR_POWER_INFORMATION[] lpOutputBuffer,
        int nOutputBufferSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr hQuery);
}
