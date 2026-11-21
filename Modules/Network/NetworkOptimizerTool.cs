using System.Diagnostics;
using System.Net.NetworkInformation;
using PulseGuard.Core.Interfaces;

namespace PulseGuard.Modules.Network;

public sealed record DnsBenchmarkEntry(
    string Name,
    string PrimaryIp,
    string SecondaryIp,
    long? PingMs);

public sealed record NetworkOptimizationDiagnostics(
    string AdapterName,
    string CurrentDns,
    IReadOnlyList<DnsBenchmarkEntry> Candidates,
    DnsBenchmarkEntry? FastestCandidate);

/// <summary>
/// Safe Windows network optimizer (no registry hacks): flushes DNS cache, benchmarks DNS resolvers,
/// switches the active adapter to the lowest-latency DNS via netsh, and normalizes TCP auto-tuning.
/// </summary>
public sealed class NetworkOptimizerTool : IOptimizerTool
{
    private static readonly (string Name, string Primary, string Secondary)[] DnsCandidates =
    [
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9", "9.9.9.9", "149.112.112.112"),
        ("Yandex", "77.88.8.8", "77.88.8.1")
    ];

    public string Id => "network-optimizer";
    public string Name => "Оптимизация интернета и DNS";
    public string Description => "Очищает кэш DNS, выбирает самый быстрый DNS-сервер и настраивает TCP-стек через штатный netsh (без правки реестра).";
    public bool CanExecute => true;

    public async Task<OptimizationPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var diag = await DiagnoseAsync(cancellationToken).ConfigureAwait(false);
        string summary = diag.FastestCandidate is { } best
            ? $"Адаптер: {diag.AdapterName} • Быстрейший DNS: {best.Name} ({best.PrimaryIp}, {best.PingMs} мс)"
            : $"Адаптер: {diag.AdapterName} • DNS: {diag.CurrentDns}";

        return new OptimizationPreview(summary, 0, diag.Candidates.Count);
    }

    public async Task<OptimizationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var diag = await DiagnoseAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(diag.AdapterName))
        {
            return new OptimizationResult(false, "Активный сетевой адаптер не найден.", 0, 0);
        }

        bool flushed = await RunCommandAsync("ipconfig.exe", cancellationToken, "/flushdns").ConfigureAwait(false);
        bool tcpOk = await RunCommandAsync(
            "netsh.exe",
            cancellationToken,
            "int", "tcp", "set", "global", "autotuninglevel=normal", "ecncapability=disabled").ConfigureAwait(false);

        if (diag.FastestCandidate is not { } best)
        {
            string msg = flushed
                ? "Кэш DNS очищен, TCP-стек проверен (не удалось замерить внешний DNS)."
                : "Не удалось выполнить оптимизацию сети (проверьте права администратора).";
            return new OptimizationResult(flushed || tcpOk, msg, 0, 1);
        }

        bool primarySet = await RunCommandAsync(
            "netsh.exe",
            cancellationToken,
            "interface", "ip", "set", "dns", $"name={diag.AdapterName}", "source=static", $"address={best.PrimaryIp}").ConfigureAwait(false);

        if (primarySet)
        {
            _ = await RunCommandAsync(
                "netsh.exe",
                cancellationToken,
                "interface", "ip", "add", "dns", $"name={diag.AdapterName}", $"address={best.SecondaryIp}", "index=2").ConfigureAwait(false);
        }

        if (!primarySet)
        {
            string partialMsg = flushed
                ? "Кэш DNS очищен, но для смены DNS адаптера требуются права администратора."
                : "Для изменения сетевых настроек требуются права администратора.";
            return new OptimizationResult(flushed, partialMsg, 0, flushed ? 1 : 0);
        }

        return new OptimizationResult(
            true,
            $"Готово: кэш DNS очищен, адаптер «{diag.AdapterName}» переключён на {best.Name} ({best.PrimaryIp}, {best.PingMs} мс), TCP Auto-Tuning нормализован.",
            0,
            1);
    }

    public async Task<OptimizationResult> FlushDnsAsync(CancellationToken cancellationToken = default)
    {
        bool ok = await RunCommandAsync("ipconfig.exe", cancellationToken, "/flushdns").ConfigureAwait(false);
        return ok
            ? new OptimizationResult(true, "Кэш DNS успешно очищен (ipconfig /flushdns).", 0, 1)
            : new OptimizationResult(false, "Не удалось очистить кэш DNS.", 0, 0);
    }

    public async Task<OptimizationResult> ResetDnsToDhcpAsync(CancellationToken cancellationToken = default)
    {
        var adapter = FindPrimaryActiveAdapter();
        if (adapter is null)
        {
            return new OptimizationResult(false, "Активный сетевой адаптер не найден.", 0, 0);
        }

        bool ok = await RunCommandAsync(
            "netsh.exe",
            cancellationToken,
            "interface", "ip", "set", "dns", $"name={adapter.Name}", "source=dhcp").ConfigureAwait(false);

        _ = await RunCommandAsync("ipconfig.exe", cancellationToken, "/flushdns").ConfigureAwait(false);

        return ok
            ? new OptimizationResult(true, $"DNS адаптера «{adapter.Name}» сброшен на автоматический (DHCP).", 0, 1)
            : new OptimizationResult(false, "Не удалось сбросить DNS (возможно, требуются права администратора).", 0, 0);
    }

    public async Task<NetworkOptimizationDiagnostics> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var adapter = FindPrimaryActiveAdapter();
        string adapterName = adapter?.Name ?? string.Empty;
        string currentDns = GetCurrentDnsText(adapter);

        var probeTasks = DnsCandidates.Select(async c =>
        {
            long? pingMs = await MeasureBestPingAsync(c.Primary, cancellationToken).ConfigureAwait(false);
            return new DnsBenchmarkEntry(c.Name, c.Primary, c.Secondary, pingMs);
        });

        var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        var fastest = results
            .Where(r => r.PingMs.HasValue)
            .OrderBy(r => r.PingMs!.Value)
            .FirstOrDefault();

        return new NetworkOptimizationDiagnostics(adapterName, currentDns, results, fastest);
    }

    private static NetworkInterface? FindPrimaryActiveAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n =>
                    n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                    && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    && !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                    && n.GetIPProperties().GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string GetCurrentDnsText(NetworkInterface? adapter)
    {
        if (adapter is null)
        {
            return "—";
        }

        try
        {
            var dns = adapter.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Take(2)
                .ToArray();

            return dns.Length > 0 ? string.Join(", ", dns) : "Авто (DHCP)";
        }
        catch
        {
            return "—";
        }
    }

    private static async Task<long?> MeasureBestPingAsync(string host, CancellationToken cancellationToken)
    {
        long? best = null;
        byte[] buffer = new byte[32];

        for (int i = 0; i < 2; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 800, buffer).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    if (!best.HasValue || reply.RoundtripTime < best.Value)
                    {
                        best = reply.RoundtripTime;
                    }
                }
            }
            catch
            {
            }
        }

        return best;
    }

    private static async Task<bool> RunCommandAsync(string fileName, CancellationToken cancellationToken, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

