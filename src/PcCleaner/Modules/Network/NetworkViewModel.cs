using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

public sealed class NetworkViewModel : PageViewModel
{
    private static readonly TimeSpan TrafficInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SocketsMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UploadSample = TimeSpan.FromSeconds(2);
    private const int MaxReverseLookups = 80;
    private static readonly TimeSpan ReverseWait = TimeSpan.FromSeconds(2.5);

    /// <summary>Reverse DNS lookups (ip → name, null = none), running or finished, shared by all visits of the page.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> ReverseLookups = new();

    /// <summary>Each lookup blocks a worker thread while Windows waits for the DNS server, so only a few run at once.</summary>
    private static readonly SemaphoreSlim ReverseGate = new(6);

    private readonly NetworkService _service;
    private readonly BulkObservableCollection<ConnectionListItem> _connectionRows = new();
    private readonly BulkObservableCollection<PortItem> _ports = new();
    private List<ConnectionGroupItem> _groups = new();
    private readonly Dictionary<string, bool> _expandedByUser = new();
    private string _connectionSortKey = ConnectionSortKeys.Count;
    private bool _connectionSortDescending = true;

    private DispatcherTimer? _timer;
    private bool _loadedOnce;
    private bool _isActive;
    private bool _sampling;
    private bool _socketsLoading;
    private Task? _loadTask;
    private TrafficSample? _lastTraffic;
    private DateTime _lastAdapterReload = DateTime.MinValue;
    private string? _lastTrafficError;
    private Task _reverseTask = Task.CompletedTask;

    private List<AdapterInfo> _adapters = new();
    private LatencyReport? _latency;
    private DnsReport? _dns;
    private WifiReport? _wifi;
    private SocketSnapshot? _sockets;
    private double? _upload;
    private bool _latencyPending;
    private bool _dnsPending;
    private bool _wifiPending;
    private bool _socketsPending;
    private bool _stopped;

    private bool _hasData;
    private bool _checked;
    private string _statusTitle = "Проверяю сеть…";
    private string _statusText = "Смотрю адаптеры, задержку, DNS, Wi-Fi, соединения и открытые порты. Ничего не меняется.";
    private NetTone _statusTone = NetTone.Neutral;
    private string _lastCheckText = "";
    private string _internetValue = "…";
    private string _internetHint = "проверяю…";
    private NetTone _internetTone = NetTone.Neutral;
    private string _connectionValue = "…";
    private string _connectionHint = "";
    private string _connectionGlyph = "";
    private NetTone _connectionTone = NetTone.Neutral;
    private string _wifiValue = "…";
    private string _wifiHint = "";
    private NetTone _wifiTone = NetTone.Neutral;
    private double _wifiPercent;
    private bool _hasWifiPercent;
    private string _socketsValue = "…";
    private string _socketsHint = "";
    private NetTone _socketsTone = NetTone.Neutral;
    private string _latencyBadge = "проверяю…";
    private NetTone _latencyTone = NetTone.Neutral;
    private string? _latencyNote;
    private string _dnsTimeText = "Время ответа: проверяю…";
    private string _dnsBadge = "проверяю…";
    private NetTone _dnsTone = NetTone.Neutral;
    private string? _dnsAdvice;
    private bool _hasWifi;
    private string _wifiSsid = "";
    private string _wifiQuality = "";
    private string? _wifiNote;
    private IReadOnlyList<AdapterField> _wifiFields = Array.Empty<AdapterField>();
    private string _wifiEmptyTitle = "Проверяю Wi-Fi…";
    private string _wifiEmptyText = "";
    private bool _wifiLocationBlocked;
    private string _searchText = "";
    private bool _showLocalConnections;
    private bool _onlyNetworkPorts = true;
    private int _socketsTab;
    private string _connectionsTabHeader = "Соединения";
    private string _portsTabHeader = "Открытые порты";
    private string _socketsUpdatedText = "";
    private int _visibleConnections;
    private int _visiblePorts;
    private string _portsHiddenNote = "";
    private string? _message;
    private NetTone _messageTone = NetTone.Ok;

    public NetworkViewModel(NetworkService service)
    {
        _service = service;

        PortsView = CollectionViewSource.GetDefaultView(_ports);
        PortsView.SortDescriptions.Add(new SortDescription(nameof(PortItem.KindRank), ListSortDirection.Ascending));
        PortsView.SortDescriptions.Add(new SortDescription(nameof(PortItem.Port), ListSortDirection.Ascending));
        PortsView.Filter = o => o is PortItem p && (!OnlyNetworkPorts || p.IsNetwork) && MatchesSearch(p.Matches);

        RecheckCommand = new AsyncRelayCommand(RunFullCheckAsync, () => !IsBusy);
        RefreshSocketsCommand = new AsyncRelayCommand(RefreshSocketsAsync, () => !_socketsLoading);
        FlushDnsCommand = new AsyncRelayCommand(FlushDnsAsync, () => !IsBusy);
        ResetNetworkCommand = new AsyncRelayCommand(ResetNetworkAsync, () => !IsBusy);
        OpenNetworkSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network-status"));
        OpenConnectionsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ncpa.cpl"));
        OpenAdapterSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network-advancedsettings"));
        OpenWifiSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network-wifi"));
        OpenLocationSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:privacy-location"));
        HowToChangeDnsCommand = new RelayCommand(ShowHowToChangeDns);
        ShowInProcessesCommand = new RelayCommand(ShowInProcesses, p => p is ConnectionListItem or PortItem);
        CheckIpCommand = new RelayCommand(CheckIp, p => p is ConnectionItem { IsPublic: true });
        SearchWebCommand = new RelayCommand(SearchWeb, p => p is ConnectionListItem or PortItem);
        CopyCommand = new RelayCommand(Copy, p => p is ConnectionListItem or PortItem);
        ToggleGroupCommand = new RelayCommand(p =>
        {
            if (p is ConnectionGroupItem g)
                g.IsExpanded = !g.IsExpanded;
        });
        ShowSocketsCommand = new RelayCommand(ShowSockets);
        DismissMessageCommand = new RelayCommand(() => Message = null);
        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !Admin.IsElevated);
    }

    public override string Title => "Сеть";

    public override string Subtitle => "Скорость подключения, задержки, Wi-Fi, открытые порты и соединения программ";

    public override string IconGlyph => "";

    /// <summary>Asks the view to scroll to the connections/ports card.</summary>
    public event Action? SocketsRequested;

    // ================================================================ collections

    public ObservableCollection<FindingItem> Findings { get; } = new();
    public ObservableCollection<AdapterItem> Adapters { get; } = new();
    public ObservableCollection<LatencyItem> LatencyItems { get; } = new();
    public ObservableCollection<DnsServerItem> DnsServers { get; } = new();
    /// <summary>Programs and (for expanded programs) their connections, already filtered and sorted.</summary>
    public BulkObservableCollection<ConnectionListItem> ConnectionRows => _connectionRows;

    public ICollectionView PortsView { get; }

    /// <summary>Current sort of the connections list (column SortMemberPath), for the header arrows.</summary>
    public string ConnectionSortKey => _connectionSortKey;

    public bool ConnectionSortDescending => _connectionSortDescending;

    /// <summary>Raised after the connections sort changed (the view updates the header arrows).</summary>
    public event Action? ConnectionSortChanged;

    // ================================================================ commands

    public AsyncRelayCommand RecheckCommand { get; }
    public AsyncRelayCommand RefreshSocketsCommand { get; }
    public AsyncRelayCommand FlushDnsCommand { get; }
    public AsyncRelayCommand ResetNetworkCommand { get; }
    public RelayCommand OpenNetworkSettingsCommand { get; }
    public RelayCommand OpenConnectionsCommand { get; }
    public RelayCommand OpenAdapterSettingsCommand { get; }
    public RelayCommand OpenWifiSettingsCommand { get; }
    public RelayCommand OpenLocationSettingsCommand { get; }
    public RelayCommand HowToChangeDnsCommand { get; }
    public RelayCommand ShowInProcessesCommand { get; }
    public RelayCommand CheckIpCommand { get; }
    public RelayCommand SearchWebCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand ShowSocketsCommand { get; }
    public RelayCommand DismissMessageCommand { get; }
    public RelayCommand ToggleGroupCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }

    // ================================================================ state

    public bool IsAdmin => Admin.IsElevated;

    public bool HasData
    {
        get => _hasData;
        private set => Set(ref _hasData, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => Set(ref _statusTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public NetTone StatusTone
    {
        get => _statusTone;
        private set => Set(ref _statusTone, value);
    }

    public string LastCheckText
    {
        get => _lastCheckText;
        private set => Set(ref _lastCheckText, value);
    }

    public bool HasFindings => Findings.Count > 0;

    public string InternetValue
    {
        get => _internetValue;
        private set => Set(ref _internetValue, value);
    }

    public string InternetHint
    {
        get => _internetHint;
        private set => Set(ref _internetHint, value);
    }

    public NetTone InternetTone
    {
        get => _internetTone;
        private set => Set(ref _internetTone, value);
    }

    public string ConnectionValue
    {
        get => _connectionValue;
        private set => Set(ref _connectionValue, value);
    }

    public string ConnectionHint
    {
        get => _connectionHint;
        private set => Set(ref _connectionHint, value);
    }

    public string ConnectionGlyph
    {
        get => _connectionGlyph;
        private set => Set(ref _connectionGlyph, value);
    }

    public NetTone ConnectionTone
    {
        get => _connectionTone;
        private set => Set(ref _connectionTone, value);
    }

    public string WifiValue
    {
        get => _wifiValue;
        private set => Set(ref _wifiValue, value);
    }

    public string WifiHint
    {
        get => _wifiHint;
        private set => Set(ref _wifiHint, value);
    }

    public NetTone WifiTone
    {
        get => _wifiTone;
        private set => Set(ref _wifiTone, value);
    }

    public double WifiPercent
    {
        get => _wifiPercent;
        private set => Set(ref _wifiPercent, value);
    }

    public bool HasWifiPercent
    {
        get => _hasWifiPercent;
        private set => Set(ref _hasWifiPercent, value);
    }

    public string SocketsValue
    {
        get => _socketsValue;
        private set => Set(ref _socketsValue, value);
    }

    public string SocketsHint
    {
        get => _socketsHint;
        private set => Set(ref _socketsHint, value);
    }

    public NetTone SocketsTone
    {
        get => _socketsTone;
        private set => Set(ref _socketsTone, value);
    }

    public bool IsLatencyRunning => _latencyPending;

    public string LatencyBadge
    {
        get => _latencyBadge;
        private set => Set(ref _latencyBadge, value);
    }

    public NetTone LatencyTone
    {
        get => _latencyTone;
        private set => Set(ref _latencyTone, value);
    }

    /// <summary>Shown when a VPN answers connections itself.</summary>
    public string? LatencyNote
    {
        get => _latencyNote;
        private set => Set(ref _latencyNote, value);
    }

    public string LatencyFootnote =>
        "Проверка отправляет по 3 крошечных запроса на соединение к Cloudflare (1.1.1.1), Google (8.8.8.8), Яндексу (ya.ru) " +
        "и Microsoft, ping роутеру и запрос к серверу, которым Windows сама проверяет интернет (msftconnecttest.com). " +
        "Соединения сразу закрываются — никакие ваши данные не передаются.";

    public string DnsTimeText
    {
        get => _dnsTimeText;
        private set => Set(ref _dnsTimeText, value);
    }

    public string DnsBadge
    {
        get => _dnsBadge;
        private set => Set(ref _dnsBadge, value);
    }

    public NetTone DnsTone
    {
        get => _dnsTone;
        private set => Set(ref _dnsTone, value);
    }

    public string? DnsAdvice
    {
        get => _dnsAdvice;
        private set => Set(ref _dnsAdvice, value);
    }

    public bool HasWifi
    {
        get => _hasWifi;
        private set => Set(ref _hasWifi, value);
    }

    public string WifiSsid
    {
        get => _wifiSsid;
        private set => Set(ref _wifiSsid, value);
    }

    public string WifiQuality
    {
        get => _wifiQuality;
        private set => Set(ref _wifiQuality, value);
    }

    public string? WifiNote
    {
        get => _wifiNote;
        private set => Set(ref _wifiNote, value);
    }

    public IReadOnlyList<AdapterField> WifiFields
    {
        get => _wifiFields;
        private set => Set(ref _wifiFields, value);
    }

    public string WifiEmptyTitle
    {
        get => _wifiEmptyTitle;
        private set => Set(ref _wifiEmptyTitle, value);
    }

    public string WifiEmptyText
    {
        get => _wifiEmptyText;
        private set => Set(ref _wifiEmptyText, value);
    }

    public bool WifiLocationBlocked
    {
        get => _wifiLocationBlocked;
        private set => Set(ref _wifiLocationBlocked, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
                RefreshViews();
        }
    }

    /// <summary>Also show connections between programs on this computer (127.0.0.1).</summary>
    public bool ShowLocalConnections
    {
        get => _showLocalConnections;
        set
        {
            if (Set(ref _showLocalConnections, value))
            {
                RebuildConnections();
                if (_checked)
                    BuildFindings();
            }
        }
    }

    /// <summary>Hide ports that only programs on this computer can reach.</summary>
    public bool OnlyNetworkPorts
    {
        get => _onlyNetworkPorts;
        set
        {
            if (Set(ref _onlyNetworkPorts, value))
                RefreshViews();
        }
    }

    public int SocketsTab
    {
        get => _socketsTab;
        set => Set(ref _socketsTab, value);
    }

    public string ConnectionsTabHeader
    {
        get => _connectionsTabHeader;
        private set => Set(ref _connectionsTabHeader, value);
    }

    public string PortsTabHeader
    {
        get => _portsTabHeader;
        private set => Set(ref _portsTabHeader, value);
    }

    public string SocketsUpdatedText
    {
        get => _socketsUpdatedText;
        private set => Set(ref _socketsUpdatedText, value);
    }

    public int VisibleConnections
    {
        get => _visibleConnections;
        private set => Set(ref _visibleConnections, value);
    }

    public int VisiblePorts
    {
        get => _visiblePorts;
        private set => Set(ref _visiblePorts, value);
    }

    public string ConnectionsEmptyText => _sockets is null
        ? (_socketsPending ? "Читаю таблицу соединений…" : _stopped ? StoppedSocketsText : "Не удалось прочитать таблицу соединений.")
        : !string.IsNullOrWhiteSpace(SearchText)
            ? $"Ничего не найдено по запросу «{SearchText.Trim()}»."
            : "Сейчас программы не обмениваются данными с другими компьютерами.";

    private const string StoppedSocketsText = "Проверка остановлена — нажмите «Обновить», чтобы прочитать список.";

    public string PortsEmptyText => _sockets is null
        ? (_socketsPending ? "Читаю таблицу портов…" : _stopped ? StoppedSocketsText : "Не удалось прочитать таблицу портов.")
        : !string.IsNullOrWhiteSpace(SearchText)
            ? $"Ничего не найдено по запросу «{SearchText.Trim()}»."
            : OnlyNetworkPorts
                ? "Ни одна программа не принимает подключения из сети — к компьютеру нельзя подключиться снаружи."
                : "Открытых портов нет.";

    public string PortsHiddenNote
    {
        get => _portsHiddenNote;
        private set => Set(ref _portsHiddenNote, value);
    }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public NetTone MessageTone
    {
        get => _messageTone;
        private set => Set(ref _messageTone, value);
    }

    // ================================================================ navigation

    public override Task OnNavigatedToAsync()
    {
        _isActive = true;
        StartTimer();
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _loadTask = RunFullCheckAsync();
        }
        else if (!IsBusy && (_sockets is null || DateTime.Now - _sockets.Time > SocketsMaxAge))
        {
            _ = RefreshSocketsAsync();
        }
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom()
    {
        _isActive = false;
        StopTimer();
    }

    /// <summary>Full read-only check (adapters, latency, DNS, Wi-Fi, connections, ports) for screenshot mode.</summary>
    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        _loadedOnce = true;
        if (_loadTask is { } load)
            await load;
        else
            await RunFullCheckAsync();
        // Give reverse DNS a moment so the connections list shows host names (never longer than 4 s).
        await Task.WhenAny(_reverseTask, Task.Delay(TimeSpan.FromSeconds(4), ct));
    }

    // ================================================================ full check

    private Task RunFullCheckAsync() => RunBusyAsync("Проверяю сеть…", async ct =>
    {
        try
        {
            await FullCheckAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The user pressed "Отмена" in the busy strip: never leave the cards spinning on "проверяю…".
            MarkStopped();
            throw;
        }
    });

    private async Task FullCheckAsync(CancellationToken ct)
    {
        _checked = false;
        ReportProgress(3, "Читаю сетевые адаптеры…");
        var adapters = await _service.GetAdaptersAsync(ct);
        ApplyAdapters(adapters);
        SetPending();

        var trafficStart = await Task.Run(_service.SampleTraffic, ct);
        var started = Stopwatch.StartNew();
        var done = 0;
        const int parts = 4;

        async Task Part<T>(string what, Func<Task<T>> probe, Action<T?> apply) where T : class
        {
            var result = await NetworkService.Safe(what, probe, ct);
            ct.ThrowIfCancellationRequested();
            apply(result);
            done++;
            ReportProgress(8 + done * 84.0 / parts, done < parts ? "Проверяю задержку, DNS и Wi-Fi…" : "Подвожу итоги…");
        }

        await Task.WhenAll(
            Part("соединения", () => _service.ReadSocketsAsync(ct), ApplySockets),
            Part("Wi-Fi", () => _service.ReadWifiAsync(adapters, ct), ApplyWifi),
            Part("DNS", () => _service.MeasureDnsAsync(ct), ApplyDns),
            Part("задержка", () => _service.MeasureLatencyAsync(adapters, ct), ApplyLatency));

        // Upload over at least 2 seconds (the checks above usually take longer).
        var remaining = UploadSample - started.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct);
        var trafficEnd = await Task.Run(_service.SampleTraffic, ct);
        _upload = NetworkProbes.UploadRate(trafficStart, trafficEnd, _adapters);

        _checked = true;
        BuildFindings();
        LastCheckText = $"Проверено в {DateTime.Now:HH:mm}";
        Log.Info(Findings.Count == 0
            ? "Сеть: проверка завершена, проблем не найдено"
            : $"Сеть: проверка завершена — {Format.Count(Findings.Count, "замечание", "замечания", "замечаний")}");
    }

    /// <summary>
    /// After a cancelled check: parts that did not finish say "не проверено" instead of "проверяю…". If everything but the
    /// upload sample was measured, the findings are still shown.
    /// </summary>
    internal void MarkStopped()
    {
        if (!HasData)
        {
            StatusTitle = "Проверка остановлена";
            StatusText = "Нажмите «Проверить снова», чтобы проверить сеть.";
            StatusTone = NetTone.Neutral;
            return;
        }

        var unfinished = _latencyPending || _dnsPending || _wifiPending || _socketsPending;
        _stopped = unfinished;
        if (_latencyPending)
        {
            _latencyPending = false;
            OnPropertyChanged(nameof(IsLatencyRunning));
            LatencyItems.Clear();
            InternetValue = "—";
            InternetHint = "не проверено";
            InternetTone = NetTone.Neutral;
            LatencyBadge = "не проверено";
            LatencyTone = NetTone.Neutral;
            LatencyNote = null;
        }
        if (_dnsPending)
        {
            _dnsPending = false;
            DnsTimeText = "Время ответа: не проверено";
            DnsBadge = "не проверено";
            DnsTone = NetTone.Neutral;
            DnsAdvice = null;
        }
        if (_wifiPending)
        {
            _wifiPending = false;
            HasWifi = false;
            HasWifiPercent = false;
            WifiNote = null;
            WifiFields = Array.Empty<AdapterField>();
            WifiValue = "—";
            WifiHint = "не проверено";
            WifiTone = NetTone.Neutral;
            WifiEmptyTitle = "Проверка остановлена";
            WifiEmptyText = "Нажмите «Проверить снова», чтобы узнать уровень сигнала.";
        }
        if (_socketsPending)
        {
            _socketsPending = false;
            if (_sockets is null)
            {
                SocketsValue = "—";
                SocketsHint = "не проверено";
                SocketsTone = NetTone.Neutral;
            }
            UpdateSocketCounts();
        }

        if (!unfinished)
        {
            // Only the upload sample was missing.
            _checked = true;
            BuildFindings();
            LastCheckText = $"Проверено в {DateTime.Now:HH:mm}";
            return;
        }
        StatusTitle = "Проверка остановлена";
        StatusText = "Часть проверок не выполнена. Нажмите «Проверить снова», чтобы проверить сеть полностью.";
        StatusTone = NetTone.Neutral;
    }

    /// <summary>Adapters are known: the cards appear and every other part shows "проверяю…".</summary>
    internal void SetPending()
    {
        HasData = true;
        _stopped = false;
        _latencyPending = _dnsPending = _wifiPending = _socketsPending = true;
        OnPropertyChanged(nameof(IsLatencyRunning));
        _latency = null;
        _dns = null;
        _wifi = null;
        _upload = null;
        StatusTitle = "Проверяю сеть…";
        StatusText = "Смотрю задержку, DNS, Wi-Fi, соединения и открытые порты. Ничего не меняется.";
        StatusTone = NetTone.Neutral;
        Findings.Clear();
        OnPropertyChanged(nameof(HasFindings));

        InternetValue = "…";
        InternetHint = "проверяю…";
        InternetTone = NetTone.Neutral;
        LatencyItems.Clear();
        var router = NetworkRules.RouterAddress(_adapters);
        if (router is not null)
            LatencyItems.Add(LatencyItem.Pending("Роутер", NetAddress.Text(router)));
        foreach (var t in NetworkRules.LatencyTargets)
            LatencyItems.Add(LatencyItem.Pending(t.Name, t.Host));
        LatencyBadge = "проверяю…";
        LatencyTone = NetTone.Neutral;
        LatencyNote = null;

        DnsTimeText = "Время ответа: проверяю…";
        DnsBadge = "проверяю…";
        DnsTone = NetTone.Neutral;
        DnsAdvice = null;

        if (_adapters.Any(a => a.Kind == AdapterKind.WiFi))
        {
            WifiValue = "…";
            WifiHint = "проверяю…";
            WifiTone = NetTone.Neutral;
            HasWifiPercent = false;
            HasWifi = false;
            WifiEmptyTitle = "Проверяю Wi-Fi…";
            WifiEmptyText = "";
        }
        else
        {
            ApplyWifi(new WifiReport());
        }

        if (_sockets is null)
        {
            SocketsValue = "…";
            SocketsHint = "проверяю…";
        }
    }

    // ================================================================ adapters and live traffic

    private void ApplyAdapters(List<AdapterInfo> adapters)
    {
        _adapters = adapters;
        var primary = NetworkRules.PrimaryAdapter(adapters);
        Adapters.Clear();
        foreach (var a in adapters)
        {
            var item = new AdapterItem(a, ReferenceEquals(a, primary));
            var previous = _lastTraffic?.Counters.TryGetValue(a.Id, out var c) == true ? c : (TrafficCounters?)null;
            item.UpdateTraffic(previous, new TrafficCounters(a.BytesReceived, a.BytesSent), 0);
            Adapters.Add(item);
        }

        if (primary is null)
        {
            ConnectionValue = "Нет";
            ConnectionHint = "нет активных подключений";
            ConnectionGlyph = "";
            ConnectionTone = NetTone.Danger;
        }
        else
        {
            var vpn = NetworkRules.ActiveVpn(adapters);
            ConnectionValue = NetworkRules.KindText(primary.Kind);
            ConnectionGlyph = NetworkRules.KindGlyph(primary.Kind);
            var hint = primary.IsVirtual || primary.SpeedBps <= 0 ? primary.Name : NetworkRules.FormatLinkSpeed(primary.SpeedBps);
            ConnectionHint = vpn is not null && !ReferenceEquals(vpn, primary) ? hint + " · VPN" : hint;
            ConnectionTone = NetworkRules.IsSlowWiredLink(primary) ? NetTone.Warning : NetTone.Ok;
        }

        DnsServers.Clear();
        foreach (var d in NetworkRules.DnsServers(adapters))
            DnsServers.Add(new DnsServerItem(d));
    }

    private void StartTimer()
    {
        if (Application.Current is null)
            return; // unit tests
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TrafficInterval };
            _timer.Tick += OnTimerTick;
        }
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_isActive || _sampling)
            return;
        _sampling = true;
        try
        {
            var sample = await Task.Run(_service.SampleTraffic);
            ApplyTraffic(sample);
            if (!IsBusy && AdapterSetChanged(sample) && DateTime.Now - _lastAdapterReload > TimeSpan.FromSeconds(10))
            {
                _lastAdapterReload = DateTime.Now;
                Log.Info("Сеть: список подключений изменился — обновляю");
                ApplyAdapters(await _service.GetAdaptersAsync());
            }
        }
        catch (Exception ex)
        {
            // Never let a live refresh problem reach the dispatcher; report each distinct problem once.
            if (_lastTrafficError != ex.Message)
            {
                _lastTrafficError = ex.Message;
                Log.Warn($"Сеть: не удалось обновить скорость: {ex.Message}");
            }
        }
        finally
        {
            _sampling = false;
        }
    }

    private void ApplyTraffic(TrafficSample sample)
    {
        var seconds = _lastTraffic is null ? 0 : (sample.TimestampTicks - _lastTraffic.TimestampTicks) / (double)Stopwatch.Frequency;
        foreach (var item in Adapters)
        {
            if (!sample.Counters.TryGetValue(item.Id, out var current))
                continue;
            TrafficCounters? previous = _lastTraffic?.Counters.TryGetValue(item.Id, out var p) == true ? p : null;
            item.UpdateTraffic(previous, current, seconds);
        }
        _lastTraffic = sample;
    }

    private bool AdapterSetChanged(TrafficSample sample)
    {
        var current = Adapters.Select(a => a.Id).ToHashSet();
        return !current.SetEquals(sample.AdapterIds);
    }

    // ================================================================ latency, DNS, Wi-Fi

    private void ApplyLatency(LatencyReport? report)
    {
        _latency = report;
        _latencyPending = false;
        OnPropertyChanged(nameof(IsLatencyRunning));
        LatencyItems.Clear();
        if (report is null)
        {
            InternetValue = "—";
            InternetHint = "не удалось проверить";
            InternetTone = NetTone.Neutral;
            LatencyBadge = "не проверено";
            LatencyTone = NetTone.Neutral;
            return;
        }

        if (report.Gateway is { } gw)
            LatencyItems.Add(LatencyItem.ForRouter(gw));
        var vpn = NetworkRules.ActiveVpn(_adapters);
        if (report.Intercepted)
        {
            LatencyItems.Add(LatencyItem.ForHttp(report.HttpMedianMs));
            LatencyNote = $"Соединения перехватывает VPN или прокси{(vpn is null ? "" : $" ({vpn.Name})")}: он отвечает на них сам, " +
                          "поэтому задержку до Cloudflare, Google и Яндекса точно измерить нельзя. Показано время ответа сервера, " +
                          "которым Windows проверяет интернет, — через VPN.";
        }
        else
        {
            foreach (var t in report.Targets)
                LatencyItems.Add(LatencyItem.ForTarget(t));
            LatencyNote = null;
        }

        var median = report.MedianMs;
        if (!report.InternetReachable)
        {
            InternetValue = "Нет";
            InternetHint = report.CaptivePortal ? "сеть просит войти" : "серверы не отвечают";
            InternetTone = NetTone.Danger;
            LatencyBadge = "нет ответа";
            LatencyTone = NetTone.Danger;
            return;
        }

        var (tone, word) = LatencyMath.RateInternet(median);
        InternetValue = "Есть";
        InternetHint = median is null
            ? "через VPN или прокси"
            : report.Intercepted ? $"≈ {LatencyMath.FormatMs(median)} · VPN" : $"{LatencyMath.FormatMs(median)} · {word}";
        InternetTone = median is > NetworkRules.HighLatencyMs ? NetTone.Warning : NetTone.Ok;
        LatencyBadge = median is null ? "есть связь" : $"{LatencyMath.FormatMs(median)} · {word}";
        LatencyTone = median is null ? NetTone.Ok : tone;
    }

    private void ApplyDns(DnsReport? report)
    {
        _dns = report;
        _dnsPending = false;
        if (report is null)
        {
            DnsTimeText = "Время ответа: не удалось проверить";
            DnsBadge = "не проверено";
            DnsTone = NetTone.Neutral;
            DnsAdvice = null;
            return;
        }

        var median = report.MedianMs;
        var (tone, word) = LatencyMath.RateDns(median);
        DnsTone = tone;
        if (report.AllFailed)
        {
            DnsBadge = "не отвечает";
            DnsTimeText = "Ни один из 5 тестовых адресов (example.com, wikipedia.org…) не удалось найти.";
            DnsAdvice = "Укажите DNS 1.1.1.1 (Cloudflare), 8.8.8.8 (Google) или 77.88.8.8 (Яндекс) в настройках адаптера или роутера.";
            return;
        }

        DnsBadge = $"{LatencyMath.FormatMs(median)} · {word}";
        var failed = report.Lookups.Count(l => l.Ms is null);
        DnsTimeText = $"Время ответа: {LatencyMath.FormatMs(median)} (медиана {Format.Count(report.Lookups.Count, "запроса", "запросов", "запросов")})" +
                      (failed > 0 ? $", без ответа: {failed}" : "");
        DnsAdvice = median > NetworkRules.SlowDnsMs
            ? "DNS отвечает медленно — сайты начинают открываться с задержкой. Попробуйте быстрый DNS: 1.1.1.1 (Cloudflare), " +
              "8.8.8.8 (Google) или 77.88.8.8 (Яндекс)."
            : null;
    }

    private void ApplyWifi(WifiReport? report)
    {
        _wifi = report;
        _wifiPending = false;
        var hasWifiAdapter = _adapters.Any(a => a.Kind == AdapterKind.WiFi);
        var connected = report?.Connected;
        WifiLocationBlocked = report?.LocationBlocked == true && connected?.SignalPercent is null;

        if (connected is not null && connected.SignalPercent is not null)
        {
            var (quality, tone) = NetworkRules.SignalQuality(connected.SignalPercent);
            HasWifi = true;
            WifiSsid = string.IsNullOrWhiteSpace(connected.Ssid) ? "Скрытая сеть" : connected.Ssid;
            WifiQuality = quality;
            WifiTone = tone;
            WifiPercent = connected.SignalPercent.Value;
            HasWifiPercent = true;
            WifiValue = $"{connected.SignalPercent}%";
            WifiHint = $"{NetworkRules.BandText(connected.BandGhz)} · {WifiSsid}";
            var adapterName = _adapters.FirstOrDefault(a => a.Kind == AdapterKind.WiFi &&
                                                           string.Equals(a.Id.Trim('{', '}'), connected.Guid, StringComparison.OrdinalIgnoreCase))?.Name
                              ?? connected.Name;
            var rate = connected.ReceiveMbps is null && connected.TransmitMbps is null
                ? "—"
                : $"{FormatMbps(connected.ReceiveMbps)} / {FormatMbps(connected.TransmitMbps)} Мбит/с";
            WifiFields = new List<AdapterField>
            {
                new("Диапазон", NetworkRules.BandText(connected.BandGhz),
                    "5 ГГц — быстрее и свободнее, 2,4 ГГц — дальше пробивает стены, но медленнее."),
                new("Канал", connected.Channel?.ToString() ?? "—", "Номер радиоканала, на котором работает роутер."),
                new("Стандарт", NetworkRules.RadioText(connected.RadioType, connected.BandGhz), "Поколение Wi-Fi: чем новее, тем быстрее."),
                new("Скорость связи", rate, "Скорость приёма / передачи между компьютером и роутером, Мбит/с."),
                new("Защита", connected.Authentication ?? "—", "WPA2 и WPA3 — надёжно; «Открытая» и WEP — нет."),
                new("Адаптер", adapterName, connected.Description),
            };
            WifiNote = connected.SignalPercent < NetworkRules.WeakSignalPercent
                ? "Сигнал слабый: поставьте роутер ближе и повыше или подключитесь кабелем."
                : connected.BandGhz is < 3
                    ? "Сеть 2,4 ГГц. Если роутер поддерживает 5 ГГц, подключитесь к ней — будет быстрее."
                    : NetworkRules.IsWeakWifiSecurity(connected.Authentication, connected.Cipher)
                        ? "Сеть без надёжного пароля — включите WPA2 или WPA3 в настройках роутера."
                        : null;
            return;
        }

        HasWifi = false;
        HasWifiPercent = false;
        WifiNote = null;
        WifiFields = Array.Empty<AdapterField>();
        if (WifiLocationBlocked)
        {
            WifiValue = "—";
            WifiHint = "Windows скрывает данные";
            WifiTone = NetTone.Neutral;
            WifiEmptyTitle = "Windows скрывает сведения о Wi-Fi";
            WifiEmptyText = "В Windows 11 уровень сигнала и название сети доступны программам только при включённом доступе " +
                            "к расположению. Его можно включить в «Параметры» → «Конфиденциальность» → «Расположение».";
        }
        else if (hasWifiAdapter)
        {
            WifiValue = "—";
            WifiHint = report?.Error is null ? "нет данных" : "не удалось прочитать";
            WifiTone = NetTone.Neutral;
            WifiEmptyTitle = "Не удалось прочитать сведения о Wi-Fi";
            WifiEmptyText = report?.Error ?? "Команда netsh не вернула данных о подключении.";
        }
        else
        {
            var wired = _adapters.Any(a => a.Kind == AdapterKind.Ethernet);
            WifiValue = wired ? "Кабель" : "Нет";
            WifiHint = "Wi-Fi не используется";
            WifiTone = NetTone.Neutral;
            WifiEmptyTitle = wired ? "Подключение по кабелю" : "Wi-Fi не подключён";
            WifiEmptyText = wired
                ? "Компьютер подключён к сети кабелем — это самый быстрый и стабильный способ. Wi-Fi сейчас не используется."
                : "Беспроводной адаптер не подключён к сети или отсутствует.";
        }
    }

    private static string FormatMbps(double? mbps) => mbps is null ? "—" : mbps.Value.ToString("0.#", Format.Ru);

    // ================================================================ connections and ports

    private async Task RefreshSocketsAsync()
    {
        if (_socketsLoading)
            return;
        _socketsLoading = true;
        RelayCommand.Refresh();
        try
        {
            var snapshot = await NetworkService.Safe("соединения", () => _service.ReadSocketsAsync(), CancellationToken.None);
            if (snapshot is not null)
                ApplySockets(snapshot);
            if (_checked)
                BuildFindings();
        }
        finally
        {
            _socketsLoading = false;
            RelayCommand.Refresh();
        }
    }

    internal void ApplySockets(SocketSnapshot? snapshot)
    {
        _socketsPending = false;
        if (snapshot is not null)
            _sockets = snapshot;
        if (_sockets is null)
        {
            SocketsValue = "—";
            SocketsHint = "не удалось прочитать";
            SocketsTone = NetTone.Neutral;
            SocketsUpdatedText = "";
            UpdateSocketCounts();
            return;
        }
        RebuildConnections();
        RebuildPorts();
        SocketsUpdatedText = $"Обновлено в {_sockets.Time:HH:mm:ss}";
    }

    private void RebuildConnections()
    {
        if (_sockets is null)
            return;
        var rows = NetworkRules.AggregateConnections(NetworkRules.BuildConnections(_sockets, ShowLocalConnections));
        foreach (var g in _groups)
            g.ExpandedChanged -= OnGroupExpandedChanged;
        _groups = rows
            .GroupBy(r => r.Process.GroupKey)
            .Select(g =>
            {
                var children = g.Select(r =>
                {
                    var item = new ConnectionItem(r);
                    if (item.IsPublic && ReverseLookups.TryGetValue(item.RemoteIp, out var lookup) &&
                        lookup.IsValueCreated && lookup.Value.IsCompletedSuccessfully)
                        item.HostName = lookup.Value.Result ?? ConnectionItem.NoHost;
                    return item;
                }).ToList();
                var group = new ConnectionGroupItem(children);
                group.ExpandedChanged += OnGroupExpandedChanged;
                return group;
            })
            .ToList();
        ApplyConnectionList();
        StartReverseDns();
    }

    private void RebuildPorts()
    {
        if (_sockets is null)
            return;
        _ports.ReplaceAll(NetworkRules.BuildPorts(_sockets).Select(p => new PortItem(p)));
        UpdateSocketCounts();
    }

    private bool MatchesSearch(Func<string, bool> matches)
    {
        var q = SearchText.Trim();
        return q.Length == 0 || matches(q);
    }

    private void RefreshViews()
    {
        ApplyConnectionList();
        PortsView.Refresh();
        UpdateSocketCounts();
    }

    /// <summary>Sorts the connections list by a column (the second click on the same column reverses the order).</summary>
    public void SortConnections(string key)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (_connectionSortKey == key)
        {
            _connectionSortDescending = !_connectionSortDescending;
        }
        else
        {
            _connectionSortKey = key;
            _connectionSortDescending = key == ConnectionSortKeys.Count;
        }
        ApplyConnectionList();
        ConnectionSortChanged?.Invoke();
    }

    private void OnGroupExpandedChanged(ConnectionGroupItem group)
    {
        _expandedByUser[group.GroupKey] = group.IsExpanded;
        ApplyConnectionList();
    }

    /// <summary>Filters, sorts and flattens the programs and their connections into <see cref="ConnectionRows"/>.</summary>
    private void ApplyConnectionList()
    {
        var query = SearchText.Trim();
        var searching = query.Length > 0;
        var flat = new List<ConnectionListItem>();
        var visible = new List<(ConnectionGroupItem Group, List<ConnectionItem> Children)>();
        foreach (var g in _groups)
        {
            var children = searching
                ? g.Children.Where(c => c.Matches(query)).ToList()
                : g.Children.ToList();
            if (children.Count > 0)
                visible.Add((g, children));
        }

        foreach (var (group, children) in NetworkSorting.SortGroups(visible, _connectionSortKey, _connectionSortDescending))
        {
            var expanded = _expandedByUser.TryGetValue(group.GroupKey, out var user)
                ? user
                : searching || group.Suspicious || group.Children.Count <= 2;
            group.InitExpanded(expanded);
            flat.Add(group);
            if (expanded)
                flat.AddRange(NetworkSorting.SortChildren(children, _connectionSortKey, _connectionSortDescending));
        }
        _connectionRows.ReplaceAll(flat);
        VisibleConnections = visible.Sum(v => v.Children.Sum(c => c.Count));
        UpdateSocketCounts();
    }

    private void UpdateSocketCounts()
    {
        VisiblePorts = PortsView.Cast<object>().Count();
        ConnectionsTabHeader = _sockets is null ? "Соединения" : $"Соединения · {Format.Number(VisibleConnections)}";
        PortsTabHeader = _sockets is null ? "Открытые порты" : $"Открытые порты · {Format.Number(VisiblePorts)}";
        var localOnly = _ports.Count(p => !p.IsNetwork);
        PortsHiddenNote = OnlyNetworkPorts && localOnly > 0
            ? $"Скрыто {Format.Count(localOnly, "локальный порт", "локальных порта", "локальных портов")} — к ним могут подключиться только программы на этом компьютере."
            : "";
        OnPropertyChanged(nameof(ConnectionsEmptyText));
        OnPropertyChanged(nameof(PortsEmptyText));

        if (_sockets is null)
            return;
        var all = _groups.SelectMany(g => g.Children).ToList();
        var total = all.Where(c => c.Row.Scope != AddressScope.Loopback).Sum(c => c.Count);
        var networkPorts = _ports.Count(p => p.IsNetwork);
        var risky = _ports.Count(p => p.Kind == PortKind.Risky);
        var suspicious = all.Where(c => c.IsSuspicious).Sum(c => c.Count);
        SocketsValue = Format.Number(total);
        SocketsHint = suspicious > 0
            ? Format.Count(suspicious, "подозрительное соединение", "подозрительных соединения", "подозрительных соединений")
            : risky > 0
                ? Format.Count(risky, "опасный порт открыт", "опасных порта открыто", "опасных портов открыто")
                : Format.Count(networkPorts, "порт открыт", "порта открыто", "портов открыто");
        SocketsTone = suspicious > 0 ? NetTone.Danger : risky > 0 ? NetTone.Warning : NetTone.Neutral;
    }

    // ================================================================ reverse DNS (background, never blocking)

    /// <summary>
    /// Looks up host names of public addresses in the background: rows that are on screen first (expanded programs,
    /// in list order), then the busiest programs. At most <see cref="MaxReverseLookups"/> addresses per refresh.
    /// </summary>
    private void StartReverseDns()
    {
        var visible = _connectionRows.OfType<ConnectionItem>();
        var rest = _groups.OrderByDescending(g => g.Count).SelectMany(g => g.Children);
        var pending = visible.Concat(rest).Where(c => c.IsHostPending).ToList();
        var order = pending.Select(c => c.RemoteIp).Distinct().Take(MaxReverseLookups).ToList();
        var scheduled = order.ToHashSet();
        foreach (var c in pending.Where(c => !scheduled.Contains(c.RemoteIp)))
            c.HostName = ConnectionItem.NoHost;
        _reverseTask = order.Count == 0 ? Task.CompletedTask : Task.WhenAll(order.Select(ShowHostAsync));
    }

    /// <summary>
    /// Shows the host name of an address. After <see cref="ReverseWait"/> without an answer the row shows "—" (the page
    /// never waits for DNS), and the name still appears if the lookup finishes later. Completes after the wait at most.
    /// </summary>
    private async Task ShowHostAsync(string ip)
    {
        try
        {
            var lookup = ReverseLookups.GetOrAdd(ip, key => new Lazy<Task<string?>>(() => GatedReverseLookupAsync(key))).Value;
            if (await Task.WhenAny(lookup, Task.Delay(ReverseWait)) != lookup)
            {
                Ui.Post(() => ApplyHost(ip, null));
                _ = lookup.ContinueWith(t => Ui.Post(() => ApplyHost(ip, t.Result)), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                return;
            }
            var name = await lookup;
            Ui.Post(() => ApplyHost(ip, name));
        }
        catch (Exception ex)
        {
            // Background work: a failure must never reach the dispatcher.
            Log.Warn($"Сеть: не удалось определить имя узла {ip}: {ex.Message}");
        }
    }

    /// <summary>One system lookup per address for the whole session; at most a few run at the same time.</summary>
    private static async Task<string?> GatedReverseLookupAsync(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return null;
        await ReverseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await NetworkProbes.ReverseLookupAsync(address).ConfigureAwait(false);
        }
        finally
        {
            ReverseGate.Release();
        }
    }

    /// <summary>Sets the host name on every row with this address (a late "no name" never overwrites a found one).</summary>
    private void ApplyHost(string ip, string? name)
    {
        foreach (var c in _groups.SelectMany(g => g.Children).Where(c => c.RemoteIp == ip))
        {
            if (name is not null)
                c.HostName = name;
            else if (c.IsHostPending)
                c.HostName = ConnectionItem.NoHost;
        }
    }

    // ================================================================ findings and status

    private void BuildFindings()
    {
        var snapshot = new NetworkSnapshot
        {
            Adapters = _adapters,
            Latency = _latency,
            Dns = _dns,
            Wifi = _wifi,
            Sockets = _sockets,
            UploadBytesPerSecond = _upload,
        };
        var findings = NetworkRules.BuildFindings(snapshot);
        Findings.Clear();
        foreach (var f in findings)
        {
            var (text, command, parameter) = ActionFor(f);
            Findings.Add(new FindingItem { Issue = f.Issue, ActionText = text, ActionCommand = command, ActionParameter = parameter });
        }
        OnPropertyChanged(nameof(HasFindings));

        var worst = findings.Count == 0 ? (IssueSeverity?)null : findings.Max(f => f.Issue.Severity);
        var noInternet = findings.Any(f => f.Kind == FindingKind.NoInternet);
        (StatusTitle, StatusTone) = worst switch
        {
            _ when noInternet => ("Нет доступа в интернет", NetTone.Danger),
            IssueSeverity.Critical or IssueSeverity.High => ("Найдены серьёзные проблемы", NetTone.Danger),
            IssueSeverity.Medium => ("Есть проблемы, на которые стоит посмотреть", NetTone.Warning),
            IssueSeverity.Low => ("Сеть работает, но кое-что можно улучшить", NetTone.Info),
            _ => ("Сеть в порядке", NetTone.Ok),
        };
        StatusText = DescribeState();
    }

    private string DescribeState()
    {
        var parts = new List<string>();
        if (_latency is { } l)
        {
            if (!l.InternetReachable)
                parts.Add("проверочные серверы не отвечают");
            else
                parts.Add(l.MedianMs is { } ms
                    ? $"интернет работает, задержка {LatencyMath.FormatMs(ms)}{(l.Intercepted ? " через VPN" : "")}"
                    : "интернет работает");
        }
        if (_dns?.MedianMs is { } dns)
            parts.Add($"DNS отвечает за {LatencyMath.FormatMs(dns)}");
        if (_wifi?.Connected?.SignalPercent is { } signal)
            parts.Add($"сигнал Wi-Fi {signal}%");
        else if (_adapters.Any(a => a.Kind == AdapterKind.Ethernet && a.HasGateway))
            parts.Add("подключение по кабелю");
        if (parts.Count == 0)
            return "Не удалось проверить подключение.";
        var text = string.Join(", ", parts) + ".";
        return char.ToUpper(text[0], Format.Ru) + text[1..];
    }

    private (string? Text, System.Windows.Input.ICommand? Command, object? Parameter) ActionFor(NetworkFinding f) => f.Kind switch
    {
        FindingKind.NoInternet => ("Параметры сети", OpenNetworkSettingsCommand, null),
        FindingKind.DnsFailed or FindingKind.SlowDns => ("Как сменить DNS", HowToChangeDnsCommand, null),
        FindingKind.WeakWifiSecurity or FindingKind.WeakSignal or FindingKind.Band24 => ("Параметры Wi-Fi", OpenWifiSettingsCommand, null),
        FindingKind.HighUpload => ("Показать соединения", ShowSocketsCommand, "connections:"),
        FindingKind.RiskyPort => ("Показать порт", ShowSocketsCommand, "ports:" + f.Argument),
        FindingKind.WindowsPorts => ("Показать порты", ShowSocketsCommand, "ports:"),
        FindingKind.SuspiciousConnection => ("Показать соединение", ShowSocketsCommand, "connections:" + f.Argument),
        _ => (null, null, null),
    };

    // ================================================================ row actions

    private void ShowSockets(object? parameter)
    {
        var text = parameter as string ?? "";
        var colon = text.IndexOf(':');
        var tab = colon < 0 ? text : text[..colon];
        var query = colon < 0 ? "" : text[(colon + 1)..];
        SocketsTab = tab == "ports" ? 1 : 0;
        if (tab == "ports" && query.Length > 0 && int.TryParse(query, out var port) &&
            _ports.Any(p => p.Port == port && !p.IsNetwork) && !_ports.Any(p => p.Port == port && p.IsNetwork))
            OnlyNetworkPorts = false;
        SearchText = query;
        SocketsRequested?.Invoke();
    }

    /// <summary>Opens the Processes page with the search box set to the program, so its row is the one on screen.</summary>
    private static void ShowInProcesses(object? parameter)
    {
        var process = parameter switch
        {
            ConnectionListItem c => c.Process,
            PortItem p => p.Row.Process,
            _ => null,
        };
        if (process is null || process.Pid == 0)
            return;
        if (process.Exited)
        {
            Dialogs.Info("Процесс уже завершился",
                $"Программа, которой принадлежало это соединение (PID {process.Pid}), уже закрылась — в списке процессов её нет.");
            return;
        }
        if (AppServices.Navigator is MainViewModel main &&
            main.Pages.OfType<PcCleaner.Modules.Processes.ProcessesViewModel>().FirstOrDefault() is { } processes)
        {
            // "Только подозрительные" or "Скрыть системные процессы Microsoft" would hide the program (svchost, System…).
            processes.SuspiciousOnly = false;
            processes.HideMicrosoft = false;
            processes.SearchText = process.Name;
        }
        AppServices.Navigator?.NavigateTo("Processes");
    }

    private static void CheckIp(object? parameter)
    {
        if (parameter is not ConnectionItem c)
            return;
        if (!c.IsPublic)
        {
            Dialogs.Info("Проверить IP", $"{c.RemoteIp} — адрес из локальной сети, его не нужно проверять в базах злоумышленников.");
            return;
        }
        ProcessRunner.ShellOpen("https://www.abuseipdb.com/check/" + Uri.EscapeDataString(c.RemoteIp));
    }

    private static void SearchWeb(object? parameter)
    {
        switch (parameter)
        {
            case ConnectionItem c:
                FileTools.SearchWeb(c.SearchText);
                break;
            case ConnectionGroupItem g:
                FileTools.SearchWeb(g.ProcessName);
                break;
            case PortItem p:
                FileTools.SearchWeb($"порт {p.Port} {p.ProtocolText} {p.ProcessName}");
                break;
        }
    }

    private static void Copy(object? parameter)
    {
        var text = parameter switch
        {
            ConnectionListItem c => c.CopyText,
            PortItem p => p.CopyText,
            _ => null,
        };
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
            Log.Info($"Скопировано: {text}");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn($"Не удалось скопировать в буфер обмена: {ex.Message}");
        }
    }

    private static void ShowHowToChangeDns()
    {
        Dialogs.Info("Как сменить DNS",
            "Быстрые и надёжные DNS-серверы:\n" +
            "• Cloudflare — 1.1.1.1 и 1.0.0.1\n" +
            "• Google — 8.8.8.8 и 8.8.4.4\n" +
            "• Яндекс — 77.88.8.8 и 77.88.8.1\n\n" +
            "Для этого компьютера (Windows 11):\n" +
            "1. «Параметры» → «Сеть и Интернет» → Ethernet или Wi-Fi → свойства вашего подключения.\n" +
            "2. «Назначение DNS-сервера» → «Изменить» → «Вручную» → включите IPv4.\n" +
            "3. Впишите основной и дополнительный DNS и нажмите «Сохранить».\n\n" +
            "В Windows 10: «Панель управления» → «Сетевые подключения» (ncpa.cpl) → свойства адаптера → " +
            "«IP версии 4 (TCP/IPv4)» → «Использовать следующие адреса DNS-серверов».\n\n" +
            "Для всех устройств дома — в настройках роутера (обычно раздел DHCP или «Интернет»).\n\n" +
            "Вернуть как было: в том же окне выберите «Автоматически (DHCP)».");
    }

    // ================================================================ tools (change the system after confirmation)

    private async Task FlushDnsAsync()
    {
        if (!Dialogs.Confirm("Очистить кэш DNS",
                "Windows забудет сохранённые адреса сайтов и при следующем открытии заново спросит их у DNS-сервера " +
                "(будет выполнена команда ipconfig /flushdns).\n\n" +
                "Это безопасно: ничего не удаляется, сайты просто откроются на долю секунды дольше в первый раз. " +
                "Отменять ничего не нужно — кэш заполнится сам.\n\nОчистить кэш DNS?"))
            return;
        await RunBusyAsync("Очищаю кэш DNS…", async ct =>
        {
            var (ok, message) = await _service.FlushDnsAsync(ct);
            ShowMessage(message, ok ? NetTone.Ok : NetTone.Warning);
        });
    }

    private async Task ResetNetworkAsync()
    {
        if (!Admin.RequireElevation("Сброс сетевых настроек"))
            return;
        if (!Dialogs.Confirm("Сбросить сетевые настройки",
                "Будут выполнены две команды Windows:\n" +
                "• netsh winsock reset — сброс каталога Winsock (исправляет сетевые компоненты, испорченные VPN-программами, " +
                "антивирусами или вирусами);\n" +
                "• netsh int ip reset — возврат параметров TCP/IP к стандартным.\n\n" +
                "После сброса нужно перезагрузить компьютер. Статический IP-адрес, свой DNS и настройки некоторых VPN-программ, " +
                "возможно, придётся ввести заново.\n\n" +
                "Перед сбросом создаётся точка восстановления (если она включена в настройках) — через неё можно вернуть " +
                "прежнее состояние.\n\n" +
                "Используйте сброс, только если интернет не работает, а перезагрузка роутера и компьютера не помогла. Сбросить?"))
            return;
        await RunBusyAsync("Сбрасываю сетевые настройки…", async ct =>
        {
            ReportProgress(-1, "Создаю точку восстановления…");
            var point = await RestorePoint.CreateAsync("Сброс сетевых настроек", ct);
            ReportProgress(-1, "Сбрасываю сетевые настройки…");
            var (ok, message) = await _service.ResetNetworkAsync(ct);
            var pointText = point switch
            {
                RestorePointResult.Created => "Точка восстановления создана. ",
                RestorePointResult.RecentPointExists => "Систему защищает точка восстановления, созданная за последние сутки. ",
                RestorePointResult.DisabledInSettings => "Точка восстановления не создавалась (выключено в настройках). ",
                _ => "Точку восстановления создать не удалось. ",
            };
            ShowMessage(pointText + message, ok ? NetTone.Ok : NetTone.Warning);
        });
    }

    private void ShowMessage(string text, NetTone tone)
    {
        MessageTone = tone;
        Message = text;
    }
}
