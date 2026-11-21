using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using PulseGuard.Core.Interfaces;
using PulseGuard.Core.Security;
using PulseGuard.Modules.Network.Models;
using PulseGuard.Modules.Processes;

namespace PulseGuard.Modules.Network;

/// <summary>
/// Real-time per-process network radar using ETW (Microsoft-Windows-Kernel-Network / KernelTCPIP)
/// with 1-second background aggregation and zero-overhead pause support.
/// </summary>
public sealed class NetworkRadarModule : IMonitorModule
{
    private const string EtwSessionName = "PulseGuard_NetworkRadar";
    private const double MinDisplayMbps = 0.15;

    private sealed class TrafficBucket
    {
        public long RxBytes;
        public long TxBytes;
    }

    private sealed class ProcessMeta
    {
        public string Name = string.Empty;
        public string? ExecutablePath;
        public long LastVerifiedTicks;
    }

    private sealed class GroupAccumulator
    {
        public string ProcessName = string.Empty;
        public readonly List<int> Pids = new(4);
        public long RxBytes;
        public long TxBytes;
        public string? ExecutablePath;

        public void Reset(string processName, string? executablePath)
        {
            ProcessName = processName;
            Pids.Clear();
            RxBytes = 0;
            TxBytes = 0;
            ExecutablePath = executablePath;
        }
    }

    private readonly ProcessMonitorModule _processMonitorModule;
    private readonly object _bufferLock = new();
    private readonly object _lifecycleLock = new();
    private readonly Dictionary<int, TrafficBucket> _pidBuckets = new(128);
    private readonly Dictionary<int, ProcessMeta> _processMetaCache = new(256);
    private readonly Dictionary<string, GroupAccumulator> _groupAccumulators = new(64, StringComparer.OrdinalIgnoreCase);
    private readonly Stack<GroupAccumulator> _accumulatorPool = new(64);
    private readonly int _currentProcessId = Environment.ProcessId;

    private TraceEventSession? _etwSession;
    private Task? _etwListenerTask;
    private Task? _aggregationTask;
    private CancellationTokenSource? _runCts;
    private long _lastFlushTicks;
    private volatile bool _isPaused;
    private bool _disposed;

    public string Id => "network-radar";
    public string Name => "Сетевой радар лагов";
    public string Description => "Подсчёт входящей и исходящей скорости сети по процессам в реальном времени через ETW.";
    public bool IsEnabled { get; set; } = true;

    public NetworkRadarSnapshot LatestSnapshot { get; private set; } = NetworkRadarSnapshot.Empty;

    public event Action<NetworkRadarSnapshot>? RadarUpdated;

    public NetworkRadarModule(ProcessMonitorModule processMonitorModule)
    {
        _processMonitorModule = processMonitorModule;
        _processMonitorModule.SuspendedProcessesChanged += OnSuspendedProcessesChanged;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartCapture();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        // Aggregation runs on its own 1-second PeriodicTimer to keep ETW buffering decoupled.
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
                StopCaptureLocked();
            }
            else if (IsEnabled)
            {
                StartCaptureLocked();
            }
        }
    }

    public void StartCapture()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _isPaused || !IsEnabled)
            {
                return;
            }

            StartCaptureLocked();
        }
    }

    private void StartCaptureLocked()
    {
        if (_runCts is not null)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        _lastFlushTicks = Stopwatch.GetTimestamp();

        _etwListenerTask = Task.Run(() => RunEtwListener(token), token);
        _aggregationTask = Task.Run(() => RunAggregationLoopAsync(token), token);
    }

    private void StopCaptureLocked()
    {
        if (_runCts is null)
        {
            return;
        }

        try
        {
            _runCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        DisposeEtwSession();

        _runCts.Dispose();
        _runCts = null;
        _etwListenerTask = null;
        _aggregationTask = null;

        lock (_bufferLock)
        {
            _pidBuckets.Clear();
        }
    }

    private void RunEtwListener(CancellationToken cancellationToken)
    {
        try
        {
            TraceEventSession.GetActiveSession(EtwSessionName)?.Dispose();
        }
        catch
        {
            // Ignore cleanup failure for stale sessions.
        }

        try
        {
            var session = new TraceEventSession(EtwSessionName)
            {
                StopOnDispose = true
            };
            _etwSession = session;

            bool kernelEnabled = false;
            try
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                kernelEnabled = true;
            }
            catch
            {
                // Fallback to explicit Microsoft-Windows-Kernel-Network provider if kernel flags are restricted.
            }

            if (kernelEnabled)
            {
                session.Source.Kernel.TcpIpRecv += OnKernelRecv;
                session.Source.Kernel.TcpIpRecvIPV6 += OnKernelRecv;
                session.Source.Kernel.UdpIpRecv += OnKernelRecv;
                session.Source.Kernel.UdpIpRecvIPV6 += OnKernelRecv;

                session.Source.Kernel.TcpIpSend += OnKernelSend;
                session.Source.Kernel.TcpIpSendIPV6 += OnKernelSend;
                session.Source.Kernel.UdpIpSend += OnKernelSend;
                session.Source.Kernel.UdpIpSendIPV6 += OnKernelSend;
            }
            else
            {
                session.EnableProvider("Microsoft-Windows-Kernel-Network");
                session.Source.Dynamic.All += OnDynamicNetworkEvent;
            }

            using var reg = cancellationToken.Register(DisposeEtwSession);
            session.Source.Process();
        }
        catch
        {
            // ETW session stopped or unavailable (e.g., non-admin or shutting down).
        }
        finally
        {
            DisposeEtwSession();
        }
    }

    private void OnKernelRecv(TcpIpTraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: true);
    private void OnKernelRecv(TcpIpV6TraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: true);
    private void OnKernelRecv(UdpIpTraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: true);
    private void OnKernelRecv(UpdIpV6TraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: true);

    private void OnKernelSend(TcpIpSendTraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: false);
    private void OnKernelSend(TcpIpV6SendTraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: false);
    private void OnKernelSend(UdpIpTraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: false);
    private void OnKernelSend(UpdIpV6TraceData data) => RecordTraffic(data.ProcessID, data.size, isDownload: false);

    private void OnDynamicNetworkEvent(TraceEvent data)
    {
        if (data.ProcessID <= 4 || data.ProcessID == _currentProcessId)
        {
            return;
        }

        string eventName = data.EventName;
        bool isRecv = eventName.Contains("Recv", StringComparison.OrdinalIgnoreCase);
        bool isSend = !isRecv && eventName.Contains("Send", StringComparison.OrdinalIgnoreCase);
        if (!isRecv && !isSend)
        {
            return;
        }

        object? sizeObj = data.PayloadByName("size");
        if (sizeObj is null)
        {
            return;
        }

        int size = Convert.ToInt32(sizeObj);
        RecordTraffic(data.ProcessID, size, isRecv);
    }

    private void RecordTraffic(int pid, int size, bool isDownload)
    {
        if (pid <= 4 || pid == _currentProcessId || size <= 0 || _disposed || _isPaused)
        {
            return;
        }

        lock (_bufferLock)
        {
            if (!_pidBuckets.TryGetValue(pid, out var bucket))
            {
                bucket = new TrafficBucket();
                _pidBuckets[pid] = bucket;
            }

            if (isDownload)
            {
                bucket.RxBytes += size;
            }
            else
            {
                bucket.TxBytes += size;
            }
        }
    }

    private async Task RunAggregationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FlushAndPublishSnapshot();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSuspendedProcessesChanged()
    {
        if (!_disposed && !_isPaused)
        {
            FlushAndPublishSnapshot(reuseLastRates: true);
        }
    }

    private void FlushAndPublishSnapshot(bool reuseLastRates = false)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedSeconds = (double)(nowTicks - _lastFlushTicks) / Stopwatch.Frequency;
        if (!reuseLastRates)
        {
            _lastFlushTicks = nowTicks;
        }

        if (elapsedSeconds < 0.1)
        {
            elapsedSeconds = 1.0;
        }

        var suspendedPids = _processMonitorModule.SuspendedProcessIds;

        foreach (var acc in _groupAccumulators.Values)
        {
            _accumulatorPool.Push(acc);
        }
        _groupAccumulators.Clear();

        if (!reuseLastRates)
        {
            lock (_bufferLock)
            {
                foreach (var (pid, bucket) in _pidBuckets)
                {
                    long rx = bucket.RxBytes;
                    long tx = bucket.TxBytes;
                    bucket.RxBytes = 0;
                    bucket.TxBytes = 0;

                    if (rx == 0 && tx == 0 && !suspendedPids.Contains(pid))
                    {
                        continue;
                    }

                    AddPidToGroup(pid, rx, tx, nowTicks);
                }
            }
        }

        // Ensure any currently suspended process stays visible so the user can resume it even at 0 Mbit/s.
        foreach (int suspendedPid in suspendedPids)
        {
            if (suspendedPid <= 4 || suspendedPid == _currentProcessId)
            {
                continue;
            }

            if (!IsPidInAnyGroup(suspendedPid))
            {
                AddPidToGroup(suspendedPid, 0, 0, nowTicks);
            }
        }

        if (reuseLastRates && LatestSnapshot.TopProcesses.Count > 0)
        {
            var updated = new List<NetworkProcessTraffic>(LatestSnapshot.TopProcesses.Count);
            foreach (var item in LatestSnapshot.TopProcesses)
            {
                bool isSuspended = _processMonitorModule.IsGroupSuspended(item.Pids);
                updated.Add(item with { IsSuspended = isSuspended });
            }

            var refreshed = new NetworkRadarSnapshot
            {
                TopProcesses = updated,
                TopConsumer = updated.FirstOrDefault(p => p.DownloadMbps >= MinDisplayMbps),
                TotalTrackedDownloadMbps = LatestSnapshot.TotalTrackedDownloadMbps,
                TotalTrackedUploadMbps = LatestSnapshot.TotalTrackedUploadMbps,
                Timestamp = DateTime.Now
            };
            LatestSnapshot = refreshed;
            RadarUpdated?.Invoke(refreshed);
            return;
        }

        var allItems = new List<NetworkProcessTraffic>(_groupAccumulators.Count);
        double totalDownMbps = 0;
        double totalUpMbps = 0;

        foreach (var acc in _groupAccumulators.Values)
        {
            double downMbps = (acc.RxBytes * 8.0) / (elapsedSeconds * 1_000_000.0);
            double upMbps = (acc.TxBytes * 8.0) / (elapsedSeconds * 1_000_000.0);

            totalDownMbps += downMbps;
            totalUpMbps += upMbps;

            bool isSuspended = _processMonitorModule.IsGroupSuspended(acc.Pids);
            if (!isSuspended && downMbps < MinDisplayMbps && upMbps < MinDisplayMbps)
            {
                continue;
            }

            bool isProtected = ProcessBlacklist.IsProtected(acc.ProcessName);
            allItems.Add(new NetworkProcessTraffic(
                acc.ProcessName,
                acc.Pids.ToArray(),
                Math.Round(downMbps, 2),
                Math.Round(upMbps, 2),
                isProtected,
                isSuspended,
                acc.ExecutablePath));
        }

        allItems.Sort(static (a, b) =>
        {
            int suspendedCmp = b.IsSuspended.CompareTo(a.IsSuspended);
            if (suspendedCmp != 0)
            {
                return suspendedCmp;
            }

            double totalA = a.DownloadMbps + a.UploadMbps;
            double totalB = b.DownloadMbps + b.UploadMbps;
            return totalB.CompareTo(totalA);
        });

        var top3 = allItems.Count <= 3 ? allItems : allItems.GetRange(0, 3);
        var topConsumer = top3.FirstOrDefault(p => !p.IsSuspended && p.DownloadMbps >= MinDisplayMbps)
            ?? top3.FirstOrDefault(p => p.DownloadMbps >= MinDisplayMbps);

        var snapshot = new NetworkRadarSnapshot
        {
            TopProcesses = top3,
            TopConsumer = topConsumer,
            TotalTrackedDownloadMbps = Math.Round(totalDownMbps, 2),
            TotalTrackedUploadMbps = Math.Round(totalUpMbps, 2),
            Timestamp = DateTime.Now
        };

        LatestSnapshot = snapshot;
        RadarUpdated?.Invoke(snapshot);
    }

    private bool IsPidInAnyGroup(int pid)
    {
        foreach (var acc in _groupAccumulators.Values)
        {
            if (acc.Pids.Contains(pid))
            {
                return true;
            }
        }
        return false;
    }

    private void AddPidToGroup(int pid, long rx, long tx, long nowTicks)
    {
        var meta = ResolveProcessMeta(pid, nowTicks);
        if (meta is null)
        {
            return;
        }

        if (string.Equals(meta.Name, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(meta.Name, "Idle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(meta.Name, "PulseGuard", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_groupAccumulators.TryGetValue(meta.Name, out var acc))
        {
            acc = _accumulatorPool.Count > 0 ? _accumulatorPool.Pop() : new GroupAccumulator();
            acc.Reset(meta.Name, meta.ExecutablePath);
            _groupAccumulators[meta.Name] = acc;
        }
        else if (acc.ExecutablePath is null && meta.ExecutablePath is not null)
        {
            acc.ExecutablePath = meta.ExecutablePath;
        }

        acc.Pids.Add(pid);
        acc.RxBytes += rx;
        acc.TxBytes += tx;
    }

    private ProcessMeta? ResolveProcessMeta(int pid, long nowTicks)
    {
        if (_processMetaCache.TryGetValue(pid, out var cached))
        {
            // Refresh metadata at most once every 15 seconds in case of PID reuse.
            if ((nowTicks - cached.LastVerifiedTicks) < Stopwatch.Frequency * 15)
            {
                return string.IsNullOrEmpty(cached.Name) ? null : cached;
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            string? exePath = null;
            try
            {
                exePath = process.MainModule?.FileName;
            }
            catch
            {
                // Access denied for elevated/protected processes; name is still valid.
            }

            if (cached is null)
            {
                cached = new ProcessMeta();
                _processMetaCache[pid] = cached;
            }

            cached.Name = name;
            cached.ExecutablePath = exePath;
            cached.LastVerifiedTicks = nowTicks;
            return cached;
        }
        catch
        {
            _processMetaCache.Remove(pid);
            return null;
        }
    }

    private void DisposeEtwSession()
    {
        var session = Interlocked.Exchange(ref _etwSession, null);
        if (session is null)
        {
            return;
        }

        try
        {
            session.Source?.Dispose();
        }
        catch
        {
        }

        try
        {
            session.Dispose();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _processMonitorModule.SuspendedProcessesChanged -= OnSuspendedProcessesChanged;

        lock (_lifecycleLock)
        {
            StopCaptureLocked();
        }

        _processMetaCache.Clear();
        _groupAccumulators.Clear();
        _accumulatorPool.Clear();
    }
}

