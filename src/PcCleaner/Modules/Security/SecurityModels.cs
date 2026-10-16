using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

/// <summary>Result of one security check / one listed item.</summary>
public enum SecurityStatus
{
    Unknown = 0,
    Ok = 1,
    Info = 2,
    Warning = 3,
    Danger = 4,
}

public static class SecurityStatusExtensions
{
    /// <summary>Severity rank used to pick the "worst" status (Unknown sits between Info and Warning).</summary>
    public static int Rank(this SecurityStatus s) => s switch
    {
        SecurityStatus.Ok => 0,
        SecurityStatus.Info => 1,
        SecurityStatus.Unknown => 2,
        SecurityStatus.Warning => 3,
        SecurityStatus.Danger => 4,
        _ => 0,
    };

    public static SecurityStatus Worst(this SecurityStatus a, SecurityStatus b) => a.Rank() >= b.Rank() ? a : b;

    public static string ToRussian(this SecurityStatus s) => s switch
    {
        SecurityStatus.Ok => "В порядке",
        SecurityStatus.Info => "К сведению",
        SecurityStatus.Warning => "Внимание",
        SecurityStatus.Danger => "Опасно",
        _ => "Нет данных",
    };
}

/// <summary>Evaluated state of one check, shown as a card.</summary>
public sealed record SecurityCheckResult(string Id, SecurityStatus Status, string State, string Details);

/// <summary>Static identifiers and presentation data of the checks.</summary>
public static class SecurityCheckIds
{
    public const string Antivirus = "antivirus";
    public const string Firewall = "firewall";
    public const string Uac = "uac";
    public const string Hosts = "hosts";
    public const string Winlogon = "winlogon";
    public const string Ifeo = "ifeo";
    public const string Proxy = "proxy";
    public const string Dns = "dns";
    public const string Updates = "updates";
    public const string SmartScreen = "smartscreen";
    public const string Rdp = "rdp";
    public const string Extensions = "extensions";

    public static readonly string[] All =
    {
        Antivirus, Firewall, Uac, Hosts, Winlogon, Ifeo, Proxy, Dns, Updates, SmartScreen, Rdp, Extensions,
    };

    public static string Title(string id) => id switch
    {
        Antivirus => "Антивирус",
        Firewall => "Брандмауэр",
        Uac => "Контроль учётных записей",
        Hosts => "Файл hosts",
        Winlogon => "Запуск рабочего стола",
        Ifeo => "Перехват запуска программ",
        Proxy => "Прокси-сервер",
        Dns => "DNS-серверы",
        Updates => "Обновления Windows",
        SmartScreen => "SmartScreen",
        Rdp => "Удалённый рабочий стол",
        Extensions => "Расширения браузеров",
        _ => id,
    };

    public static string Glyph(string id) => id switch
    {
        Antivirus => "",
        Firewall => "",
        Uac => "",
        Hosts => "",
        Winlogon => "",
        Ifeo => "",
        Proxy => "",
        Dns => "",
        Updates => "",
        SmartScreen => "",
        Rdp => "",
        Extensions => "",
        _ => "",
    };

    /// <summary>Plain-language tooltip explaining what the check is about.</summary>
    public static string Explanation(string id) => id switch
    {
        Antivirus => "Антивирус проверяет файлы и программы в момент запуска и не даёт вирусам закрепиться в системе.",
        Firewall => "Брандмауэр (сетевой экран) не пускает в компьютер чужие подключения из сети.",
        Uac => "UAC — окно «Разрешить этому приложению вносить изменения?». Оно не даёт программам молча получать права администратора.",
        Hosts => "Файл hosts вручную задаёт адреса сайтов. Вирусы подменяют в нём адреса, чтобы уводить на поддельные страницы.",
        Winlogon => "Параметры Winlogon (Shell и Userinit) определяют, что запускается при входе в Windows. Вирусы встраиваются сюда, чтобы стартовать вместе с рабочим столом.",
        Ifeo => "Раздел реестра Image File Execution Options позволяет подменить запуск любой программы. Вирусы так блокируют антивирусы и Диспетчер задач.",
        Proxy => "Прокси-сервер — посредник между браузером и интернетом. Рекламные программы подменяют его, чтобы встраивать рекламу и следить за сайтами.",
        Dns => "DNS-сервер превращает адреса сайтов в IP-адреса. Подменённый DNS может уводить на поддельные сайты.",
        Updates => "Обновления закрывают уязвимости Windows, через которые заражают компьютеры.",
        SmartScreen => "SmartScreen предупреждает перед запуском скачанных программ с плохой репутацией.",
        Rdp => "Удалённый рабочий стол (RDP) позволяет управлять компьютером по сети. Злоумышленники подбирают к нему пароли.",
        Extensions => "Расширения видят все открытые страницы. Рекламные расширения подменяют поиск и показывают лишнюю рекламу.",
        _ => "",
    };
}

/// <summary>An antivirus or firewall product registered in Windows Security Center.</summary>
public sealed record SecurityProduct(string Name, int ProductState, bool Enabled, bool UpToDate, bool IsMicrosoft);

/// <summary>Microsoft Defender status (MSFT_MpComputerStatus + policies + preferences).</summary>
public sealed class DefenderStatus
{
    public bool ServiceEnabled { get; init; }
    public bool AntivirusEnabled { get; init; }
    public bool RealTimeEnabled { get; init; }
    public bool? TamperProtected { get; init; }
    public DateTime? SignatureUpdated { get; init; }
    public int? SignatureAgeDays { get; init; }
    public int? QuickScanAgeDays { get; init; }
    public int? FullScanAgeDays { get; init; }
    public string? Version { get; init; }
    public string? RunningMode { get; init; }
    public bool PolicyDisablesAntivirus { get; init; }
    public bool PolicyDisablesRealtime { get; init; }
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();
    public bool ExclusionsHidden { get; init; }

    public bool IsPassive => RunningMode is not null &&
                             RunningMode.Contains("Passive", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One detection from the Defender protection history.</summary>
public sealed class ThreatDetection
{
    public DateTime? Time { get; init; }
    public long ThreatId { get; init; }
    public string Name { get; init; } = "";
    public int SeverityId { get; init; }
    public int StatusId { get; init; }
    public string Resources { get; init; } = "";
    public string? Process { get; init; }

    public bool IsResolved => SecurityRules.IsThreatResolved(StatusId);
    public string StatusText => SecurityRules.ThreatStatusText(StatusId);
    public string SeverityText => SecurityRules.ThreatSeverityText(SeverityId);
    public SecurityStatus Risk => IsResolved ? SecurityStatus.Ok : SecurityStatus.Danger;
    public SecurityStatus SeverityRisk => SeverityId >= 4 ? SecurityStatus.Danger : SeverityId >= 2 ? SecurityStatus.Warning : SecurityStatus.Info;
    public string TimeText => Format.DateTime(Time);
}

public sealed record FirewallProfileState(string Name, bool? Enabled)
{
    public string DisplayName => Name.ToLowerInvariant() switch
    {
        "domain" => "доменная сеть",
        "private" => "частная сеть",
        "public" => "общедоступная сеть",
        _ => Name,
    };

    /// <summary>"частная", "общедоступная", "доменная".</summary>
    public string ShortName => Name.ToLowerInvariant() switch
    {
        "domain" => "доменная",
        "private" => "частная",
        "public" => "общедоступная",
        _ => Name,
    };

    /// <summary>"частной", "общедоступной", "доменной" (for "выключен для ... сети").</summary>
    public string Genitive => Name.ToLowerInvariant() switch
    {
        "domain" => "доменной",
        "private" => "частной",
        "public" => "общедоступной",
        _ => Name,
    };
}

public sealed record UacState(bool Readable, int? EnableLua, int? ConsentPromptBehaviorAdmin, int? PromptOnSecureDesktop);

public sealed record WinlogonState(bool Readable, string? Shell, string? Userinit, string? UserShell);

/// <summary>An IFEO "Debugger" redirection.</summary>
public sealed class IfeoEntry
{
    public required string ImageName { get; init; }
    public required string Debugger { get; init; }
    public bool IsWow64 { get; init; }

    /// <summary>Registry location of the image subkey.</summary>
    public required RegLocation Location { get; init; }

    /// <summary>Full registry path of the image subkey, e.g. "HKLM\SOFTWARE\...\taskmgr.exe".</summary>
    public string KeyPath => Location.FullPath;

    public SecurityStatus Risk { get; set; }
    public string Reason { get; set; } = "";
    public string ViewText => IsWow64 ? "32-битные программы" : "64-битные программы";
}

public sealed record ProxyState(bool Readable, bool Enabled, string? Server, string? AutoConfigUrl);

public enum DnsKind
{
    Known,
    Private,
    Unknown,
}

public sealed record DnsServerInfo(string Adapter, string Address, string Label, DnsKind Kind)
{
    public SecurityStatus Risk => Kind == DnsKind.Unknown ? SecurityStatus.Info : SecurityStatus.Ok;
}

public sealed record UpdatesState(DateTime? LastInstalled, int Count, string? LastHotFix, bool? ServiceDisabled);

public sealed record SmartScreenState(string? ExplorerValue, int? PolicyValue);

public sealed record RdpState(int? DenyConnections);

/// <summary>Full read-only snapshot of the security state.</summary>
public sealed class SecurityReport
{
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public DefenderStatus? Defender { get; init; }
    public IReadOnlyList<SecurityProduct> Antiviruses { get; init; } = Array.Empty<SecurityProduct>();
    public IReadOnlyList<SecurityProduct> Firewalls { get; init; } = Array.Empty<SecurityProduct>();
    public IReadOnlyList<ThreatDetection> Threats { get; init; } = Array.Empty<ThreatDetection>();
    public IReadOnlyList<FirewallProfileState> FirewallProfiles { get; init; } = Array.Empty<FirewallProfileState>();
    public UacState Uac { get; init; } = new(false, null, null, null);
    public HostsReport Hosts { get; init; } = new();
    public WinlogonState Winlogon { get; init; } = new(false, null, null, null);
    public IReadOnlyList<IfeoEntry> Ifeo { get; init; } = Array.Empty<IfeoEntry>();
    public bool IfeoPartial { get; init; }
    public bool IfeoReadable { get; init; } = true;
    public ProxyState Proxy { get; init; } = new(false, false, null, null);
    public IReadOnlyList<DnsServerInfo> Dns { get; init; } = Array.Empty<DnsServerInfo>();
    public bool DnsReadable { get; init; } = true;
    public UpdatesState Updates { get; init; } = new(null, 0, null, null);
    public SmartScreenState SmartScreen { get; init; } = new(null, null);
    public RdpState Rdp { get; init; } = new(null);
    public IReadOnlyList<BrowserExtension> Extensions { get; init; } = Array.Empty<BrowserExtension>();
    public int BrowsersFound { get; init; }
    public bool MpCmdRunAvailable { get; init; }
    public IReadOnlyList<SecurityCheckResult> Checks { get; init; } = Array.Empty<SecurityCheckResult>();

    public SecurityCheckResult? Check(string id) => Checks.FirstOrDefault(c => c.Id == id);

    /// <summary>True if an enabled non-Microsoft antivirus protects the PC.</summary>
    public bool HasThirdPartyAntivirus => Antiviruses.Any(a => !a.IsMicrosoft && a.Enabled);
}
