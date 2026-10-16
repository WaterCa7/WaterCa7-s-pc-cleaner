using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

/// <summary>
/// Read-only measurements of the real system: adapters, traffic counters, TCP connect latency, router ping,
/// DNS resolve time, Wi-Fi (netsh) and socket tables. Nothing here changes any setting.
/// </summary>
public static class NetworkProbes
{
    /// <summary>
    /// A documentation address (RFC 5737, TEST-NET-1) that is never routed on the internet. If a TCP connection to it
    /// "succeeds", a VPN or proxy answers connections itself, so TCP connect times would be meaningless.
    /// </summary>
    private static readonly IPEndPoint InterceptionCanary = new(IPAddress.Parse("192.0.2.1"), 443);

    /// <summary>The address Windows itself uses to check for internet access (NCSI).</summary>
    public const string ConnectivityTestUrl = "http://www.msftconnecttest.com/connecttest.txt";

    private const string ConnectivityTestContent = "Microsoft Connect Test";

    // ===================================================================== adapters and traffic

    /// <summary>Filter-driver instances ("…-QoS Packet Scheduler-0000") and WAN miniports are not real connections.</summary>
    private static readonly Regex PseudoAdapter = new(@"-\d{4}$|wan miniport|kernel debug", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Up, not loopback/tunnel, has at least one IP address and is not a filter-driver instance (.NET lists those too,
    /// and they mirror the traffic counters of the real adapter).
    /// </summary>
    private static bool IsRelevant(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up ||
            nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return false;
        if (PseudoAdapter.IsMatch(nic.Name) || PseudoAdapter.IsMatch(nic.Description))
            return false;
        return nic.GetIPProperties().UnicastAddresses.Count > 0;
    }

    /// <summary>Adapters that are up (not loopback/tunnel), physical ones first.</summary>
    public static List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"Сеть: не удалось получить список адаптеров: {ex.Message}");
            return list;
        }

        foreach (var nic in nics)
        {
            try
            {
                if (IsRelevant(nic))
                    list.Add(Read(nic));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                Log.Warn($"Сеть: не удалось прочитать адаптер {nic.Name}: {ex.Message}");
            }
        }

        return list
            .OrderBy(a => a.IsVirtual ? 1 : 0)
            .ThenBy(a => a.HasGateway ? 0 : 1)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static AdapterInfo Read(NetworkInterface nic)
    {
        var props = nic.GetIPProperties();
        var unicast = props.UnicastAddresses.Select(u => u.Address).ToList();
        var ipv4 = unicast.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        var ipv6 = unicast.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal).ToList();
        var gateways = props.GatewayAddresses
            .Select(g => g.Address)
            .Where(a => a is not null && !NetAddress.IsAny(a))
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
        bool? dhcp = null;
        try
        {
            dhcp = props.GetIPv4Properties()?.IsDhcpEnabled;
        }
        catch (NetworkInformationException)
        {
            // no IPv4 on this adapter
        }

        long rx = 0, tx = 0;
        try
        {
            var stats = nic.GetIPStatistics();
            rx = stats.BytesReceived;
            tx = stats.BytesSent;
        }
        catch (NetworkInformationException)
        {
            // statistics unavailable for some virtual adapters
        }

        var mac = nic.GetPhysicalAddress().GetAddressBytes();
        return new AdapterInfo
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            Type = nic.NetworkInterfaceType,
            Kind = NetworkRules.DetectKind(nic.NetworkInterfaceType, nic.Name, nic.Description),
            SpeedBps = nic.Speed,
            Mac = mac.Length == 6 ? string.Join("-", mac.Select(b => b.ToString("X2"))) : null,
            IPv4 = ipv4,
            IPv6 = ipv6,
            Gateways = gateways,
            DnsServers = props.DnsAddresses.ToList(),
            Dhcp = dhcp,
            BytesReceived = rx,
            BytesSent = tx,
        };
    }

    /// <summary>Receive/send byte counters of every adapter that is up (cheap, called once a second).</summary>
    public static TrafficSample SampleTraffic()
    {
        var counters = new Dictionary<string, TrafficCounters>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (!IsRelevant(nic))
                        continue;
                    var stats = nic.GetIPStatistics();
                    counters[nic.Id] = new TrafficCounters(stats.BytesReceived, stats.BytesSent);
                }
                catch (NetworkInformationException)
                {
                    // skip
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"Сеть: не удалось прочитать счётчики трафика: {ex.Message}");
        }
        return new TrafficSample(counters, Stopwatch.GetTimestamp());
    }

    /// <summary>Average upload of physical adapters between two samples, bytes/s.</summary>
    public static double UploadRate(TrafficSample first, TrafficSample second, IReadOnlyList<AdapterInfo> adapters)
    {
        var seconds = (second.TimestampTicks - first.TimestampTicks) / (double)Stopwatch.Frequency;
        var physical = adapters.Where(a => !a.IsVirtual).Select(a => a.Id).ToHashSet();
        double total = 0;
        foreach (var (id, now) in second.Counters)
        {
            if (!physical.Contains(id) || !first.Counters.TryGetValue(id, out var before))
                continue;
            total += LatencyMath.Rate(before.Sent, now.Sent, seconds);
        }
        return total;
    }

    /// <summary>Takes two traffic samples <paramref name="duration"/> apart and returns the upload rate.</summary>
    public static async Task<double?> MeasureUploadAsync(IReadOnlyList<AdapterInfo> adapters, TimeSpan duration, CancellationToken ct)
    {
        var first = await Task.Run(SampleTraffic, ct).ConfigureAwait(false);
        await Task.Delay(duration, ct).ConfigureAwait(false);
        var second = await Task.Run(SampleTraffic, ct).ConfigureAwait(false);
        return adapters.Count == 0 ? null : UploadRate(first, second, adapters);
    }

    // ===================================================================== latency

    /// <summary>
    /// TCP connect time to the well-known targets (median of 3, 2 s timeout), router ping and the Windows connectivity
    /// test. Only tiny connection requests are sent; no data is transferred.
    /// </summary>
    public static async Task<LatencyReport> MeasureLatencyAsync(IReadOnlyList<AdapterInfo> adapters, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(11));
        var token = budget.Token;

        var targets = NetworkRules.LatencyTargets.Select(t => MeasureTargetAsync(t, token)).ToList();
        var canary = TcpConnectAsync(InterceptionCanary, TimeSpan.FromMilliseconds(1500), token);
        var http = MeasureHttpAsync(token);
        var router = NetworkRules.RouterAddress(adapters);
        var ping = router is null ? Task.FromResult<GatewayResult?>(null) : PingRouterAsync(router, token);

        await Task.WhenAll(Task.WhenAll(targets), canary, http, ping).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var results = targets.Select(t => t.Result).ToList();
        var (httpMs, captive) = http.Result;
        var intercepted = canary.Result is not null ||
                          // Fallback: a VPN is up and every "internet" server answers faster than physically possible.
                          (NetworkRules.ActiveVpn(adapters) is not null && results.All(r => r.MedianMs is < 0.5));
        return new LatencyReport
        {
            Targets = results,
            Gateway = ping.Result,
            Intercepted = intercepted,
            HttpMedianMs = captive ? null : httpMs,
            CaptivePortal = captive,
        };
    }

    private static async Task<LatencyResult> MeasureTargetAsync(LatencyTarget target, CancellationToken ct)
    {
        IPAddress? address;
        if (IPAddress.TryParse(target.Host, out var literal))
        {
            address = literal;
        }
        else
        {
            address = await ResolveAsync(target.Host, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (address is null)
                return new LatencyResult(target, new double?[] { null, null, null }, "не удалось найти адрес сервера");
        }

        var attempts = new List<double?>();
        var endpoint = new IPEndPoint(address, target.Port);
        for (var i = 0; i < 3; i++)
        {
            if (ct.IsCancellationRequested)
            {
                attempts.Add(null);
                continue;
            }
            attempts.Add(await TcpConnectAsync(endpoint, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false));
        }
        return new LatencyResult(target, attempts);
    }

    private static async Task<IPAddress?> ResolveAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var v4 = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token).ConfigureAwait(false);
            if (v4.Length > 0)
                return v4[0];
            var any = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return any.FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Time to establish a TCP connection in ms, or null if it failed or timed out. The connection is closed at once.</summary>
    public static async Task<double?> TcpConnectAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // already closed by the other side
            }
            return ms;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Three requests to the Windows connectivity test (the same one Windows makes itself) over one connection;
    /// median time. Captive portal = an answer with unexpected content.
    /// </summary>
    private static async Task<(double? MedianMs, bool CaptivePortal)> MeasureHttpAsync(CancellationToken ct)
    {
        var times = new List<double?>();
        var captive = false;
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3.5) };
            for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var response = await client.GetAsync(ConnectivityTestUrl, ct).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var code = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode && body.StartsWith(ConnectivityTestContent, StringComparison.Ordinal))
                    {
                        times.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    else if (code is 200 or (>= 300 and < 400))
                    {
                        // Someone else answered (a hotel/cafe login page redirects every request).
                        captive = true;
                        break;
                    }
                    else
                    {
                        times.Add(null); // e.g. a proxy reports that the server is unreachable
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    times.Add(null);
                    if (ex is TaskCanceledException && ct.IsCancellationRequested)
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or NotSupportedException)
        {
            Log.Warn($"Сеть: проверка доступа в интернет через HTTP не удалась: {ex.Message}");
        }
        return (LatencyMath.Median(times), captive);
    }

    /// <summary>Three ICMP echo requests to the router (some routers do not answer ping — that is normal).</summary>
    public static async Task<GatewayResult?> PingRouterAsync(IPAddress router, CancellationToken ct)
    {
        var attempts = new List<double?>();
        using var ping = new Ping();
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(router, 1000).ConfigureAwait(false);
                attempts.Add(reply.Status == IPStatus.Success ? Math.Max(reply.RoundtripTime, 0.5) : null);
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException)
            {
                attempts.Add(null);
            }
        }
        return new GatewayResult(router, attempts);
    }

    // ===================================================================== DNS

    /// <summary>Resolves a few different well-known names in parallel and times each lookup (3 s timeout each).</summary>
    public static async Task<DnsReport> MeasureDnsAsync(CancellationToken ct)
    {
        var lookups = NetworkRules.DnsTestNames.Select(async name =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var sw = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(name, cts.Token).ConfigureAwait(false);
                return new DnsLookup(name, addresses.Length > 0 ? sw.Elapsed.TotalMilliseconds : null);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                return new DnsLookup(name, null);
            }
        }).ToList();
        var results = await Task.WhenAll(lookups).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new DnsReport(results);
    }

    // ===================================================================== Wi-Fi

    /// <summary>Reads "netsh wlan show interfaces" (only when a wireless adapter is up).</summary>
    public static async Task<WifiReport> ReadWifiAsync(IReadOnlyList<AdapterInfo> adapters, CancellationToken ct)
    {
        if (!adapters.Any(a => a.Kind == AdapterKind.WiFi))
            return new WifiReport();
        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        ProcessResult result;
        try
        {
            // Latin-1 keeps the raw bytes: netsh writes UTF-8 or the OEM code page depending on the console it gets.
            result = await ProcessRunner.RunAsync(netsh, "wlan show interfaces", TimeSpan.FromSeconds(8), ct, Encoding.Latin1)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Сеть: не удалось запустить netsh: {ex.Message}");
            return new WifiReport { Error = ex.Message };
        }
        var stdout = DecodeConsoleOutput(Encoding.Latin1.GetBytes(result.StdOut));
        var stderr = DecodeConsoleOutput(Encoding.Latin1.GetBytes(result.StdErr));
        var interfaces = WlanParser.Parse(stdout);
        var blocked = interfaces.All(i => i.SignalPercent is null) && WlanParser.IsLocationBlocked(stdout + "\n" + stderr);
        if (result.TimedOut)
            Log.Warn("Сеть: netsh wlan не ответил вовремя");
        return new WifiReport
        {
            Interfaces = interfaces,
            LocationBlocked = blocked,
            Error = interfaces.Count == 0 && !blocked ? FirstLine(stdout + "\n" + stderr) : null,
        };
    }

    /// <summary>Console tools write UTF-8 when the console code page is 65001 and the OEM code page otherwise.</summary>
    public static string DecodeConsoleOutput(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ProcessRunner.OemEncoding.GetString(bytes);
        }
    }

    private static string? FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    // ===================================================================== sockets

    /// <summary>TCP and UDP tables with process owners (read-only, no admin needed).</summary>
    public static SocketSnapshot ReadSockets(ProcessIdentities identities)
    {
        var entries = IpTables.ReadAll();
        var owners = identities.GetAll(entries.Select(e => e.Pid));
        return new SocketSnapshot(entries, owners, DateTime.Now);
    }

    /// <summary>
    /// Reverse DNS name of an address (PTR), null if there is none. Never throws. The lookup is a blocking system call
    /// that cannot be cancelled and may take several seconds when the DNS server does not answer, so callers stop
    /// waiting on their own (see the page's background lookups) instead of relying on a token.
    /// </summary>
    public static async Task<string?> ReverseLookupAsync(IPAddress address)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
            var name = entry.HostName?.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) || name == address.ToString() || IPAddress.TryParse(name, out _) ? null : name;
        }
        catch (Exception)
        {
            // Best effort: no name (no PTR record, DNS timeout, no network) is a normal answer here.
            return null;
        }
    }
}
