using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

public enum DashboardScanState
{
    NotScanned,
    Scanning,
    Completed,
    Cancelled,
}

/// <summary>
/// "Главная": health score of the computer, live system tiles, the smart scan over all modules and quick links.
/// </summary>
public sealed class DashboardViewModel : PageViewModel
{
    private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IHealthContributor> _contributors;
    private readonly AppSettings? _settingsOverride;

    private bool _initialized;
    private Task<StaticSystemInfo?>? _staticTask;
    private CancellationTokenSource? _liveCts;

    private DashboardScanState _state = DashboardScanState.NotScanned;
    private ScanSummary? _summary;
    private bool _summaryFromThisSession;
    private bool _lastScanPersisted;
    private List<HealthIssue> _issues = new();
    private ObservableCollection<ModuleScanRow> _moduleRows = new();
    private ObservableCollection<IssueGroup> _groups = new();
    private ObservableCollection<SeverityChip> _severityChips = new();
    private int _completedModules;
    private int _fixedCount;
    private string? _actionSummary;
    private string? _scanWarning;
    private string? _reportStatus;
    private string? _reportStatusToolTip;
    private bool _reportStatusOk = true;

    // live tiles
    private double _cpuPercent;
    private string _cpuText = "…";
    private double _memoryPercent;
    private string _memoryText = "…";
    private string _memoryDetail = "";
    private double _diskPercent;
    private string _diskText = "…";
    private string _diskDetail = "";
    private string _diskTitle = "Системный диск";
    private string _uptimeText = "…";
    private string _uptimeDetail = "";
    private bool _isLongUptime;
    private bool _hasLiveData;

    // static info
    private StaticSystemInfo? _info;

    public DashboardViewModel()
        : this(AppServices.HealthContributors)
    {
    }

    internal DashboardViewModel(IEnumerable<IHealthContributor> contributors)
        : this(contributors, null)
    {
    }

    internal DashboardViewModel(IEnumerable<IHealthContributor> contributors, AppSettings? settings)
    {
        _contributors = contributors.Where(c => c is not null).ToList();
        _settingsOverride = settings;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        FixAllCommand = new AsyncRelayCommand(FixAllAsync, () => !IsBusy && FixAllCount > 0);
        NavigateCommand = new RelayCommand(p => Navigate(p as string));
        // Restarting closes the app: never offer it while a scan or a batch of fixes is running.
        RequestAdminCommand = new RelayCommand(() => Admin.RequireElevation("полная проверка компьютера"),
            () => !Admin.IsElevated && !IsBusy);

        SaveReportCommand = new AsyncRelayCommand(SaveReportAsync, () => !IsBusy && HasReport);
        CopyReportCommand = new AsyncRelayCommand(CopyReportAsync, () => !IsBusy && HasReport);

        // The first link is drawn as a wide tile above the others: 1 + 6 tiles fill every row with 3 or 2 columns.
        QuickLinks = new[]
        {
            new QuickLink("Cleaner", "Очистка", "Временные файлы, кэш браузеров и программ, корзина — освободите место без риска", ""),
            new QuickLink("Startup", "Автозагрузка", "Что запускается вместе с Windows", ""),
            new QuickLink("Processes", "Процессы", "Что работает в фоне прямо сейчас", ""),
            new QuickLink("Programs", "Программы", "Установленные и нежелательные", ""),
            new QuickLink("Optimizer", "Ускорение", "Настройки Windows и железо", ""),
            // U+2011 (non-breaking hyphen): a narrow tile must not wrap "Wi-" / "Fi".
            new QuickLink("Network", "Сеть", "Скорость, задержки, Wi\u2011Fi и порты", ""),
            new QuickLink("Security", "Безопасность", "Антивирус, брандмауэр, hosts", ""),
        };
        MoreLinks = QuickLinks.Skip(1).ToList();
    }

    public override string Title => "Главная";

    public override string Subtitle => "Общее состояние компьютера и проверка в один клик";

    public override string IconGlyph => "";

    private AppSettings Settings => _settingsOverride ?? AppServices.Settings;

    // ===================== Test / injection points =====================

    /// <summary>Per-module timeout of the smart scan (tests use a short one).</summary>
    internal TimeSpan ModuleTimeout { get; set; } = SmartScanner.DefaultModuleTimeout;

    /// <summary>Creates a restore point before a batch of fixes (tests replace it so nothing is created).</summary>
    internal Func<string, CancellationToken, Task<bool>> CreateRestorePointAsync { get; set; }
        = (description, ct) => RestorePoint.TryCreateAsync(description, ct);

    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    /// <summary>Static facts about the computer (tests replace the registry/WMI read).</summary>
    internal Func<StaticSystemInfo> ReadStaticInfo { get; set; } = SystemInfo.ReadStatic;

    /// <summary>Asks where to save the report (default: SaveFileDialog). Returns null when the user cancels.</summary>
    internal Func<string, string?> PickReportPath { get; set; } = ShowSaveReportDialog;

    /// <summary>Puts text on the clipboard (tests replace it; the real clipboard can be locked by another program).</summary>
    internal Action<string> SetClipboardText { get; set; } = Clipboard.SetText;

    /// <summary>Opens a Windows settings page or a web page from an issue row (tests replace it so nothing is opened).</summary>
    internal Action<string> OpenUri { get; set; } = ProcessRunner.ShellOpen;

    /// <summary>
    /// Offers to restart the app as administrator after "Исправить всё" (tests replace it so nothing restarts).
    /// Gets what needs the rights; the result is true when the offer was shown.
    /// </summary>
    internal Func<string, bool> RequestElevation { get; set; } = reason =>
    {
        Admin.RequireElevation(reason);
        return true;
    };

    internal IReadOnlyList<HealthIssue> CurrentIssues => _issues;

    // ===================== Commands =====================

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand FixAllCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand RequestAdminCommand { get; }

    public AsyncRelayCommand SaveReportCommand { get; }

    public AsyncRelayCommand CopyReportCommand { get; }

    public IReadOnlyList<QuickLink> QuickLinks { get; }

    /// <summary>Wide tile above the grid ("Очистка").</summary>
    public QuickLink FeaturedLink => QuickLinks[0];

    /// <summary>The other six tiles: 3 × 2 or 2 × 3, every row full.</summary>
    public IReadOnlyList<QuickLink> MoreLinks { get; }

    // ===================== Hero card =====================

    public DashboardScanState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
                return;
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(ShowIntro));
            OnPropertyChanged(nameof(ShowPreviousResult));
            OnPropertyChanged(nameof(ShowResults));
            OnPropertyChanged(nameof(ShowNoProblems));
            OnPropertyChanged(nameof(ShowScanningPlaceholder));
            OnPropertyChanged(nameof(IsCancelled));
            OnPropertyChanged(nameof(HasReport));
            RaiseHero();
        }
    }

    public bool IsScanning => State == DashboardScanState.Scanning;

    public bool IsCancelled => State == DashboardScanState.Cancelled;

    /// <summary>Score shown on the gauge: null before the first scan.</summary>
    public int? Score => _summary?.Score;

    public bool HasScore => Score is not null;

    public double GaugeValue => Score ?? 0;

    public string ScoreText => Score?.ToString(Format.Ru) ?? "—";

    /// <summary>Modules of the shown scan that failed or timed out: the score only counts the others.</summary>
    private int FailedModules => _summary?.FailedModules ?? 0;

    /// <summary>The shown score is incomplete (some modules could not be checked) — it may be too high.</summary>
    public bool IsIncomplete => FailedModules > 0;

    /// <summary>Theme brush key of the gauge ring and the verdict: never green for an incomplete scan.</summary>
    public string ScoreBrushKey => HealthScoring.ScoreBrushKey(Score, FailedModules);

    public string VerdictText => IsScanning ? "Идёт проверка…" : HealthScoring.Verdict(Score, FailedModules);

    public string VerdictDescription
    {
        get
        {
            if (IsScanning)
            {
                return _moduleRows.Count == 0
                    ? "Собираем сведения о компьютере…"
                    : $"Проверено разделов: {_completedModules} из {_moduleRows.Count}. Пока идёт проверка, ничего не меняется.";
            }
            return HealthScoring.VerdictDescription(Score, _summary?.ProblemCount ?? 0, FixedCount > 0, FailedModules);
        }
    }

    public string LastScanText => _summary is null
        ? "Проверка ещё не проводилась"
        : "Последняя проверка: " + HealthScoring.RelativeTime(_summary.Time, Now());

    /// <summary>Last scan is older than a week — nudge the user (not while a new scan is running).</summary>
    public bool IsScanOld => !IsScanning && _summary is not null && Now() - _summary.Time > TimeSpan.FromDays(7);

    /// <summary>Everything the remaining findings could free (see <see cref="Reclaimable"/> for the honest split).</summary>
    public long ReclaimableBytes => _summary?.ReclaimableBytes ?? 0;

    /// <summary>Fixable right here vs. only after the user reviews it (old downloads, Windows.old...).</summary>
    public ReclaimableSplit Reclaimable => _summary?.Reclaimable ?? default;

    public bool HasReclaimable => !IsScanning && ReclaimableBytes > 0;

    /// <summary>One honest line: "Можно освободить 2 ГБ (ещё до 40 ГБ — если решите удалить сами)".</summary>
    public string ReclaimableText => Reclaimable.SummaryText ?? "";

    public bool HasAutoReclaimable => !IsScanning && Reclaimable.AutoBytes > 0;

    public string AutoReclaimableText => Reclaimable.AutoText ?? "";

    public bool HasManualReclaimable => !IsScanning && Reclaimable.ManualBytes > 0;

    public string ManualReclaimableText => Reclaimable.ManualText ?? "";

    /// <summary>"Было 52 → стало 78 (+26)" against the scan before the last one; null on the first scan.</summary>
    public ScoreDelta? ScoreDelta => _summary?.Delta;

    public bool HasScoreDelta => !IsScanning && ScoreDelta is not null;

    public string ScoreDeltaText => ScoreDelta?.Text ?? "";

    public ScoreTrend ScoreTrend => ScoreDelta?.Trend ?? ScoreTrend.Same;

    public string ScoreDeltaToolTip => ScoreDelta?.Explanation(Now()) ?? "";

    /// <summary>The report can be saved/copied: a full scan finished in this session.</summary>
    public bool HasReport => State == DashboardScanState.Completed && _summary is not null && _summaryFromThisSession;

    /// <summary>Result of the last "Сохранить отчёт" / "Скопировать отчёт" (shown under the buttons).</summary>
    public string? ReportStatus
    {
        get => _reportStatus;
        private set
        {
            if (Set(ref _reportStatus, value))
                OnPropertyChanged(nameof(HasReportStatus));
        }
    }

    public bool HasReportStatus => !string.IsNullOrEmpty(ReportStatus);

    /// <summary>False when the last report action failed (warning icon instead of a check mark).</summary>
    public bool ReportStatusOk
    {
        get => _reportStatusOk;
        private set => Set(ref _reportStatusOk, value);
    }

    /// <summary>Details of <see cref="ReportStatus"/> (e.g. the full path of the saved file).</summary>
    public string? ReportStatusToolTip
    {
        get => _reportStatusToolTip;
        private set => Set(ref _reportStatusToolTip, value);
    }

    private void SetReportStatus(string? text, bool ok = true, string? toolTip = null)
    {
        ReportStatusOk = ok;
        ReportStatusToolTip = toolTip ?? text;
        ReportStatus = text;
    }

    public int RemainingIssueCount => _summary?.IssueCount ?? 0;

    public bool HasIssueCount => _summary is not null && !IsScanning;

    public string IssueCountText
    {
        get
        {
            var problems = _summary?.ProblemCount ?? 0;
            var tips = _summary?.TipCount ?? 0;
            if (problems == 0 && tips == 0)
            {
                if (IsIncomplete)
                    return FixedCount > 0 ? "Найденные проблемы исправлены" : "В проверенных разделах проблем нет";
                return FixedCount > 0 ? "Все проблемы исправлены" : "Проблем не найдено";
            }
            if (problems == 0)
                return "Проблем нет · " + Format.Count(tips, "совет", "совета", "советов");
            return "Найдено: " + HealthScoring.FindingsText(problems, tips);
        }
    }

    public string ScanButtonText => _summary is null ? "Проверить компьютер" : "Проверить снова";

    public bool IsAdmin => Admin.IsElevated;

    // ===================== Results =====================

    public bool ShowIntro => State == DashboardScanState.NotScanned && _summary is null;

    public bool ShowPreviousResult => State == DashboardScanState.NotScanned && _summary is not null;

    public bool ShowResults => State != DashboardScanState.NotScanned;

    public bool ShowScanningPlaceholder => IsScanning && _issues.Count == 0;

    /// <summary>"Проблем не найдено · Компьютер в отличной форме" — never when some modules could not be checked.</summary>
    public bool ShowNoProblems => State == DashboardScanState.Completed && _issues.Count == 0 && !IsIncomplete;

    public bool HasIssues => _issues.Count > 0;

    public string PreviousResultText
    {
        get
        {
            if (_summary is null)
                return "";
            var sb = new StringBuilder();
            sb.Append($"Прошлая проверка ({HealthScoring.RelativeTime(_summary.Time, Now())}): ");
            sb.Append(_summary.IssueCount == 0
                ? "проблем не найдено."
                : $"найдено {HealthScoring.FindingsText(_summary.ProblemCount, _summary.TipCount)}"
                  + (_summary.ImportantCount > 0 ? $", из них важных — {_summary.ImportantCount}." : "."));
            if (_summary.FailedModules > 0)
                sb.Append(" Часть разделов тогда проверить не удалось.");
            sb.Append(" Список проблем не сохраняется между запусками — проверьте компьютер снова, чтобы увидеть подробности и исправить их.");
            return sb.ToString();
        }
    }

    public ObservableCollection<ModuleScanRow> ModuleRows
    {
        get => _moduleRows;
        private set => Set(ref _moduleRows, value);
    }

    public ObservableCollection<IssueGroup> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public ObservableCollection<SeverityChip> SeverityChips
    {
        get => _severityChips;
        private set => Set(ref _severityChips, value);
    }

    public string ResultsSummaryText
    {
        get
        {
            if (IsScanning)
                return $"Проверено разделов: {_completedModules} из {_moduleRows.Count}";
            if (_issues.Count == 0)
                return IsCancelled ? "Проверка остановлена до завершения"
                    : _moduleRows.All(r => r.State == ModuleScanState.Done) ? "Все разделы проверены" : "Проверка завершена";
            var problems = _issues.Count(HealthScoring.IsProblem);
            var text = $"Найдено {HealthScoring.FindingsText(problems, _issues.Count - problems)}";
            if (FixedCount > 0)
                text += $", исправлено {FixedCount}";
            var fixable = _issues.Count(i => i.CanFix);
            if (fixable > 0)
                text += $" · можно исправить отсюда: {fixable}";
            return text;
        }
    }

    /// <summary>Issues "Исправить всё" runs: fixable ones except those that open their own windows, restart or uninstall.</summary>
    public int FixAllCount => _issues.Count(i => i.CanFix && i.InFixAll);

    public bool HasFixAll => !IsScanning && FixAllCount > 0;

    public string FixAllText => $"Исправить всё ({FixAllCount})";

    public int FixedCount
    {
        get => _fixedCount;
        private set
        {
            if (Set(ref _fixedCount, value))
                OnPropertyChanged(nameof(ShowRescanHint));
        }
    }

    public bool ShowRescanHint => FixedCount > 0 && !IsScanning;

    /// <summary>Summary of the last fix action shown in the results card.</summary>
    public string? ActionSummary
    {
        get => _actionSummary;
        private set => Set(ref _actionSummary, value);
    }

    /// <summary>"Не удалось проверить: …" / "Проверка остановлена …".</summary>
    public string? ScanWarning
    {
        get => _scanWarning;
        private set => Set(ref _scanWarning, value);
    }

    // ===================== Live tiles =====================

    public bool HasLiveData
    {
        get => _hasLiveData;
        private set => Set(ref _hasLiveData, value);
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        private set => Set(ref _cpuPercent, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set => Set(ref _cpuText, value);
    }

    public string CpuDetail => _info is null
        ? "Загрузка всех ядер"
        : Format.Count(_info.LogicalProcessors, "логический процессор", "логических процессора", "логических процессоров");

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set => Set(ref _memoryPercent, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => Set(ref _memoryText, value);
    }

    public string MemoryDetail
    {
        get => _memoryDetail;
        private set => Set(ref _memoryDetail, value);
    }

    public string DiskTitle
    {
        get => _diskTitle;
        private set => Set(ref _diskTitle, value);
    }

    public double DiskPercent
    {
        get => _diskPercent;
        private set => Set(ref _diskPercent, value);
    }

    public string DiskText
    {
        get => _diskText;
        private set => Set(ref _diskText, value);
    }

    public string DiskDetail
    {
        get => _diskDetail;
        private set => Set(ref _diskDetail, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => Set(ref _uptimeText, value);
    }

    public string UptimeDetail
    {
        get => _uptimeDetail;
        private set => Set(ref _uptimeDetail, value);
    }

    /// <summary>More than 7 days without a restart.</summary>
    public bool IsLongUptime
    {
        get => _isLongUptime;
        private set => Set(ref _isLongUptime, value);
    }

    // ===================== Static info =====================

    public bool HasInfo => _info is not null;

    public string WindowsName => _info?.WindowsName ?? "…";

    public string WindowsVersion => _info?.WindowsVersion ?? "…";

    public string ComputerName => _info?.ComputerName ?? "…";

    public string CpuName => _info?.CpuName ?? "…";

    public string RamText => _info is null ? "…" : Format.Bytes(_info.InstalledRamBytes);

    public string GpuLabel => _info?.Gpus.Count > 1 ? "Видеокарты" : "Видеокарта";

    public string GpuText => _info is null ? "…" : _info.Gpus.Count == 0 ? "Нет данных" : string.Join("\n", _info.Gpus);

    public string AccessText => Admin.IsElevated ? "Администратор — доступны все проверки" : "Обычный пользователь — часть проверок ограничена";

    // ===================== Navigation lifecycle =====================

    public override Task OnNavigatedToAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            try
            {
                _summary = ScanSummary.Load(Settings);
            }
            catch (Exception ex)
            {
                Log.Warn($"Главная: не удалось загрузить результат прошлой проверки: {ex.Message}");
            }
            RaiseHero();
            OnPropertyChanged(nameof(ShowIntro));
            OnPropertyChanged(nameof(ShowPreviousResult));
            _staticTask = LoadStaticAsync();
        }
        else
        {
            RaiseTimeTexts();
        }
        StartLive();
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom() => StopLive();

    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        await EnsureStaticInfoAsync();
        // Read-only: AnalyzeAsync of every module is read-only by contract. The result is not persisted, so
        // verification runs never overwrite the user's real "last scan".
        await RunBusyAsync("Проверяем компьютер…", async busyCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyCt, ct);
            await ScanCoreAsync(linked.Token, persist: false);
        });
        // Let the gauge animation settle before the screenshot is taken.
        await Task.Delay(900, CancellationToken.None);
    }

    private Task<StaticSystemInfo?> EnsureStaticInfoAsync() => _staticTask ??= LoadStaticAsync();

    private async Task<StaticSystemInfo?> LoadStaticAsync()
    {
        try
        {
            var read = ReadStaticInfo;
            var info = await Task.Run(read);
            Ui.Post(() =>
            {
                _info = info;
                OnPropertyChanged(nameof(HasInfo));
                OnPropertyChanged(nameof(WindowsName));
                OnPropertyChanged(nameof(WindowsVersion));
                OnPropertyChanged(nameof(ComputerName));
                OnPropertyChanged(nameof(CpuName));
                OnPropertyChanged(nameof(CpuDetail));
                OnPropertyChanged(nameof(RamText));
                OnPropertyChanged(nameof(GpuLabel));
                OnPropertyChanged(nameof(GpuText));
            });
            return info;
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось прочитать сведения о компьютере: {ex.Message}");
            return null;
        }
    }

    private void StartLive()
    {
        if (_liveCts is not null)
            return;
        _liveCts = new CancellationTokenSource();
        _ = LiveLoopAsync(_liveCts.Token);
    }

    private void StopLive()
    {
        var cts = _liveCts;
        _liveCts = null;
        if (cts is null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task LiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var snapshot = await Task.Run(SystemInfo.ReadLive, ct);
                if (ct.IsCancellationRequested)
                    break;
                Ui.Post(() => ApplyLive(snapshot));
                await Task.Delay(LiveInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // page left
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: обновление показателей остановлено: {ex.Message}");
        }
    }

    internal void ApplyLive(LiveSnapshot s)
    {
        if (s.CpuPercent is { } cpu)
        {
            CpuPercent = cpu;
            CpuText = Format.Percent(Math.Round(cpu));
        }
        else
        {
            CpuText = "н/д";
        }

        if (s.Memory is { } m && m.TotalBytes > 0)
        {
            MemoryPercent = 100.0 * m.UsedBytes / m.TotalBytes;
            MemoryText = Format.Percent(Math.Round(MemoryPercent));
            MemoryDetail = $"Занято {Format.Bytes(m.UsedBytes)} из {Format.Bytes(m.TotalBytes)}";
        }
        else
        {
            MemoryText = "н/д";
            MemoryDetail = "Не удалось получить данные";
        }

        if (s.SystemDriveName is not null && s.SystemDriveTotal > 0)
        {
            DiskTitle = $"Системный диск ({s.SystemDriveName})";
            DiskPercent = 100.0 * (s.SystemDriveTotal - s.SystemDriveFree) / s.SystemDriveTotal;
            DiskText = Format.Bytes(s.SystemDriveFree);
            DiskDetail = $"свободно из {Format.Bytes(s.SystemDriveTotal)} · занято {Format.Percent(Math.Round(DiskPercent))}";
        }
        else
        {
            DiskText = "н/д";
            DiskDetail = "Не удалось прочитать диск";
        }

        UptimeText = Format.Duration(s.Uptime);
        IsLongUptime = s.Uptime > TimeSpan.FromDays(7);
        UptimeDetail = IsLongUptime
            ? "Давно без перезагрузки — перезагрузите компьютер"
            : "Включён " + HealthScoring.RelativeTime(Now() - s.Uptime, Now());
        HasLiveData = true;
        RaiseTimeTexts();
    }

    private void RaiseTimeTexts()
    {
        OnPropertyChanged(nameof(LastScanText));
        OnPropertyChanged(nameof(ScoreDeltaToolTip));
        OnPropertyChanged(nameof(IsScanOld));
        OnPropertyChanged(nameof(PreviousResultText));
    }

    private void RaiseHero()
    {
        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(HasScore));
        OnPropertyChanged(nameof(GaugeValue));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(IsIncomplete));
        OnPropertyChanged(nameof(ScoreBrushKey));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(VerdictDescription));
        OnPropertyChanged(nameof(ReclaimableBytes));
        OnPropertyChanged(nameof(Reclaimable));
        OnPropertyChanged(nameof(HasReclaimable));
        OnPropertyChanged(nameof(ReclaimableText));
        OnPropertyChanged(nameof(HasAutoReclaimable));
        OnPropertyChanged(nameof(AutoReclaimableText));
        OnPropertyChanged(nameof(HasManualReclaimable));
        OnPropertyChanged(nameof(ManualReclaimableText));
        OnPropertyChanged(nameof(ScoreDelta));
        OnPropertyChanged(nameof(HasScoreDelta));
        OnPropertyChanged(nameof(ScoreDeltaText));
        OnPropertyChanged(nameof(ScoreTrend));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(RemainingIssueCount));
        OnPropertyChanged(nameof(HasIssueCount));
        OnPropertyChanged(nameof(IssueCountText));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(ShowRescanHint));
        RaiseTimeTexts();
    }

    private void RaiseResults()
    {
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(ShowNoProblems));
        OnPropertyChanged(nameof(ShowScanningPlaceholder));
        OnPropertyChanged(nameof(ResultsSummaryText));
        OnPropertyChanged(nameof(FixAllCount));
        OnPropertyChanged(nameof(HasFixAll));
        OnPropertyChanged(nameof(FixAllText));
        RelayCommand.Refresh();
    }

    // ===================== Smart scan =====================

    private Task ScanAsync() => RunBusyAsync("Проверяем компьютер…", ct => ScanCoreAsync(ct, persist: true));

    /// <summary>
    /// Runs AnalyzeAsync of every module in parallel (each isolated, with its own timeout), then computes the score,
    /// fills the issue groups and (if <paramref name="persist"/>) saves the summary. Never throws on cancellation.
    /// </summary>
    internal async Task<ScanOutcome> ScanCoreAsync(CancellationToken ct, bool persist)
    {
        var contributors = _contributors;
        var rows = contributors.Select(c => new ModuleScanRow(SafeName(c))).ToList();
        var total = rows.Count;

        Ui.Invoke(() =>
        {
            _issues = new List<HealthIssue>();
            _completedModules = 0;
            ModuleRows = new ObservableCollection<ModuleScanRow>(rows);
            Groups = new ObservableCollection<IssueGroup>();
            SeverityChips = new ObservableCollection<SeverityChip>();
            FixedCount = 0;
            ActionSummary = null;
            ScanWarning = null;
            SetReportStatus(null);
            State = DashboardScanState.Scanning;
            RaiseResults();
        });
        Log.Info($"Главная: проверка компьютера запущена ({Format.Count(total, "раздел", "раздела", "разделов")})");
        ReportProgress(total == 0 ? -1 : 0, "Проверяем компьютер…");

        var completed = 0;

        async Task<ModuleScanResult> RunRowAsync(IHealthContributor contributor, ModuleScanRow row)
        {
            Ui.Post(row.SetRunning);
            var result = await SmartScanner.RunModuleAsync(contributor, ModuleTimeout, ct);
            var done = Interlocked.Increment(ref completed);
            Ui.Post(() =>
            {
                row.SetResult(result);
                _completedModules = done;
                OnPropertyChanged(nameof(VerdictDescription));
                OnPropertyChanged(nameof(ResultsSummaryText));
            });
            if (!ct.IsCancellationRequested)
                ReportProgress(100.0 * done / Math.Max(1, total), $"Проверяем компьютер: готово {done} из {total} — {result.ModuleName}");
            return result;
        }

        try
        {
            var results = await Task.WhenAll(contributors.Select((c, i) => RunRowAsync(c, rows[i])));
            var cancelled = ct.IsCancellationRequested;
            var issues = results.Where(r => r.State == ModuleScanState.Done).SelectMany(r => r.Issues).ToList();
            int? score = null;

            Ui.Invoke(() =>
            {
                _issues = issues;
                BuildGroups();

                var failed = results.Where(r => r.State is ModuleScanState.Failed or ModuleScanState.TimedOut)
                    .Select(r => r.ModuleName).ToList();
                if (cancelled)
                {
                    ScanWarning = "Проверка остановлена. Показаны только проблемы из уже проверенных разделов, оценка не обновлена.";
                    State = DashboardScanState.Cancelled;
                }
                else
                {
                    score = HealthScoring.ComputeScore(issues);
                    // "Было → стало": compare with what the previous scan MEASURED. Its post-fix score is only an
                    // estimate; comparing with it would show "worse" right after fixing if a fix freed a bit less.
                    // An incomplete previous scan (failed modules, score possibly inflated) passes on the baseline it
                    // inherited instead. Failed modules of THIS scan only add 0 points, so its score may be too high:
                    // the summary is marked incomplete (verdict "Проверка неполная", no delta, never a baseline).
                    var baseline = _summary?.NextBaseline;
                    _summary = MakeSummary(score.Value, score.Value, Now(), failed.Count, baseline?.Score, baseline?.Time);
                    _summaryFromThisSession = true;
                    _lastScanPersisted = persist;
                    if (persist)
                        SaveSummary();
                    if (failed.Count > 0)
                        ScanWarning = $"Не удалось проверить: {string.Join(", ", failed)}. Оценка может быть неточной — откройте эти разделы, чтобы посмотреть подробности.";
                    State = DashboardScanState.Completed;
                }
                RaiseHero();
                RaiseResults();
            });

            if (cancelled)
            {
                Log.Info("Главная: проверка остановлена пользователем");
            }
            else
            {
                var problems = issues.Count(HealthScoring.IsProblem);
                var notChecked = results.Count(r => r.State is ModuleScanState.Failed or ModuleScanState.TimedOut);
                Log.Info($"Главная: проверка завершена — оценка {score}" +
                         (notChecked > 0 ? $" (неполная: не проверено {Format.Count(notChecked, "раздел", "раздела", "разделов")})" : "") +
                         ", " + (issues.Count == 0 ? "проблем не найдено" : "найдено " + HealthScoring.FindingsText(problems, issues.Count - problems)));
            }
            return new ScanOutcome(cancelled, score, results);
        }
        catch
        {
            // Never leave the page stuck in "Идёт проверка…" with spinning rows when something unexpected breaks
            // (e.g. malformed module data). RunBusyAsync reports the exception itself.
            RecoverFromBrokenScan(rows);
            throw;
        }
    }

    private void RecoverFromBrokenScan(IReadOnlyList<ModuleScanRow> rows)
    {
        try
        {
            Ui.Invoke(() =>
            {
                foreach (var row in rows.Where(r => r.State is ModuleScanState.Waiting or ModuleScanState.Running))
                    row.SetResult(new ModuleScanResult(row.ModuleName, ModuleScanState.Cancelled, Array.Empty<HealthIssue>(), null, TimeSpan.Zero));
                if (State != DashboardScanState.Scanning)
                    return;
                ScanWarning = "Проверка прервалась из-за внутренней ошибки, оценка не обновлена. Попробуйте проверить компьютер ещё раз.";
                State = DashboardScanState.Cancelled;
                RaiseHero();
                RaiseResults();
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось восстановить состояние после ошибки проверки: {ex.Message}");
        }
    }

    private static string SafeName(IHealthContributor c)
    {
        try
        {
            return string.IsNullOrWhiteSpace(c.ModuleName) ? c.GetType().Name : c.ModuleName;
        }
        catch
        {
            return c.GetType().Name;
        }
    }

    private ScanSummary MakeSummary(int score, int measuredScore, DateTime time, int failedModules, int? previousScore,
        DateTime? previousTime)
    {
        var split = ReclaimableSplit.From(_issues);
        return new ScanSummary
        {
            Score = score,
            MeasuredScore = measuredScore,
            Time = time,
            IssueCount = _issues.Count(i => !i.IsFixed),
            TipCount = _issues.Count(i => !i.IsFixed && !HealthScoring.IsProblem(i)),
            ImportantCount = _issues.Count(i => !i.IsFixed && i.Severity >= IssueSeverity.High),
            FixableCount = _issues.Count(i => i.CanFix),
            ReclaimableBytes = split.TotalBytes,
            ManualReclaimableBytes = split.ManualBytes,
            FailedModules = failedModules,
            PreviousScore = previousScore,
            PreviousTime = previousScore is null ? null : previousTime,
        };
    }

    private void SaveSummary()
    {
        try
        {
            _summary?.Save(Settings);
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось сохранить результат проверки: {ex.Message}");
        }
    }

    private void BuildGroups()
    {
        var groups = HealthScoring.Group(_issues)
            .Select(g => new IssueGroup(g.Severity, g.Issues.Select(CreateItem)))
            .ToList();
        Groups = new ObservableCollection<IssueGroup>(groups);
        BuildChips();
    }

    /// <summary>Severity counters of the remaining (not fixed) issues.</summary>
    private void BuildChips()
        => SeverityChips = new ObservableCollection<SeverityChip>(_groups
            .Where(g => g.RemainingCount > 0)
            .Select(g => new SeverityChip(g.Severity, g.Title, g.RemainingCount)));

    private IssueItem CreateItem(HealthIssue issue)
    {
        var item = new IssueItem(issue);
        item.FixCommand = new AsyncRelayCommand(() => FixIssueAsync(item), () => !IsBusy && !item.IsFixing && item.Issue.CanFix);
        item.OpenPageCommand = new RelayCommand(() => Navigate(issue.TargetPageKey), () => item.HasTargetPage);
        item.OpenSettingsCommand = new RelayCommand(() => OpenSettings(item), () => item.HasSettings);
        item.CopyCommand = new RelayCommand(() => CopyIssue(issue));
        item.SearchCommand = new RelayCommand(() => FileTools.SearchWeb(issue.Title ?? ""));
        // The UAC prompt must name what actually needs the rights (this fix), not the whole check.
        item.RequestAdminCommand = new RelayCommand(
            () => Admin.RequireElevation($"исправление «{issue.Title}»"),
            () => !Admin.IsElevated && !IsBusy);
        return item;
    }

    private IEnumerable<IssueItem> AllItems => _groups.SelectMany(g => g.AllItems);

    /// <summary>"Открыть настройки" / "Открыть страницу" on a row. Only opens a page: nothing on the system changes.</summary>
    private void OpenSettings(IssueItem item)
    {
        var uri = item.Issue.SettingsUri;
        if (string.IsNullOrWhiteSpace(uri))
            return;
        try
        {
            OpenUri(uri);
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось открыть {uri}: {ErrorText.For(ex)}");
            Dialogs.Error("Не удалось открыть", $"{uri}\n\n{ErrorText.Sentence(ex)}");
        }
    }

    // ===================== Fixes =====================

    /// <summary>"Исправить" on one row: confirm → FixAsync → result on the row.</summary>
    internal async Task FixIssueAsync(IssueItem item)
    {
        var issue = item.Issue;
        if (IsBusy || item.IsFixing || !issue.CanFix)
            return;

        if (!Dialogs.Confirm("Исправить проблему", FixConfirmText(item)))
        {
            Log.Info($"Главная: исправление «{issue.Title}» отменено пользователем");
            return;
        }

        await RunBusyAsync($"Исправляем: {issue.Title}", async ct =>
        {
            ReportProgress(-1);
            await FixOneCoreAsync(item, ct);
        });
        AfterFixes();
        ActionSummary = issue.IsFixed
            ? $"Исправлено: {issue.Title}. {issue.FixResult}"
            : $"Не исправлено: {issue.Title}. {issue.FixResult}";
    }

    /// <summary>Confirmation text of a single fix: the problem, its explanation, the expected result and who does it.</summary>
    internal static string FixConfirmText(IssueItem item)
    {
        var issue = item.Issue;
        var message = new StringBuilder();
        message.AppendLine($"Проблема: {issue.Title}");
        if (!string.IsNullOrWhiteSpace(issue.Details))
            message.AppendLine().AppendLine(ShortenForDialog(issue.Details));
        if (item.GainText is { } gain)
            message.AppendLine().AppendLine($"Что изменится: {gain}");
        message.AppendLine()
            .AppendLine($"Исправление выполнит раздел «{issue.Module}» — только то, что описано выше. " +
                        "Всё сделанное запишется в журнал («Настройки» → журнал действий).")
            .AppendLine().Append("Исправить сейчас?");
        return message.ToString();
    }

    /// <summary>
    /// Keeps a message box on the screen: very long module texts are cut (the full text stays visible on the issue row).
    /// </summary>
    internal static string ShortenForDialog(string text, int maxChars = 1200, int maxLines = 20)
    {
        var lines = text.Trim().Replace("\r\n", "\n").Split('\n');
        var cut = lines.Length > maxLines;
        var result = string.Join(Environment.NewLine, lines.Take(maxLines)).TrimEnd();
        if (result.Length > maxChars)
        {
            result = result[..maxChars].TrimEnd();
            cut = true;
        }
        return cut ? result + "…" + Environment.NewLine + "(полное описание — в строке проблемы на главной странице)" : result;
    }

    /// <summary>Appended to the module's text when "Отмена" stopped a fix that then returned what it managed to do.</summary>
    internal const string InterruptedNote =
        "Исправление было остановлено — возможно, сделано не всё. Нажмите «Проверить снова», чтобы увидеть, что осталось.";

    /// <summary>
    /// Runs one fix. Returns true if it succeeded. Rethrows only cancellation — also when the fix itself did not throw
    /// but returned early after "Отмена" (cleaning returns what it deleted so far): such a fix is never marked fixed.
    /// </summary>
    internal async Task<bool> FixOneCoreAsync(IssueItem item, CancellationToken ct)
    {
        var issue = item.Issue;
        var fix = issue.FixAsync;
        if (fix is null || issue.IsFixed)
            return false;

        item.IsFixing = true;
        item.FixFailed = false;
        item.AccessDenied = false;
        string? stoppedResult = null;
        try
        {
            // Task.Run: a module's fix must never block the UI thread.
            var result = await Task.Run(() => fix(ct), ct);
            var text = string.IsNullOrWhiteSpace(result) ? "Готово." : result.Trim();
            if (ct.IsCancellationRequested)
            {
                // Several fixes stop early on cancellation and return a partial result instead of throwing: the
                // problem may be only partly fixed, so it stays open (no "Исправлено", its penalty and size stay).
                stoppedResult = text;
                ct.ThrowIfCancellationRequested();
            }
            issue.FixResult = text;
            if (HealthScoring.LooksLikeFailure(text))
            {
                item.FixFailed = true;
                item.RaiseNeedsAdmin();
                Log.Warn($"Главная: «{issue.Title}» ({issue.Module}) не исправлено: {text}");
                return false;
            }
            issue.IsFixed = true;
            Log.Action($"Главная: исправлено «{issue.Title}» ({issue.Module}) — {text}");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (stoppedResult is null)
            {
                issue.FixResult = "Исправление отменено.";
            }
            else
            {
                issue.FixResult = stoppedResult + Environment.NewLine + InterruptedNote;
                Log.Warn($"Главная: исправление «{issue.Title}» ({issue.Module}) остановлено, проблема не отмечена исправленной: {stoppedResult}");
            }
            item.FixFailed = true;
            throw;
        }
        catch (Exception ex)
        {
            issue.FixResult = $"Не удалось исправить: {ErrorText.For(ex)}.";
            item.AccessDenied = ex is UnauthorizedAccessException or SecurityException;
            item.FixFailed = true;
            item.RaiseNeedsAdmin();
            if (ex is UnauthorizedAccessException or SecurityException or Win32Exception or IOException)
                Log.Warn($"Главная: не удалось исправить «{issue.Title}» ({issue.Module}): {ex.Message}");
            else
                Log.Error($"Главная: ошибка при исправлении «{issue.Title}» ({issue.Module})", ex);
            return false;
        }
        finally
        {
            item.IsFixing = false;
        }
    }

    /// <summary>
    /// "Исправить всё (N)": ONE confirmation listing every CanFix &amp;&amp; InFixAll issue → restore point →
    /// fixes one by one → summary (→ offer to restart as administrator when some fixes needed the rights).
    /// Returns null if nothing was done.
    /// </summary>
    internal async Task<FixBatchResult?> FixAllAsync()
    {
        if (IsBusy)
            return null;
        var items = AllItems.Where(i => i.Issue.CanFix && i.Issue.InFixAll).ToList();
        if (items.Count == 0)
            return null;

        if (!Dialogs.Confirm("Исправить всё", FixAllConfirmText(items)))
        {
            Log.Info("Главная: «Исправить всё» отменено пользователем");
            return null;
        }

        FixBatchResult? batch = null;
        await RunBusyAsync("Исправляем…", async ct =>
        {
            int ok = 0, failed = 0, needAdmin = 0;
            long freed = 0;
            var cancelled = false;
            var interrupted = false;
            IssueItem? running = null;
            try
            {
                ReportProgress(-1, "Создаём точку восстановления…");
                await CreateRestorePointAsync("Исправления с главной страницы", ct);

                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[i];
                    ReportProgress(100.0 * i / items.Count, $"Исправляем ({i + 1} из {items.Count}): {item.Issue.Title}");
                    running = item;
                    var fixedOk = await FixOneCoreAsync(item, ct);
                    running = null;
                    if (fixedOk)
                    {
                        // Only fully completed fixes count (a fix stopped by "Отмена" throws above): the scan's
                        // estimate of a completed fix is what "Освобождено примерно" reports.
                        ok++;
                        freed = freed > long.MaxValue - Math.Max(0, item.Issue.ReclaimableBytes)
                            ? long.MaxValue
                            : freed + Math.Max(0, item.Issue.ReclaimableBytes);
                    }
                    else
                    {
                        failed++;
                        if (item.FixFailed && HealthScoring.LooksLikeAdminRequired(item.Issue.FixResult))
                            needAdmin++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // "Отмена" during the restore point or between/inside fixes: report what was done so far. A fix
                // stopped while running may have changed part of what it promised — never say "ничего не изменено".
                cancelled = true;
                interrupted = running is not null;
            }
            ReportProgress(100);
            batch = new FixBatchResult(items.Count, ok, failed, cancelled, freed, interrupted, needAdmin);
        });

        AfterFixes();
        if (batch is not null)
        {
            var text = BatchSummaryText(batch);
            ActionSummary = text;
            Log.Info("Главная: " + text);
            Dialogs.Info(batch.Cancelled ? "Исправление остановлено" : "Исправление завершено", text);
            // The rights are asked for once, after the summary: the restart closes the program.
            if (batch.NeedAdmin > 0 && !Admin.IsElevated)
                RequestElevation("исправления, которым нужны права администратора");
        }
        return batch;
    }

    /// <summary>
    /// The ONE confirmation of "Исправить всё". Every item is named: up to 20 one per line (with gain and module),
    /// longer lists compactly per module so the message box still fits on the screen.
    /// </summary>
    internal static string FixAllConfirmText(IReadOnlyList<IssueItem> items)
    {
        const int maxDetailedLines = 20;
        var message = new StringBuilder();
        message.AppendLine(items.Count == 1
            ? "Будет исправлено:"
            : $"Будут исправлены проблемы ({items.Count}):");
        message.AppendLine();
        if (items.Count <= maxDetailedLines)
        {
            foreach (var item in items)
                message.AppendLine($"• {item.Issue.Title}" + (item.GainText is { } g ? $" — {g}" : "") + $" ({item.Issue.Module})");
        }
        else
        {
            foreach (var module in items.GroupBy(i => i.Issue.Module ?? ""))
                message.AppendLine($"• {module.Key} ({module.Count()}): " + string.Join("; ", module.Select(i => i.Issue.Title)));
        }
        message.AppendLine()
            .AppendLine("Каждое действие можно отменить вручную: файлы уходят в Корзину, настройки Windows включаются " +
                        "и выключаются обратно. Удаление программ и перезагрузка сюда не входят — для них есть отдельные кнопки.")
            .AppendLine("Перед изменениями программа попробует создать точку восстановления Windows (если это включено " +
                        "в настройках и есть права администратора). Если в Windows выключена защита системы, точка не " +
                        "создаётся — программа её не включает сама. Всё сделанное запишется в журнал.")
            .AppendLine().Append("Продолжить?");
        return message.ToString();
    }

    internal static string BatchSummaryText(FixBatchResult r)
    {
        if (r.Cancelled && r.Fixed == 0 && r.Failed == 0 && !r.Interrupted)
            return "Исправление отменено — ничего не изменено.";
        var sb = new StringBuilder();
        sb.Append(r.Fixed == r.Total
            ? $"Исправлено {HealthScoring.ProblemsText(r.Fixed)}."
            : $"Исправлено {r.Fixed} из {r.Total}.");
        if (r.FreedBytes > 0)
            sb.Append($" Освобождено примерно {Format.Bytes(r.FreedBytes)}.");
        if (r.Failed > 0)
            sb.Append($" Не получилось: {r.Failed} — причина указана в строке проблемы.");
        if (r.NeedAdmin > 0)
            sb.Append($" Для {r.NeedAdmin} нужны права администратора.");
        if (r.Interrupted)
            sb.Append(" Исправление, которое шло в момент отмены, остановлено на середине — часть изменений могла " +
                      "успеть выполниться (подробности — в строке проблемы).");
        if (r.Cancelled && r.Fixed + r.Failed + (r.Interrupted ? 1 : 0) < r.Total)
            sb.Append(" Остальные исправления отменены.");
        sb.Append(" Нажмите «Проверить снова», чтобы обновить оценку.");
        return sb.ToString();
    }

    /// <summary>Re-computes the score and counters after fixes (fixed issues no longer count).</summary>
    private void AfterFixes()
    {
        FixedCount = _issues.Count(i => i.IsFixed);
        if (State == DashboardScanState.Completed && _summary is not null && _summaryFromThisSession)
        {
            _summary = MakeSummary(HealthScoring.ComputeScore(_issues), _summary.BaselineScore, _summary.Time,
                _summary.FailedModules, _summary.PreviousScore, _summary.PreviousTime);
            if (_lastScanPersisted)
                SaveSummary();
        }
        foreach (var g in _groups)
            g.Refresh();
        BuildChips();
        RaiseHero();
        RaiseResults();
    }

    // ===================== Report =====================

    /// <summary>Collects everything the report shows: score, computer, module results and findings with fix results.</summary>
    internal async Task<DashboardReport?> BuildReportAsync()
    {
        var summary = _summary;
        if (!HasReport || summary is null)
            return null;
        // Snapshot before awaiting: a new scan started meanwhile must not mix into this report.
        var modules = _moduleRows.Select(ReportModule.From).ToList();
        var groups = DashboardReport.GroupIssues(_issues);
        var warning = ScanWarning;

        var info = await EnsureStaticInfoAsync();
        var drive = await Task.Run(SystemInfo.ReadSystemDrive);
        return new DashboardReport
        {
            CreatedAt = Now(),
            ScanTime = summary.Time,
            Score = summary.Score,
            Delta = summary.Delta,
            Reclaimable = summary.Reclaimable,
            Pc = new ReportPcInfo
            {
                ComputerName = info?.ComputerName ?? Environment.MachineName,
                WindowsName = info?.WindowsName ?? "Windows",
                WindowsVersion = info?.WindowsVersion ?? "",
                CpuName = info?.CpuName ?? "",
                LogicalProcessors = info?.LogicalProcessors ?? Environment.ProcessorCount,
                RamBytes = info?.InstalledRamBytes ?? 0,
                Gpus = info?.Gpus ?? Array.Empty<string>(),
                SystemDrive = drive.Name,
                SystemDriveFree = drive.Free,
                SystemDriveTotal = drive.Total,
                Uptime = SystemStats.Uptime,
                IsAdmin = Admin.IsElevated,
            },
            Modules = modules,
            Groups = groups,
            Warning = warning,
            FailedModules = summary.FailedModules,
        };
    }

    /// <summary>
    /// "Сохранить отчёт": file dialog (Documents, "Отчёт WaterCa7s PC Cleaner &lt;дата&gt;.html") → self-contained HTML →
    /// journal → offer to open it. Returns the saved path, or null if nothing was saved.
    /// </summary>
    internal async Task<string?> SaveReportAsync()
    {
        if (IsBusy || !HasReport)
            return null;

        var path = PickReportPath(ReportBuilder.DefaultFileName(Now()));
        if (string.IsNullOrWhiteSpace(path))
        {
            Log.Info("Главная: сохранение отчёта отменено");
            return null;
        }

        var report = await BuildReportAsync();
        if (report is null)
            return null;
        var html = ReportBuilder.BuildHtml(report);
        try
        {
            // BOM: old browsers and Notepad then never guess a wrong (non-UTF-8) encoding.
            await Task.Run(() => File.WriteAllText(path, html, new UTF8Encoding(true)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or NotSupportedException or ArgumentException)
        {
            Log.Warn($"Главная: не удалось сохранить отчёт в «{path}»: {ex.Message}");
            SetReportStatus("Отчёт не сохранён — выберите другую папку", ok: false);
            Dialogs.Error("Не удалось сохранить отчёт",
                $"{path}\n\n{ErrorText.Sentence(ex)}\n\nПопробуйте сохранить отчёт в другую папку, например в «Документы».");
            return null;
        }

        Log.Action($"Главная: отчёт о проверке сохранён — {path}");
        SetReportStatus("Отчёт сохранён", toolTip: path);
        ActionSummary = $"Отчёт о проверке сохранён: {path}";
        if (!IsHtmlFile(path))
        {
            // The user typed another extension (.txt, .bat, ...): never hand such a file to the shell — it would not
            // open in a browser and could even be run as a script (issue details contain program paths).
            Dialogs.Info("Отчёт сохранён",
                $"Отчёт о проверке сохранён в файл:\n{path}\n\nЭто веб-страница: чтобы открыть её в браузере, " +
                "сохраните отчёт с расширением .html.");
            return path;
        }
        if (Dialogs.Ask("Отчёт сохранён",
                $"Отчёт о проверке сохранён в файл:\n{path}\n\nЕго можно открыть в любом браузере, распечатать или отправить. " +
                "Открыть отчёт в браузере?"))
            ProcessRunner.ShellOpen(path);
        return path;
    }

    /// <summary>"Скопировать отчёт": the plain-text version to the clipboard. Returns true on success.</summary>
    internal async Task<bool> CopyReportAsync()
    {
        if (IsBusy || !HasReport)
            return false;
        var report = await BuildReportAsync();
        if (report is null)
            return false;

        var text = ReportBuilder.BuildText(report);
        try
        {
            SetClipboardText(text);
        }
        catch (Exception ex)
        {
            // Another program may hold the clipboard open (CLIPBRD_E_CANT_OPEN) — an environment problem, not a bug.
            Log.Warn($"Главная: не удалось скопировать отчёт в буфер обмена: {ex.Message}");
            SetReportStatus("Буфер обмена занят — попробуйте ещё раз", ok: false);
            Dialogs.Error("Не удалось скопировать отчёт",
                "Буфер обмена сейчас занят другой программой. Подождите пару секунд и нажмите «Скопировать отчёт» ещё раз " +
                "или сохраните отчёт в файл.");
            return false;
        }

        Log.Info("Главная: отчёт о проверке скопирован в буфер обмена");
        SetReportStatus("Отчёт скопирован — вставьте его клавишами Ctrl+V");
        return true;
    }

    /// <summary>Only .html / .htm files are offered to be opened in the browser.</summary>
    internal static bool IsHtmlFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ShowSaveReportDialog(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить отчёт о проверке",
            FileName = defaultFileName,
            DefaultExt = ".html",
            AddExtension = true,
            Filter = "Веб-страница HTML (*.html)|*.html",
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var owner = Application.Current?.MainWindow;
        var ok = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    // ===================== Misc actions =====================

    private void Navigate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        var navigator = AppServices.Navigator;
        if (navigator is null)
        {
            Log.Warn($"Главная: навигация недоступна (страница «{key}»)");
            return;
        }
        navigator.NavigateTo(key);
    }

    private static void CopyIssue(HealthIssue issue)
    {
        var text = $"{issue.Title}\n{issue.Details}".Trim();
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось скопировать в буфер обмена: {ex.Message}");
        }
    }
}
