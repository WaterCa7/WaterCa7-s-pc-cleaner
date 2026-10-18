using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

/// <summary>What the "Оценка" column shows.</summary>
public enum ProcessStatusKind
{
    Clean,
    Pending,
    NoAccess,
    Trusted,
    Low,
    Medium,
    High,
    /// <summary>WaterCa7s PC Cleaner itself or a helper it started: not judged.</summary>
    Own,
}

/// <summary>
/// What a checkbox of the process list stands for: one process (<see cref="Process"/>) or a whole program
/// (<see cref="Group"/>, the program's group key). A program's key never equals a process's key, so checking a program
/// does not check its processes one by one (they are taken at action time instead).
/// </summary>
internal readonly record struct ProcessCheckKey(ProcessKey Process, string? Group)
{
    public static ProcessCheckKey ForProcess(ProcessKey key) => new(key, null);

    public static ProcessCheckKey ForProgram(string groupKey) => new(default, groupKey);
}

/// <summary>
/// A row of the process grid: either a program (group of processes with the same executable) or a single process.
/// Rows are long-lived and updated in place on every refresh so the grid keeps selection and scroll position.
/// </summary>
public abstract class ProcessItem : ObservableObject
{
    /// <summary>Checkbox tooltip of a protected Windows process.</summary>
    internal const string CannotCheckText = "Системный процесс Windows — его нельзя завершать";

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private double _cpu;
    private long _memory;
    private string? _path;
    private string? _description;
    private string? _company;
    private SignatureInfo? _signature;
    private SuspicionResult? _suspicion;
    private bool _isTrusted;
    private bool _accessDenied;
    private bool _isChild;
    private bool _isFlat;
    private string? _defenderStatus;
    private bool _isDefenderScanning;
    private string? _searchText;
    private string? _quarantineBlockReason = "";
    private ProcessOwnRole _ownRole;

    internal KillVerdict Verdict { get; private set; }

    internal string? ProtectionReason { get; private set; }

    public abstract bool IsGroup { get; }

    /// <summary>The instance whose details are shown (for a group: the oldest process).</summary>
    public abstract ProcessRow? MainRow { get; }

    /// <summary>Executable file name, e.g. "chrome.exe".</summary>
    public abstract string ExeName { get; }

    /// <summary>Description from the file (e.g. "Google Chrome"), or the file name.</summary>
    public string FriendlyName => string.IsNullOrWhiteSpace(Description) ? ExeName : Description!;

    public abstract string PrimaryText { get; }

    public abstract string SecondaryText { get; }

    /// <summary>Shown in the details header under the name.</summary>
    public abstract string HeaderSubtitle { get; }

    public abstract string StartTimeText { get; }

    /// <summary>Group with more than one process (shows the expand arrow and a counter).</summary>
    public virtual bool HasChildren => false;

    public virtual string CountText => "";

    public virtual string CountToolTip => "";

    public virtual bool IsExpanded
    {
        get => false;
        set { }
    }

    /// <summary>Command line, parent and services are shown for single processes only.</summary>
    public virtual bool ShowInstanceDetails => true;

    public virtual string InstancesText => "";

    internal abstract int SortPid { get; }

    public double Cpu
    {
        get => _cpu;
        internal set
        {
            if (Math.Abs(_cpu - value) < 0.0001)
                return;
            _cpu = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuText));
        }
    }

    public string CpuText => ProcessMath.CpuText(Cpu);

    public long Memory
    {
        get => _memory;
        internal set
        {
            if (Set(ref _memory, value))
                OnPropertyChanged(nameof(MemoryText));
        }
    }

    public string MemoryText => Format.Bytes(Memory);

    public string? Path
    {
        get => _path;
        internal set
        {
            if (!Set(ref _path, value))
                return;
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(HasPath));
            OnPropertyChanged(nameof(SignatureBadgeText));
            OnPropertyChanged(nameof(SignatureText));
            OnPropertyChanged(nameof(ExeName));
            OnPropertyChanged(nameof(FriendlyName));
            OnTextChanged();
        }
    }

    public bool HasPath => Path is not null;

    public string PathText => Path ?? (AccessDenied
        ? Admin.IsElevated ? "Windows не даёт доступа к этому процессу" : "Нет доступа — нужны права администратора"
        : "У этого процесса нет файла на диске (часть ядра Windows)");

    public string? Description
    {
        get => _description;
        internal set
        {
            if (!Set(ref _description, value))
                return;
            OnPropertyChanged(nameof(FriendlyName));
            OnPropertyChanged(nameof(DescriptionText));
            OnTextChanged();
        }
    }

    public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? "—" : Description!;

    public string? Company
    {
        get => _company;
        internal set
        {
            if (!Set(ref _company, value))
                return;
            OnPropertyChanged(nameof(PublisherText));
            OnTextChanged();
        }
    }

    /// <summary>Verified signer if the signature is valid, otherwise the (unverified) company from the file.</summary>
    public string PublisherText
    {
        get
        {
            if (Signature is { IsValid: true, Publisher: { Length: > 0 } signer })
                return signer;
            if (!string.IsNullOrWhiteSpace(Company))
                return Company!;
            return Path is null && AccessDenied ? "нет доступа" : "—";
        }
    }

    public SignatureInfo? Signature
    {
        get => _signature;
        internal set
        {
            if (!Set(ref _signature, value))
                return;
            OnPropertyChanged(nameof(PublisherText));
            OnPropertyChanged(nameof(SignatureText));
            OnPropertyChanged(nameof(SignatureKind));
            OnPropertyChanged(nameof(SignatureBadgeText));
            OnPropertyChanged(nameof(CanTrust));
            OnPropertyChanged(nameof(TrustToolTip));
            OnStatusChanged();
            OnTextChanged();
        }
    }

    public string SignatureText => Signature?.StatusText ?? (Path is null ? "Не проверялась — нет доступа к файлу" : "Проверяется…");

    /// <summary>For the signature badge colour: Valid / Unsigned / Invalid / ... or null while unknown.</summary>
    public SignatureStatus? SignatureKind => Signature?.Status;

    public string SignatureBadgeText => Signature?.Status switch
    {
        SignatureStatus.Valid => "Подписан",
        SignatureStatus.Unsigned => "Без подписи",
        SignatureStatus.Invalid => "Подпись повреждена",
        SignatureStatus.Untrusted => "Недоверенная подпись",
        SignatureStatus.NotFound => "Файл не найден",
        SignatureStatus.Error => "Подпись не проверена",
        _ => Path is null ? "" : "Проверка подписи…",
    };

    /// <summary>"1" for a process, the number of processes for a program.</summary>
    public virtual string InstanceCountText => "1";

    /// <summary>Null while the signature is still being checked.</summary>
    public SuspicionResult? Suspicion
    {
        get => _suspicion;
        internal set
        {
            if (!Set(ref _suspicion, value))
                return;
            OnPropertyChanged(nameof(Reasons));
            OnPropertyChanged(nameof(HasReasons));
            OnPropertyChanged(nameof(IsSuspicious));
            OnPropertyChanged(nameof(IsVerifiedClean));
            OnPropertyChanged(nameof(CanTrust));
            OnPropertyChanged(nameof(TrustToolTip));
            OnStatusChanged();
        }
    }

    public IReadOnlyList<string> Reasons => Suspicion?.Reasons ?? Array.Empty<string>();

    public bool HasReasons => Reasons.Count > 0;

    public bool IsSuspicious => Suspicion?.IsSuspicious == true;

    /// <summary>Analysed and nothing found (shown as a green note in the details).</summary>
    public bool IsVerifiedClean => Suspicion is { Score: 0 } && !IsTrusted && !IsOwn && (Path is not null || IsPseudo);

    /// <summary>The cleaner itself or a helper it started (for a program: all of its processes are).</summary>
    public ProcessOwnRole OwnRole
    {
        get => _ownRole;
        internal set
        {
            if (!Set(ref _ownRole, value))
                return;
            OnPropertyChanged(nameof(IsOwn));
            OnPropertyChanged(nameof(IsVerifiedClean));
            OnOwnNoteChanged();
            OnStatusChanged();
            OnTextChanged();
        }
    }

    public bool IsOwn => OwnRole != ProcessOwnRole.None;

    /// <summary>Explanation in the details panel for the cleaner's own helpers (null when there is nothing to say).</summary>
    public virtual string? OwnNote => OwnRole == ProcessOwnRole.Helper
        ? "Это служебный процесс WaterCa7s PC Cleaner: программа запустила его сама, чтобы проверить систему. " +
          "Свои процессы программа не оценивает — они завершаются сами, когда проверка закончится."
        : null;

    protected void OnOwnNoteChanged() => OnPropertyChanged(nameof(OwnNote));

    public bool IsTrusted
    {
        get => _isTrusted;
        internal set
        {
            if (!Set(ref _isTrusted, value))
                return;
            OnPropertyChanged(nameof(TrustButtonText));
            OnPropertyChanged(nameof(TrustToolTip));
            OnPropertyChanged(nameof(CanTrust));
            OnPropertyChanged(nameof(IsVerifiedClean));
            OnStatusChanged();
        }
    }

    public bool AccessDenied
    {
        get => _accessDenied;
        internal set
        {
            if (!Set(ref _accessDenied, value))
                return;
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(PublisherText));
            OnStatusChanged();
        }
    }

    /// <summary>Kernel pseudo process without a file ("System", "Registry", ...).</summary>
    public bool IsPseudo => KnownWindowsProcesses.IsPseudo(ExeName);

    public ProcessStatusKind StatusKind
    {
        get
        {
            if (IsOwn)
                return ProcessStatusKind.Own;
            if (IsTrusted)
                return ProcessStatusKind.Trusted;
            if (Suspicion is { } s && s.Level > SuspicionLevel.None)
                return s.Level switch
                {
                    SuspicionLevel.High => ProcessStatusKind.High,
                    SuspicionLevel.Medium => ProcessStatusKind.Medium,
                    _ => ProcessStatusKind.Low,
                };
            if (Path is not null && Signature is null)
                return ProcessStatusKind.Pending;
            if (Path is null && AccessDenied && !KnownWindowsProcesses.IsKnown(ExeName))
                return ProcessStatusKind.NoAccess;
            return ProcessStatusKind.Clean;
        }
    }

    public string StatusText => StatusKind switch
    {
        ProcessStatusKind.High => "Высокий риск",
        ProcessStatusKind.Medium => "Подозрительно",
        ProcessStatusKind.Low => "Внимание",
        ProcessStatusKind.Trusted => "Доверенный",
        ProcessStatusKind.Pending => "Проверка…",
        ProcessStatusKind.NoAccess => "нет доступа",
        ProcessStatusKind.Own => OwnRole == ProcessOwnRole.Self ? "Эта программа" : "Служебный",
        _ => "",
    };

    public string StatusToolTip => StatusKind switch
    {
        ProcessStatusKind.Own => OwnRole == ProcessOwnRole.Self
            ? "Это сам WaterCa7s PC Cleaner. Свои процессы программа не оценивает."
            : "Служебный процесс WaterCa7s PC Cleaner: программа запустила его сама для проверки системы и не оценивает его.",
        ProcessStatusKind.High => "Несколько серьёзных признаков вредоносной программы. Откройте подробности справа.",
        ProcessStatusKind.Medium => "Есть признаки, из-за которых программу стоит проверить. Подробности — справа.",
        ProcessStatusKind.Low => "Мелкое замечание (например, нет цифровой подписи). Обычно это не опасно.",
        ProcessStatusKind.Trusted => "Вы отметили эту программу как доверенную.",
        ProcessStatusKind.Pending => "Проверяется цифровая подпись файла.",
        ProcessStatusKind.NoAccess => "Windows не даёт прочитать сведения об этом процессе без прав администратора.",
        _ => "Ничего подозрительного не найдено.",
    };

    /// <summary>Tooltip of the status icon on a narrow list: the badge's caption plus its explanation (null when clean).</summary>
    public string? StatusIconToolTip
    {
        get
        {
            if (StatusText is not { Length: > 0 } text)
                return null;
            var caption = char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
            return caption + (caption.EndsWith('…') ? " " : ". ") + StatusToolTip;
        }
    }

    /// <summary>Sort order for the "Оценка" column (risk first when descending).</summary>
    internal int StatusRank => StatusKind switch
    {
        ProcessStatusKind.High => 60 + (Suspicion?.Score ?? 0) / 10,
        ProcessStatusKind.Medium => 50,
        ProcessStatusKind.Low => 40,
        ProcessStatusKind.NoAccess => 20,
        ProcessStatusKind.Pending => 10,
        ProcessStatusKind.Trusted => 5,
        ProcessStatusKind.Own => 2,
        _ => 0,
    };

    /// <summary>Windows component signed by Microsoft (hidden by "Скрыть системные процессы Microsoft").</summary>
    public virtual bool IsMicrosoftSystem
        => Path is not null
            ? Signature?.IsMicrosoft == true && Core.Suspicion.IsUnder(Path, WindowsDir)
            : KnownWindowsProcesses.IsKnown(ExeName);

    public bool IsChild
    {
        get => _isChild;
        internal set
        {
            if (!Set(ref _isChild, value))
                return;
            OnPropertyChanged(nameof(PrimaryText));
            OnPropertyChanged(nameof(SecondaryText));
        }
    }

    /// <summary>Shown in the ungrouped list.</summary>
    internal bool IsFlat
    {
        get => _isFlat;
        set
        {
            if (_isFlat == value)
                return;
            _isFlat = value;
            OnPropertyChanged(nameof(PrimaryText));
            OnPropertyChanged(nameof(SecondaryText));
        }
    }

    // ---- protection / actions ----

    public bool CanKill => Verdict != KillVerdict.Blocked;

    public bool IsProtected => Verdict == KillVerdict.Blocked;

    public bool HasKillWarning => Verdict == KillVerdict.Warn;

    /// <summary>Why the process is protected or what the extra warning is (shown in the details panel).</summary>
    public string? ProtectionText => ProtectionReason;

    public string KillToolTip => Verdict == KillVerdict.Blocked
        ? ProtectionReason ?? ProcessGuard.CriticalText
        : IsGroup && this is ProcessGroup { Count: > 1 } g
            ? $"Принудительно завершить все процессы программы ({g.Count}). Несохранённые данные будут потеряны."
            : "Принудительно завершить процесс. Несохранённые данные будут потеряны.";

    public string KillTreeToolTip => Verdict == KillVerdict.Blocked
        ? ProtectionReason ?? ProcessGuard.CriticalText
        : "Завершить процесс и все запущенные им процессы (дочерние окна, помощники, установщики).";

    public bool CanQuarantine => _quarantineBlockReason is null;

    public string QuarantineToolTip => _quarantineBlockReason is { Length: > 0 } reason
        ? reason
        : _quarantineBlockReason is null
            ? "Завершить процесс и переместить файл в карантин. Файл переименуется и не сможет запуститься; восстановить его можно в «Настройках»."
            : "Подождите, идёт проверка.";

    // ---- checkbox ("Завершить отмеченные") ----

    /// <summary>The page's checked set (null for rows made outside the page, which then stay unchecked).</summary>
    internal CheckedSet<ProcessCheckKey>? Checks { get; init; }

    internal abstract ProcessCheckKey CheckKey { get; }

    /// <summary>Kept in the page's <see cref="CheckedSet{TKey}"/> by key, so the 2-second refresh never loses it.</summary>
    public bool IsChecked
    {
        get => Checks?.IsChecked(CheckKey) == true;
        set
        {
            // A protected process is never checked: its checkbox is disabled, and code that sets it is refused too.
            if (Checks is null || (value && !CanCheck))
            {
                OnPropertyChanged();
                return;
            }
            Checks.Set(CheckKey, value);
        }
    }

    /// <summary>Only what may be terminated can be checked (for a program: none of its processes is protected).</summary>
    public bool CanCheck => CanKill;

    /// <summary>Why the checkbox is disabled; null when it can be checked.</summary>
    public string? CheckTooltip => CanCheck
        ? null
        : ProtectionReason == ProcessGuard.SelfText ? ProcessGuard.SelfText : CannotCheckText;

    /// <summary>The page's checked set changed: the checkbox reads it again.</summary>
    internal void OnCheckedChanged() => OnPropertyChanged(nameof(IsChecked));

    public string TrustButtonText => IsTrusted ? "Не доверять" : "Доверять";

    /// <summary>
    /// Trusting only makes sense for a file with warnings (or to undo an earlier "Доверять"). A Microsoft-signed file is
    /// never flagged for itself — only for how it was started (PowerShell/cmd with a hidden script, a URL...). Trusting its
    /// path would silence those checks for every future script it runs, in all modules, so it is not offered.
    /// </summary>
    public bool CanTrust => Path is not null && (IsTrusted || (Suspicion is { Score: > 0 } && !IsMicrosoftSigned));

    private bool IsMicrosoftSigned => Signature?.IsMicrosoft == true;

    public string TrustToolTip => IsTrusted
        ? "Снова проверять эту программу и показывать предупреждения о ней."
        : CanTrust
            ? "Больше не считать эту программу подозрительной (во всех разделах). Используйте только для программ, в которых уверены."
            : Path is null
                ? "Нет доступа к файлу процесса."
                : IsMicrosoftSigned && Suspicion is { Score: > 0 }
                    ? "Это программа Microsoft: замечание относится к тому, с какими параметрами её запустили, а не к самому файлу. " +
                      "Отметить её доверенной нельзя — иначе приложение перестанет замечать опасные скрипты, запущенные через неё."
                    : "К этой программе нет замечаний — отмечать её доверенной не нужно.";

    public string? DefenderStatus
    {
        get => _defenderStatus;
        internal set => Set(ref _defenderStatus, value);
    }

    public bool IsDefenderScanning
    {
        get => _isDefenderScanning;
        internal set => Set(ref _isDefenderScanning, value);
    }

    /// <summary>Lower-case text the search box matches against.</summary>
    internal string SearchText => _searchText ??= BuildSearchText().ToLowerInvariant();

    internal abstract string BuildSearchText();

    protected void OnTextChanged() => _searchText = null;

    internal void InvalidateSearchText() => _searchText = null;

    internal void SetProtection(KillVerdict verdict, string? reason)
    {
        if (Verdict == verdict && ProtectionReason == reason)
            return;
        Verdict = verdict;
        ProtectionReason = reason;
        OnPropertyChanged(nameof(CanKill));
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(HasKillWarning));
        OnPropertyChanged(nameof(ProtectionText));
        OnPropertyChanged(nameof(KillToolTip));
        OnPropertyChanged(nameof(KillTreeToolTip));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CheckTooltip));
    }

    internal void SetQuarantineBlockReason(string? reason)
    {
        if (_quarantineBlockReason == reason)
            return;
        _quarantineBlockReason = reason;
        OnPropertyChanged(nameof(CanQuarantine));
        OnPropertyChanged(nameof(QuarantineToolTip));
    }

    protected void OnStatusChanged()
    {
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusToolTip));
        OnPropertyChanged(nameof(StatusIconToolTip));
    }
}

/// <summary>One running process.</summary>
public sealed class ProcessRow : ProcessItem
{
    private string? _commandLine;
    private bool _detailsLoaded;
    private IReadOnlyList<string> _services = Array.Empty<string>();
    private string _parentText = "—";
    private int _parentPid;
    private object? _suspicionInputs;

    internal ProcessRow(ProcessStaticInfo info)
    {
        Info = info;
        Pid = info.Key.Pid;
        CreateTime = info.Key.CreateTime;
        StartTime = ProcessMath.FromFileTime(info.Key.CreateTime);
    }

    internal ProcessStaticInfo Info { get; }

    internal ProcessKey Key => Info.Key;

    internal override ProcessCheckKey CheckKey => ProcessCheckKey.ForProcess(Key);

    internal long CreateTime { get; }

    internal ProcessGroup? Group { get; set; }

    public int Pid { get; }

    public override bool IsGroup => false;

    public override ProcessRow MainRow => this;

    public override string ExeName => Info.FileName;

    internal override int SortPid => Pid;

    public override string PrimaryText => IsChild ? ExeName : FriendlyName;

    public override string SecondaryText
    {
        get
        {
            if (IsChild)
                return Services.Count > 0 ? $"PID {Pid} · {ServicesShort}" : $"PID {Pid}";
            return string.Equals(FriendlyName, ExeName, StringComparison.OrdinalIgnoreCase)
                ? $"PID {Pid}"
                : $"{ExeName} · PID {Pid}";
        }
    }

    public override string HeaderSubtitle => $"{ExeName} · PID {Pid}";

    public DateTime? StartTime { get; }

    public override string StartTimeText => StartTime is { } t
        ? $"{Format.DateTime(t)} · работает {Format.Duration(DateTime.Now - t)}"
        : "—";

    public int ParentPid
    {
        get => _parentPid;
        internal set => Set(ref _parentPid, value);
    }

    public string ParentText
    {
        get => _parentText;
        internal set => Set(ref _parentText, value);
    }

    public string? CommandLine
    {
        get => _commandLine;
        internal set
        {
            if (!Set(ref _commandLine, value))
                return;
            OnPropertyChanged(nameof(CommandLineText));
            OnTextChanged();
        }
    }

    internal bool DetailsLoaded
    {
        get => _detailsLoaded;
        set
        {
            if (Set(ref _detailsLoaded, value))
                OnPropertyChanged(nameof(CommandLineText));
        }
    }

    public string CommandLineText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CommandLine))
                return CommandLine!;
            if (!DetailsLoaded)
                return "Загрузка…";
            if (IsPseudo)
                return "—";
            return Admin.IsElevated ? "Недоступна" : "Недоступна — для процессов других пользователей и служб нужны права администратора";
        }
    }

    public IReadOnlyList<string> Services
    {
        get => _services;
        internal set
        {
            if (ReferenceEquals(_services, value) || _services.SequenceEqual(value))
                return;
            _services = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasServices));
            OnPropertyChanged(nameof(ServicesText));
            OnPropertyChanged(nameof(SecondaryText));
            OnTextChanged();
        }
    }

    public bool HasServices => Services.Count > 0;

    public string ServicesText => string.Join("\n", Services);

    private string ServicesShort => Services.Count == 1 ? Services[0] : $"{Services[0]} и ещё {Services.Count - 1}";

    /// <summary>Kept the processor at 80%+ over several refreshes (see <see cref="CpuStreaks"/>).</summary>
    internal bool SustainedHighCpu { get; set; }

    /// <summary>Recomputes the suspicion only when its inputs changed (the analysis touches the disk).</summary>
    internal void Evaluate(Func<ProcessRow, SuspicionResult?> analyze, int trustVersion)
    {
        // IsTrusted is part of the inputs: other pages (Автозагрузка, Программы) can change the shared trusted list.
        var inputs = (Path, Signature, CommandLine, Description, Company, trustVersion, DetailsLoaded, IsTrusted,
            (OwnRole, SustainedHighCpu));
        if (Equals(_suspicionInputs, inputs))
            return;
        _suspicionInputs = inputs;
        Suspicion = analyze(this);
    }

    internal override string BuildSearchText()
        => string.Join(" ", ExeName, Description, Company, Signature?.Publisher, Path, Pid.ToString(), CommandLine,
            string.Join(" ", Services), IsOwn ? OwnProcesses.HelperLabel : null);
}

/// <summary>All processes of one program (same executable).</summary>
public sealed class ProcessGroup : ProcessItem
{
    private List<ProcessRow> _instances = new();
    private bool _isExpanded;
    private string? _ownSearchText;
    private string _exeName;

    internal ProcessGroup(string key, string exeName)
    {
        Key = key;
        _exeName = exeName;
    }

    internal string Key { get; }

    internal override ProcessCheckKey CheckKey => ProcessCheckKey.ForProgram(Key);

    public IReadOnlyList<ProcessRow> Instances => _instances;

    public int Count => _instances.Count;

    public override bool HasChildren => Count > 1;

    public override string CountText => Count.ToString();

    public override string InstanceCountText => Count.ToString();

    public override string CountToolTip => $"{Format.Count(Count, "процесс", "процесса", "процессов")}. Нажмите на стрелку, чтобы увидеть каждый.";

    public override bool IsGroup => true;

    public override ProcessRow? MainRow => _instances.Count > 0 ? _instances[0] : null;

    public override string ExeName => Path is { } p ? System.IO.Path.GetFileName(p) : _exeName;

    internal override int SortPid => MainRow?.Pid ?? 0;

    public override string PrimaryText => FriendlyName;

    public override string SecondaryText
        => string.Equals(FriendlyName, ExeName, StringComparison.OrdinalIgnoreCase) ? "" : ExeName;

    public override string HeaderSubtitle => Count == 1 && MainRow is { } r
        ? $"{ExeName} · PID {r.Pid}"
        : $"{ExeName} · {Format.Count(Count, "процесс", "процесса", "процессов")}";

    public override bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Single-process groups show the instance details (command line, parent...) directly.</summary>
    public override bool ShowInstanceDetails => Count == 1;

    public override string InstancesText
    {
        get
        {
            const int max = 12;
            var pids = string.Join(", ", _instances.Take(max).Select(r => r.Pid));
            return _instances.Count > max ? $"PID {pids} и ещё {_instances.Count - max}" : $"PID {pids}";
        }
    }

    public override string StartTimeText => MainRow?.StartTimeText ?? "—";

    public override bool IsMicrosoftSystem => _instances.Count > 0 && _instances.All(r => r.IsMicrosoftSystem);

    /// <summary>How many of the program's processes are the cleaner's own helpers.</summary>
    public int OwnCount => _instances.Count(r => r.IsOwn);

    /// <summary>A program whose processes are partly the cleaner's helpers (e.g. powershell.exe) explains which ones.</summary>
    public override string? OwnNote
    {
        get
        {
            if (IsOwn)
                return base.OwnNote;
            var own = OwnCount;
            if (own == 0)
                return null;
            return own == 1
                ? "Один из процессов — служебный процесс WaterCa7s PC Cleaner: программа запустила его сама, чтобы проверить систему, " +
                  "и не оценивает его."
                : $"{own} из {Count} {Format.Plural(Count, "процесса", "процессов", "процессов")} — служебные процессы WaterCa7s PC Cleaner: программа запустила их сама, " +
                  "чтобы проверить систему, и не оценивает их.";
        }
    }

    /// <summary>Recomputes what depends on the instances' own-process roles.</summary>
    internal void SetOwnRoleFromInstances()
    {
        var role = _instances.Count > 0 && _instances.All(r => r.IsOwn)
            ? _instances.Any(r => r.OwnRole == ProcessOwnRole.Self) ? ProcessOwnRole.Self : ProcessOwnRole.Helper
            : ProcessOwnRole.None;
        OwnRole = role;
        var note = OwnNote;
        if (note != _lastOwnNote)
        {
            _lastOwnNote = note;
            OnOwnNoteChanged();
        }
    }

    private string? _lastOwnNote;

    internal string OwnSearchText => _ownSearchText ??=
        string.Join(" ", ExeName, Description, Company, Signature?.Publisher, Path, IsOwn ? OwnProcesses.HelperLabel : null)
            .ToLowerInvariant();

    internal override string BuildSearchText()
    {
        _ownSearchText = null;
        // Command lines too, so "--type=renderer" or a script name finds the program in the (default) grouped view.
        return string.Join(" ", OwnSearchText, string.Join(" ", _instances.Select(r => r.Pid)),
            string.Join(" ", _instances.SelectMany(r => r.Services).Distinct()),
            string.Join(" ", _instances.Select(r => r.CommandLine).Where(c => !string.IsNullOrEmpty(c)).Distinct()),
            _instances.Any(r => r.IsOwn) ? OwnProcesses.HelperLabel : null);
    }

    /// <summary>Replaces the instance list (oldest first) and refreshes the totals.</summary>
    internal void SetInstances(List<ProcessRow> instances)
    {
        instances.Sort((a, b) =>
        {
            var r = a.CreateTime.CompareTo(b.CreateTime);
            return r != 0 ? r : a.Pid.CompareTo(b.Pid);
        });
        var changed = instances.Count != _instances.Count || !instances.SequenceEqual(_instances);
        _instances = instances;
        foreach (var r in instances)
            r.Group = this;
        if (instances.Count > 0)
            _exeName = instances[0].ExeName;
        if (changed)
        {
            OnPropertyChanged(nameof(Instances));
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(CountToolTip));
            OnPropertyChanged(nameof(MainRow));
            OnPropertyChanged(nameof(HeaderSubtitle));
            OnPropertyChanged(nameof(InstancesText));
            OnPropertyChanged(nameof(ShowInstanceDetails));
            OnPropertyChanged(nameof(StartTimeText));
            OnPropertyChanged(nameof(KillToolTip));
            InvalidateSearchText();
        }
        if (instances.Count <= 1 && IsExpanded)
            IsExpanded = false;
    }
}
