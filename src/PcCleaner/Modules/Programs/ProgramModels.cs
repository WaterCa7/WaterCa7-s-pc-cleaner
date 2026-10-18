using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

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

    /// <summary>
    /// The install folder is on a network, removable or disconnected drive and was not checked (see
    /// ProgramRules.IsUnprobeablePath): it is neither "there" nor "gone".
    /// </summary>
    public bool InstallLocationUnchecked { get; init; }

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

    /// <summary>
    /// Microsoft runtimes (Visual C++, .NET, DirectX…) can be uninstalled, but other programs need them: the details panel
    /// offers that only as a muted secondary button, and the confirmation starts with a strong warning.
    /// </summary>
    public bool UninstallNotRecommended => CanUninstall && IsRuntime;

    /// <summary>The red "Удалить программу" main action (everything that can be uninstalled except runtimes).</summary>
    public bool ShowPrimaryUninstall => CanUninstall && !IsRuntime;

    /// <summary>"Тихое удаление" is never offered for runtimes: removing one without any window is too easy to regret.</summary>
    public bool ShowQuietUninstall => CanQuietUninstall && !IsRuntime;

    // ---- checkbox ("Удалить отмеченные") ----

    /// <summary>
    /// The page's checked programs, by <see cref="CheckKey"/> (null for programs made outside the page, which then stay unchecked).
    /// A rescan builds new objects for the same entries: they read their check from here, so the checks survive it.
    /// </summary>
    internal CheckedSet<string>? Checks { get; set; }

    /// <summary>The registry key, upper-cased: registry keys are case-insensitive, the checked set is not.</summary>
    internal string CheckKey => RegistryKeyPath.ToUpperInvariant();

    /// <summary>
    /// Only what "Удалить отмеченные" can really uninstall is checked. Runtimes never are: other programs need them, so they are removed
    /// one by one, with the warning. Nor are entries without a working uninstaller (a broken entry is removed from the list instead).
    /// </summary>
    public bool CanCheck => !IsRuntime && CanUninstall;

    /// <summary>Why the checkbox is disabled; null when it can be checked.</summary>
    public string? CheckTooltip => CanCheck ? null
        : IsRuntime ? T._("Компоненты .NET, Visual C++ и т. п. нужны другим программам — удаляйте их по одной")
        : IsBroken ? T._("Деинсталлятор не найден — уберите запись кнопкой «Убрать запись из списка»")
        : T._("У программы не указан деинсталлятор. Попробуйте удалить её через «Параметры» → «Приложения».");

    public bool IsChecked
    {
        get => Checks?.IsChecked(CheckKey) == true;
        set
        {
            // What can't be checked never is: its checkbox is disabled, and code that sets it is refused too.
            if (Checks is null || (value && !CanCheck))
            {
                OnPropertyChanged();
                return;
            }
            Checks.Set(CheckKey, value);
        }
    }

    /// <summary>The page's checked set changed: the checkbox reads it again.</summary>
    internal void OnCheckedChanged() => OnPropertyChanged(nameof(IsChecked));

    public bool HasPublisher => !string.IsNullOrWhiteSpace(Publisher);

    public string PublisherText => HasPublisher ? Publisher!.Trim() : T._("Не указан");

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
        ? T._("Место уже освобождено — программы нет на диске")
        : EstimatedSizeBytes > 0
        ? T.F("{0} (по данным установщика)", Format.Bytes(EstimatedSizeBytes))
        : ComputedSizeBytes > 0
            ? T.F("{0} (размер папки установки)", Format.Bytes(ComputedSizeBytes))
            : T._("Неизвестен");

    public string DateText => Format.Date(InstallDate);

    public string DateDetailsText => InstallDate is null
        ? T._("Неизвестна")
        : InstallDateFromFolder
            ? T.F("{0} (по дате создания папки)", Format.Date(InstallDate))
            : Format.Date(InstallDate);

    public string ScopeText => Scope switch
    {
        ProgramScope.Machine64 => T._("Для всех пользователей (64-разрядная)"),
        ProgramScope.Machine32 => T._("Для всех пользователей (32-разрядная)"),
        _ => T._("Только для текущего пользователя"),
    };

    public string InstallerTypeText => IsMsi
        ? "Windows Installer (MSI)"
        : Launch is null ? T._("Деинсталлятор не указан") : T._("Собственный деинсталлятор программы");

    public string InstallLocationText => string.IsNullOrWhiteSpace(InstallLocation)
        ? T._("Не указана")
        : InstallLocationExists ? InstallLocation!
        : InstallLocationUnchecked ? T.F("{0} (сетевой, съёмный или отключённый диск — не проверялась)", InstallLocation)
        : T.F("{0} (папки нет)", InstallLocation);

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

    /// <summary>
    /// The program file "Доверять" also trusts, so the «Процессы» and «Автозагрузка» pages (which recognise programs by
    /// file path) stop flagging it too: <see cref="ProgramRules.TrustFileFor"/>, found once at scan time. Null if none qualifies.
    /// </summary>
    public string? TrustFile { get; init; }

    /// <summary>Recomputes the badge list after flags or trust change.</summary>
    public void RefreshBadges()
    {
        var list = new List<ProgramBadge>();
        if (IsPup)
            list.Add(new ProgramBadge(T._("Нежелательная"), T._("Нежелательная"), BadgeKind.Danger,
                T.F("Похожа на потенциально нежелательную программу: {0}", PupReason)));
        if (IsBroken)
            list.Add(new ProgramBadge(T._("Битая запись"), T._("Битая запись"), BadgeKind.Warning,
                T._("Программа, скорее всего, уже удалена: нет ни деинсталлятора, ни папки установки. Осталась только строка в списке.")));
        if (IsRecent)
            list.Add(new ProgramBadge(T._("Недавно установлена"), T._("Недавняя"), BadgeKind.Info,
                T.F("Установлена или обновлена за последние {0} дней. Если вы её не ставили — проверьте: такие программы часто приходят «в нагрузку» к другим установщикам.", ProgramRules.RecentDays)));
        if (IsRuntime)
            list.Add(new ProgramBadge(T._("Нужна другим программам — не удалять"), T._("Нужна другим"), BadgeKind.Success,
                T._("Компонент Microsoft (Visual C++, .NET, DirectX и т. п.), который используют другие программы и игры. Без него они могут перестать запускаться.")));
        if (IsDriver)
            list.Add(new ProgramBadge(T._("Драйвер устройства"), T._("Драйвер"), BadgeKind.Neutral,
                T._("Пакет драйвера. Удаляйте, только если устройство больше не используется.")));
        if (IsTrusted)
            list.Add(new ProgramBadge(T._("Доверенная"), T._("Доверенная"), BadgeKind.Success,
                T._("Вы отметили программу как доверенную — проверки на нежелательность для неё отключены.")));
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

    // ---- checkbox ("Удалить отмеченные") ----

    /// <summary>
    /// The page's checked apps, by <see cref="CheckKey"/> (null for apps made outside the page, which then stay unchecked).
    /// A rescan builds new objects for the same packages: they read their check from here, so the checks survive it.
    /// </summary>
    internal CheckedSet<string>? Checks { get; set; }

    /// <summary>The package full name, upper-cased: Windows compares package names without case, the checked set does not.</summary>
    internal string CheckKey => PackageFullName.ToUpperInvariant();

    /// <summary>Only what may be removed can be checked: apps Windows needs never are.</summary>
    public bool CanCheck => CanRemove;

    /// <summary>Why the checkbox is disabled; null when it can be checked.</summary>
    public string? CheckTooltip => CanCheck ? null : ProtectReason ?? T._("Это приложение нужно Windows — удалять его не стоит.");

    public bool IsChecked
    {
        get => Checks?.IsChecked(CheckKey) == true;
        set
        {
            // A protected app is never checked: its checkbox is disabled, and code that sets it is refused too.
            if (Checks is null || (value && !CanCheck))
            {
                OnPropertyChanged();
                return;
            }
            Checks.Set(CheckKey, value);
        }
    }

    /// <summary>The page's checked set changed: the checkbox reads it again.</summary>
    internal void OnCheckedChanged() => OnPropertyChanged(nameof(IsChecked));

    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version!;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ProgramBadge? Badge => Kind switch
    {
        StoreAppKind.Bloatware => new ProgramBadge(T._("Предустановленная, можно удалить"), T._("Можно удалить"), BadgeKind.Info,
            T._("Приложение поставляется вместе с Windows или ноутбуком. Если вы им не пользуетесь, его можно удалить — при необходимости оно снова устанавливается из Microsoft Store.")),
        StoreAppKind.Protected => new ProgramBadge(T._("Системное — не удалять"), T._("Не удалять"), BadgeKind.Neutral,
            ProtectReason ?? T._("Нужен Windows или другим приложениям.")),
        _ => null,
    };

    /// <summary>Signed by Microsoft Store: after removal it can be installed again from the Store.</summary>
    public bool IsFromStore => string.Equals(SignatureKind, "Store", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the user gets the app back after removal (only Store-signed packages come back from the Store).</summary>
    public string UndoText => IsFromStore
        ? T._("Вернуть приложение можно в любой момент — установите его заново из Microsoft Store.")
        : T._("Это приложение установлено не из Microsoft Store (его поставила другая программа или MSIX-файл), поэтому вернуть его через Store не получится — только переустановив программу, которая его установила.");

    public string SourceText => SignatureKind switch
    {
        "Store" => "Microsoft Store",
        "Developer" => T._("Установлено вручную (MSIX-пакет)"),
        "Enterprise" => T._("Установлено организацией"),
        _ => string.IsNullOrWhiteSpace(SignatureKind) ? "—" : SignatureKind!,
    };

    public string SearchText => $"{DisplayName} {Name} {PublisherDisplay}";
}
