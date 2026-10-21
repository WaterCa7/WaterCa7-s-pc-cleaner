using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Security;

public sealed class SecurityViewModel : PageViewModel
{
    private const string WindowsSecurityUri = "windowsdefender:";
    private const string ProtectionHistoryUri = "windowsdefender://history";
    private const string DeviceSecurityUri = "windowsdefender://devicesecurity";
    private const string CoreIsolationUri = "windowsdefender://coreisolation";

    public const int HostsTab = 0;
    public const int ThreatsTab = 1;
    public const int ExtensionsTab = 2;
    public const int DnsTab = 3;
    public const int IfeoTab = 4;

    private readonly SecurityService _service;
    private SecurityReport? _report;
    private Task? _scanTask;
    private bool _loadedOnce;

    private bool _hasData;
    private int _score;
    private string _scoreLabel = "";
    private SecurityStatus _overallStatus = SecurityStatus.Unknown;
    private string _overallText = T._("Проверяем антивирус, брандмауэр, файл hosts и другие параметры защиты…");
    private int _okCount;
    private int _infoCount;
    private int _warningCount;
    private int _dangerCount;
    private int _unknownCount;
    private string _lastScanText = "";
    private string? _findingsText;
    private ObservableCollection<SecurityCheckCard> _checks = new();
    private ObservableCollection<SecurityCheckCard> _attentionChecks = new();
    private ObservableCollection<SecurityCheckCard> _okChecks = new();
    private ObservableCollection<HostsEntry> _hostsEntries = new();
    private ObservableCollection<ThreatDetection> _threats = new();
    private ObservableCollection<BrowserExtension> _extensions = new();
    private List<BrowserExtension> _allExtensions = new();
    private ObservableCollection<DnsServerInfo> _dnsServers = new();
    private ObservableCollection<IfeoEntry> _ifeoEntries = new();
    private bool _onlySuspiciousExtensions;
    private int _selectedHostsCount;
    private int _selectedDetailsTab;
    private string? _message;
    private SecurityStatus _messageStatus = SecurityStatus.Ok;
    private string _hostsPath = "";
    private bool _hostsReadOnly;

    public SecurityViewModel(SecurityService service)
    {
        _service = service;

        RescanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        OpenWindowsSecurityCommand = new RelayCommand(() => ProcessRunner.ShellOpen(WindowsSecurityUri));
        OpenProtectionHistoryCommand = new RelayCommand(() => ProcessRunner.ShellOpen(ProtectionHistoryUri));
        QuickScanCommand = new AsyncRelayCommand(() => DefenderScanAsync(full: false), () => !IsBusy);
        FullScanCommand = new AsyncRelayCommand(() => DefenderScanAsync(full: true), () => !IsBusy);
        UpdateSignaturesCommand = new AsyncRelayCommand(UpdateSignaturesAsync, () => !IsBusy);
        EnableRealtimeCommand = new AsyncRelayCommand(EnableRealtimeAsync, () => !IsBusy);
        EnableFirewallCommand = new AsyncRelayCommand(EnableFirewallAsync, () => !IsBusy);
        OpenFirewallSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("windowsdefender://network"));
        EnableUacCommand = new AsyncRelayCommand(EnableUacAsync, () => !IsBusy);
        OpenUacSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("UserAccountControlSettings.exe"));
        RemoveHostsLinesCommand = new AsyncRelayCommand(RemoveHostsLinesAsync, () => !IsBusy && SelectedHostsCount > 0);
        OpenHostsInNotepadCommand = new RelayCommand(OpenHostsInNotepad);
        OpenHostsFolderCommand = new RelayCommand(() => ProcessRunner.ShowInExplorer(HostsPath));
        RestoreWinlogonCommand = new AsyncRelayCommand(RestoreWinlogonAsync, () => !IsBusy);
        RemoveIfeoCommand = new AsyncRelayCommand(p => RemoveIfeoAsync(p as IfeoEntry), p => !IsBusy && p is IfeoEntry);
        DisableProxyCommand = new AsyncRelayCommand(DisableProxyAsync, () => !IsBusy);
        OpenProxySettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network-proxy"));
        OpenNetworkSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network"));
        OpenWindowsUpdateCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:windowsupdate"));
        OpenSmartScreenSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("windowsdefender://appbrowser"));
        OpenRdpSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:remotedesktop"));
        HowToRemoveExtensionCommand = new RelayCommand(ShowHowToRemoveExtension);
        SearchExtensionCommand = new RelayCommand(p =>
        {
            if (p is BrowserExtension e)
                FileTools.SearchWeb(T.F("{0} расширение {1}", e.Name, e.Browser));
        }, p => p is BrowserExtension);
        OpenExtensionStoreCommand = new RelayCommand(p =>
        {
            if (p is BrowserExtension { StoreUrl: { } url })
                ProcessRunner.ShellOpen(url);
        }, p => p is BrowserExtension);
        OpenExtensionFolderCommand = new RelayCommand(p =>
        {
            if (p is BrowserExtension e && !string.IsNullOrEmpty(e.FolderPath))
                ProcessRunner.ShowInExplorer(e.FolderPath);
        }, p => p is BrowserExtension { FolderPath.Length: > 0 });
        SearchThreatCommand = new RelayCommand(p =>
        {
            if (p is ThreatDetection t)
                FileTools.SearchWeb(t.Name);
        }, p => p is ThreatDetection);
        SearchTextCommand = new RelayCommand(p =>
        {
            var text = p switch
            {
                DnsServerInfo d => d.Address,
                IfeoEntry i => i.Debugger,
                HostsEntry h => h.Hostnames.FirstOrDefault() ?? h.Address,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
                FileTools.SearchWeb(text);
        }, p => p is DnsServerInfo or IfeoEntry or HostsEntry);
        ShowDetailsCommand = new RelayCommand(p =>
        {
            if (p is int tab || (p is string s && int.TryParse(s, out tab)))
                ShowDetails(tab);
        });
        DismissMessageCommand = new RelayCommand(() => Message = null);
        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !Admin.IsElevated);
        RemoveThreatsCommand = new AsyncRelayCommand(RemoveThreatsAsync, () => !IsBusy && HasActiveThreats);
        OpenProgramsCommand = new RelayCommand(() => AppServices.Navigator?.NavigateTo("Programs"));
        DisableSmb1Command = new AsyncRelayCommand(DisableSmb1Async, () => !IsBusy);
        OpenWindowsFeaturesCommand = new RelayCommand(() => ProcessRunner.ShellOpen("optionalfeatures.exe"));
        OpenDeviceSecurityCommand = new RelayCommand(() => ProcessRunner.ShellOpen(DeviceSecurityUri));
        OpenCoreIsolationCommand = new RelayCommand(() => ProcessRunner.ShellOpen(CoreIsolationUri));
        SecureBootHowToCommand = new RelayCommand(() => Dialogs.Info(T._("Как включить Secure Boot"), SecurityRules.SecureBootHowTo));
        TpmHowToCommand = new RelayCommand(() => Dialogs.Info(T._("Как включить TPM"), SecurityRules.TpmHowTo));
        OpenEncryptionSettingsCommand = new RelayCommand(OpenEncryptionSettings);
    }

    public override string Title => T._("Безопасность");

    public override string Subtitle => T._("Антивирус, брандмауэр, файл hosts, SMBv1, Secure Boot, TPM, шифрование и другие проверки защиты");

    public override string IconGlyph => "";

    /// <summary>Raised when a card asks to show its detailed list (the view scrolls to it).</summary>
    public event Action? DetailsRequested;

    // ===================== Summary =====================

    public bool HasData
    {
        get => _hasData;
        private set => Set(ref _hasData, value);
    }

    public int Score
    {
        get => _score;
        private set => Set(ref _score, value);
    }

    public string ScoreLabel
    {
        get => _scoreLabel;
        private set => Set(ref _scoreLabel, value);
    }

    public SecurityStatus OverallStatus
    {
        get => _overallStatus;
        private set => Set(ref _overallStatus, value);
    }

    public string OverallText
    {
        get => _overallText;
        private set => Set(ref _overallText, value);
    }

    public int OkCount
    {
        get => _okCount;
        private set => Set(ref _okCount, value);
    }

    public int InfoCount
    {
        get => _infoCount;
        private set => Set(ref _infoCount, value);
    }

    public int WarningCount
    {
        get => _warningCount;
        private set => Set(ref _warningCount, value);
    }

    public int DangerCount
    {
        get => _dangerCount;
        private set => Set(ref _dangerCount, value);
    }

    public int UnknownCount
    {
        get => _unknownCount;
        private set => Set(ref _unknownCount, value);
    }

    public string OkCountText => T.F("{0} в порядке", OkCount);

    public string InfoCountText => T.F("{0} — к сведению", InfoCount);

    public string WarningCountText => T.F("{0} — внимание", WarningCount);

    public string DangerCountText => T.F("{0} — опасно", DangerCount);

    public string UnknownCountText => T.F("{0} — нет данных", UnknownCount);

    public string LastScanText
    {
        get => _lastScanText;
        private set => Set(ref _lastScanText, value);
    }

    public bool IsAdmin => Admin.IsElevated;

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public SecurityStatus MessageStatus
    {
        get => _messageStatus;
        private set => Set(ref _messageStatus, value);
    }

    // ===================== Lists =====================

    /// <summary>All cards, most urgent first.</summary>
    public ObservableCollection<SecurityCheckCard> Checks
    {
        get => _checks;
        private set => Set(ref _checks, value);
    }

    /// <summary>Cards worth a look: dangers, warnings, checks without data and notes.</summary>
    public ObservableCollection<SecurityCheckCard> AttentionChecks
    {
        get => _attentionChecks;
        private set => Set(ref _attentionChecks, value);
    }

    /// <summary>Cards that are fine (shown compact, below the others).</summary>
    public ObservableCollection<SecurityCheckCard> OkChecks
    {
        get => _okChecks;
        private set => Set(ref _okChecks, value);
    }

    public bool HasAttentionChecks => AttentionChecks.Count > 0;

    public bool HasOkChecks => OkChecks.Count > 0;

    /// <summary>
    /// "Стоит посмотреть · 6 проблем и 1 совет": counted like the dashboard tile and the log line. Without problems and
    /// tips (only "к сведению" / "нет данных" cards) there is no count, so the header never says "проблем нет" above cards.
    /// </summary>
    public string AttentionHeader => _findingsText is null ? T._("Стоит посмотреть") : T.F("Стоит посмотреть · {0}", _findingsText);

    public string OkHeader => T.F("В порядке · {0}", OkChecks.Count);

    public ObservableCollection<HostsEntry> HostsEntries
    {
        get => _hostsEntries;
        private set => Set(ref _hostsEntries, value);
    }

    public string HostsPath
    {
        get => _hostsPath;
        private set => Set(ref _hostsPath, value);
    }

    public bool HostsReadOnly
    {
        get => _hostsReadOnly;
        private set => Set(ref _hostsReadOnly, value);
    }

    public int SelectedHostsCount
    {
        get => _selectedHostsCount;
        private set
        {
            if (Set(ref _selectedHostsCount, value))
            {
                OnPropertyChanged(nameof(RemoveHostsText));
                RelayCommand.Refresh();
            }
        }
    }

    public string RemoveHostsText => SelectedHostsCount > 0
        ? T.F("Удалить выбранные ({0})", SelectedHostsCount)
        : T._("Удалить выбранные строки");

    public ObservableCollection<ThreatDetection> Threats
    {
        get => _threats;
        private set => Set(ref _threats, value);
    }

    public ObservableCollection<BrowserExtension> Extensions
    {
        get => _extensions;
        private set => Set(ref _extensions, value);
    }

    public bool OnlySuspiciousExtensions
    {
        get => _onlySuspiciousExtensions;
        set
        {
            if (Set(ref _onlySuspiciousExtensions, value))
                ApplyExtensionFilter();
        }
    }

    public ObservableCollection<DnsServerInfo> DnsServers
    {
        get => _dnsServers;
        private set => Set(ref _dnsServers, value);
    }

    public ObservableCollection<IfeoEntry> IfeoEntries
    {
        get => _ifeoEntries;
        private set => Set(ref _ifeoEntries, value);
    }

    public bool HasIfeo => IfeoEntries.Count > 0;

    public int SelectedDetailsTab
    {
        get => _selectedDetailsTab;
        set => Set(ref _selectedDetailsTab, value);
    }

    public string HostsTabHeader => T.F("Файл hosts ({0})", HostsEntries.Count);

    public string ThreatsTabHeader => T.F("Найденные угрозы ({0})", Threats.Count);

    /// <summary>Defender works and has detections it did not neutralize ("Обезвредить угрозы" is shown).</summary>
    public bool HasActiveThreats => _report?.CanRemoveThreats == true;

    public string RemoveThreatsText => T.F("Обезвредить угрозы ({0})", _report?.ActiveThreats.Count ?? 0);

    public string ExtensionsTabHeader => T.F("Расширения ({0})", _allExtensions.Count);

    public string DnsTabHeader => $"DNS ({DnsServers.Count})";

    public string IfeoTabHeader => T.F("Перехваты ({0})", IfeoEntries.Count);

    public string ThreatsEmptyText => _report?.Defender is null && _report is not null
        ? T._("Список угроз Защитника Windows недоступен — возможно, используется другой антивирус.")
        : T._("Защитник Windows ничего не находил — отлично!");

    public string ExtensionsEmptyText => OnlySuspiciousExtensions && _allExtensions.Count > 0
        ? T._("Подозрительных расширений нет — все расширения в порядке.")
        : _report?.BrowsersFound == 0
            ? T._("Поддерживаемые браузеры не найдены.")
            : T._("В браузерах нет установленных расширений.");

    // ===================== Commands =====================

    public ICommand RescanCommand { get; }
    public ICommand OpenWindowsSecurityCommand { get; }
    public ICommand OpenProtectionHistoryCommand { get; }
    public ICommand QuickScanCommand { get; }
    public ICommand FullScanCommand { get; }
    public ICommand UpdateSignaturesCommand { get; }
    public ICommand EnableRealtimeCommand { get; }
    public ICommand EnableFirewallCommand { get; }
    public ICommand OpenFirewallSettingsCommand { get; }
    public ICommand EnableUacCommand { get; }
    public ICommand OpenUacSettingsCommand { get; }
    public ICommand RemoveHostsLinesCommand { get; }
    public ICommand OpenHostsInNotepadCommand { get; }
    public ICommand OpenHostsFolderCommand { get; }
    public ICommand RestoreWinlogonCommand { get; }
    public ICommand RemoveIfeoCommand { get; }
    public ICommand DisableProxyCommand { get; }
    public ICommand OpenProxySettingsCommand { get; }
    public ICommand OpenNetworkSettingsCommand { get; }
    public ICommand OpenWindowsUpdateCommand { get; }
    public ICommand OpenSmartScreenSettingsCommand { get; }
    public ICommand OpenRdpSettingsCommand { get; }
    public ICommand HowToRemoveExtensionCommand { get; }
    public ICommand SearchExtensionCommand { get; }
    public ICommand OpenExtensionStoreCommand { get; }
    public ICommand OpenExtensionFolderCommand { get; }
    public ICommand SearchThreatCommand { get; }
    public ICommand SearchTextCommand { get; }
    public ICommand ShowDetailsCommand { get; }
    public ICommand DismissMessageCommand { get; }
    public ICommand RestartAsAdminCommand { get; }
    public ICommand RemoveThreatsCommand { get; }
    public ICommand OpenProgramsCommand { get; }
    public ICommand DisableSmb1Command { get; }
    public ICommand OpenWindowsFeaturesCommand { get; }
    public ICommand OpenDeviceSecurityCommand { get; }
    public ICommand OpenCoreIsolationCommand { get; }
    public ICommand SecureBootHowToCommand { get; }
    public ICommand TpmHowToCommand { get; }
    public ICommand OpenEncryptionSettingsCommand { get; }

    // ===================== Navigation / scan =====================

    public override Task OnNavigatedToAsync()
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _ = ScanAsync();
        }
        else if (_report is not null && !IsBusy && DateTime.Now - _report.ScannedAt > TimeSpan.FromMinutes(5))
        {
            _ = ScanAsync();
        }
        return Task.CompletedTask;
    }

    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() => Ui.Post(() =>
        {
            if (CancelCommand.CanExecute(null))
                CancelCommand.Execute(null);
        }));
        if (_scanTask is { IsCompleted: false })
        {
            await _scanTask;
            return;
        }
        await ScanAsync();
    }

    private Task ScanAsync()
    {
        if (_scanTask is { IsCompleted: false })
            return _scanTask;
        _scanTask = RunBusyAsync(T._("Проверяем защиту компьютера…"), async ct =>
        {
            var report = await _service.ScanAsync((p, text) => ReportProgress(p, text), ct);
            Ui.Invoke(() => Apply(report));
        });
        return _scanTask;
    }

    private void Apply(SecurityReport report)
    {
        _report = report;

        var cards = BuildCards(report);
        Checks = new ObservableCollection<SecurityCheckCard>(cards);
        AttentionChecks = new ObservableCollection<SecurityCheckCard>(cards.Where(c => c.Status != SecurityStatus.Ok));
        OkChecks = new ObservableCollection<SecurityCheckCard>(cards.Where(c => c.Status == SecurityStatus.Ok));

        foreach (var old in HostsEntries)
            old.PropertyChanged -= OnHostsEntryChanged;
        var hosts = report.Hosts.Entries.OrderByDescending(e => e.Risk.Rank()).ThenBy(e => e.LineIndex).ToList();
        foreach (var e in hosts)
        {
            e.IsSelected = e.Risk == SecurityStatus.Danger;
            e.PropertyChanged += OnHostsEntryChanged;
        }
        HostsEntries = new ObservableCollection<HostsEntry>(hosts);
        HostsPath = report.Hosts.Path;
        HostsReadOnly = report.Hosts.IsReadOnly;
        UpdateSelectedHosts();

        Threats = new ObservableCollection<ThreatDetection>(report.Threats);
        _allExtensions = report.Extensions
            .OrderByDescending(e => e.Risk.Rank())
            .ThenBy(e => e.Browser, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyExtensionFilter();
        DnsServers = new ObservableCollection<DnsServerInfo>(report.Dns);
        IfeoEntries = new ObservableCollection<IfeoEntry>(report.Ifeo.OrderByDescending(e => e.Risk.Rank()));
        if (SelectedDetailsTab == IfeoTab && IfeoEntries.Count == 0)
            SelectedDetailsTab = HostsTab;

        var checks = report.Checks;
        OkCount = checks.Count(c => c.Status == SecurityStatus.Ok);
        InfoCount = checks.Count(c => c.Status == SecurityStatus.Info);
        WarningCount = checks.Count(c => c.Status == SecurityStatus.Warning);
        DangerCount = checks.Count(c => c.Status == SecurityStatus.Danger);
        UnknownCount = checks.Count(c => c.Status == SecurityStatus.Unknown);
        Score = SecurityRules.Score(checks);
        var (problems, tips) = _service.CountFindings(report);
        _findingsText = problems + tips > 0 ? SecurityRules.FindingsText(problems, tips) : null;
        ScoreLabel = Score >= 90 ? T._("Отлично") : Score >= 75 ? T._("Хорошо") : Score >= 50 ? T._("Есть проблемы") : T._("Плохо");
        OverallStatus = DangerCount > 0 ? SecurityStatus.Danger
            : WarningCount > 0 ? SecurityStatus.Warning
            : UnknownCount > 0 ? SecurityStatus.Info
            : SecurityStatus.Ok;
        OverallText = DangerCount > 0
            ? T._("Найдены серьёзные проблемы защиты. Начните с карточек, отмеченных красным.")
            : WarningCount > 0
                ? T._("Защита работает, но кое-что стоит поправить — посмотрите жёлтые карточки.")
                : UnknownCount > 0
                    ? T.F("Явных проблем не найдено, но {0} выполнить не удалось (серые карточки) — их стоит посмотреть в «Безопасности Windows».", Format.Count(UnknownCount, "проверку", "проверки", "проверок"))
                    : T._("Всё в порядке: компьютер хорошо защищён.");
        LastScanText = T.F("Проверено в {0:HH:mm}", report.ScannedAt);
        HasData = true;

        foreach (var name in new[]
                 {
                     nameof(OkCountText), nameof(InfoCountText), nameof(WarningCountText), nameof(DangerCountText),
                     nameof(UnknownCountText), nameof(HasAttentionChecks), nameof(HasOkChecks), nameof(AttentionHeader),
                     nameof(OkHeader),
                     nameof(HostsTabHeader), nameof(ThreatsTabHeader), nameof(ExtensionsTabHeader), nameof(DnsTabHeader),
                     nameof(IfeoTabHeader), nameof(HasIfeo), nameof(ThreatsEmptyText), nameof(ExtensionsEmptyText),
                     nameof(HasActiveThreats), nameof(RemoveThreatsText),
                 })
            OnPropertyChanged(name);
    }

    private void OnHostsEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostsEntry.IsSelected))
            UpdateSelectedHosts();
    }

    private void UpdateSelectedHosts() => SelectedHostsCount = HostsEntries.Count(e => e.IsSelected);

    private void ApplyExtensionFilter()
    {
        Extensions = new ObservableCollection<BrowserExtension>(OnlySuspiciousExtensions
            ? _allExtensions.Where(e => e.Risk != SecurityStatus.Ok)
            : _allExtensions);
        OnPropertyChanged(nameof(ExtensionsEmptyText));
    }

    private void ShowDetails(int tab)
    {
        SelectedDetailsTab = tab;
        DetailsRequested?.Invoke();
    }

    // ===================== Cards =====================

    /// <summary>
    /// Cards in order of urgency: dangers, warnings, checks without data, notes, then everything that is fine.
    /// Cards with the same status keep the order of importance (antivirus first).
    /// </summary>
    internal IReadOnlyList<SecurityCheckCard> BuildCards(SecurityReport report)
        => report.Checks
            .OrderByDescending(c => c.Status.Rank())
            .ThenBy(c => SecurityCheckIds.Importance(c.Id))
            .Select(c => BuildCard(c, report))
            .ToList();

    private SecurityCheckCard BuildCard(SecurityCheckResult r, SecurityReport report)
    {
        var actions = new List<CardAction>();
        var bad = r.Status is SecurityStatus.Danger or SecurityStatus.Warning;
        switch (r.Id)
        {
            case SecurityCheckIds.Antivirus:
            {
                var d = report.Defender;
                var realtimeOff = d is not null && !report.HasThirdPartyAntivirus && d.AntivirusEnabled && !d.RealTimeEnabled &&
                                  !d.PolicyDisablesRealtime;
                if (realtimeOff)
                    actions.Add(new CardAction(T._("Включить защиту"), EnableRealtimeCommand, CardActionKind.Primary,
                        T._("Включить защиту в реальном времени Защитника Windows (нужны права администратора)")));
                // Scans and updates need a working Defender engine (on or in passive mode next to another antivirus).
                if (report.MpCmdRunAvailable && d is not null && d.ServiceEnabled && (d.AntivirusEnabled || d.IsPassive))
                {
                    actions.Add(new CardAction(T._("Быстрая проверка"), QuickScanCommand,
                        realtimeOff ? CardActionKind.Normal : CardActionKind.Primary,
                        T._("Проверить места, где чаще всего прячутся вирусы (5–15 минут)")));
                    actions.Add(new CardAction(T._("Полная проверка"), FullScanCommand, CardActionKind.Normal,
                        T._("Проверить все файлы на всех дисках — может занять несколько часов")));
                    actions.Add(new CardAction(T._("Обновить базы"), UpdateSignaturesCommand, CardActionKind.Normal,
                        T._("Скачать свежие антивирусные базы Защитника Windows")));
                }
                if (report.CanRemoveThreats)
                    actions.Insert(0, new CardAction(T._("Обезвредить угрозы"), RemoveThreatsCommand, CardActionKind.Danger,
                        T._("Защитник Windows применит к найденным угрозам своё действие — обычно карантин (нужны права администратора)")));
                if (report.Threats.Count > 0)
                    actions.Add(new CardAction(T._("Найденные угрозы"), ShowDetailsCommand, CardActionKind.Normal,
                        T._("Показать список угроз, которые находил Защитник Windows"), ThreatsTab));
                if (report.ActiveAntivirusNames.Count >= 2)
                    actions.Add(new CardAction(T._("Открыть «Программы»"), OpenProgramsCommand, CardActionKind.Normal,
                        T._("Удалить лишний антивирус в разделе «Программы»")));
                if (actions.Count == 0)
                    actions.Add(new CardAction(T._("Безопасность Windows"), OpenWindowsSecurityCommand, CardActionKind.Normal,
                        T._("Открыть приложение «Безопасность Windows»")));
                break;
            }
            case SecurityCheckIds.Firewall:
                if (bad && !report.FirewallOffOnlyByPolicy)
                    actions.Add(new CardAction(T._("Включить брандмауэр"), EnableFirewallCommand, CardActionKind.Primary,
                        T._("Включить брандмауэр Windows для всех типов сетей (нужны права администратора)")));
                actions.Add(new CardAction(T._("Настройки"), OpenFirewallSettingsCommand, CardActionKind.Normal,
                    T._("«Безопасность Windows» → «Брандмауэр и защита сети»")));
                break;
            case SecurityCheckIds.Uac:
                if (bad)
                    actions.Add(new CardAction(r.Status == SecurityStatus.Danger ? T._("Включить UAC") : T._("Включить запросы"),
                        EnableUacCommand, CardActionKind.Primary,
                        T._("Вернуть стандартные настройки UAC (нужны права администратора)")));
                actions.Add(new CardAction(T._("Настройки UAC"), OpenUacSettingsCommand, CardActionKind.Normal,
                    T._("Открыть окно «Параметры управления учётными записями»")));
                break;
            case SecurityCheckIds.Hosts:
                if (report.Hosts.Entries.Count > 0)
                    actions.Add(new CardAction(T._("Показать записи"), ShowDetailsCommand,
                        bad ? CardActionKind.Primary : CardActionKind.Normal, T._("Список строк файла hosts с оценкой"), HostsTab));
                if (report.Hosts.Exists)
                    actions.Add(new CardAction(T._("Открыть в Блокноте"), OpenHostsInNotepadCommand, CardActionKind.Normal,
                        T._("Открыть файл hosts для просмотра")));
                break;
            case SecurityCheckIds.Winlogon:
                if (r.Status == SecurityStatus.Danger)
                    actions.Add(new CardAction(T._("Восстановить по умолчанию"), RestoreWinlogonCommand, CardActionKind.Danger,
                        T._("Вернуть Shell = explorer.exe и Userinit = userinit.exe (с резервной копией реестра)")));
                break;
            case SecurityCheckIds.Ifeo:
                if (report.Ifeo.Count == 1 && bad)
                    actions.Add(new CardAction(T._("Удалить перехват"), RemoveIfeoCommand, CardActionKind.Danger,
                        T._("Удалить значение Debugger (с резервной копией реестра)"), report.Ifeo[0]));
                if (report.Ifeo.Count > 0)
                    actions.Add(new CardAction(T._("Показать список"), ShowDetailsCommand, CardActionKind.Normal,
                        T._("Все найденные перехваты запуска"), IfeoTab));
                break;
            case SecurityCheckIds.Proxy:
                if (bad)
                    actions.Add(new CardAction(T._("Отключить прокси"), DisableProxyCommand, CardActionKind.Primary,
                        T._("Выключить прокси и скрипт настройки (с резервной копией реестра)")));
                actions.Add(new CardAction(T._("Настройки прокси"), OpenProxySettingsCommand, CardActionKind.Normal,
                    T._("«Параметры» → «Сеть и Интернет» → «Прокси-сервер»")));
                break;
            case SecurityCheckIds.Dns:
                if (report.Dns.Count > 0)
                    actions.Add(new CardAction(T._("Показать серверы"), ShowDetailsCommand, CardActionKind.Normal,
                        T._("DNS-серверы всех подключений"), DnsTab));
                actions.Add(new CardAction(T._("Настройки сети"), OpenNetworkSettingsCommand, CardActionKind.Normal,
                    T._("«Параметры» → «Сеть и Интернет»")));
                break;
            case SecurityCheckIds.Updates:
                actions.Add(new CardAction(T._("Открыть Центр обновления"), OpenWindowsUpdateCommand,
                    bad ? CardActionKind.Primary : CardActionKind.Normal, T._("Открыть Центр обновления Windows")));
                break;
            case SecurityCheckIds.SmartScreen:
                actions.Add(new CardAction(T._("Настройки"), OpenSmartScreenSettingsCommand,
                    bad ? CardActionKind.Primary : CardActionKind.Normal,
                    T._("«Безопасность Windows» → «Управление приложениями/браузером»")));
                break;
            case SecurityCheckIds.Rdp:
                actions.Add(new CardAction(T._("Настройки"), OpenRdpSettingsCommand,
                    bad ? CardActionKind.Primary : CardActionKind.Normal, T._("«Параметры» → «Система» → «Удалённый рабочий стол»")));
                break;
            case SecurityCheckIds.Extensions:
                if (report.Extensions.Count > 0)
                    actions.Add(new CardAction(T._("Показать список"), ShowDetailsCommand,
                        bad ? CardActionKind.Primary : CardActionKind.Normal, T._("Все расширения браузеров с оценкой"), ExtensionsTab));
                if (bad)
                    actions.Add(new CardAction(T._("Как удалить"), HowToRemoveExtensionCommand, CardActionKind.Normal,
                        T._("Инструкция по удалению расширений в каждом браузере")));
                break;
            case SecurityCheckIds.Smb1:
                if (bad)
                    actions.Add(new CardAction(T._("Отключить SMBv1"), DisableSmb1Command, CardActionKind.Primary,
                        T._("Отключить компонент Windows «SMB 1.0» (нужны права администратора, затем — перезагрузка)")));
                actions.Add(new CardAction(T._("Компоненты Windows"), OpenWindowsFeaturesCommand, CardActionKind.Normal,
                    T._("Открыть окно «Включение или отключение компонентов Windows»")));
                break;
            case SecurityCheckIds.SecureBoot:
                if (bad)
                    actions.Add(new CardAction(T._("Как включить"), SecureBootHowToCommand, CardActionKind.Primary,
                        T._("Пошаговая инструкция для BIOS/UEFI")));
                actions.Add(new CardAction(T._("Безопасность устройства"), OpenDeviceSecurityCommand, CardActionKind.Normal,
                    T._("«Безопасность Windows» → «Безопасность устройства»")));
                break;
            case SecurityCheckIds.Tpm:
                if (bad)
                    actions.Add(new CardAction(T._("Как включить"), TpmHowToCommand, CardActionKind.Primary,
                        T._("Где включить fTPM (AMD) или PTT (Intel) в BIOS/UEFI")));
                actions.Add(new CardAction(T._("Безопасность устройства"), OpenDeviceSecurityCommand, CardActionKind.Normal,
                    T._("«Безопасность Windows» → «Безопасность устройства» → «Обработчик безопасности»")));
                break;
            case SecurityCheckIds.BitLocker:
                if (report.BitLocker.Encryption is not (DriveEncryption.NotSupported or DriveEncryption.Unknown))
                    actions.Add(new CardAction(T._("Параметры шифрования"), OpenEncryptionSettingsCommand,
                        bad ? CardActionKind.Primary : CardActionKind.Normal,
                        report.BitLocker.IsHomeEdition
                            ? T._("«Параметры» → «Конфиденциальность и защита» → «Шифрование устройства»")
                            : T._("«Панель управления» → «Шифрование диска BitLocker»")));
                break;
            case SecurityCheckIds.MemoryIntegrity:
                actions.Add(new CardAction(T._("Изоляция ядра"), OpenCoreIsolationCommand,
                    r.Status == SecurityStatus.Info && !report.DeviceGuard.HvciConfigured ? CardActionKind.Primary : CardActionKind.Normal,
                    T._("«Безопасность Windows» → «Безопасность устройства» → «Изоляция ядра»")));
                break;
        }

        return new SecurityCheckCard
        {
            Id = r.Id,
            Title = SecurityCheckIds.Title(r.Id),
            Glyph = SecurityCheckIds.Glyph(r.Id),
            Status = r.Status,
            State = r.State,
            Details = r.Details,
            Explanation = SecurityCheckIds.Explanation(r.Id),
            Actions = actions,
            ColumnSpan = r.Id == SecurityCheckIds.Antivirus ? 2 : 1,
            // Cards that are fine keep the page short: their details move into a tooltip. The antivirus card keeps
            // its facts (scan and update dates) visible.
            IsCompact = r.Status == SecurityStatus.Ok && r.Id != SecurityCheckIds.Antivirus,
        };
    }

    // ===================== Actions =====================

    private async Task RunActionAsync(string busyText, string title, Func<CancellationToken, Task<SecurityActionResult>> action)
    {
        SecurityActionResult? result = null;
        await RunBusyAsync(busyText, async ct => result = await action(ct));
        if (result is null)
            return;
        ShowResult(title, result);
        await ScanAsync();
    }

    private void ShowResult(string title, SecurityActionResult r)
    {
        Message = r.Message.Split("\n\n")[0];
        MessageStatus = r.Success ? SecurityStatus.Ok
            : r.ThreatsFound ? SecurityStatus.Danger
            : r.Cancelled ? SecurityStatus.Info
            : SecurityStatus.Warning;
        if (r.Cancelled)
            return;
        if (r.ThreatsFound)
        {
            // Opening the journal changes nothing: a neutral question, not a confirmation.
            if (Dialogs.Ask(title, r.Message + "\n\n" + T._("Открыть журнал защиты сейчас?")))
                ProcessRunner.ShellOpen(ProtectionHistoryUri);
        }
        else if (r.Success)
        {
            Dialogs.Info(title, r.Message);
        }
        else
        {
            Dialogs.Error(title, r.Message);
        }
    }

    private async Task DefenderScanAsync(bool full)
    {
        var title = full ? T._("Полная проверка") : T._("Быстрая проверка");
        var question = full
            ? T._("Защитник Windows проверит все файлы на всех дисках. Это может занять несколько часов, компьютер в это время может работать медленнее.\n\n")
            : T._("Защитник Windows проверит места, где чаще всего прячутся вирусы: запущенные программы, автозагрузку и системные папки. Обычно это 5–15 минут.\n\n");
        question += T._("Найденные угрозы Защитник Windows обезвредит сам (их можно восстановить в «Безопасность Windows» → «Журнал защиты»). Проверку можно остановить кнопкой «Отмена».\n\nНачать проверку?");
        if (!Dialogs.Confirm(title, question))
            return;

        var text = full ? T._("Полная проверка Защитником Windows…") : T._("Быстрая проверка Защитником Windows…");
        SecurityActionResult? result = null;
        await RunBusyAsync(text, async ct =>
        {
            var sw = Stopwatch.StartNew();
            var task = _service.RunDefenderScanAsync(full, ct);
            while (!task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(1000));
                ReportProgress(-1, T.F("{0} Прошло {1}.", text, Format.Duration(sw.Elapsed)));
            }
            result = await task;
        });
        if (result is null)
            return;
        ShowResult(title, result);
        await ScanAsync();
    }

    private async Task UpdateSignaturesAsync()
    {
        if (!Dialogs.Confirm(T._("Обновить базы"),
                T._("Защитник Windows скачает свежие антивирусные базы с серверов Microsoft. Обычно это 1–3 минуты.\n\nОбновить базы?")))
            return;
        await RunActionAsync(T._("Обновляем антивирусные базы…"), T._("Обновление баз"), _service.UpdateSignaturesAsync);
    }

    private async Task EnableRealtimeAsync()
    {
        if (!Admin.RequireElevation(T._("Включение защиты в реальном времени")))
            return;
        if (!Dialogs.Confirm(T._("Включить защиту"),
                T._("Будет включена защита в реальном времени Защитника Windows (Set-MpPreference -DisableRealtimeMonitoring $false).\n\nВыключить её снова можно в «Безопасность Windows» → «Защита от вирусов и угроз» → «Управление настройками».\n\nВключить?")))
            return;
        await RunActionAsync(T._("Включаем защиту в реальном времени…"), T._("Защита в реальном времени"),
            _service.EnableRealtimeProtectionAsync);
    }

    private async Task EnableFirewallAsync()
    {
        if (!Admin.RequireElevation(T._("Включение брандмауэра")))
            return;
        if (!Dialogs.Confirm(T._("Включить брандмауэр"),
                T._("Будет включён брандмауэр Windows для всех типов сетей — доменной, частной и общедоступной (команда netsh advfirewall set allprofiles state on).\n\nЕсли какой-то программе понадобится доступ к сети, Windows спросит разрешение. Выключить брандмауэр снова можно в «Безопасность Windows» → «Брандмауэр и защита сети».\n\nВключить?")))
            return;
        await RunActionAsync(T._("Включаем брандмауэр…"), T._("Брандмауэр"), _service.EnableFirewallAsync);
    }

    private async Task EnableUacAsync()
    {
        if (!Admin.RequireElevation(T._("Включение UAC")))
            return;
        var (luaOff, consentOff) = SecurityService.UacChangesNeeded(_report?.Uac.EnableLua, _report?.Uac.ConsentPromptBehaviorAdmin);
        var changes = new List<string>();
        if (luaOff)
            changes.Add(T._("• EnableLUA = 1 (UAC включён)"));
        if (consentOff)
            changes.Add(T._("• ConsentPromptBehaviorAdmin = 5 (спрашивать разрешение)"));
        if (changes.Count == 0)
            return;
        if (!Dialogs.Confirm(T._("Контроль учётных записей"),
                T.F("Будут восстановлены стандартные настройки UAC:\n{0}\n\nПеред изменением раздел реестра сохраняется в резервную копию — по ней можно вернуть всё как было.", string.Join("\n", changes)) +
                (luaOff ? "\n\n" + T._("После включения UAC нужно перезагрузить компьютер.") : "") + "\n\n" + T._("Продолжить?")))
            return;
        await RunActionAsync(T._("Включаем UAC…"), T._("Контроль учётных записей"), _service.EnableUacAsync);
    }

    private async Task RemoveHostsLinesAsync()
    {
        var selected = HostsEntries.Where(e => e.IsSelected).OrderBy(e => e.LineIndex).ToList();
        if (selected.Count == 0)
        {
            Dialogs.Info(T._("Файл hosts"), T._("Отметьте строки, которые нужно удалить."));
            return;
        }
        if (!Admin.RequireElevation(T._("Изменение файла hosts")))
            return;
        var shown = selected.Take(12).Select(e => T.F("• строка {0}: {1}", e.LineNumber, e.RawLine.Trim())).ToList();
        if (selected.Count > 12)
            shown.Add(T.F("• … и ещё {0}", selected.Count - 12));
        if (!Dialogs.Confirm(T._("Удалить строки из файла hosts"),
                T.F("Из файла hosts {0}:\n{1}\n\nСтрока удаляется целиком, со всеми сайтами в ней. Остальные строки не изменятся. Перед изменением копия файла сохраняется в папку {2}, после — очищается кэш DNS.",
                    SecurityRules.LinesToRemoveText(selected.Count), string.Join("\n", shown), AppPaths.BackupsDir) +
                (HostsReadOnly ? " " + T._("Атрибут «только чтение» снимается на время записи и возвращается обратно.") : "") +
                "\n\n" + T._("Удалить?")))
            return;
        await RunActionAsync(T._("Изменяем файл hosts…"), T._("Файл hosts"), ct => _service.RemoveHostsLinesAsync(selected, ct));
    }

    private void OpenHostsInNotepad()
    {
        if (string.IsNullOrEmpty(HostsPath))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{HostsPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn(T.F("Не удалось открыть Блокнот: {0}", ErrorText.For(ex)), ex);
            Dialogs.Error(T._("Файл hosts"), T.F("Не удалось открыть Блокнот: {0}.", ErrorText.For(ex)));
        }
    }

    private async Task RestoreWinlogonAsync()
    {
        var w = _report?.Winlogon;
        if (w is null)
            return;
        var win = _service.Env.WindowsDir;
        var hklm = !SecurityRules.IsShellDefault(w.Shell, win) || !SecurityRules.IsUserinitDefault(w.Userinit, win);
        if (hklm && !Admin.RequireElevation(T._("Восстановление параметров входа в систему")))
            return;
        var lines = new List<string>();
        if (!SecurityRules.IsShellDefault(w.Shell, win))
            lines.Add(T.F("• Shell: «{0}» → «explorer.exe»", w.Shell));
        if (!SecurityRules.IsUserinitDefault(w.Userinit, win))
            lines.Add(T.F("• Userinit: «{0}» → «{1}»", w.Userinit, SecurityRules.DefaultUserinit(win)));
        if (!string.IsNullOrWhiteSpace(w.UserShell) && !SecurityRules.IsShellDefault(w.UserShell, win))
            lines.Add(T.F("• удалить пользовательский Shell «{0}»", w.UserShell));
        if (!Dialogs.Confirm(T._("Восстановить параметры входа"),
                T.F("Будут восстановлены стандартные значения Winlogon:\n{0}\n\nПеред изменением создаётся точка восстановления (если она включена в настройках), а раздел реестра сохраняется в резервную копию — по ней можно вернуть всё как было. После восстановления перезагрузите компьютер и проверьте его антивирусом.", string.Join("\n", lines)) +
                "\n\n" + T._("Продолжить?")))
            return;
        await RunActionAsync(T._("Восстанавливаем параметры входа…"), T._("Параметры входа"), async ct =>
        {
            RestorePointResult? restorePoint = null;
            if (hklm)
            {
                ReportProgress(-1, T._("Создаём точку восстановления…"));
                restorePoint = await RestorePoint.CreateAsync(T._("Восстановление параметров входа (Winlogon)"), ct);
                ReportProgress(-1, T._("Восстанавливаем параметры входа…"));
            }
            // Pass the confirmed values: if they changed since the scan, nothing is modified.
            var result = await _service.RestoreWinlogonAsync(w, ct);
            return result.Success && restorePoint is { } rp
                ? result with { Message = result.Message + "\n\n" + SecurityRules.RestorePointText(rp) }
                : result;
        });
    }

    private async Task RemoveIfeoAsync(IfeoEntry? entry)
    {
        if (entry is null)
            return;
        if (entry.Location.NeedsAdmin && !Admin.RequireElevation(T._("Удаление перехвата запуска")))
            return;
        if (!Dialogs.Confirm(T._("Удалить перехват запуска"),
                T.F("Для программы {0} будет удалено значение Debugger = «{1}» в разделе:\n{2}\n\nДругие настройки раздела не изменятся. Перед изменением раздел сохраняется в резервную копию.\n\nУдалить перехват?", entry.ImageName, entry.Debugger, entry.KeyPath)))
            return;
        await RunActionAsync(T._("Удаляем перехват запуска…"), T._("Перехват запуска"), ct => _service.RemoveIfeoDebuggerAsync(entry, ct));
    }

    private async Task DisableProxyAsync()
    {
        var p = _report?.Proxy;
        if (p is null)
            return;
        var what = new List<string>();
        if (p.Enabled)
            what.Add(T.F("• выключить прокси-сервер {0}", p.Server));
        if (!string.IsNullOrWhiteSpace(p.AutoConfigUrl))
            what.Add(T.F("• удалить скрипт настройки {0}", p.AutoConfigUrl));
        if (what.Count == 0)
            what.Add(T._("• выключить прокси-сервер"));
        if (!Dialogs.Confirm(T._("Отключить прокси"),
                T.F("Будет сделано:\n{0}\n\nЕсли прокси нужен для VPN, работы или обхода блокировок, интернет через него перестанет работать. Включить прокси снова можно в «Параметры» → «Сеть и Интернет» → «Прокси-сервер». Перед изменением настройки сохраняются в резервную копию.\n\nОтключить прокси?", string.Join("\n", what))))
            return;
        // Pass the confirmed settings: if they changed since the scan, nothing is modified.
        await RunActionAsync(T._("Отключаем прокси…"), T._("Прокси-сервер"), ct => _service.DisableProxyAsync(p, ct));
    }

    private async Task RemoveThreatsAsync()
    {
        var active = _report?.ActiveThreats ?? Array.Empty<ThreatDetection>();
        if (active.Count == 0)
            return;
        if (!Admin.RequireElevation(T._("Обезвреживание угроз")))
            return;
        var names = active.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"• {g.Key}" + (string.IsNullOrWhiteSpace(g.First().Resources) ? "" : $" — {Shorten(g.First().Resources, 90)}"))
            .ToList();
        var shown = names.Take(10).ToList();
        if (names.Count > 10)
            shown.Add(T.F("• … и ещё {0}", names.Count - 10));
        if (!Dialogs.Confirm(T._("Обезвредить угрозы"),
                T.F("Защитник Windows обезвредит все найденные и не обезвреженные угрозы ({0}):\n{1}\n\nОн применит к ним своё стандартное действие — обычно поместит файлы в карантин (команда Remove-MpThreat). Если файл оказался нужным, его можно восстановить в «Безопасность Windows» → «Журнал защиты».\n\nОбезвредить?", active.Count, string.Join("\n", shown))))
            return;
        await RunActionAsync(T._("Обезвреживаем угрозы…"), T._("Найденные угрозы"), _service.RemoveActiveThreatsAsync);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private async Task DisableSmb1Async()
    {
        if (!Admin.RequireElevation(T._("Отключение SMBv1")))
            return;
        if (!Dialogs.Confirm(T._("Отключить SMBv1"),
                T._("Будет отключён компонент Windows «Поддержка общего доступа к файлам SMB 1.0/CIFS» (Disable-WindowsOptionalFeature SMB1Protocol). Перед этим создаётся точка восстановления (если она включена в настройках).\n\nИзменение вступит в силу после перезагрузки. Современные сетевые папки, принтеры и NAS работают по SMB 2/3 и ничего не заметят; перестанут открываться только очень старые устройства, которые умеют лишь SMBv1.\n\nВернуть: «Включение или отключение компонентов Windows» → «Поддержка общего доступа к файлам SMB 1.0/CIFS».\n\nОтключить SMBv1?")))
            return;
        await RunActionAsync(T._("Отключаем SMBv1 — это может занять несколько минут, не закрывайте программу…"), "SMBv1",
            _service.DisableSmb1Async);
    }

    private void OpenEncryptionSettings()
    {
        if (_report?.BitLocker.IsHomeEdition == true)
        {
            ProcessRunner.ShellOpen("ms-settings:deviceencryption");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.BitLockerDriveEncryption") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn(T.F("Не удалось открыть параметры BitLocker: {0}", ErrorText.For(ex)), ex);
            Dialogs.Error(T._("Шифрование диска"), T.F("Не удалось открыть параметры BitLocker: {0}.", ErrorText.For(ex)));
        }
    }

    private void ShowHowToRemoveExtension()
    {
        Dialogs.Info(T._("Как удалить расширение"),
            T._("Расширения удаляются в самом браузере:\n\n• Google Chrome — откройте адрес chrome://extensions и нажмите «Удалить» у расширения.\n• Microsoft Edge — edge://extensions → «Удалить».\n• Яндекс Браузер — меню ☰ → «Дополнения» (browser://tune) → наведите на расширение → «Удалить».\n• Opera — opera://extensions → «Удалить».\n• Brave и Vivaldi — brave://extensions или vivaldi://extensions.\n• Firefox — меню ☰ → «Дополнения и темы» (about:addons) → «…» → «Удалить».\n\nЕсли кнопки «Удалить» нет, а браузер пишет «Установлено администратором», расширение закреплено политикой. Удалите программу, которая его поставила (раздел «Программы»), и проверьте компьютер антивирусом."));
    }
}
