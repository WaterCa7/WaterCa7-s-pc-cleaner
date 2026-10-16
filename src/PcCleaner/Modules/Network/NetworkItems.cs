using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Input;
using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

/// <summary>"Label: value" pair shown in an adapter card.</summary>
public sealed record AdapterField(string Label, string Value, string? ToolTip = null);

/// <summary>An active adapter with live traffic.</summary>
public sealed class AdapterItem : ObservableObject
{
    private string _receiveRate = "—";
    private string _sendRate = "—";
    private string _totals = "";
    private double _receiveBps;
    private double _sendBps;

    public AdapterItem(AdapterInfo info, bool isPrimary)
    {
        Info = info;
        IsPrimary = isPrimary;
        var fields = new List<AdapterField>();
        fields.Add(new AdapterField("IPv4-адрес", info.IPv4.Count == 0 ? "нет" : string.Join(", ", info.IPv4.Select(NetAddress.Text)),
            "Адрес компьютера в сети. 192.168.x.x, 10.x.x.x и 172.16–31.x.x — внутренние адреса домашней сети."));
        fields.Add(new AdapterField(info.IsVirtual ? "Шлюз" : "Шлюз (роутер)",
            info.Gateways.Count == 0 ? "нет" : string.Join(", ", info.Gateways.Take(2).Select(NetAddress.Text)),
            info.IsVirtual
                ? "Через этот адрес трафик уходит в VPN или виртуальную сеть."
                : "Через этот адрес компьютер выходит в интернет — обычно это ваш роутер."));
        var dns = info.DnsServers.Where(d => !NetworkRules.IsPlaceholderDns(d)).Select(NetAddress.Text).Distinct().Take(3).ToList();
        fields.Add(new AdapterField("DNS", dns.Count == 0 ? "нет" : string.Join(", ", dns),
            "Серверы, которые переводят адреса сайтов (ya.ru) в IP-адреса."));
        if (info.Dhcp is { } dhcp && !info.IsVirtual)
            fields.Add(new AdapterField("DHCP", dhcp ? "включён" : "выключен",
                dhcp ? "Адрес выдаётся автоматически (роутером) — так настроено почти у всех."
                     : "Адрес задан вручную (статический IP)."));
        if (info.Mac is not null)
            fields.Add(new AdapterField("MAC-адрес", info.Mac, "Физический адрес сетевой карты — уникальный номер устройства."));
        if (info.IPv6.Count > 0)
            fields.Add(new AdapterField("IPv6", string.Join(", ", info.IPv6.Take(2).Select(NetAddress.Text)),
                "Адрес нового протокола IPv6. Если провайдер его не поддерживает, это поле пустое — это нормально."));
        Fields = fields;
    }

    public AdapterInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Description => Info.Description;
    public bool IsPrimary { get; }
    public bool IsVirtual => Info.IsVirtual;
    public string KindText => NetworkRules.KindText(Info.Kind);
    public string Glyph => NetworkRules.KindGlyph(Info.Kind);

    public string KindToolTip => Info.Kind switch
    {
        AdapterKind.Ethernet => "Проводное подключение по кабелю",
        AdapterKind.WiFi => "Беспроводное подключение",
        AdapterKind.Vpn => "Виртуальный адаптер VPN или прокси: трафик программ идёт через него к серверу VPN",
        AdapterKind.Virtual => "Виртуальный адаптер (Hyper-V, виртуальные машины, WSL) — не настоящая сетевая карта",
        _ => "Другой тип подключения (Bluetooth, телефон в режиме модема и т. п.)",
    };

    public bool HasSpeed => !Info.IsVirtual && Info.SpeedBps > 0;
    public string SpeedText => NetworkRules.FormatLinkSpeed(Info.SpeedBps);

    public string SpeedToolTip => Info.Kind == AdapterKind.WiFi
        ? "Скорость связи с роутером по Wi-Fi (скорость интернета зависит ещё и от тарифа)"
        : "Скорость связи сетевой карты с роутером (скорость интернета зависит ещё и от тарифа)";

    public bool IsSlowLink => NetworkRules.IsSlowWiredLink(Info);

    public string SlowLinkText =>
        $"Карта работает на {SpeedText}. Обычно это значит, что кабель плохой: для 1 Гбит/с нужен Cat5e или Cat6 со всеми 8 жилами. " +
        "Попробуйте другой кабель или другой порт роутера.";

    public IReadOnlyList<AdapterField> Fields { get; }

    public string ReceiveRate
    {
        get => _receiveRate;
        private set => Set(ref _receiveRate, value);
    }

    public string SendRate
    {
        get => _sendRate;
        private set => Set(ref _sendRate, value);
    }

    public string Totals
    {
        get => _totals;
        private set => Set(ref _totals, value);
    }

    public double ReceiveBps => _receiveBps;
    public double SendBps => _sendBps;

    public void UpdateTraffic(TrafficCounters? previous, TrafficCounters current, double seconds)
    {
        if (previous is { } p && seconds > 0)
        {
            _receiveBps = LatencyMath.Rate(p.Received, current.Received, seconds);
            _sendBps = LatencyMath.Rate(p.Sent, current.Sent, seconds);
            ReceiveRate = LatencyMath.FormatRate(_receiveBps);
            SendRate = LatencyMath.FormatRate(_sendBps);
        }
        Totals = $"Всего: ↓ {Format.Bytes(current.Received)} · ↑ {Format.Bytes(current.Sent)}";
    }
}

/// <summary>One row of the latency card.</summary>
public sealed class LatencyItem
{
    public required string Name { get; init; }
    public required string Host { get; init; }
    public double? Ms { get; init; }
    public bool IsPending { get; init; }
    public NetTone Tone { get; init; }
    public string ValueText { get; init; } = "";
    public string ToolTip { get; init; } = "";

    /// <summary>0..100 for the bar (200 ms and more = full bar).</summary>
    public double BarValue => Ms is null ? (IsPending ? 0 : 100) : Math.Clamp(Ms.Value / 2, 2, 100);

    public static LatencyItem Pending(string name, string host) => new()
    {
        Name = name,
        Host = host,
        IsPending = true,
        Tone = NetTone.Neutral,
        ValueText = "…",
        ToolTip = "Проверяется…",
    };

    public static LatencyItem ForTarget(LatencyResult r)
    {
        var (tone, word) = LatencyMath.RateInternet(r.MedianMs);
        var attempts = string.Join(", ", r.Attempts.Select(a => a is null ? "нет ответа" : LatencyMath.FormatMs(a)));
        return new LatencyItem
        {
            Name = r.Target.Name,
            Host = r.Target.Host,
            Ms = r.MedianMs,
            Tone = tone,
            ValueText = r.MedianMs is null ? "нет ответа" : LatencyMath.FormatMs(r.MedianMs),
            ToolTip = r.MedianMs is null
                ? $"{r.Target.Host}: сервер не ответил за 2 секунды ни на одну из 3 попыток" + (r.Error is null ? "" : $" ({r.Error})")
                : $"{r.Target.Host}: время установки соединения — {word}. Попытки: {attempts}",
        };
    }

    public static LatencyItem ForRouter(GatewayResult g)
    {
        var (tone, word) = LatencyMath.RateRouter(g.MedianMs);
        return new LatencyItem
        {
            Name = "Роутер",
            Host = NetAddress.Text(g.Address),
            Ms = g.MedianMs,
            Tone = tone,
            ValueText = g.MedianMs is null ? "не отвечает" : LatencyMath.FormatMs(g.MedianMs),
            ToolTip = g.MedianMs is null
                ? "Роутер не ответил на ping. Многие роутеры специально не отвечают на такие запросы — это не ошибка."
                : $"Задержка до роутера по локальной сети — {word}. По кабелю обычно меньше 1 мс, по Wi-Fi — 1–5 мс.",
        };
    }

    public static LatencyItem ForHttp(double? ms) => new()
    {
        Name = "Проверка Windows",
        Host = "msftconnecttest.com",
        Ms = ms,
        Tone = LatencyMath.RateInternet(ms).Tone,
        ValueText = ms is null ? "нет ответа" : LatencyMath.FormatMs(ms),
        ToolTip = "Время ответа сервера, по которому Windows сама проверяет доступ в интернет (через VPN или прокси).",
    };
}

/// <summary>A DNS server in the DNS card.</summary>
public sealed class DnsServerItem
{
    public DnsServerItem(DnsServerEntry entry)
    {
        Address = NetAddress.Text(entry.Address);
        Label = entry.Label;
        AdapterName = entry.AdapterName;
        Tone = entry.Kind switch
        {
            DnsKind.Known => NetTone.Ok,
            DnsKind.Vpn or DnsKind.Local => NetTone.Info,
            _ => NetTone.Neutral,
        };
        ToolTip = entry.Kind switch
        {
            DnsKind.Known => $"{entry.Label} — известный публичный DNS-сервер.",
            DnsKind.Router => "Адрес из домашней сети: запросы идут через роутер к DNS провайдера.",
            DnsKind.Vpn => "DNS-сервер VPN: адреса сайтов определяет VPN-сервис.",
            DnsKind.Local => "Запросы обрабатывает программа на этом компьютере (VPN, фильтр рекламы и т. п.).",
            _ => "Обычно это DNS вашего интернет-провайдера.",
        };
    }

    public string Address { get; }
    public string Label { get; }
    public string AdapterName { get; }
    public NetTone Tone { get; }
    public string ToolTip { get; }
}

/// <summary>A finding shown on the page (same rules as the dashboard).</summary>
public sealed class FindingItem
{
    public required HealthIssue Issue { get; init; }
    public IssueSeverity Severity => Issue.Severity;
    public string Title => Issue.Title;
    public string Details => Issue.Details;
    public string? ActionText { get; init; }
    public ICommand? ActionCommand { get; init; }
    public object? ActionParameter { get; init; }
    public bool HasAction => ActionText is not null && ActionCommand is not null;
}

/// <summary>A row of the connections list: a program (expandable) or one of its connections.</summary>
public abstract class ConnectionListItem : ObservableObject
{
    public abstract bool IsGroup { get; }
    public bool IsChild => !IsGroup;
    public abstract ProcessIdentity Process { get; }
    public string ProcessName => Process.Name;
    public string ProcessDisplay => Process.DisplayName;
    public string? ProcessPath => Process.Path;
    public int Pid => Process.Pid;
    public string GroupKey => Process.GroupKey;

    /// <summary>First column: program name (group) or "ip:port" (connection).</summary>
    public abstract string PrimaryText { get; }

    /// <summary>Muted text after the primary one (exe name of a program).</summary>
    public abstract string SecondaryText { get; }

    /// <summary>"Узел" column: host name (connection) or the addresses summary (program).</summary>
    public abstract string HostText { get; }

    public abstract string Service { get; }
    public abstract string StateText { get; }
    public virtual string StateToolTip => StateText;

    /// <summary>"Соединений" column: the total of a program, "×N" for identical connections of one address.</summary>
    public abstract string CountText { get; }
    public abstract string CountToolTip { get; }
    public abstract bool HasCount { get; }
    public abstract bool IsSuspicious { get; }
    public abstract string ToolTip { get; }
    public abstract string CopyText { get; }
}

/// <summary>A program with its connections (rows below it when expanded).</summary>
public sealed class ConnectionGroupItem : ConnectionListItem
{
    private bool _isExpanded;

    public ConnectionGroupItem(IReadOnlyList<ConnectionItem> children)
    {
        Children = children;
        Process = children[0].Process;
        Count = children.Sum(c => c.Count);
        Suspicious = children.Any(c => c.IsSuspicious);
        var established = children.Where(c => c.IsEstablished).Sum(c => c.Count);
        var opening = Count - established;
        StateText = established > 0 ? "Установлено" : "Подключается";
        StateToolTip = opening == 0
            ? $"Установлено: {Format.Number(established)}"
            : $"Установлено: {Format.Number(established)}, ещё подключаются: {Format.Number(opening)}";
        var services = children.Select(c => c.Service).Where(s => s.Length > 0).Distinct().ToList();
        Service = services.Count switch
        {
            0 => "",
            1 => services[0],
            _ => "разные",
        };
        var endpoints = children.Select(c => c.RemoteText).Distinct().ToList();
        HostText = endpoints.Count <= 2
            ? string.Join(", ", endpoints)
            : $"{string.Join(", ", endpoints.Take(2))} и ещё {Format.Number(endpoints.Count - 2)}";
    }

    public IReadOnlyList<ConnectionItem> Children { get; }
    public override bool IsGroup => true;
    public override ProcessIdentity Process { get; }

    /// <summary>All connections of the program (identical ones counted separately).</summary>
    public int Count { get; }
    public bool Suspicious { get; }
    public override string PrimaryText => Process.DisplayName;
    public override string SecondaryText => Process.Name;
    public override string HostText { get; }
    public override string Service { get; }
    public override string StateText { get; }
    public override string StateToolTip { get; }
    public override string CountText => Format.Number(Count);
    public override string CountToolTip => $"У программы {Format.Count(Count, "соединение", "соединения", "соединений")}";
    public override bool HasCount => true;
    public override bool IsSuspicious => Suspicious;

    /// <summary>Raised when the user expands/collapses the program (the list is rebuilt).</summary>
    public event Action<ConnectionGroupItem>? ExpandedChanged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                ExpandedChanged?.Invoke(this);
        }
    }

    /// <summary>Sets the state without notifying the page (used while building the list).</summary>
    internal void InitExpanded(bool value) => _isExpanded = value;

    public override string ToolTip =>
        $"{Process.DisplayName} ({Process.Name}, PID {Process.Pid})" + (Process.Path is null ? "" : $"\n{Process.Path}") +
        $"\n{Format.Count(Count, "соединение", "соединения", "соединений")}" +
        (Suspicious ? "\n\nЕсть соединения с подозрительными портами — раскройте строку." : "");

    public override string CopyText =>
        $"{Process.Name} (PID {Process.Pid}): {Format.Count(Count, "соединение", "соединения", "соединений")}\n" +
        string.Join("\n", Children.Select(c => "  " + c.CopyText));
}

/// <summary>One TCP connection (identical ones merged, see <see cref="Count"/>).</summary>
public sealed class ConnectionItem : ConnectionListItem
{
    /// <summary>Shown while the reverse DNS name of a public address is being looked up.</summary>
    public const string PendingHost = "определяю…";

    /// <summary>The address has no reverse DNS name (or the lookup was skipped).</summary>
    public const string NoHost = "—";

    private string _hostName;

    public ConnectionItem(ConnectionRow row)
    {
        Row = row;
        RemoteText = NetAddress.Endpoint(row.RemoteAddress, row.RemotePort);
        RemoteIp = NetAddress.Text(row.RemoteAddress);
        Service = NetworkRules.RemoteServiceName(row.RemotePort);
        StateText = NetworkRules.StateText(row.State);
        _hostName = row.Scope switch
        {
            AddressScope.Public => PendingHost,
            AddressScope.Loopback => "этот компьютер",
            AddressScope.Cgnat => "сеть провайдера",
            _ => "локальная сеть",
        };
    }

    public ConnectionRow Row { get; }
    public override bool IsGroup => false;
    public override ProcessIdentity Process => Row.Process;

    /// <summary>Identical connections merged into this row.</summary>
    public int Count => Row.Count;
    public override bool HasCount => Row.Count > 1;
    public override string CountText => $"×{Format.Number(Row.Count)}";
    public override string CountToolTip =>
        $"{Format.Count(Row.Count, "одинаковое соединение", "одинаковых соединения", "одинаковых соединений")} с {RemoteText}";
    public string RemoteText { get; }
    public string RemoteIp { get; }
    public IPAddress RemoteAddress => Row.RemoteAddress;
    public int RemotePort => Row.RemotePort;
    public override string PrimaryText => RemoteText;
    public override string SecondaryText => "";
    public override string HostText => HostName;
    public override string Service { get; }
    public override string StateText { get; }
    public bool IsEstablished => Row.State == TcpState.Established;
    public bool IsPublic => Row.Scope == AddressScope.Public;
    public override bool IsSuspicious => Row.IsSuspicious;

    /// <summary>Address sort key: IPv4 before IPv6, numeric order.</summary>
    public string RemoteSortKey => string.Concat(Row.RemoteAddress.GetAddressBytes().Select(b => b.ToString("X2"))).PadLeft(32, '0') +
                                   Row.RemotePort.ToString("D5");

    private bool HasHostName => IsPublic && HostName is not PendingHost and not NoHost;

    /// <summary>True while the reverse DNS name is not known yet.</summary>
    public bool IsHostPending => IsPublic && HostName == PendingHost;

    public override string ToolTip =>
        $"{Row.Process.DisplayName} ({Row.Process.Name}, PID {Row.Process.Pid})\n" +
        $"Удалённый адрес: {RemoteText}" + (HasHostName ? $" ({HostName})" : "") + "\n" +
        (Row.Count > 1
            ? $"{Format.Count(Row.Count, "одинаковое соединение", "одинаковых соединения", "одинаковых соединений")} · {StateText}"
            : $"Локальный порт: {Row.LocalPort} · {StateText}") +
        (Row.SuspiciousReason is null ? "" : $"\n\nПорт {Row.RemotePort} — {Row.SuspiciousReason}.");

    /// <summary>Reverse DNS name of the remote address (<see cref="PendingHost"/> while resolving, <see cref="NoHost"/> if it has none).</summary>
    public string HostName
    {
        get => _hostName;
        set
        {
            if (Set(ref _hostName, value))
            {
                OnPropertyChanged(nameof(HostText));
                OnPropertyChanged(nameof(IsHostPending));
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public override string CopyText =>
        $"{Row.Process.Name} (PID {Row.Process.Pid}) → {RemoteText}" + (HasHostName ? $" ({HostName})" : "") + $", {StateText}" +
        (Row.Count > 1 ? $", соединений: {Row.Count}" : "");

    /// <summary>What "Найти в интернете" looks for: the host name if known, otherwise the address.</summary>
    public string SearchText => HasHostName ? HostName : RemoteIp;

    public bool Matches(string query)
        => ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           ProcessDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           (ProcessPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
           RemoteText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           HostName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           Service.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           Pid.ToString() == query;
}

/// <summary>One listening port row.</summary>
public sealed class PortItem
{
    public PortItem(PortRow row)
    {
        Row = row;
        AddressText = string.Join(", ", row.Addresses.Select(NetAddress.Text));
        AccessText = row.IsNetwork ? "из сети" : "только этот ПК";
        KindText = NetworkRules.PortKindText(row.Kind, row.Process);
        Tone = NetworkRules.PortKindTone(row.Kind);
        ServiceText = string.IsNullOrEmpty(row.Service) ? "—" : row.Service;
    }

    public PortRow Row { get; }
    public int Port => Row.Port;
    public string ProtocolText => Row.Protocol == SocketProtocol.Tcp ? "TCP" : "UDP";
    public string AddressText { get; }
    public string AccessText { get; }
    public bool IsNetwork => Row.IsNetwork;
    public string ProcessName => Row.Process.Name;
    public string ProcessDisplay => Row.Process.DisplayName;
    public string? ProcessPath => Row.Process.Path;
    public int Pid => Row.Process.Pid;
    public string ServiceText { get; }
    public PortKind Kind => Row.Kind;
    public int KindRank => NetworkRules.PortKindRank(Row.Kind);
    public string KindText { get; }
    public NetTone Tone { get; }
    public string Explanation => Row.Explanation;

    public string AccessToolTip => Row.IsNetwork
        ? $"Адрес {AddressText}: " + (Row.Addresses.Any(NetAddress.IsAny)
            ? "0.0.0.0 и :: означают «на всех сетевых адаптерах» — к порту могут подключиться другие устройства в сети."
            : "к порту могут подключиться другие устройства в сети.")
        : $"Адрес {AddressText}: 127.0.0.1 и ::1 — это сам компьютер, из сети к порту не подключиться.";

    public string ToolTip => $"{Row.Process.DisplayName} ({Row.Process.Name}, PID {Row.Process.Pid})\n{ProtocolText} {AddressText} : {Port}\n\n{Explanation}";

    public string CopyText => $"{ProtocolText} {AddressText} : {Port} — {Row.Process.Name} (PID {Row.Process.Pid})" +
                              (ServiceText == "—" ? "" : $" — {ServiceText}");

    public bool Matches(string query)
        => ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           ProcessDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           (ProcessPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
           AddressText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           ServiceText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           KindText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           Port.ToString() == query ||
           Pid.ToString() == query;
}
