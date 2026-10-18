using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Modules.Security;

namespace PcCleaner.Modules.Network;

/// <summary>What a finding is about (lets the page attach the right button).</summary>
public enum FindingKind
{
    NoInternet,
    HighLatency,
    DnsFailed,
    SlowDns,
    SlowLink,
    WeakSignal,
    WeakWifiSecurity,
    Band24,
    HighUpload,
    RiskyPort,
    WindowsPorts,
    SuspiciousConnection,
    FirewallOff,
}

/// <summary>
/// A health issue plus what it refers to (port number, remote address…). <paramref name="PageOnly"/>: shown on the
/// Network page only — the Security module already reports the same setting on the dashboard.
/// </summary>
public sealed record NetworkFinding(HealthIssue Issue, FindingKind Kind, string? Argument = null, bool PageOnly = false);

public static class LatencyMath
{
    /// <summary>Median of the successful attempts (null = failed attempt). Null if nothing succeeded.</summary>
    public static double? Median(IEnumerable<double?> attempts)
    {
        var ok = attempts.Where(a => a is not null && !double.IsNaN(a.Value)).Select(a => a!.Value).OrderBy(a => a).ToList();
        if (ok.Count == 0)
            return null;
        var mid = ok.Count / 2;
        return ok.Count % 2 == 1 ? ok[mid] : (ok[mid - 1] + ok[mid]) / 2;
    }

    /// <summary>"&lt; 1 мс", "2,4 мс", "23 мс".</summary>
    public static string FormatMs(double? ms)
    {
        if (ms is null)
            return "—";
        if (ms < 1)
            return "< 1 мс";
        if (ms < 10)
            return ms.Value.ToString("0.#", Format.Ru) + " мс";
        return Math.Round(ms.Value).ToString("0", Format.Ru) + " мс";
    }

    /// <summary>Bytes/s from two counter readings (a counter reset gives 0, never a negative rate).</summary>
    public static double Rate(long previous, long current, double seconds)
        => seconds <= 0 || current < previous ? 0 : (current - previous) / seconds;

    /// <summary>"1,2 МБ/с".</summary>
    public static string FormatRate(double bytesPerSecond) => Format.Bytes((long)Math.Max(0, bytesPerSecond)) + "/с";

    /// <summary>Internet latency → tone and word.</summary>
    public static (NetTone Tone, string Word) RateInternet(double? ms) => ms switch
    {
        null => (NetTone.Danger, "нет ответа"),
        <= 50 => (NetTone.Ok, "отлично"),
        <= 100 => (NetTone.Info, "нормально"),
        <= 200 => (NetTone.Warning, "высокая"),
        _ => (NetTone.Danger, "очень высокая"),
    };

    /// <summary>Router (LAN) latency → tone and word.</summary>
    public static (NetTone Tone, string Word) RateRouter(double? ms) => ms switch
    {
        null => (NetTone.Neutral, "не отвечает на ping"),
        <= 5 => (NetTone.Ok, "отлично"),
        <= 20 => (NetTone.Info, "нормально"),
        <= 50 => (NetTone.Warning, "медленно"),
        _ => (NetTone.Danger, "очень медленно"),
    };

    /// <summary>DNS resolve time → tone and word.</summary>
    public static (NetTone Tone, string Word) RateDns(double? ms) => ms switch
    {
        null => (NetTone.Danger, "не отвечает"),
        <= 60 => (NetTone.Ok, "быстрый"),
        <= NetworkRules.SlowDnsMs => (NetTone.Info, "нормальный"),
        _ => (NetTone.Warning, "медленный"),
    };
}

/// <summary>
/// Pure rules of the Network module: adapter kinds and link speed, DNS labels, port and connection classification,
/// Wi-Fi descriptions and the health issues built from a <see cref="NetworkSnapshot"/>.
/// </summary>
public static class NetworkRules
{
    public const string ModuleName = "Сеть";
    public const string PageKey = "Network";

    public const double HighLatencyMs = 100;
    public const double SlowDnsMs = 150;
    public const double HighUploadBytesPerSecond = 2 * 1024 * 1024;
    public const int WeakSignalPercent = 50;

    /// <summary>Servers used for the latency check (TCP connect to port 443, nothing is sent).</summary>
    public static readonly IReadOnlyList<LatencyTarget> LatencyTargets = new[]
    {
        new LatencyTarget("Cloudflare", "1.1.1.1"),
        new LatencyTarget("Google", "8.8.8.8"),
        new LatencyTarget("Яндекс", "ya.ru"),
        new LatencyTarget("Microsoft", "www.microsoft.com"),
    };

    /// <summary>Different well-known names for the DNS speed check.</summary>
    public static readonly IReadOnlyList<string> DnsTestNames = new[]
    {
        "example.com", "wikipedia.org", "github.com", "mozilla.org", "cloudflare.com",
    };

    // ===================================================================== adapters

    private static readonly Regex VpnPattern = new(
        @"\bvpn\b|wireguard|wintun|openvpn|\btap[-\s]|\btun\b|tunnel|zerotier|tailscale|hamachi|\boutline\b|nordlynx|proton|anyconnect|fortinet|globalprotect|v2ray|xray|clash|sing-?box",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VirtualPattern = new(
        @"hyper-v|vethernet|virtualbox|vmware|virtual|\bwsl\b|docker|npcap|loopback|kernel debug|parallels|qemu|vbox",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Bluetooth PAN and phone tethering report odd link speeds: never judge them as cables.</summary>
    private static readonly Regex OtherPattern = new(@"bluetooth|rndis|remote ndis|mobile device|android|iphone|apple mobile",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Ethernet, Wi-Fi, VPN, virtual (Hyper-V, VirtualBox…) or other.</summary>
    public static AdapterKind DetectKind(NetworkInterfaceType type, string name, string description)
    {
        var text = name + " " + description;
        if (type == NetworkInterfaceType.Ppp || VpnPattern.IsMatch(text))
            return AdapterKind.Vpn;
        if (VirtualPattern.IsMatch(text) || (int)type == 53) // 53 = IF_TYPE_PROP_VIRTUAL
            return AdapterKind.Virtual;
        if (OtherPattern.IsMatch(text))
            return AdapterKind.Other;
        if (type == NetworkInterfaceType.Wireless80211)
            return AdapterKind.WiFi;
        if (IsWiredType(type))
            return AdapterKind.Ethernet;
        return AdapterKind.Other;
    }

    public static bool IsWiredType(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet
        or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
        or NetworkInterfaceType.Ethernet3Megabit;

    public static string KindText(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Virtual => "Виртуальный",
        _ => "Другое",
    };

    public static string KindGlyph(AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => "",
        AdapterKind.WiFi => "",
        AdapterKind.Vpn => "",
        AdapterKind.Virtual => "",
        _ => "",
    };

    /// <summary>100000000 → "100 Мбит/с", 2500000000 → "2,5 Гбит/с". Unknown → "—".</summary>
    public static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
            return "—";
        var mbps = bitsPerSecond / 1_000_000.0;
        if (mbps >= 1000)
            return (mbps / 1000).ToString("0.#", Format.Ru) + " Гбит/с";
        if (mbps >= 1)
            return mbps.ToString("0.#", Format.Ru) + " Мбит/с";
        return (bitsPerSecond / 1000.0).ToString("0", Format.Ru) + " Кбит/с";
    }

    private static readonly Regex GigabitPattern = new(
        @"gigabit|\bgbe\b|\d(\.\d)?\s*gbe|\b(1|2\.5|5|10)\s*g(b|bps|bit)?\b|2\.5g|10/100/1000|1000base",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Cards that only support 100 Мбит/с ("Realtek USB FE Family Controller" = USB Fast Ethernet dongle).</summary>
    private static readonly Regex FastEthernetOnlyPattern = new(@"fast ethernet|\b10/100\b(?!/1000)|\bfe family\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The adapter's name says it supports 1 Gbit/s or more (Gigabit, GbE, 2.5G…).</summary>
    public static bool MentionsGigabit(string? description) => !string.IsNullOrEmpty(description) && GigabitPattern.IsMatch(description);

    /// <summary>
    /// A wired adapter linked at 100 Мбит/с or less (or below 1 Гбит/с although it is a gigabit card): almost always a bad
    /// cable (needs Cat5e/Cat6 with all 8 wires) or router port. Adapters that only support 100 Мбит/с are not flagged.
    /// </summary>
    public static bool IsSlowWiredLink(AdapterKind kind, string? description, long speedBps)
    {
        if (kind != AdapterKind.Ethernet || speedBps <= 0)
            return false;
        var mbps = speedBps / 1_000_000.0;
        if (mbps >= 1000)
            return false;
        var gigabitCard = MentionsGigabit(description);
        if (!gigabitCard && !string.IsNullOrEmpty(description) && FastEthernetOnlyPattern.IsMatch(description))
            return false;
        return mbps <= 100 || gigabitCard;
    }

    public static bool IsSlowWiredLink(AdapterInfo a) => IsSlowWiredLink(a.Kind, a.Description, a.SpeedBps);

    /// <summary>The physical adapter that carries the traffic: Ethernet/Wi-Fi with a gateway first.</summary>
    public static AdapterInfo? PrimaryAdapter(IReadOnlyList<AdapterInfo> adapters)
        => adapters.FirstOrDefault(a => !a.IsVirtual && a.HasGateway)
           ?? adapters.FirstOrDefault(a => !a.IsVirtual)
           ?? adapters.FirstOrDefault(a => a.HasGateway)
           ?? adapters.FirstOrDefault();

    /// <summary>Router address to ping: IPv4 gateway of the primary physical adapter.</summary>
    public static IPAddress? RouterAddress(IReadOnlyList<AdapterInfo> adapters)
    {
        foreach (var a in adapters.Where(a => !a.IsVirtual).Concat(adapters.Where(a => a.IsVirtual)))
        {
            var gw = a.Gateways.FirstOrDefault(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                     ?? a.Gateways.FirstOrDefault();
            if (gw is not null)
                return gw;
        }
        return null;
    }

    public static AdapterInfo? ActiveVpn(IReadOnlyList<AdapterInfo> adapters)
        => adapters.FirstOrDefault(a => a.Kind == AdapterKind.Vpn && (a.HasGateway || a.IPv4.Count > 0));

    // ===================================================================== DNS

    private static readonly Dictionary<string, string> KnownDns = BuildKnownDns();

    private static Dictionary<string, string> BuildKnownDns()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, params string[] ips)
        {
            foreach (var ip in ips)
                d[IPAddress.Parse(ip).ToString()] = label;
        }
        Add("Google", "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844");
        Add("Cloudflare", "1.1.1.1", "1.0.0.1", "1.1.1.2", "1.0.0.2", "1.1.1.3", "1.0.0.3", "2606:4700:4700::1111", "2606:4700:4700::1001");
        Add("Quad9", "9.9.9.9", "149.112.112.112", "9.9.9.10", "2620:fe::fe", "2620:fe::9");
        Add("Яндекс", "77.88.8.8", "77.88.8.1", "77.88.8.88", "77.88.8.2", "77.88.8.7", "77.88.8.3",
            "2a02:6b8::feed:0ff", "2a02:6b8:0:1::feed:0ff");
        Add("AdGuard", "94.140.14.14", "94.140.15.15", "94.140.14.15", "94.140.15.16", "94.140.14.140", "94.140.14.141",
            "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff");
        Add("OpenDNS", "208.67.222.222", "208.67.220.220", "208.67.222.123", "208.67.220.123", "2620:119:35::35", "2620:119:53::53");
        return d;
    }

    /// <summary>Friendly owner of a DNS server address.</summary>
    public static (string Label, DnsKind Kind) DnsLabel(IPAddress address, bool adapterIsVpn = false)
    {
        var key = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        var percent = key.IndexOf('%');
        if (percent > 0)
            key = key[..percent];
        if (KnownDns.TryGetValue(key, out var label))
            return (label, DnsKind.Known);
        return NetAddress.Classify(address) switch
        {
            AddressScope.Loopback => ("Этот компьютер (программа-посредник)", DnsKind.Local),
            AddressScope.Private or AddressScope.LinkLocal or AddressScope.Cgnat when adapterIsVpn => ("DNS VPN-сервиса", DnsKind.Vpn),
            AddressScope.Private or AddressScope.LinkLocal or AddressScope.Cgnat => ("Роутер / провайдер", DnsKind.Router),
            _ when adapterIsVpn => ("DNS VPN-сервиса", DnsKind.Vpn),
            _ => ("Провайдер или другой сервер", DnsKind.Other),
        };
    }

    /// <summary>Windows' placeholder "fec0:0:0:ffff::1" means "no IPv6 DNS configured" and is not shown.</summary>
    public static bool IsPlaceholderDns(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return false;
        var b = address.GetAddressBytes();
        return b[0] == 0xFE && b[1] == 0xC0;
    }

    /// <summary>DNS servers of all adapters (deduplicated per adapter), physical adapters first.</summary>
    public static List<DnsServerEntry> DnsServers(IReadOnlyList<AdapterInfo> adapters)
    {
        var list = new List<DnsServerEntry>();
        foreach (var a in adapters.OrderBy(a => a.IsVirtual ? 1 : 0))
        {
            foreach (var ip in a.DnsServers.Where(d => !IsPlaceholderDns(d)).Distinct())
            {
                var (label, kind) = DnsLabel(ip, a.Kind == AdapterKind.Vpn);
                list.Add(new DnsServerEntry(ip, label, kind, a.Name));
            }
        }
        return list;
    }

    // ===================================================================== ports

    private sealed record PortInfo(string Service, string? Explanation = null);

    private const string DatabaseExplanation =
        "База данных принимает подключения из сети. Открытые в сеть базы — частая причина утечек и взломов: проверьте, что задан " +
        "пароль, и разрешите подключения только с этого компьютера (адрес 127.0.0.1) или закройте порт в брандмауэре. " +
        "Если не знаете, откуда эта программа, посмотрите её в разделе «Процессы».";

    /// <summary>Services that should not be reachable from the network unless you really need them.</summary>
    private static readonly Dictionary<int, PortInfo> RiskyTcp = new()
    {
        [21] = new("FTP — передача файлов без шифрования",
            "FTP-сервер принимает подключения из сети. Пароли по FTP передаются открытым текстом, а открытый в интернет FTP " +
            "часто взламывают. Если вы не раздаёте файлы по FTP — остановите эту программу или службу."),
        [23] = new("Telnet — управление компьютером без шифрования",
            "Telnet-сервер позволяет управлять компьютером по сети, а пароли идут открытым текстом. Сегодня он почти не нужен — " +
            "отключите его (компонент «Telnet Server» или программу, которая его открыла)."),
        [3389] = new("Удалённый рабочий стол (RDP)",
            "К компьютеру можно подключиться удалённо. Если порт доступен из интернета, его постоянно пытаются взломать подбором " +
            "пароля. Если вы не подключаетесь к этому компьютеру удалённо — выключите: «Параметры» → «Система» → «Удалённый рабочий стол»."),
        [5900] = new("VNC — удалённое управление",
            "Программа удалённого доступа (VNC) ждёт подключений. Если вы её не устанавливали — это повод насторожиться. " +
            "Если устанавливали — задайте надёжный пароль и не открывайте этот порт в интернет."),
        [5901] = new("VNC — удалённое управление",
            "Программа удалённого доступа (VNC) ждёт подключений. Если вы её не устанавливали — это повод насторожиться. " +
            "Если устанавливали — задайте надёжный пароль и не открывайте этот порт в интернет."),
        [5985] = new("WinRM — удалённое управление Windows",
            "Служба удалённого управления Windows (PowerShell Remoting) ждёт подключений. Дома она обычно не нужна: если вы её " +
            "не включали, остановите службу «Служба удалённого управления Windows (WS-Management)»."),
        [5986] = new("WinRM — удалённое управление Windows (HTTPS)",
            "Служба удалённого управления Windows (PowerShell Remoting) ждёт подключений. Дома она обычно не нужна: если вы её " +
            "не включали, остановите службу «Служба удалённого управления Windows (WS-Management)»."),
        [1433] = new("База данных Microsoft SQL Server", DatabaseExplanation),
        [3306] = new("База данных MySQL / MariaDB", DatabaseExplanation),
        [5432] = new("База данных PostgreSQL", DatabaseExplanation),
        [6379] = new("База данных Redis (часто работает без пароля)", DatabaseExplanation),
        [27017] = new("База данных MongoDB (часто работает без пароля)", DatabaseExplanation),
        [11211] = new("Кэш Memcached (работает без пароля)", DatabaseExplanation),
    };

    private static readonly Dictionary<int, PortInfo> RiskyUdp = new()
    {
        [3389] = new("Удалённый рабочий стол (RDP)", RiskyTcp[3389].Explanation),
        [11211] = new("Кэш Memcached (работает без пароля)", DatabaseExplanation),
    };

    /// <summary>Ports Windows opens by itself.</summary>
    private static readonly Dictionary<int, string> WindowsTcp = new()
    {
        [135] = "RPC — служебный обмен Windows",
        [139] = "NetBIOS — общий доступ к файлам (старый)",
        [445] = "SMB — общий доступ к файлам и принтерам",
        [2869] = "UPnP — обнаружение устройств в сети",
        [5040] = "Платформа подключённых устройств Windows",
        [5357] = "WSD — обнаружение принтеров и устройств",
        [5358] = "WSD — обнаружение принтеров и устройств",
        [7680] = "Оптимизация доставки обновлений Windows",
        [10243] = "Общий доступ к мультимедиа Windows",
    };

    private static readonly Dictionary<int, string> WindowsUdp = new()
    {
        [67] = "DHCP — получение IP-адреса",
        [68] = "DHCP — получение IP-адреса",
        [123] = "NTP — синхронизация времени",
        [137] = "NetBIOS — имена компьютеров в сети",
        [138] = "NetBIOS — служба датаграмм",
        [500] = "IKE — подключения VPN",
        [546] = "DHCPv6 — получение IPv6-адреса",
        [1900] = "SSDP / UPnP — обнаружение устройств",
        [3702] = "WS-Discovery — обнаружение устройств",
        [4500] = "IPsec NAT-T — подключения VPN",
        [5050] = "Служба обнаружения Windows",
        [5353] = "mDNS — поиск устройств в сети",
        [5355] = "LLMNR — поиск компьютеров по имени",
    };

    private static readonly Dictionary<int, string> CommonServices = new()
    {
        [20] = "FTP (данные)",
        [22] = "SSH — защищённое удалённое управление",
        [25] = "SMTP — отправка почты",
        [53] = "DNS",
        [80] = "HTTP — веб-сервер",
        [110] = "POP3 — почта",
        [143] = "IMAP — почта",
        [443] = "HTTPS — веб-сервер",
        [465] = "SMTP — почта (защищённая)",
        [587] = "SMTP — почта",
        [853] = "DNS через TLS",
        [993] = "IMAP — почта (защищённая)",
        [995] = "POP3 — почта (защищённая)",
        [1080] = "SOCKS-прокси",
        [1194] = "OpenVPN",
        [1935] = "RTMP — трансляции",
        [3478] = "STUN — звонки и видеосвязь",
        [3479] = "STUN — звонки и видеосвязь",
        [5222] = "XMPP — мессенджеры",
        [5223] = "Push-уведомления Apple",
        [5228] = "Push-уведомления Google",
        [8080] = "HTTP — веб-сервер / прокси",
        [8443] = "HTTPS (альтернативный)",
        [9050] = "Tor (SOCKS)",
        [9150] = "Tor Browser (SOCKS)",
        [27036] = "Steam — передача игр по локальной сети",
        [51820] = "WireGuard VPN",
        [57621] = "Spotify — воспроизведение на устройствах",
    };

    private static readonly Dictionary<int, string> SuspiciousRemote = new()
    {
        [4444] = "порт по умолчанию у инструментов взлома и удалённого управления (Metasploit)",
        [1337] = "«хакерский» порт — его часто используют трояны и бэкдоры",
        [31337] = "«хакерский» порт — его часто используют трояны и бэкдоры",
        [6666] = "IRC — через такие каналы управляют ботнетами (если вы сами не пользуетесь IRC-чатом)",
        [6667] = "IRC — через такие каналы управляют ботнетами (если вы сами не пользуетесь IRC-чатом)",
        [3333] = "порт майнинг-пулов — так скрытые майнеры отправляют результаты работы",
        [5555] = "порт майнинг-пулов — так скрытые майнеры отправляют результаты работы (или отладка Android-устройств)",
        [7777] = "порт майнинг-пулов — так скрытые майнеры отправляют результаты работы (некоторые игры тоже его используют)",
        [14444] = "порт майнинг-пулов (Monero) — так скрытые майнеры отправляют результаты работы",
        [14433] = "порт майнинг-пулов (Monero, TLS) — так скрытые майнеры отправляют результаты работы",
        [45700] = "порт майнинг-пулов (Monero) — так скрытые майнеры отправляют результаты работы",
        [9050] = "сеть Tor — через неё вирусы прячут связь со своими серверами (Tor Browser тоже её использует)",
        [9150] = "сеть Tor — через неё вирусы прячут связь со своими серверами (Tor Browser тоже её использует)",
    };

    /// <summary>
    /// Unusual ports that ordinary programs practically never use (hacking tools, Monero pools): a connection to one of
    /// them is a strong sign on its own. The other ports above also have everyday uses (games, IRC chats, Android
    /// debugging, Tor Browser), so for them the port alone only means "worth a look".
    /// </summary>
    private static readonly HashSet<int> StrongSuspiciousRemote = new() { 4444, 1337, 31337, 14444, 14433, 45700 };

    public static bool IsStrongSuspiciousPort(int port) => StrongSuspiciousRemote.Contains(port);

    /// <summary>Windows' own Remote Desktop port.</summary>
    public const int RdpPort = 3389;

    /// <summary>
    /// Torrent and other peer-to-peer clients connect to hundreds of peers that listen on random ports, so a peer on
    /// 7777 or 5555 means nothing for them.
    /// </summary>
    private static readonly HashSet<string> PeerToPeerClients = new(StringComparer.OrdinalIgnoreCase)
    {
        "qbittorrent.exe", "utorrent.exe", "utweb.exe", "bittorrent.exe", "bitcomet.exe", "transmission-qt.exe",
        "transmission-daemon.exe", "deluge.exe", "deluged.exe", "tixati.exe", "biglybt.exe", "azureus.exe", "vuze.exe",
        "frostwire.exe", "emule.exe", "aria2c.exe", "picotorrent.exe", "mediaget.exe", "zona.exe", "motrix.exe", "fdm.exe",
        "tribler.exe", "webtorrent.exe",
    };

    public static bool IsPeerToPeerClient(ProcessIdentity process) => PeerToPeerClients.Contains(process.Name);

    public static bool IsRiskyPort(SocketProtocol protocol, int port)
        => (protocol == SocketProtocol.Tcp ? RiskyTcp : RiskyUdp).ContainsKey(port);

    public static bool IsWindowsDefaultPort(SocketProtocol protocol, int port)
        => (protocol == SocketProtocol.Tcp ? WindowsTcp : WindowsUdp).ContainsKey(port) ||
           (protocol == SocketProtocol.Tcp && port is >= 49664 and <= 49670);

    /// <summary>What a remote port usually means when a program connects to it on the internet; null = nothing unusual.</summary>
    public static string? SuspiciousRemotePortReason(int port) => SuspiciousRemote.TryGetValue(port, out var r) ? r : null;

    /// <summary>Short description of a port ("HTTPS — веб-сервер"), empty if unknown.</summary>
    public static string ServiceName(SocketProtocol protocol, int port)
    {
        if ((protocol == SocketProtocol.Tcp ? RiskyTcp : RiskyUdp).TryGetValue(port, out var risky))
            return risky.Service;
        if ((protocol == SocketProtocol.Tcp ? WindowsTcp : WindowsUdp).TryGetValue(port, out var win))
            return win;
        if (protocol == SocketProtocol.Tcp && port is >= 49664 and <= 49670)
            return "Динамический порт RPC Windows";
        return CommonServices.TryGetValue(port, out var common) ? common : "";
    }

    /// <summary>Short name of a remote service for the connections list ("HTTPS", "DNS"…).</summary>
    public static string RemoteServiceName(int port) => port switch
    {
        443 => "HTTPS",
        80 => "HTTP",
        53 => "DNS",
        853 => "DNS через TLS",
        22 => "SSH",
        993 or 143 => "Почта (IMAP)",
        995 or 110 => "Почта (POP3)",
        465 or 587 or 25 => "Почта (SMTP)",
        3478 or 3479 => "Звонки (STUN)",
        5222 => "Мессенджер (XMPP)",
        5223 => "Уведомления Apple",
        5228 => "Уведомления Google",
        1935 => "Трансляция (RTMP)",
        3389 => "Удалённый стол",
        8080 => "HTTP (прокси)",
        8443 => "HTTPS",
        >= 27015 and <= 27050 => "Steam",
        >= 6881 and <= 6889 => "Торрент",
        _ => SuspiciousRemote.ContainsKey(port) ? UnusualPortText : "",
    };

    /// <summary>Label of a connection to a port from <see cref="SuspiciousRemote"/> (the port alone proves nothing).</summary>
    public const string UnusualPortText = "Необычный порт";

    /// <summary>
    /// Classifies a listening port: loopback-only is always safe; risky services on the network → Risky;
    /// Windows' own ports → Windows; anything else reachable from the network → App.
    /// </summary>
    public static (PortKind Kind, string Service, string Explanation) ClassifyPort(
        SocketProtocol protocol, int port, bool isNetwork, ProcessIdentity owner)
    {
        var service = ServiceName(protocol, port);
        if (!isNetwork)
            return (PortKind.LocalOnly, service,
                "Порт доступен только программам на этом компьютере — из сети к нему не подключиться. Это безопасно.");

        var risky = protocol == SocketProtocol.Tcp ? RiskyTcp : RiskyUdp;
        if (risky.TryGetValue(port, out var info))
            return (PortKind.Risky, info.Service, info.Explanation ?? "");

        var windowsDynamic = owner.IsWindows && port >= 49152;
        if (IsWindowsDefaultPort(protocol, port) || windowsDynamic || owner.Pid == 4)
        {
            if (string.IsNullOrEmpty(service))
                service = windowsDynamic ? "Динамический порт службы Windows" : "Служба Windows";
            return (PortKind.Windows, service,
                $"{service}. Такие порты открыты почти на каждом компьютере с Windows — в домашней сети это нормально. " +
                "Главное — не пробрасывайте их на роутере (проброс портов, DMZ) и не выключайте брандмауэр.");
        }

        return (PortKind.App, service, protocol == SocketProtocol.Udp
            ? $"{owner.DisplayName} получает данные по UDP через этот порт. Обычно это нормально: так работают звонки, игры, " +
              "трансляции, VPN и быстрые соединения браузеров (QUIC). Насторожиться стоит, если вы не знаете эту программу."
            : $"{owner.DisplayName} принимает подключения из локальной сети. Обычно это нормально: так работают синхронизация, " +
              "игры, мессенджеры и передача файлов. Насторожиться стоит, если вы не знаете эту программу.");
    }

    public static string PortKindText(PortKind kind, ProcessIdentity owner) => kind switch
    {
        PortKind.Risky => "Опасный",
        PortKind.Windows => owner.IsWindows || owner.Pid == 4 ? "Windows" : "Стандартный",
        PortKind.App => "Обычный",
        _ => "Безопасный",
    };

    public static NetTone PortKindTone(PortKind kind) => kind switch
    {
        PortKind.Risky => NetTone.Warning,
        PortKind.Windows => NetTone.Info,
        PortKind.App => NetTone.Neutral,
        _ => NetTone.Ok,
    };

    public static int PortKindRank(PortKind kind) => kind switch
    {
        PortKind.Risky => 0,
        PortKind.App => 1,
        PortKind.Windows => 2,
        _ => 3,
    };

    // ===================================================================== connections and listening sockets

    /// <summary>
    /// Established connections and the ones being opened. Closing states (FIN_WAIT, CLOSE_WAIT, TIME_WAIT…) are left
    /// out: they are finished connections that Windows keeps for a while, often after the program has already exited.
    /// </summary>
    private static readonly HashSet<TcpState> ActiveStates = new()
    {
        TcpState.Established, TcpState.SynSent, TcpState.SynReceived,
    };

    public static string StateText(TcpState state) => state switch
    {
        TcpState.Established => "Установлено",
        TcpState.SynSent => "Подключается",
        TcpState.SynReceived => "Принимается",
        TcpState.Listen => "Ждёт подключений",
        TcpState.TimeWait => "Завершено",
        TcpState.CloseWait or TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing or TcpState.LastAck => "Закрывается",
        _ => "—",
    };

    /// <summary>
    /// Established (and opening) TCP connections to other computers. Loopback (program-to-program on this PC) only if
    /// requested.
    /// </summary>
    public static List<ConnectionRow> BuildConnections(SocketSnapshot snapshot, bool includeLoopback)
    {
        // Connections accepted on a listening port were opened by the other side: their remote port is just the
        // other computer's outgoing port (behind a provider NAT it can be any number), not a server it connects to.
        var listening = snapshot.Entries
            .Where(e => e.Protocol == SocketProtocol.Tcp && e.State == TcpState.Listen)
            .Select(e => (e.Pid, e.LocalPort))
            .ToHashSet();
        var rows = new List<ConnectionRow>();
        foreach (var e in snapshot.Entries)
        {
            if (e.Protocol != SocketProtocol.Tcp || !ActiveStates.Contains(e.State) || e.RemoteAddress is null ||
                NetAddress.IsAny(e.RemoteAddress) || e.RemotePort == 0)
                continue;
            var scope = NetAddress.Classify(e.RemoteAddress);
            if (scope == AddressScope.Loopback && !includeLoopback)
                continue;
            var owner = snapshot.Owner(e.Pid);
            var outgoing = (e.State is TcpState.Established or TcpState.SynSent) && !listening.Contains((e.Pid, e.LocalPort));
            var suspicious = scope == AddressScope.Public && outgoing && !IsPeerToPeerClient(owner)
                ? SuspiciousRemotePortReason(e.RemotePort)
                : null;
            // A program the user marked as trusted is never flagged (neither in the list nor in the findings).
            if (suspicious is not null && AppServices.Settings.IsTrusted(owner.Path))
                suspicious = null;
            rows.Add(new ConnectionRow
            {
                Process = owner,
                RemoteAddress = e.RemoteAddress,
                RemotePort = e.RemotePort,
                LocalPort = e.LocalPort,
                State = e.State,
                Scope = scope,
                SuspiciousReason = suspicious,
                ProgramWarning = suspicious is null ? null : snapshot.ProgramWarning(owner),
            });
        }
        return rows;
    }

    /// <summary>
    /// Merges identical connections (same program, remote address, port and state) into one row with a count:
    /// a browser or an AI client often keeps dozens of connections to one server.
    /// </summary>
    public static List<ConnectionRow> AggregateConnections(IEnumerable<ConnectionRow> rows)
        => rows
            .GroupBy(r => (r.Process.GroupKey, Address: NetAddress.Text(r.RemoteAddress), r.RemotePort, r.State))
            .Select(g => g.First() with { Count = g.Sum(x => x.Count) })
            .ToList();

    /// <summary>Listening TCP and bound UDP ports; IPv4/IPv6 rows of the same program and port are merged.</summary>
    public static List<PortRow> BuildPorts(SocketSnapshot snapshot)
    {
        var groups = snapshot.Entries
            .Where(e => e.LocalPort > 0 && (e.Protocol == SocketProtocol.Udp || e.State == TcpState.Listen))
            .GroupBy(e => (e.Protocol, e.LocalPort, e.Pid, Network: !NetAddress.IsLoopback(e.LocalAddress)));
        var rows = new List<PortRow>();
        foreach (var g in groups)
        {
            var owner = snapshot.Owner(g.Key.Pid);
            var (kind, service, explanation) = ClassifyPort(g.Key.Protocol, g.Key.LocalPort, g.Key.Network, owner);
            rows.Add(new PortRow
            {
                Process = owner,
                Protocol = g.Key.Protocol,
                Port = g.Key.LocalPort,
                Addresses = g.Select(e => e.LocalAddress).Distinct().OrderBy(a => a.AddressFamily).ToList(),
                IsNetwork = g.Key.Network,
                Kind = kind,
                Service = service,
                Explanation = explanation,
            });
        }
        return rows
            .OrderBy(r => PortKindRank(r.Kind))
            .ThenBy(r => r.Port)
            .ThenBy(r => r.Protocol)
            .ToList();
    }

    // ===================================================================== Wi-Fi

    public static (string Text, NetTone Tone) SignalQuality(int? percent) => percent switch
    {
        null => ("нет данных", NetTone.Neutral),
        >= 80 => ("Отличный сигнал", NetTone.Ok),
        >= 60 => ("Хороший сигнал", NetTone.Ok),
        >= WeakSignalPercent => ("Средний сигнал", NetTone.Info),
        >= 30 => ("Слабый сигнал", NetTone.Warning),
        _ => ("Очень слабый сигнал", NetTone.Danger),
    };

    /// <summary>"802.11ax" → "Wi-Fi 6 (802.11ax)".</summary>
    public static string RadioText(string? radio, double? bandGhz = null)
    {
        if (string.IsNullOrWhiteSpace(radio))
            return "—";
        var r = radio.Trim();
        var generation = r.ToLowerInvariant() switch
        {
            var x when x.EndsWith("be") => "Wi-Fi 7",
            var x when x.EndsWith("ax") => bandGhz >= 5.9 ? "Wi-Fi 6E" : "Wi-Fi 6",
            var x when x.EndsWith("ac") => "Wi-Fi 5",
            var x when x.EndsWith("n") => "Wi-Fi 4",
            _ => null,
        };
        return generation is null ? r : $"{generation} ({r})";
    }

    public static string BandText(double? ghz) => ghz switch
    {
        null => "—",
        < 3 => "2,4 ГГц",
        < 5.9 => "5 ГГц",
        _ => "6 ГГц",
    };

    /// <summary>Open network (no password) or obsolete WEP.</summary>
    public static bool IsWeakWifiSecurity(string? authentication, string? cipher)
    {
        if (string.IsNullOrWhiteSpace(authentication))
            return false;
        var a = authentication.Trim().ToLowerInvariant();
        var c = (cipher ?? "").Trim().ToLowerInvariant();
        return IsOpenWifi(a) || a.Contains("wep") || a.StartsWith("shared") || a.StartsWith("общ") || c.Contains("wep");
    }

    /// <summary>"Open" / "Открытая" / "Відкрита" / "Offen" (netsh translates the value too).</summary>
    private static bool IsOpenWifi(string? authentication)
    {
        var a = (authentication ?? "").Trim().ToLowerInvariant();
        return a.StartsWith("open") || a.StartsWith("открыт") || a.StartsWith("відкрит") || a.StartsWith("offen");
    }

    // ===================================================================== issues

    /// <summary>The Windows page that shows the connection ("Нет доступа в интернет" on the dashboard opens it).</summary>
    internal const string NetworkStatusUri = "ms-settings:network-status";

    /// <summary>Text of the dashboard button that clears the DNS cache.</summary>
    internal const string FlushDnsFixLabel = "Сбросить кэш DNS";

    /// <summary>
    /// The issue of one finding. Dashboard extras depend on its kind: "Нет доступа в интернет" links to the network status
    /// page, "DNS не отвечает" gets <paramref name="flushDns"/> as its fix (null = no button).
    /// </summary>
    private static HealthIssue Issue(FindingKind kind, string title, string details, IssueSeverity severity,
        Func<CancellationToken, Task<string>>? flushDns)
    {
        var fixesDns = kind == FindingKind.DnsFailed && flushDns is not null;
        return new()
        {
            Module = ModuleName,
            Title = title,
            Details = details,
            Severity = severity,
            TargetPageKey = PageKey,
            SettingsUri = kind == FindingKind.NoInternet ? NetworkStatusUri : null,
            FixAsync = fixesDns ? flushDns : null,
            FixLabel = fixesDns ? FlushDnsFixLabel : HealthIssue.DefaultFixLabel,
        };
    }

    /// <summary>
    /// All findings for the dashboard, most important first (without the page-only ones the Security module already
    /// reports). Pure: only reads the snapshot; <paramref name="flushDns"/> is just handed to the DNS issue as its fix.
    /// </summary>
    public static List<HealthIssue> BuildIssues(NetworkSnapshot s, Func<CancellationToken, Task<string>>? flushDns = null)
        => BuildFindings(s, flushDns).Where(f => !f.PageOnly).Select(f => f.Issue).ToList();

    /// <summary>All findings with their kind, most important first. Pure: only reads the snapshot.</summary>
    public static List<NetworkFinding> BuildFindings(NetworkSnapshot s, Func<CancellationToken, Task<string>>? flushDns = null)
    {
        var issues = new List<NetworkFinding>();
        void Add(FindingKind kind, string title, string details, IssueSeverity severity, string? argument = null, bool pageOnly = false)
            => issues.Add(new NetworkFinding(Issue(kind, title, details, severity, flushDns), kind, argument, pageOnly));
        var vpn = ActiveVpn(s.Adapters);

        // --- Internet and latency
        var latency = s.Latency;
        var internetOk = latency?.InternetReachable ?? true;
        if (latency is not null && !latency.InternetReachable)
        {
            var details = s.Adapters.Count == 0
                ? "Компьютер не подключён ни к одной сети. "
                : "Ни один из проверочных серверов (Cloudflare, Google, Яндекс, Microsoft) не ответил. ";
            if (latency.CaptivePortal)
                details += "Похоже, сеть просит войти (как в кафе или гостинице): откройте браузер — должна появиться страница входа. ";
            details += "Что сделать: проверьте кабель или подключение к Wi-Fi; выключите роутер на 30 секунд и включите снова" +
                       (vpn is not null ? $"; попробуйте отключить VPN ({vpn.Name})" : "") +
                       ". Если не помогло — в разделе «Сеть» нажмите «Сбросить сетевые настройки».";
            Add(FindingKind.NoInternet, "Нет доступа в интернет", details, IssueSeverity.High);
        }
        else if (latency?.MedianMs is { } median && median > HighLatencyMs)
        {
            var details = $"Ответ от серверов в интернете приходит за {LatencyMath.FormatMs(median)} — в играх и видеозвонках будут заметны задержки. ";
            details += vpn is not null || latency.Intercepted
                ? "Соединение идёт через VPN или прокси, а он добавляет задержку: выберите сервер VPN поближе или отключите VPN, когда он не нужен. " +
                  "Также помогают подключение кабелем вместо Wi-Fi и перезагрузка роутера."
                : "Что помогает: подключение кабелем вместо Wi-Fi, закрытие загрузок и торрентов, перезагрузка роутера.";
            Add(FindingKind.HighLatency, "Высокая задержка", details, IssueSeverity.Low);
        }

        // --- DNS
        if (s.Dns is { } dns && internetOk)
        {
            if (dns.AllFailed)
            {
                // With a fix (the dashboard) the text describes the button; without one (the page) it points to the cache tool.
                var advice = flushDns is not null
                    ? "Кнопка «Сбросить кэш DNS» очистит кэш и проверит адреса ещё раз. Если это не поможет, нужно сменить " +
                      "DNS-сервер: укажите 1.1.1.1 (Cloudflare), 8.8.8.8 (Google) или 77.88.8.8 (Яндекс) в настройках адаптера или роутера."
                    : "Укажите DNS 1.1.1.1 (Cloudflare), 8.8.8.8 (Google) или 77.88.8.8 (Яндекс) в настройках адаптера или роутера, " +
                      "затем очистите кэш DNS в разделе «Сеть».";
                Add(FindingKind.DnsFailed, "DNS не отвечает — сайты не открываются",
                    "Компьютер не смог узнать адреса сайтов (example.com, wikipedia.org…), хотя связь с интернетом есть. " + advice,
                    IssueSeverity.High);
            }
            else if (dns.MedianMs is { } dnsMs && dnsMs > SlowDnsMs)
            {
                Add(FindingKind.SlowDns, "Медленный DNS",
                    $"DNS-сервер отвечает в среднем за {LatencyMath.FormatMs(dnsMs)}, поэтому каждый сайт начинает открываться с задержкой. " +
                    "Укажите быстрый DNS — 1.1.1.1 (Cloudflare), 8.8.8.8 (Google) или 77.88.8.8 (Яндекс): «Параметры» → «Сеть и Интернет» → " +
                    "ваше подключение → «Назначение DNS-сервера» → «Изменить», либо в настройках роутера.", IssueSeverity.Low);
            }
        }

        // --- Adapters
        foreach (var a in s.Adapters.Where(IsSlowWiredLink))
        {
            Add(FindingKind.SlowLink, $"Адаптер {a.Name} работает на {FormatLinkSpeed(a.SpeedBps)}",
                $"Сетевая карта «{a.Description}» договорилась с роутером только о {FormatLinkSpeed(a.SpeedBps)}. Почти всегда виноват кабель: " +
                "для 1 Гбит/с нужен кабель категории Cat5e или Cat6, в котором подключены все 8 жил (в дешёвых кабелях их бывает 4). " +
                "Попробуйте другой кабель или другой порт роутера. Если ваш тариф медленнее 100 Мбит/с, на скорость интернета это не влияет.",
                IssueSeverity.Low);
        }

        // --- Wi-Fi
        if (s.Wifi?.Connected is { } wifi)
        {
            var ssid = string.IsNullOrWhiteSpace(wifi.Ssid) ? "Wi-Fi" : wifi.Ssid;
            if (wifi.SignalPercent is { } signal && signal < WeakSignalPercent)
            {
                Add(FindingKind.WeakSignal, "Слабый сигнал Wi-Fi",
                    $"Сигнал сети «{ssid}» — {signal}%. Из-за этого падает скорость и растут задержки. Поставьте роутер ближе и повыше " +
                    "(не в шкаф и не на пол), подальше от толстых стен и микроволновки; рядом с роутером лучше подключаться к сети 5 ГГц, " +
                    "а для компьютера, который стоит на месте, надёжнее всего кабель.", IssueSeverity.Low);
            }
            if (IsWeakWifiSecurity(wifi.Authentication, wifi.Cipher))
            {
                var open = IsOpenWifi(wifi.Authentication);
                Add(FindingKind.WeakWifiSecurity, "Сеть Wi-Fi без надёжного пароля",
                    open
                        ? $"Сеть «{ssid}» не защищена паролем: всё, что передаётся без шифрования, могут увидеть другие. Если это ваш роутер — " +
                          "включите в его настройках WPA2 или WPA3 с надёжным паролем. В общественных сетях не вводите пароли на сайтах без " +
                          "значка замка и используйте VPN."
                        : $"Сеть «{ssid}» защищена устаревшим способом WEP, который взламывается за несколько минут. В настройках роутера " +
                          "выберите WPA2 или WPA3 и задайте надёжный пароль.",
                    IssueSeverity.Medium);
            }
            if (wifi.BandGhz is < 3)
            {
                Add(FindingKind.Band24, "Wi-Fi работает в диапазоне 2,4 ГГц",
                    "Диапазон 2,4 ГГц медленнее и сильнее загружен соседскими сетями. Если роутер поддерживает 5 ГГц (часто это сеть " +
                    "с «5G» в названии), подключитесь к ней — скорость обычно выше в несколько раз. Далеко от роутера, через несколько " +
                    "стен, 2,4 ГГц может работать стабильнее.", IssueSeverity.Info);
            }
        }

        // --- Upload
        if (s.UploadBytesPerSecond is { } upload && upload > HighUploadBytesPerSecond)
        {
            var details = $"Компьютер отправлял в сеть в среднем {LatencyMath.FormatRate(upload)}. Если вы сейчас ничего не загружаете в облако, " +
                          "не ведёте трансляцию или видеозвонок и не раздаёте торренты — посмотрите в разделе «Сеть» на вкладке «Соединения», " +
                          "какая программа обменивается данными.";
            if (s.Sockets is { } sockets)
            {
                var top = BuildConnections(sockets, includeLoopback: false)
                    .Where(c => c.Scope == AddressScope.Public)
                    .GroupBy(c => c.Process.GroupKey)
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .Select(g => $"{g.First().Process.Name} ({g.Count()})")
                    .ToList();
                if (top.Count > 0)
                    details += " Больше всего соединений у: " + string.Join(", ", top) + ".";
            }
            Add(FindingKind.HighUpload, "Высокая исходящая активность", details, IssueSeverity.Medium);
        }

        // --- Ports and connections
        if (s.Sockets is { } snap)
        {
            var ports = BuildPorts(snap);
            foreach (var g in ports.Where(p => p.Kind == PortKind.Risky)
                         .Where(p => !AppServices.Settings.IsTrusted(p.Process.Path))
                         .GroupBy(p => (p.Port, p.Process.GroupKey)))
            {
                var p = g.First();
                // Remote Desktop of Windows itself: the Security module reports the same setting ("Удалённый рабочий стол
                // включён", Low), so the dashboard gets it once and both pages rate it the same.
                var windowsRdp = p.Port == RdpPort && p.Process.IsWindows;
                Add(FindingKind.RiskyPort, $"Открыт порт {p.Port} ({p.Process.Name})",
                    $"{p.Service}. {p.Explanation}" + (windowsRdp ? " Об этом же сообщает раздел «Безопасность»." : ""),
                    windowsRdp ? IssueSeverity.Low : IssueSeverity.Medium, p.Port.ToString(), pageOnly: windowsRdp);
            }

            var windowsPorts = ports
                .Where(p => p.Kind == PortKind.Windows && p.Protocol == SocketProtocol.Tcp && IsWindowsDefaultPort(p.Protocol, p.Port))
                .Select(p => p.Port)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            if (windowsPorts.Count > 0)
            {
                var list = string.Join(", ", windowsPorts.Take(8)) + (windowsPorts.Count > 8 ? "…" : "");
                var unprotected = FirewallOffNetworks(s.Firewall);
                if (unprotected.Count > 0)
                    AddFirewallOff(unprotected, list);
                else
                    Add(FindingKind.WindowsPorts, "Открыты стандартные порты Windows",
                        $"Порты {list} принимают подключения из локальной сети — через них работают общий доступ к файлам и принтерам " +
                        "и службы Windows. Это нормально. Только не пробрасывайте их на роутере (проброс портов, DMZ) и не выключайте " +
                        "брандмауэр — из интернета они должны быть закрыты.", IssueSeverity.Info);
            }

            foreach (var g in BuildConnections(snap, includeLoopback: false)
                         .Where(c => c.IsSuspicious) // programs the user trusts are never suspicious
                         .GroupBy(c => (c.Process.GroupKey, c.RemotePort)))
            {
                var c = g.First();
                var others = g.Select(x => x.RemoteAddress).Distinct().Count() - 1;
                var highRisk = g.Any(x => x.IsHighRisk);
                var warning = g.Select(x => x.ProgramWarning).FirstOrDefault(w => w is not null);
                Add(FindingKind.SuspiciousConnection, $"{c.Process.Name} подключён к {NetAddress.Endpoint(c.RemoteAddress, c.RemotePort)}",
                    $"Порт {c.RemotePort} — {c.SuspiciousReason}." +
                    (warning is not null ? $" Сама программа тоже вызывает вопросы: {warning}." : "") +
                    (others > 0 ? $" Таких соединений у программы ещё {others}." : "") +
                    (highRisk
                        ? " Проверьте эту программу в разделе «Процессы» (VirusTotal, Защитник Windows)"
                        : " Если это ваша игра или чат — всё в порядке. Если вы не знаете эту программу, проверьте её в разделе " +
                          "«Процессы» (VirusTotal, Защитник Windows)") +
                    ", а адрес — значком щита «Проверить IP» в строке соединения (или через меню правой кнопки мыши).",
                    highRisk ? IssueSeverity.High : IssueSeverity.Medium, NetAddress.Text(c.RemoteAddress));
            }
        }

        return issues
            .OrderByDescending(i => i.Issue.Severity)
            .ToList();

        void AddFirewallOff(IReadOnlyList<ConnectedNetwork> networks, string ports)
        {
            var first = networks[0];
            var profile = new FirewallProfileState(first.Profile, null);
            var names = string.Join(", ", networks.Select(n => $"«{n.Name}»"));
            var onlyDomain = networks.All(n => n.Profile.Equals("Domain", StringComparison.OrdinalIgnoreCase));
            Add(FindingKind.FirewallOff,
                networks.Count == 1 ? $"Брандмауэр выключен для сети «{first.Name}»" : "Брандмауэр выключен для подключённых сетей",
                (networks.Count == 1
                    ? $"Windows считает сеть «{first.Name}» {profile.Genitive}, а брандмауэр для таких сетей выключен. "
                    : $"Брандмауэр выключен для сетей {names}. ") +
                $"Поэтому порты {ports} (общий доступ к файлам и принтерам, службы Windows) открыты для любого устройства " +
                (networks.Count == 1 ? "в этой сети" : "в этих сетях") +
                " — без брандмауэра так быть не должно. Включите брандмауэр в разделе «Безопасность» (или «Безопасность " +
                "Windows» → «Брандмауэр и защита сети»).",
                // Same severity as the Security module's "Брандмауэр Windows выключен", which also puts it on the dashboard.
                onlyDomain ? IssueSeverity.Low : IssueSeverity.High, pageOnly: true);
        }
    }

    /// <summary>
    /// Why the programs connected to an unusual port look unusual themselves (path → text): <paramref name="analyze"/>
    /// (normally <see cref="Suspicion.Analyze"/> with the signature) must rate the program at least "Подозрительно" —
    /// a missing signature alone is not enough, many games are unsigned. Only these few programs are checked.
    /// </summary>
    public static Dictionary<string, string> ProgramWarnings(SocketSnapshot snapshot, Func<string, SuspicionResult> analyze)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = BuildConnections(snapshot, includeLoopback: false)
            .Where(c => c.IsSuspicious && !string.IsNullOrWhiteSpace(c.Process.Path))
            .Select(c => c.Process.Path!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var suspicion = analyze(path);
            if (suspicion.IsSuspicious && ProgramWarningText(suspicion.Reasons) is { Length: > 0 } text)
                result[path] = text;
        }
        return result;
    }

    /// <summary>["Нет цифровой подписи", "Файл скрыт"] → "нет цифровой подписи; файл скрыт" (used inside a sentence).</summary>
    public static string ProgramWarningText(IEnumerable<string> reasons)
        => string.Join("; ", reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => LowerFirst(r.Trim())));

    private static string LowerFirst(string text)
        => text.Length == 0 ? text : char.ToLower(text[0], Format.Ru) + text[1..];

    // ===================================================================== firewall

    /// <summary>
    /// Connected networks whose Windows Firewall profile is off. An enabled third-party firewall protects them, and an
    /// unknown state is never reported.
    /// </summary>
    public static List<ConnectedNetwork> FirewallOffNetworks(FirewallReport? firewall)
    {
        if (firewall is null || firewall.ThirdPartyFirewall is not null)
            return new List<ConnectedNetwork>();
        return firewall.Networks
            .Where(n => firewall.Profiles.Any(p => p.Key.Equals(n.Profile, StringComparison.OrdinalIgnoreCase) && p.Value == false))
            .ToList();
    }

    /// <summary>MSFT_NetConnectionProfile.NetworkCategory → firewall profile name (0 Public, 1 Private, 2 Domain).</summary>
    public static string? ProfileOfCategory(long? category) => category switch
    {
        0 => "Public",
        1 => "Private",
        2 => "Domain",
        _ => null,
    };

    // ===================================================================== severity labels

    /// <summary>
    /// Badge of a finding on the page. Noun-like labels: a bare "Низко" in front of "Высокая задержка" read as a
    /// contradiction. The dashboard groups by severity with its own headers and hints.
    /// </summary>
    public static string SeverityText(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => "Критично",
        IssueSeverity.High => "Важно",
        IssueSeverity.Medium => "Внимание",
        IssueSeverity.Low => "Мелочь",
        _ => "Совет",
    };

    /// <summary>What the badge means (tooltip).</summary>
    public static string SeverityToolTip(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => "Угрожает безопасности или работе сети — исправьте в первую очередь.",
        IssueSeverity.High => "Заметно мешает работе сети или защите компьютера.",
        IssueSeverity.Medium => "Стоит посмотреть и исправить, когда будет минутка.",
        IssueSeverity.Low => "Мелочь: исправление чуть-чуть улучшит работу сети.",
        _ => "Полезная рекомендация — на ваше усмотрение.",
    };

    /// <summary>Parses "866.7" / "866,7".</summary>
    internal static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
        return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }
}
