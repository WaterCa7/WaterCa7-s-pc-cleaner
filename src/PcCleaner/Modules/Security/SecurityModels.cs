using PcCleaner.Core;
using PcCleaner.Core.Localization;

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
        SecurityStatus.Ok => T._("В порядке"),
        SecurityStatus.Info => T._("К сведению"),
        SecurityStatus.Warning => T._("Внимание"),
        SecurityStatus.Danger => T._("Опасно"),
        _ => T._("Нет данных"),
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
    public const string Smb1 = "smb1";
    public const string SecureBoot = "secureboot";
    public const string Tpm = "tpm";
    public const string BitLocker = "bitlocker";
    public const string MemoryIntegrity = "memoryintegrity";

    /// <summary>All checks in order of importance (cards with the same status are shown in this order).</summary>
    public static readonly string[] All =
    {
        Antivirus, Firewall, Winlogon, Ifeo, Uac, Hosts, Smb1, Updates, Proxy, SmartScreen, Rdp, Extensions, Dns,
        SecureBoot, MemoryIntegrity, Tpm, BitLocker,
    };

    /// <summary>Position in <see cref="All"/> (unknown ids go last).</summary>
    public static int Importance(string id)
    {
        var i = Array.IndexOf(All, id);
        return i < 0 ? int.MaxValue : i;
    }

    public static string Title(string id) => id switch
    {
        Antivirus => T._("Антивирус"),
        Firewall => T._("Брандмауэр"),
        Uac => T._("Контроль учётных записей"),
        Hosts => T._("Файл hosts"),
        Winlogon => T._("Запуск рабочего стола"),
        Ifeo => T._("Перехват запуска программ"),
        Proxy => T._("Прокси-сервер"),
        Dns => T._("DNS-серверы"),
        Updates => T._("Обновления Windows"),
        SmartScreen => "SmartScreen",
        Rdp => T._("Удалённый рабочий стол"),
        Extensions => T._("Расширения браузеров"),
        Smb1 => T._("Протокол SMBv1"),
        SecureBoot => "Secure Boot",
        Tpm => T._("Модуль TPM"),
        BitLocker => T._("Шифрование диска"),
        MemoryIntegrity => T._("Изоляция ядра"),
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
        Smb1 => "",
        SecureBoot => "",
        Tpm => "",
        BitLocker => "",
        MemoryIntegrity => "",
        _ => "",
    };

    /// <summary>Plain-language tooltip explaining what the check is about.</summary>
    public static string Explanation(string id) => id switch
    {
        Antivirus => T._("Антивирус проверяет файлы и программы в момент запуска и не даёт вирусам закрепиться в системе. Встроенный антивирус Windows — Защитник Windows (Microsoft Defender)."),
        Firewall => T._("Брандмауэр (сетевой экран) не пускает в компьютер чужие подключения из сети."),
        Uac => T._("UAC — окно «Разрешить этому приложению вносить изменения?». Оно не даёт программам молча получать права администратора."),
        Hosts => T._("Файл hosts вручную задаёт адреса сайтов. Вирусы подменяют в нём адреса, чтобы уводить на поддельные страницы."),
        Winlogon => T._("Параметры Winlogon (Shell и Userinit) определяют, что запускается при входе в Windows. Вирусы встраиваются сюда, чтобы стартовать вместе с рабочим столом."),
        Ifeo => T._("Раздел реестра Image File Execution Options позволяет подменить запуск любой программы. Вирусы так блокируют антивирусы и Диспетчер задач."),
        Proxy => T._("Прокси-сервер — посредник между браузером и интернетом. Рекламные программы подменяют его, чтобы встраивать рекламу и следить за сайтами."),
        Dns => T._("DNS-сервер превращает адреса сайтов в IP-адреса. Подменённый DNS может уводить на поддельные сайты."),
        Updates => T._("Обновления закрывают уязвимости Windows, через которые заражают компьютеры."),
        SmartScreen => T._("SmartScreen предупреждает перед запуском скачанных программ с плохой репутацией."),
        Rdp => T._("Удалённый рабочий стол (RDP) позволяет управлять компьютером по сети. Злоумышленники подбирают к нему пароли."),
        Extensions => T._("Расширения видят все открытые страницы. Рекламные расширения подменяют поиск и показывают лишнюю рекламу."),
        Smb1 => T._("SMBv1 — устаревшая версия протокола общего доступа к файлам и принтерам по сети. Через её уязвимости в 2017 году распространялись вирусы WannaCry и NotPetya."),
        SecureBoot => T._("Secure Boot (безопасная загрузка) — функция UEFI, которая разрешает запускать только подписанные загрузчики. Она не даёт буткитам стартовать раньше Windows."),
        Tpm => T._("TPM — чип или функция процессора (fTPM у AMD, PTT у Intel), которая хранит ключи шифрования. Нужен для Windows 11, BitLocker и Windows Hello."),
        BitLocker => T._("BitLocker и «Шифрование устройства» шифруют системный диск: без пароля или ключа восстановления данные не прочитать, даже если вынуть диск."),
        MemoryIntegrity => T._("«Целостность памяти» (HVCI) — часть изоляции ядра. С помощью виртуализации она проверяет драйверы и не даёт вредоносному коду внедриться в ядро Windows."),
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

    /// <summary>The user chose "Разрешить на устройстве" — a deliberate decision, not an unhandled threat.</summary>
    public bool IsAllowed => SecurityRules.IsThreatAllowed(StatusId);

    /// <summary>Detected but neither neutralized nor allowed by the user.</summary>
    public bool IsActive => !IsResolved && !IsAllowed;

    public string StatusText => SecurityRules.ThreatStatusText(StatusId);
    public string SeverityText => SecurityRules.ThreatSeverityText(SeverityId);
    public SecurityStatus Risk => IsResolved ? SecurityStatus.Ok : IsAllowed ? SecurityStatus.Warning : SecurityStatus.Danger;
    public SecurityStatus SeverityRisk => SeverityId >= 4 ? SecurityStatus.Danger : SeverityId >= 2 ? SecurityStatus.Warning : SecurityStatus.Info;
    public string TimeText => Format.DateTime(Time);
}

/// <param name="Name">Domain / Private / Public.</param>
/// <param name="Enabled">Effective state (a Group Policy value overrides the local setting).</param>
/// <param name="ByPolicy">True if the state is forced by Group Policy (netsh cannot change it).</param>
public sealed record FirewallProfileState(string Name, bool? Enabled, bool ByPolicy = false)
{
    public string DisplayName => Name.ToLowerInvariant() switch
    {
        "domain" => T._("доменная сеть"),
        "private" => T._("частная сеть"),
        "public" => T._("общедоступная сеть"),
        _ => Name,
    };

    /// <summary>"частная", "общедоступная", "доменная".</summary>
    public string ShortName => Name.ToLowerInvariant() switch
    {
        "domain" => T._("доменная"),
        "private" => T._("частная"),
        "public" => T._("общедоступная"),
        _ => Name,
    };

    /// <summary>"частной", "общедоступной", "доменной" (for "выключен для ... сети").</summary>
    public string Genitive => Name.ToLowerInvariant() switch
    {
        "domain" => T._("доменной"),
        "private" => T._("частной"),
        "public" => T._("общедоступной"),
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

    public string KeyPathLabel => T.F("Раздел реестра: {0}", KeyPath);

    public SecurityStatus Risk { get; set; }
    public string Reason { get; set; } = "";
    public string ViewText => IsWow64 ? T._("32-битные программы") : T._("64-битные программы");
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
    public Smb1State Smb1 { get; init; } = Smb1State.Unknown;
    public SecureBootState SecureBoot { get; init; } = new(false, null, null);
    public TpmState Tpm { get; init; } = TpmState.Unknown;
    public BitLockerState BitLocker { get; init; } = BitLockerState.Unknown;
    public DeviceGuardState DeviceGuard { get; init; } = DeviceGuardState.Unknown;
    public IReadOnlyList<SecurityCheckResult> Checks { get; init; } = Array.Empty<SecurityCheckResult>();

    public SecurityCheckResult? Check(string id) => Checks.FirstOrDefault(c => c.Id == id);

    /// <summary>True if an enabled non-Microsoft antivirus protects the PC.</summary>
    public bool HasThirdPartyAntivirus => Antiviruses.Any(a => !a.IsMicrosoft && a.Enabled);

    /// <summary>Antiviruses that protect in real time at the same time (2+ means they get in each other's way).</summary>
    public IReadOnlyList<string> ActiveAntivirusNames => SecurityRules.ActiveAntivirusNames(Antiviruses, Defender);

    /// <summary>Detections that Defender neither neutralized nor the user allowed.</summary>
    public IReadOnlyList<ThreatDetection> ActiveThreats => Threats.Where(t => t.IsActive).ToList();

    /// <summary>"Обезвредить угрозы" makes sense: Defender works and has active detections.</summary>
    public bool CanRemoveThreats => SecurityRules.CanRemoveThreats(Defender, Threats);

    /// <summary>
    /// True if every Windows Firewall profile that is off is forced off by Group Policy, so
    /// "netsh advfirewall set allprofiles state on" cannot turn anything on.
    /// </summary>
    public bool FirewallOffOnlyByPolicy => FirewallProfiles.Any(p => p.Enabled == false) &&
                                           FirewallProfiles.Where(p => p.Enabled == false).All(p => p.ByPolicy);
}
