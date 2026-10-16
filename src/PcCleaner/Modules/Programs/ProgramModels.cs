using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

/// <summary>Where an Uninstall entry lives.</summary>
public enum ProgramScope
{
    /// <summary>HKLM, 64-bit view.</summary>
    Machine64,
    /// <summary>HKLM\SOFTWARE\WOW6432Node (32-bit programs).</summary>
    Machine32,
    /// <summary>HKCU (installed only for the current user).</summary>
    User,
}

public enum BadgeKind
{
    Danger,
    Warning,
    Info,
    Neutral,
    Success,
}

/// <summary>A small colored status label shown in the grid and the details panel.</summary>
public sealed record ProgramBadge(string Text, string ShortText, BadgeKind Kind, string ToolTip);

/// <summary>One registry location with Uninstall entries. Tests point it to a sandbox key.</summary>
public sealed record UninstallSource(RegistryHive Hive, RegistryView View, string SubKeyPath, ProgramScope Scope)
{
    public string HiveShortName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => Hive.ToString(),
    };

    public bool IsMachineWide => Hive == RegistryHive.LocalMachine;

    /// <summary>The real locations used by Windows "Apps &amp; features".</summary>
    public static IReadOnlyList<UninstallSource> Defaults { get; } = new[]
    {
        new UninstallSource(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", ProgramScope.Machine64),
        new UninstallSource(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", ProgramScope.Machine32),
        new UninstallSource(RegistryHive.CurrentUser, RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall", ProgramScope.User),
        new UninstallSource(RegistryHive.CurrentUser, RegistryView.Registry32,
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall", ProgramScope.User),
        new UninstallSource(RegistryHive.CurrentUser, RegistryView.Registry64,
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", ProgramScope.User),
    };
}

/// <summary>Raw values of one Uninstall registry key (what the parser needs; easy to build in tests).</summary>
public sealed class UninstallEntryValues
{
    public string KeyName { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallDate { get; init; }
    public long? EstimatedSizeKb { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public bool WindowsInstaller { get; init; }
    public bool SystemComponent { get; init; }
    public string? ParentKeyName { get; init; }
    public string? ReleaseType { get; init; }

    /// <summary>Cached setup of a WiX Burn bundle (it can uninstall the bundle even when the custom uninstaller is gone).</summary>
    public string? BundleCachePath { get; init; }
}

/// <summary>A classic (Win32) program from the Uninstall registry keys.</summary>
public sealed class InstalledProgram : ObservableObject
{
    private long _computedSizeBytes;
    private ImageSource? _icon;
    private SuspicionResult _suspicion = SuspicionResult.Clean;
    private bool _isTrusted;
    private IReadOnlyList<ProgramBadge> _badges = Array.Empty<ProgramBadge>();

    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }

    /// <summary>True when InstallDate was missing and the install folder creation time was used instead.</summary>
    public bool InstallDateFromFolder { get; init; }

    /// <summary>EstimatedSize from the registry (KB converted to bytes), 0 if missing.</summary>
    public long EstimatedSizeBytes { get; init; }

    public string? InstallLocation { get; init; }
    public bool InstallLocationExists { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public bool IsWindowsInstaller { get; init; }

    /// <summary>MSI product code like {GUID}, if this is a Windows Installer package.</summary>
    public string? ProductCode { get; init; }

    public required string KeyName { get; init; }
    public required UninstallSource Source { get; init; }

    public bool IsBroken { get; init; }
    public bool IsRecent { get; init; }
    public bool IsRuntime { get; init; }
    public bool IsDriver { get; init; }

    /// <summary>The launch for the standard (interactive) uninstall, or null if none could be built.</summary>
    public UninstallLaunch? Launch { get; init; }

    /// <summary>The launch for QuietUninstallString (built at scan time), or null.</summary>
    public UninstallLaunch? QuietLaunch { get; init; }

    // ---- derived ----

    public string RegistryKeyPath => $@"{Source.HiveShortName}\{Source.SubKeyPath}\{KeyName}";

    public ProgramScope Scope => Source.Scope;

    public bool IsMachineWide => Source.IsMachineWide;

    public bool IsMsi => Launch?.IsMsi == true;

    /// <summary>"Тихое удаление" is offered only when the installer left a usable QuietUninstallString.</summary>
    public bool HasQuietUninstall => QuietLaunch is not null;

    public bool CanUninstall => !IsBroken && Launch is not null;

    public bool CanQuietUninstall => CanUninstall && HasQuietUninstall;

    public bool HasPublisher => !string.IsNullOrWhiteSpace(Publisher);

    public string PublisherText => HasPublisher ? Publisher!.Trim() : "Не указан";

    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version!.Trim();

    public long ComputedSizeBytes
    {
        get => _computedSizeBytes;
        set
        {
            if (Set(ref _computedSizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeBytes));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(SizeDetailsText));
                OnPropertyChanged(nameof(IsLarge));
            }
        }
    }

    /// <summary>
    /// Best known size: registry estimate, otherwise the measured install folder.
    /// Broken entries take no space (the program is already gone), so they report 0.
    /// </summary>
    public long SizeBytes => IsBroken ? 0 : EstimatedSizeBytes > 0 ? EstimatedSizeBytes : ComputedSizeBytes;

    public bool IsLarge => SizeBytes >= ProgramRules.LargeProgramBytes;

    public string SizeText => SizeBytes > 0 ? Format.Bytes(SizeBytes) : "—";

    public string SizeDetailsText => IsBroken
        ? "Место уже освобождено — программы нет на диске"
        : EstimatedSizeBytes > 0
        ? Format.Bytes(EstimatedSizeBytes) + " (по данным установщика)"
        : ComputedSizeBytes > 0
            ? Format.Bytes(ComputedSizeBytes) + " (размер папки установки)"
            : "Неизвестен";

    public string DateText => Format.Date(InstallDate);

    public string DateDetailsText => InstallDate is null
        ? "Неизвестна"
        : InstallDateFromFolder
            ? Format.Date(InstallDate) + " (по дате создания папки)"
            : Format.Date(InstallDate);

    public string ScopeText => Scope switch
    {
        ProgramScope.Machine64 => "Для всех пользователей (64-разрядная)",
        ProgramScope.Machine32 => "Для всех пользователей (32-разрядная)",
        _ => "Только для текущего пользователя",
    };

    public string InstallerTypeText => IsMsi
        ? "Windows Installer (MSI)"
        : Launch is null ? "Деинсталлятор не указан" : "Собственный деинсталлятор программы";

    public string InstallLocationText => string.IsNullOrWhiteSpace(InstallLocation)
        ? "Не указана"
        : InstallLocationExists ? InstallLocation! : InstallLocation + " (папки нет)";

    public string UninstallCommandText => Launch is null
        ? (string.IsNullOrWhiteSpace(UninstallString) ? "—" : UninstallString!)
        : Launch.DisplayText;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => Icon is not null;

    public SuspicionResult Suspicion
    {
        get => _suspicion;
        set
        {
            if (Set(ref _suspicion, value ?? SuspicionResult.Clean))
            {
                OnPropertyChanged(nameof(IsPup));
                OnPropertyChanged(nameof(PupReason));
                OnPropertyChanged(nameof(HasCautionHint));
                OnPropertyChanged(nameof(CautionHint));
            }
        }
    }

    /// <summary>Potentially unwanted program (Suspicion level Medium or higher).</summary>
    public bool IsPup => Suspicion.IsSuspicious;

    public string PupReason => Suspicion.ReasonsText;

    /// <summary>An unusual install folder (Temp, Downloads...) that does not make it a PUP but is worth a note.</summary>
    public bool HasCautionHint => !IsPup && !IsTrusted && CautionHint.Length > 0;

    public string CautionHint => ProgramRules.DescribeInstallLocationRisk(InstallLocation) ?? "";

    public bool IsTrusted
    {
        get => _isTrusted;
        set => Set(ref _isTrusted, value);
    }

    public IReadOnlyList<ProgramBadge> Badges
    {
        get => _badges;
        set
        {
            if (Set(ref _badges, value))
                OnPropertyChanged(nameof(PrimaryBadge));
        }
    }

    public ProgramBadge? PrimaryBadge => Badges.Count > 0 ? Badges[0] : null;

    /// <summary>Text used by the search box.</summary>
    public string SearchText => $"{DisplayName} {Publisher} {Version} {InstallLocation}";

    /// <summary>
    /// Main executable for a VirusTotal lookup (DisplayIcon .exe, else the uninstaller), found once at scan time:
    /// command CanExecute checks run on the UI thread all the time and must not touch the disk.
    /// </summary>
    public string? MainExecutable { get; init; }

    /// <summary>Recomputes the badge list after flags or trust change.</summary>
    public void RefreshBadges()
    {
        var list = new List<ProgramBadge>();
        if (IsPup)
            list.Add(new ProgramBadge("Нежелательная", "Нежелательная", BadgeKind.Danger,
                "Похожа на потенциально нежелательную программу: " + PupReason));
        if (IsBroken)
            list.Add(new ProgramBadge("Битая запись", "Битая запись", BadgeKind.Warning,
                "Программа, скорее всего, уже удалена: нет ни деинсталлятора, ни папки установки. Осталась только строка в списке."));
        if (IsRecent)
            list.Add(new ProgramBadge("Недавно установлена", "Недавняя", BadgeKind.Info,
                $"Установлена или обновлена за последние {ProgramRules.RecentDays} дней. Если вы её не ставили — проверьте: " +
                "такие программы часто приходят «в нагрузку» к другим установщикам."));
        if (IsRuntime)
            list.Add(new ProgramBadge("Нужна другим программам — не удалять", "Нужна другим", BadgeKind.Success,
                "Компонент Microsoft (Visual C++, .NET, DirectX и т. п.), который используют другие программы и игры. " +
                "Без него они могут перестать запускаться."));
        if (IsDriver)
            list.Add(new ProgramBadge("Драйвер устройства", "Драйвер", BadgeKind.Neutral,
                "Пакет драйвера. Удаляйте, только если устройство больше не используется."));
        if (IsTrusted)
            list.Add(new ProgramBadge("Доверенная", "Доверенная", BadgeKind.Success,
                "Вы отметили программу как доверенную — проверки на нежелательность для неё отключены."));
        Badges = list;
    }
}

/// <summary>How to launch an uninstaller (built from UninstallString / QuietUninstallString).</summary>
public sealed record UninstallLaunch(string FileName, string Arguments, string? WorkingDirectory, bool IsMsi, string? ProductCode)
{
    public string DisplayText => Arguments.Length == 0 ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
}

public enum StoreAppKind
{
    /// <summary>Installed by the user or the PC maker; may be removed after review.</summary>
    Regular,
    /// <summary>Preinstalled Windows/OEM app that is safe to remove.</summary>
    Bloatware,
    /// <summary>Must not be removed: Store, runtimes, codecs, drivers' control panels, core apps.</summary>
    Protected,
}

/// <summary>A Microsoft Store (AppX/MSIX) app of the current user.</summary>
public sealed class StoreApp : ObservableObject
{
    private long _sizeBytes;
    private ImageSource? _icon;
    private string _displayName = "";
    private string _publisherDisplay = "";

    public required string Name { get; init; }
    public required string PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    public string? SignatureKind { get; init; }

    public StoreAppKind Kind { get; init; }

    /// <summary>What the app is, in plain Russian (for well-known apps).</summary>
    public string? Description { get; init; }

    /// <summary>Why the app must not be removed (for Protected apps).</summary>
    public string? ProtectReason { get; init; }

    /// <summary>Logo file from the manifest (loaded lazily as <see cref="Icon"/>).</summary>
    internal string? LogoPath { get; init; }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string PublisherDisplay
    {
        get => _publisherDisplay;
        set => Set(ref _publisherDisplay, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (Set(ref _sizeBytes, value))
                OnPropertyChanged(nameof(SizeText));
        }
    }

    public string SizeText => SizeBytes > 0 ? Format.Bytes(SizeBytes) : "—";

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => Icon is not null;

    public bool IsBloatware => Kind == StoreAppKind.Bloatware;

    public bool IsProtected => Kind == StoreAppKind.Protected;

    public bool CanRemove => Kind != StoreAppKind.Protected;

    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version!;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ProgramBadge? Badge => Kind switch
    {
        StoreAppKind.Bloatware => new ProgramBadge("Предустановленная, можно удалить", "Можно удалить", BadgeKind.Info,
            "Приложение поставляется вместе с Windows или ноутбуком. Если вы им не пользуетесь, его можно удалить — " +
            "при необходимости оно снова устанавливается из Microsoft Store."),
        StoreAppKind.Protected => new ProgramBadge("Системное — не удалять", "Не удалять", BadgeKind.Neutral,
            ProtectReason ?? "Нужен Windows или другим приложениям."),
        _ => null,
    };

    /// <summary>Signed by Microsoft Store: after removal it can be installed again from the Store.</summary>
    public bool IsFromStore => string.Equals(SignatureKind, "Store", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the user gets the app back after removal (only Store-signed packages come back from the Store).</summary>
    public string UndoText => IsFromStore
        ? "Вернуть приложение можно в любой момент — установите его заново из Microsoft Store."
        : "Это приложение установлено не из Microsoft Store (его поставила другая программа или MSIX-файл), поэтому " +
          "вернуть его через Store не получится — только переустановив программу, которая его установила.";

    public string SourceText => SignatureKind switch
    {
        "Store" => "Microsoft Store",
        "Developer" => "Установлено вручную (MSIX-пакет)",
        "Enterprise" => "Установлено организацией",
        _ => string.IsNullOrWhiteSpace(SignatureKind) ? "—" : SignatureKind!,
    };

    public string SearchText => $"{DisplayName} {Name} {PublisherDisplay}";
}
