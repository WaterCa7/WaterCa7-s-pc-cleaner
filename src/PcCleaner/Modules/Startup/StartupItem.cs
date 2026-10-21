using System.IO;
using Microsoft.Win32;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

public enum StartupKind
{
    /// <summary>Value in a ...\CurrentVersion\Run key.</summary>
    RegistryRun,
    /// <summary>Value in a ...\CurrentVersion\RunOnce key (runs once at next logon, cannot be disabled).</summary>
    RegistryRunOnce,
    /// <summary>File/shortcut in a Startup folder.</summary>
    StartupFolder,
    /// <summary>Scheduled task with a logon or boot trigger.</summary>
    ScheduledTask,
    /// <summary>Automatic-start Win32 service.</summary>
    Service,
}

public enum StartupScope
{
    CurrentUser,
    AllUsers,
}

/// <summary>What the status badge shows.</summary>
public enum StartupState
{
    Enabled,
    Disabled,
    /// <summary>RunOnce entry waiting for the next logon.</summary>
    Pending,
    /// <summary>The target file no longer exists.</summary>
    Broken,
}

/// <summary>Source groups used by the segmented filter.</summary>
public enum StartupSourceFilter
{
    All,
    RegistryAndFolders,
    Tasks,
    Services,
}

/// <summary>Quick filters behind the summary cards.</summary>
public enum StartupStatusFilter
{
    All,
    Enabled,
    Disabled,
    Suspicious,
    Broken,
}

/// <summary>
/// One thing that starts automatically with Windows: a Run value, a Startup-folder shortcut,
/// a logon/boot scheduled task or an auto-start service.
/// </summary>
public sealed class StartupItem : ObservableObject
{
    private bool _isEnabled = true;
    private DateTime? _disabledTime;
    private SuspicionResult _suspicion = SuspicionResult.Clean;
    private bool _isTrusted;

    /// <summary>Stable identity used to keep the selection across refreshes.</summary>
    public required string Id { get; init; }

    public required StartupKind Kind { get; init; }

    public required StartupScope Scope { get; init; }

    public required string Name { get; init; }

    /// <summary>Full command line as Windows will run it.</summary>
    public string Command { get; init; } = "";

    /// <summary>The file that actually runs (CommandLine.TargetFile, .lnk target, ServiceDll...).</summary>
    public string? TargetFile { get; init; }

    /// <summary>Entry from the 32-bit (WOW6432Node) registry view.</summary>
    public bool Is32Bit { get; init; }

    /// <summary>Human-readable location: registry key, folder, task path or service key.</summary>
    public string LocationText { get; init; } = "";

    // ---- registry entries (Run / RunOnce) ----
    public RegistryHive Hive { get; init; }
    public string? RegistryKeyPath { get; init; }
    public string? RegistryValueName { get; init; }

    // ---- StartupApproved (Run and Startup folder entries) ----
    public RegistryHive ApprovedHive { get; init; }
    public string? ApprovedKeyPath { get; init; }
    public string? ApprovedValueName { get; init; }

    // ---- Startup folder ----
    /// <summary>Path of the shortcut/file inside the Startup folder.</summary>
    public string? FilePath { get; init; }

    // ---- scheduled tasks ----
    /// <summary>Full task path, e.g. "\Vendor\Updater".</summary>
    public string? TaskPath { get; init; }
    public string? TaskAuthor { get; init; }
    public string? TriggerText { get; init; }
    /// <summary>The task runs as the current user (no admin rights needed to change it).</summary>
    public bool TaskRunsAsCurrentUser { get; init; }

    // ---- services ----
    public string? ServiceName { get; init; }
    public string? ServiceStatusText { get; init; }
    /// <summary>Auto-start mode for enabled services ("auto" / "delayed-auto").</summary>
    public string? ServiceStartMode { get; init; }

    /// <summary>Description from the service or task, if any.</summary>
    public string? Description { get; init; }

    // ---- enrichment (filled after enumeration) ----
    public SignatureInfo? Signature { get; set; }

    /// <summary>Publisher from the signature, or the CompanyName of the file.</summary>
    public string? Publisher { get; set; }

    /// <summary>FileDescription from the version resource.</summary>
    public string? FileDescription { get; set; }

    /// <summary>true = exists, false = definitely missing, null = could not be checked.</summary>
    public bool? TargetExists { get; set; }

    public SuspicionResult Suspicion
    {
        get => _suspicion;
        set
        {
            if (Set(ref _suspicion, value))
                NotifyStateChanged();
        }
    }

    public bool IsTrusted
    {
        get => _isTrusted;
        set
        {
            if (Set(ref _isTrusted, value))
                NotifyStateChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
                NotifyStateChanged();
        }
    }

    public DateTime? DisabledTime
    {
        get => _disabledTime;
        set
        {
            if (Set(ref _disabledTime, value))
                NotifyStateChanged();
        }
    }

    // ---- derived flags ----

    public bool IsMicrosoft => Signature?.IsMicrosoft == true;

    public bool IsSigned => Signature?.IsValid == true;

    public bool IsBroken => TargetExists == false;

    public bool IsRegistry => Kind is StartupKind.RegistryRun or StartupKind.RegistryRunOnce;

    public bool IsFolder => Kind == StartupKind.StartupFolder;

    public bool IsTask => Kind == StartupKind.ScheduledTask;

    public bool IsService => Kind == StartupKind.Service;

    public bool IsRunOnce => Kind == StartupKind.RegistryRunOnce;

    public StartupSourceFilter SourceGroup => Kind switch
    {
        StartupKind.ScheduledTask => StartupSourceFilter.Tasks,
        StartupKind.Service => StartupSourceFilter.Services,
        _ => StartupSourceFilter.RegistryAndFolders,
    };

    /// <summary>
    /// Built-in Windows task: under \Microsoft\ and its target is Microsoft-signed, or cannot be checked at all (a COM handler
    /// hosted by Windows, a binary removed with a Windows component). Hidden by default and never changed.
    /// Tasks under \Microsoft\ that run an existing non-Microsoft file stay visible — that is a classic way for malware to hide —
    /// and so do suspicious ones: "powershell -w hidden -enc …" or "mshta http://…" run Microsoft-signed interpreters, so the
    /// signature alone must never shield them from being disabled or removed.
    /// </summary>
    public bool IsWindowsTask => IsTask &&
                                 (TaskPath ?? "").StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) &&
                                 !Suspicion.IsSuspicious &&
                                 (IsMicrosoft || string.IsNullOrWhiteSpace(TargetFile) || TargetExists != true);

    /// <summary>
    /// Microsoft service (or a Microsoft-signed binary in the Windows folder): hidden by default, never changed.
    /// A service that looks suspicious (e.g. "cmd.exe /c powershell -enc …") is never treated as a Windows service.
    /// </summary>
    public bool IsMicrosoftService => IsService && !Suspicion.IsSuspicious &&
                                      (IsMicrosoft || IsProtectedSystemFile(TargetFile, Signature));

    /// <summary>
    /// The target path depends on who logs on (%LOCALAPPDATA%, %APPDATA%, %USERPROFILE%… in a machine-wide entry or in a task
    /// of another account): a file missing in the current profile may exist for the user the entry really runs for.
    /// </summary>
    public bool TargetDependsOnUser { get; init; }

    /// <summary>Items the app never modifies.</summary>
    public bool IsProtected => IsWindowsTask || IsMicrosoftService;

    public bool CanToggle => !IsRunOnce && !IsProtected;

    public bool CanDelete => !IsService && !IsProtected;

    // ---- checkbox ("Отключить / Включить / Удалить" for several entries) ----

    /// <summary>
    /// The page's checked entries, by <see cref="Id"/> (null for items made outside the page, which then stay unchecked).
    /// A rescan builds new objects for the same entries: they read their check from here, so the checks survive it.
    /// </summary>
    internal CheckedSet<string>? Checks { get; set; }

    /// <summary>Only what the app may change can be checked: switched, or deleted (a service can't be, a run-once entry can't be switched).</summary>
    public bool CanCheck => CanToggle || CanDelete;

    /// <summary>Why the checkbox is disabled; null when it can be checked.</summary>
    public string? CheckTooltip => CanCheck ? null : ToggleBlockedReason;

    public bool IsChecked
    {
        get => Checks?.IsChecked(Id) == true;
        set
        {
            // What can't be changed is never checked: its checkbox is disabled, and code that sets it is refused too.
            if (Checks is null || (value && !CanCheck))
            {
                OnPropertyChanged();
                return;
            }
            Checks.Set(Id, value);
        }
    }

    /// <summary>The page's checked set changed: the checkbox reads it again.</summary>
    internal void OnCheckedChanged() => OnPropertyChanged(nameof(IsChecked));

    /// <summary>Changing the item needs administrator rights (HKLM, common Startup folder, services, system tasks).</summary>
    public bool RequiresAdmin => Kind switch
    {
        StartupKind.Service => true,
        StartupKind.ScheduledTask => !TaskRunsAsCurrentUser,
        _ => Scope == StartupScope.AllUsers,
    };

    public bool IsSuspicious => !IsTrusted && Suspicion.IsSuspicious;

    public SuspicionLevel RiskLevel => IsTrusted ? SuspicionLevel.None : Suspicion.Level;

    public int RiskScore => IsTrusted ? 0 : Suspicion.Score;

    public StartupState State => IsBroken ? StartupState.Broken
        : IsRunOnce ? StartupState.Pending
        : IsEnabled ? StartupState.Enabled
        : StartupState.Disabled;

    /// <summary>Sort key for the status column.</summary>
    public int StateSortKey => (int)State;

    // ---- display text ----

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? T._("(без имени)") : Name;

    public string PublisherText => !string.IsNullOrWhiteSpace(Publisher) ? Publisher!
        : TargetExists == false ? T._("Файл не найден")
        : T._("Издатель неизвестен");

    public string SignatureText => Signature?.StatusText ?? (TargetExists == false ? T._("Файл не найден") : T._("Подпись не проверена"));

    /// <summary>Who the entry starts for; the grid shows the short form of the same wording (<see cref="SourceDetail"/>).</summary>
    public string ScopeText => Scope == StartupScope.CurrentUser ? T._("Для вас") : T._("Для всех пользователей");

    public string SourceText => Kind switch
    {
        StartupKind.RegistryRun => Is32Bit ? T._("Реестр: Run (32-бит)") : T._("Реестр: Run"),
        StartupKind.RegistryRunOnce => Is32Bit ? T._("Реестр: RunOnce (32-бит)") : T._("Реестр: RunOnce"),
        StartupKind.StartupFolder => T._("Папка «Автозагрузка»"),
        StartupKind.ScheduledTask => T._("Планировщик заданий"),
        _ => T._("Служба Windows"),
    };

    /// <summary>Short source label for the grid.</summary>
    public string SourceShort => Kind switch
    {
        StartupKind.RegistryRun => T._("Реестр"),
        StartupKind.RegistryRunOnce => T._("Реестр, 1 раз"),
        StartupKind.StartupFolder => T._("Папка"),
        StartupKind.ScheduledTask => T._("Планировщик"),
        _ => T._("Служба"),
    };

    /// <summary>Second line under the source in the grid.</summary>
    public string SourceDetail => Kind switch
    {
        StartupKind.ScheduledTask => TriggerText ?? "",
        StartupKind.Service => ServiceStartMode == "delayed-auto" ? T._("отложенный запуск") : T._("автозапуск"),
        _ => Scope == StartupScope.CurrentUser ? T._("для вас") : T._("для всех"),
    };

    public string KindGlyph => Kind switch
    {
        StartupKind.StartupFolder => "",
        StartupKind.ScheduledTask => "",
        StartupKind.Service => "",
        _ => "",
    };

    public string StateText => State switch
    {
        StartupState.Broken => T._("Файл не найден"),
        StartupState.Pending => T._("Однократно"),
        StartupState.Enabled => T._("Включено"),
        _ => T._("Отключено"),
    };

    public string StateTooltip => State switch
    {
        StartupState.Broken => T._("Запись указывает на файл, которого больше нет. Она ничего не запускает — её можно безопасно удалить."),
        StartupState.Pending => T._("Windows выполнит эту команду один раз при следующем входе и сама удалит запись."),
        StartupState.Enabled => T._("Запускается автоматически вместе с Windows."),
        _ => DisabledTime is null
            ? T._("Отключено: Windows не запускает эту запись.")
            : T.F("Отключено {0}: Windows не запускает эту запись.", Format.DateTime(DisabledTime)),
    };

    public string StateGlyph => State switch
    {
        StartupState.Broken => "\uE7BA",
        StartupState.Pending => "\uE823",
        StartupState.Enabled => "\uE73E",
        _ => "\uE769",
    };

    public string RiskGlyph => IsTrusted ? "\uEA18" : RiskLevel switch
    {
        SuspicionLevel.High or SuspicionLevel.Medium => "\uE7BA",
        SuspicionLevel.Low => "\uE946",
        _ => "\uE73E",
    };

    public string RiskText => IsTrusted ? T._("Доверено") : RiskLevel switch
    {
        SuspicionLevel.High => T._("Высокий риск"),
        SuspicionLevel.Medium => T._("Подозрительно"),
        SuspicionLevel.Low => T._("Внимание"),
        _ => T._("Норма"),
    };

    public string RiskTooltip
    {
        get
        {
            if (IsTrusted)
                return T._("Вы отметили эту запись как доверенную.");
            if (Suspicion.Reasons.Count == 0)
                return IsMicrosoft ? T._("Компонент Microsoft с действительной подписью.") : T._("Подозрительных признаков не найдено.");
            return string.Join(Environment.NewLine, Suspicion.Reasons.Select(r => "• " + r));
        }
    }

    public bool HasReasons => !IsTrusted && Suspicion.Reasons.Count > 0;

    public IReadOnlyList<string> Reasons => IsTrusted ? Array.Empty<string>() : Suspicion.Reasons;

    public string ToggleActionText => IsEnabled ? T._("Отключить") : T._("Включить");

    public string TrustActionText => IsTrusted ? T._("Не доверять") : T._("Доверять");

    public string ToggleTooltip => !CanToggle
        ? ToggleBlockedReason
        : IsEnabled
            ? T._("Отключить автозапуск (обратимо, как в Диспетчере задач)")
            : T._("Снова включить автозапуск");

    public string ToggleBlockedReason => IsRunOnce
        ? T._("Однократный запуск нельзя отключить — Windows сама удалит запись после следующего входа. Если она не нужна, её можно удалить.")
        : IsWindowsTask
            ? T._("Это системное задание Windows — программа его не меняет.")
            : IsMicrosoftService
                ? T._("Это системная служба Microsoft — программа её не меняет.")
                : "";

    public string DisabledTimeText => DisabledTime is null ? "" : Format.DateTime(DisabledTime);

    /// <summary>Plain-language explanation of what this kind of entry is.</summary>
    public string Explanation => Kind switch
    {
        StartupKind.RegistryRun => Scope == StartupScope.CurrentUser
            ? T._("Запись в разделе реестра Run. Программа запускается каждый раз, когда вы входите в Windows.")
            : T._("Запись в разделе реестра Run для всего компьютера. Программа запускается при входе любого пользователя."),
        StartupKind.RegistryRunOnce =>
            T._("Однократный запуск: Windows выполнит команду при следующем входе и сама удалит запись. Так обычно завершают установку программ и обновлений."),
        StartupKind.StartupFolder => Scope == StartupScope.CurrentUser
            ? T._("Ярлык в вашей папке «Автозагрузка». Всё, что лежит в этой папке, открывается при входе в Windows.")
            : T._("Ярлык в общей папке «Автозагрузка». Он открывается при входе любого пользователя."),
        StartupKind.ScheduledTask =>
            T.F("Задание планировщика Windows, которое запускается {0}. Часто так работают программы обновления.", TriggerText ?? T._("автоматически")),
        _ => T._("Служба Windows с автоматическим запуском. Работает в фоне с момента включения компьютера, даже до входа в систему."),
    };

    /// <summary>Short safety advice for non-experts.</summary>
    public string Advice
    {
        get
        {
            if (IsProtected)
                return T._("Это часть Windows. Оставьте как есть.");
            if (IsBroken)
                return T._("Файл программы удалён, поэтому запись ничего не запускает. Её можно спокойно удалить.");
            if (IsSuspicious)
                return IsRunOnce
                    ? T._("Запись выглядит необычно. Проверьте файл на VirusTotal или Защитником Windows. Если программа вам незнакома — удалите эту запись (однократный запуск нельзя отключить).")
                    : T._("Запись выглядит необычно. Проверьте файл на VirusTotal или Защитником Windows. Если программа вам незнакома — отключите её.");
            if (IsRunOnce)
                return T._("Обычно эту запись лучше не трогать: Windows сама удалит её после следующего входа.");
            if (IsService)
                return T._("Службы драйверов, антивирусов и резервного копирования лучше оставить. Ручной режим безопасен: служба запустится, когда понадобится программе.");
            if (IsMicrosoft)
                return T._("Компонент Microsoft. Отключайте, только если не пользуетесь этой функцией.");
            if (IsTask)
                return T._("Отключать безопасно: задание просто перестанет запускаться. Если это обновление программы, она будет обновляться, когда вы её откроете.");
            return T._("Отключать безопасно: программа просто перестанет запускаться сама, а открыть её можно как обычно.");
        }
    }

    /// <summary>File name for web searches.</summary>
    public string SearchQuery
    {
        get
        {
            var file = string.IsNullOrWhiteSpace(TargetFile) ? null : Path.GetFileName(TargetFile);
            return string.IsNullOrWhiteSpace(file) ? T.F("{0} автозагрузка что это", DisplayName) : T.F("{0} что это за процесс", file);
        }
    }

    /// <summary>Raises change notifications for every derived property (also resets toggle switches bound one-way).</summary>
    public void NotifyStateChanged()
    {
        foreach (var name in DerivedProperties)
            OnPropertyChanged(name);
    }

    private static readonly string[] DerivedProperties =
    {
        nameof(IsEnabled), nameof(DisabledTime), nameof(State), nameof(StateGlyph), nameof(RiskGlyph), nameof(StateSortKey), nameof(StateText), nameof(StateTooltip),
        nameof(RiskLevel), nameof(RiskScore), nameof(RiskText), nameof(RiskTooltip), nameof(IsSuspicious), nameof(HasReasons),
        nameof(Reasons), nameof(ToggleActionText), nameof(TrustActionText), nameof(ToggleTooltip), nameof(DisabledTimeText), nameof(Advice),
        nameof(IsTrusted), nameof(Suspicion), nameof(CanToggle), nameof(CanDelete),
        nameof(IsWindowsTask), nameof(IsMicrosoftService), nameof(IsProtected), nameof(ToggleBlockedReason),
        nameof(CanCheck), nameof(CheckTooltip), nameof(IsChecked),
    };

    /// <summary>Microsoft-signed file inside the Windows folder (protected even if the item is not flagged as Microsoft).</summary>
    internal static bool IsProtectedSystemFile(string? path, SignatureInfo? signature)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Core.Suspicion.IsUnder(path, windows))
            return false;
        // Inside Windows: protected unless it carries a valid third-party signature (OEM drivers' helper services).
        return signature is null || !signature.IsValid || signature.IsMicrosoft;
    }
}
