using System.Net;
using System.Net.NetworkInformation;

namespace PcCleaner.Modules.Network;

/// <summary>Colour of a value on the page (mapped to theme brushes by <see cref="NetToneBrushConverter"/>).</summary>
public enum NetTone
{
    Neutral,
    Ok,
    Info,
    Warning,
    Danger,
}

public enum AdapterKind
{
    Ethernet,
    WiFi,
    Vpn,
    Virtual,
    Other,
}

/// <summary>One active network adapter (read from <see cref="NetworkInterface"/>).</summary>
public sealed record AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public NetworkInterfaceType Type { get; init; }
    public AdapterKind Kind { get; init; }

    /// <summary>VPN, Hyper-V, VirtualBox, TAP/TUN… — its speed and traffic are not physical.</summary>
    public bool IsVirtual => Kind is AdapterKind.Vpn or AdapterKind.Virtual;

    /// <summary>Link speed in bits per second (≤ 0 = unknown).</summary>
    public long SpeedBps { get; init; }

    public string? Mac { get; init; }
    public IReadOnlyList<IPAddress> IPv4 { get; init; } = Array.Empty<IPAddress>();
    public IReadOnlyList<IPAddress> IPv6 { get; init; } = Array.Empty<IPAddress>();
    public IReadOnlyList<IPAddress> Gateways { get; init; } = Array.Empty<IPAddress>();
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = Array.Empty<IPAddress>();

    /// <summary>IPv4 address obtained automatically (null = unknown / no IPv4).</summary>
    public bool? Dhcp { get; init; }

    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }

    public bool HasGateway => Gateways.Count > 0;
}

/// <summary>Bytes counters of one adapter at one moment.</summary>
public readonly record struct TrafficCounters(long Received, long Sent);

/// <summary>Traffic counters of all active adapters, taken at <see cref="TimestampTicks"/> (Stopwatch ticks).</summary>
public sealed record TrafficSample(IReadOnlyDictionary<string, TrafficCounters> Counters, long TimestampTicks);

/// <summary>A well-known server used to measure internet latency.</summary>
public sealed record LatencyTarget(string Name, string Host, int Port = 443)
{
    public string Display => $"{Host}";
}

/// <summary>TCP connect times to one target (null attempt = failed or timed out).</summary>
public sealed record LatencyResult(LatencyTarget Target, IReadOnlyList<double?> Attempts, string? Error = null)
{
    public double? MedianMs => LatencyMath.Median(Attempts);

    public bool Reachable => MedianMs is not null;
}

/// <summary>ICMP ping of the router (default gateway).</summary>
public sealed record GatewayResult(IPAddress Address, IReadOnlyList<double?> Attempts)
{
    public double? MedianMs => LatencyMath.Median(Attempts);

    public bool Replied => MedianMs is not null;
}

/// <summary>Result of the "is there internet and how fast does it answer" check.</summary>
public sealed record LatencyReport
{
    public IReadOnlyList<LatencyResult> Targets { get; init; } = Array.Empty<LatencyResult>();
    public GatewayResult? Gateway { get; init; }

    /// <summary>
    /// A VPN/proxy answers TCP connections itself (a connection to a non-routable test address succeeded),
    /// so TCP connect times are meaningless and the HTTP check is used instead.
    /// </summary>
    public bool Intercepted { get; init; }

    /// <summary>Median time of the Windows connectivity test request (http://www.msftconnecttest.com), null = failed.</summary>
    public double? HttpMedianMs { get; init; }

    /// <summary>The connectivity test answered with unexpected content: a Wi-Fi login page (hotel, cafe).</summary>
    public bool CaptivePortal { get; init; }

    public bool InternetReachable => Intercepted
        ? HttpMedianMs is not null
        : Targets.Any(t => t.Reachable) || HttpMedianMs is not null;

    /// <summary>Median latency to the internet, ms (null = no internet or not measurable).</summary>
    public double? MedianMs => Intercepted
        ? HttpMedianMs
        : LatencyMath.Median(Targets.Select(t => t.MedianMs).Where(m => m is not null).ToList());
}

public sealed record DnsLookup(string Name, double? Ms);

/// <summary>How fast the configured DNS answers.</summary>
public sealed record DnsReport(IReadOnlyList<DnsLookup> Lookups)
{
    public double? MedianMs => LatencyMath.Median(Lookups.Select(l => l.Ms).ToList());

    public bool AllFailed => Lookups.Count > 0 && Lookups.All(l => l.Ms is null);
}

public enum DnsKind
{
    /// <summary>Well-known public resolver (Google, Cloudflare…).</summary>
    Known,
    /// <summary>Router or provider (private address).</summary>
    Router,
    /// <summary>DNS of a VPN adapter.</summary>
    Vpn,
    /// <summary>A program on this computer (127.0.0.1).</summary>
    Local,
    /// <summary>Other public address — usually the provider.</summary>
    Other,
}

/// <summary>One DNS server of an adapter with a friendly label.</summary>
public sealed record DnsServerEntry(IPAddress Address, string Label, DnsKind Kind, string AdapterName);

/// <summary>One block of "netsh wlan show interfaces".</summary>
public sealed record WifiInterfaceInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Guid { get; init; }
    public string State { get; init; } = "";
    public bool IsConnected { get; init; }
    public string? Ssid { get; init; }
    public string? Bssid { get; init; }
    public string? Band { get; init; }

    /// <summary>2.4 / 5 / 6 (GHz), from "Band" or guessed from the channel.</summary>
    public double? BandGhz { get; init; }

    public int? Channel { get; init; }
    public string? RadioType { get; init; }
    public string? Authentication { get; init; }
    public string? Cipher { get; init; }
    public double? ReceiveMbps { get; init; }
    public double? TransmitMbps { get; init; }
    public int? SignalPercent { get; init; }
    public int? Rssi { get; init; }
    public string? Profile { get; init; }
}

/// <summary>Wi-Fi state: interfaces from netsh plus why nothing could be read.</summary>
public sealed record WifiReport
{
    public IReadOnlyList<WifiInterfaceInfo> Interfaces { get; init; } = Array.Empty<WifiInterfaceInfo>();

    /// <summary>Windows 11 hides Wi-Fi details from netsh until apps may use the location.</summary>
    public bool LocationBlocked { get; init; }

    public string? Error { get; init; }

    public WifiInterfaceInfo? Connected => Interfaces.FirstOrDefault(i => i.IsConnected);
}

public enum SocketProtocol
{
    Tcp,
    Udp,
}

/// <summary>One row of the TCP/UDP tables (GetExtendedTcpTable / GetExtendedUdpTable).</summary>
public sealed record SocketEntry(
    SocketProtocol Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int RemotePort,
    TcpState State,
    int Pid);

/// <summary>Who owns a socket.</summary>
public sealed record ProcessIdentity(int Pid, string Name, string? Path, string DisplayName, bool IsWindows, bool Exited = false)
{
    /// <summary>Key used to group connections of one program (all chrome.exe processes, all installed versions together).</summary>
    public string GroupKey => (DisplayName + "|" + Name).ToLowerInvariant();
}

/// <summary>TCP/UDP tables with process owners.</summary>
public sealed record SocketSnapshot(IReadOnlyList<SocketEntry> Entries, IReadOnlyDictionary<int, ProcessIdentity> Processes, DateTime Time)
{
    public ProcessIdentity Owner(int pid)
        => Processes.TryGetValue(pid, out var p) ? p : new ProcessIdentity(pid, $"PID {pid}", null, $"Процесс {pid}", false, true);
}

/// <summary>An established (or opening/closing) TCP connection to another computer.</summary>
public sealed record ConnectionRow
{
    public required ProcessIdentity Process { get; init; }
    public required IPAddress RemoteAddress { get; init; }
    public int RemotePort { get; init; }
    public int LocalPort { get; init; }
    public TcpState State { get; init; }
    public AddressScope Scope { get; init; }

    /// <summary>What the remote port usually means if it is a red flag (botnets, miners, Tor), otherwise null.</summary>
    public string? SuspiciousReason { get; init; }

    /// <summary>How many identical connections (same program, address, port and state) this row stands for.</summary>
    public int Count { get; init; } = 1;

    public bool IsSuspicious => SuspiciousReason is not null;
}

public enum PortKind
{
    /// <summary>A service that is dangerous to expose (RDP, databases, Telnet…) listening on the network.</summary>
    Risky,
    /// <summary>Normal Windows service port (RPC, SMB, discovery…).</summary>
    Windows,
    /// <summary>A program accepts connections from the network.</summary>
    App,
    /// <summary>Only programs on this computer can connect (127.0.0.1 / ::1).</summary>
    LocalOnly,
}

/// <summary>A listening TCP port or a bound UDP port (IPv4 and IPv6 rows of the same program merged).</summary>
public sealed record PortRow
{
    public required ProcessIdentity Process { get; init; }
    public SocketProtocol Protocol { get; init; }
    public int Port { get; init; }
    public IReadOnlyList<IPAddress> Addresses { get; init; } = Array.Empty<IPAddress>();
    public bool IsNetwork { get; init; }
    public PortKind Kind { get; init; }
    public string Service { get; init; } = "";
    public string Explanation { get; init; } = "";
}

public enum AddressScope
{
    Unspecified,
    Loopback,
    Private,
    LinkLocal,
    Cgnat,
    Multicast,
    Reserved,
    Public,
}

/// <summary>Everything the analysis looks at. Every part is optional (null = not measured / failed).</summary>
public sealed record NetworkSnapshot
{
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = Array.Empty<AdapterInfo>();
    public LatencyReport? Latency { get; init; }
    public DnsReport? Dns { get; init; }
    public WifiReport? Wifi { get; init; }
    public SocketSnapshot? Sockets { get; init; }

    /// <summary>Average upload of physical adapters during a short sample, bytes/s.</summary>
    public double? UploadBytesPerSecond { get; init; }
}
