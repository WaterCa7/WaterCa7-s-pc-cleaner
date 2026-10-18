using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

public sealed class ProcessesViewModel : PageViewModel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ProcessesService _service;
    private readonly CpuSampler _sampler = new();
    private readonly CpuStreaks _streaks = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<ProcessKey, ProcessRow> _rows = new();
    private readonly Dictionary<string, ProcessGroup> _groups = new();
    private readonly HashSet<string> _requestedSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _pending = new();
    private readonly Dictionary<string, string> _defenderResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _defenderRunning = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _timer;
    private Task? _loadTask;
    private bool _refreshing;
    private bool _loadedOnce;
    private bool _isActive;
    private bool _liveRefresh;
    private bool _windowHidden;
    private bool _detailsRunning;
    private int _detailsDirty;
    private bool _syncingItems;
    private bool _selectionDisturbed;
    private int _trustVersion;
    private int _signaturesRequested;
    private int _signaturesDone;
    private string? _lastRefreshError;
    private bool _loggedFirstLoad;

    private ProcessItem? _selectedItem;
    private string _searchText = "";
    private bool _groupByProgram = true;
    private bool _suspiciousOnly;
    private bool _hideMicrosoft;
    private bool _isPaused;
    private ProcessSortColumn _sortColumn = ProcessSortColumn.Memory;
    private bool _sortDescending = true;
    private bool _hasLoaded;

    private double _cpuPercent;
    private string _cpuText = "—";
    private double _memoryPercent;
    private string _memoryText = "—";
    private string _memoryDetails = "";
    private int _processCount;
    private int _programCount;
    private int _suspiciousCount;
    private int _suspiciousProgramCount;
    private int _inaccessibleCount;
    private int _visibleCount;

    public ProcessesViewModel(ProcessesService service)
    {
        _service = service;

        KillCommand = new AsyncRelayCommand(p => KillAsync(Target(p), tree: false), p => Target(p) is { CanKill: true });
        KillTreeCommand = new AsyncRelayCommand(p => KillAsync(Target(p), tree: true), p => Target(p) is { CanKill: true });
        OpenLocationCommand = new RelayCommand(p => OpenLocation(Target(p)), p => Target(p) is { HasPath: true });
        CopyPathCommand = new RelayCommand(p => CopyPath(Target(p)), p => Target(p) is { HasPath: true });
        SearchWebCommand = new RelayCommand(p => SearchWeb(Target(p)), p => Target(p) is not null);
        VirusTotalCommand = new AsyncRelayCommand(p => VirusTotalAsync(Target(p)), p => Target(p) is { HasPath: true });
        DefenderCommand = new RelayCommand(p => _ = ScanWithDefenderAsync(Target(p)),
            p => Target(p) is { HasPath: true, IsDefenderScanning: false });
        QuarantineCommand = new AsyncRelayCommand(p => QuarantineAsync(Target(p)), p => Target(p) is { CanQuarantine: true });
        TrustCommand = new RelayCommand(p => ToggleTrust(Target(p)), p => Target(p) is { CanTrust: true });
        ToggleExpandCommand = new RelayCommand(p => ToggleExpand(p as ProcessItem));
        TogglePauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        ShowSuspiciousCommand = new RelayCommand(() =>
        {
            SuspiciousOnly = true;
            SelectedItem ??= Items.FirstOrDefault();
            Reveal(SelectedItem);
        });
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        RestartAsAdminCommand = new RelayCommand(() => Admin.RequireElevation("Проверка всех процессов"), () => !Admin.IsElevated);

        Checked.Changed += (_, _) => OnChecksChanged();
        KillCheckedCommand = new AsyncRelayCommand(KillCheckedAsync, () => Checked.Count > 0);
        ClearChecksCommand = new RelayCommand(Checked.Clear);
        ToggleCheckCommand = new RelayCommand(p => ToggleCheck(p as ProcessItem), p => p is ProcessItem { CanCheck: true });
        ToggleAllVisibleCommand = new RelayCommand(ToggleAllVisible, () => Items.Any(i => i.CanCheck));
    }

    public override string Title => "Процессы";

    public override string Subtitle => "Что сейчас работает на компьютере, сколько ресурсов занимает и нет ли среди этого подозрительного";

    public override string IconGlyph => "";

    /// <summary>Pause between the two samples of the very first snapshot (so CPU % is meaningful right away).</summary>
    internal TimeSpan BaselineDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    // ================================================================ bindable state

    /// <summary>Rows shown in the grid (groups and, when expanded, their processes).</summary>
    public ObservableCollection<ProcessItem> Items { get; } = new();

    public ProcessItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_syncingItems)
            {
                // The grid briefly drops the selection while rows move; keep ours and restore it afterwards.
                _selectionDisturbed = true;
                return;
            }
            if (Set(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>Asks the view to scroll a row into view (raised when the page itself changes the selection).</summary>
    public event Action<ProcessItem>? RevealRequested;

    private void Reveal(ProcessItem? item)
    {
        if (item is not null)
            RevealRequested?.Invoke(item);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
                UpdateViewSafe();
        }
    }

    public bool GroupByProgram
    {
        get => _groupByProgram;
        set
        {
            if (Set(ref _groupByProgram, value))
                UpdateViewSafe();
        }
    }

    public bool SuspiciousOnly
    {
        get => _suspiciousOnly;
        set
        {
            if (Set(ref _suspiciousOnly, value))
                UpdateViewSafe();
        }
    }

    public bool HideMicrosoft
    {
        get => _hideMicrosoft;
        set
        {
            if (Set(ref _hideMicrosoft, value))
                UpdateViewSafe();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!Set(ref _isPaused, value))
                return;
            OnPropertyChanged(nameof(PauseGlyph));
            OnPropertyChanged(nameof(PauseToolTip));
            if (!value && _isActive)
                _ = SafeRefreshAsync();
        }
    }

    public string PauseGlyph => IsPaused ? "" : "";

    public string PauseToolTip => IsPaused
        ? "Обновление приостановлено. Нажмите, чтобы снова обновлять список каждые 2 секунды."
        : "Список обновляется каждые 2 секунды. Нажмите, чтобы приостановить (удобно, если строки «прыгают»).";

    /// <summary>Sort key for the grid header arrows: Name, Cpu, Memory, Publisher or Status.</summary>
    public string SortKey => _sortColumn.ToString();

    public bool SortDescending => _sortDescending;

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set
        {
            if (Set(ref _hasLoaded, value))
                UpdateEmptyState();
        }
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

    public string MemoryDetails
    {
        get => _memoryDetails;
        private set => Set(ref _memoryDetails, value);
    }

    public int ProcessCount
    {
        get => _processCount;
        private set
        {
            if (Set(ref _processCount, value))
                OnPropertyChanged(nameof(ProcessCountText));
        }
    }

    public string ProcessCountText => HasLoaded ? Format.Number(ProcessCount) : "—";

    public int ProgramCount
    {
        get => _programCount;
        private set
        {
            if (Set(ref _programCount, value))
                OnPropertyChanged(nameof(ProgramCountText));
        }
    }

    public string ProgramCountText => HasLoaded
        ? $"в {Format.Number(ProgramCount)} {Format.Plural(ProgramCount, "программе", "программах", "программах")}"
        : "загрузка…";

    /// <summary>Suspicious processes (the big number on the card, like the "Процессы" card).</summary>
    public int SuspiciousCount
    {
        get => _suspiciousCount;
        private set
        {
            if (!Set(ref _suspiciousCount, value))
                return;
            OnPropertyChanged(nameof(HasSuspicious));
            OnSuspiciousTextChanged();
        }
    }

    /// <summary>Programs they belong to — the rows the grouped list shows with "Только подозрительные".</summary>
    public int SuspiciousProgramCount
    {
        get => _suspiciousProgramCount;
        private set
        {
            if (Set(ref _suspiciousProgramCount, value))
                OnSuspiciousTextChanged();
        }
    }

    public bool HasSuspicious => SuspiciousCount > 0;

    public string SuspiciousCountText => HasLoaded ? Format.Number(SuspiciousCount) : "—";

    /// <summary>"в 1 программе" under the number (mirrors "в 119 программах" on the "Процессы" card).</summary>
    public string SuspiciousHint
    {
        get
        {
            if (!HasLoaded)
                return "загрузка…";
            if (SuspiciousCount > 0)
                return $"в {Format.Number(SuspiciousProgramCount)} {Format.Plural(SuspiciousProgramCount, "программе", "программах", "программах")}";
            return IsCheckingSignatures ? "идёт проверка подписей…" : "ничего подозрительного";
        }
    }

    /// <summary>"2 процесса в 1 программе" — the card's tooltip and the summary used by tests.</summary>
    public string SuspiciousSummary
    {
        get
        {
            if (!HasLoaded)
                return "Подозрительные процессы ещё проверяются.";
            const string signs = "Признаки нежелательных программ: нет цифровой подписи, необычное расположение, " +
                                 "маскировка под системные файлы, скрытый запуск скриптов.";
            if (SuspiciousCount == 0)
                return (IsCheckingSignatures
                    ? "Пока ничего подозрительного, но проверка цифровых подписей ещё идёт."
                    : "Ни один из запущенных процессов не вызывает подозрений.") + "\n\n" + signs;
            return $"{Format.Count(SuspiciousCount, "подозрительный процесс", "подозрительных процесса", "подозрительных процессов")} " +
                   $"в {Format.Number(SuspiciousProgramCount)} {Format.Plural(SuspiciousProgramCount, "программе", "программах", "программах")}. " +
                   "В списке с группировкой процессы одной программы собраны в одну строку.\n\n" + signs;
        }
    }

    private void OnSuspiciousTextChanged()
    {
        OnPropertyChanged(nameof(SuspiciousCountText));
        OnPropertyChanged(nameof(SuspiciousHint));
        OnPropertyChanged(nameof(SuspiciousSummary));
    }

    public bool IsCheckingSignatures => _signaturesDone < _signaturesRequested;

    public int InaccessibleCount
    {
        get => _inaccessibleCount;
        private set
        {
            if (!Set(ref _inaccessibleCount, value))
                return;
            OnPropertyChanged(nameof(ShowAdminHint));
            OnPropertyChanged(nameof(AdminHintText));
        }
    }

    public bool ShowAdminHint => !Admin.IsElevated && InaccessibleCount > 0;

    public string AdminHintText =>
        $"Сведения о {Format.Number(InaccessibleCount)} {Format.Plural(InaccessibleCount, "процессе", "процессах", "процессах")} " +
        "(в основном службах Windows) видны только администратору.";

    public int VisibleCount
    {
        get => _visibleCount;
        private set
        {
            if (Set(ref _visibleCount, value))
                UpdateEmptyState();
        }
    }

    public bool IsEmpty => VisibleCount == 0;

    // "Скрыть системные процессы Microsoft" never hides suspicious ones, so it does not matter here.
    public string EmptyTitle => !HasLoaded
        ? "Собираю список процессов…"
        : SuspiciousOnly && string.IsNullOrWhiteSpace(SearchText)
            ? "Подозрительных процессов нет"
            : "Ничего не найдено";

    public string EmptyText => !HasLoaded
        ? "Это займёт пару секунд."
        : SuspiciousOnly && string.IsNullOrWhiteSpace(SearchText)
            ? IsCheckingSignatures
                ? "Проверка цифровых подписей ещё идёт — список может пополниться."
                : "Ни один из запущенных процессов не вызывает подозрений. Это хорошо!"
            : "Попробуйте изменить запрос или отключить фильтры.";

    public bool ShowResetFilters => HasLoaded && IsEmpty;

    /// <summary>
    /// Checked processes, by <see cref="ProcessKey"/> (PID + start time) only: rows and programs read
    /// <see cref="ProcessItem.IsChecked"/> from here, so the checks survive the 2-second refresh and switching
    /// "Группировать по программам" shows the same checks either way. Exactly these processes get ended.
    /// </summary>
    internal CheckedSet<ProcessKey> Checked { get; } = new();

    /// <summary>"Отмечено: N" of the bulk action bar (processes).</summary>
    public int CheckedCount => Checked.Count;

    /// <summary>Header checkbox over the processes behind the visible rows: true = all checked, false = none, null = some.</summary>
    public bool? AllVisibleChecked => Checked.StateOf(VisibleCheckKeys());

    /// <summary>A visible program stands for all its processes, a visible process for itself.</summary>
    private IEnumerable<ProcessKey> VisibleCheckKeys() => Items.Where(i => i.CanCheck).SelectMany(i => i.CheckKeys);

    // ================================================================ commands

    public AsyncRelayCommand KillCommand { get; }

    public AsyncRelayCommand KillTreeCommand { get; }

    public RelayCommand OpenLocationCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    public RelayCommand SearchWebCommand { get; }

    public AsyncRelayCommand VirusTotalCommand { get; }

    public RelayCommand DefenderCommand { get; }

    public AsyncRelayCommand QuarantineCommand { get; }

    public RelayCommand TrustCommand { get; }

    public RelayCommand ToggleExpandCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand ShowSuspiciousCommand { get; }

    public RelayCommand ResetFiltersCommand { get; }

    public RelayCommand RestartAsAdminCommand { get; }

    /// <summary>"Завершить отмеченные".</summary>
    public AsyncRelayCommand KillCheckedCommand { get; }

    /// <summary>"Снять отметки".</summary>
    public RelayCommand ClearChecksCommand { get; }

    /// <summary>A row's checkbox click (parameter: the row). An indeterminate program becomes fully checked.</summary>
    public RelayCommand ToggleCheckCommand { get; }

    /// <summary>Header checkbox click: checks every visible row that can be checked, or unchecks them when all are.</summary>
    public RelayCommand ToggleAllVisibleCommand { get; }

    /// <summary>The page's Yes/No question before "Завершить отмеченные" (<see cref="Dialogs.Confirm"/>; tests replace it).</summary>
    internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    /// <summary>Test seam: replaces <see cref="ProcessesService.Kill"/> for "Завершить отмеченные", so tests end nothing real.</summary>
    internal Func<IReadOnlyList<KillTarget>, KillReport>? KillOverride { get; set; }

    /// <summary>"Завершить отмеченные" messages that are not errors (tests replace it).</summary>
    internal Action<string, string> ShowInfo { get; set; } = Dialogs.Info;

    internal Func<bool> IsElevated { get; set; } = () => Admin.IsElevated;

    /// <summary>Offers the restart as administrator after "Завершить отмеченные" (tests replace it so nothing restarts).</summary>
    internal Func<string, bool> RequireElevation { get; set; } = Admin.RequireElevation;

    private ProcessItem? Target(object? parameter) => parameter as ProcessItem ?? SelectedItem;

    // ================================================================ navigation

    public override Task OnNavigatedToAsync()
    {
        _isActive = true;
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _loadTask = RunBusyAsync("Собираю список процессов…", async ct =>
            {
                await RefreshAsync(ct);
                await WaitForBackgroundAsync(ct, reportProgress: true);
            });
        }
        else if (!IsPaused)
        {
            _ = SafeRefreshAsync();
        }
        StartTimer();
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom()
    {
        _isActive = false;
        StopTimer();
    }

    /// <summary>One full read-only snapshot including signatures and suspicion, then the live refresh stays stopped.</summary>
    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        StopTimer();
        _loadedOnce = true;
        if (_loadTask is { } load)
            await load;
        await RunBusyAsync("Обновляю сведения о процессах…", async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            await RefreshAsync(linked.Token);
            await WaitForBackgroundAsync(linked.Token, reportProgress: true);
        });
        StopTimer();
        if (SelectedItem is null)
            SelectedItem = Items.FirstOrDefault(i => i.IsSuspicious) ?? Items.FirstOrDefault();
        Reveal(SelectedItem);
    }

    private void StartTimer()
    {
        _liveRefresh = true;
        if (Application.Current is null)
            return; // unit tests
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
            _timer.Tick += OnTimerTick;
        }
        _timer.Start();
    }

    private void StopTimer()
    {
        _liveRefresh = false;
        _timer?.Stop();
    }

    /// <summary>
    /// The live refresh is on and its result can be seen: the page is open, not paused, and the window is neither
    /// minimized nor hidden. Otherwise every new process would still cost a WMI query and signature checks for nothing.
    /// </summary>
    internal bool IsLiveRefreshDue => _liveRefresh && _isActive && !IsPaused && !_windowHidden;

    /// <summary>
    /// Called by the view when the main window is minimized/hidden or shown again. While hidden the live refresh skips
    /// its ticks; when the window comes back the list is refreshed at once instead of showing stale numbers.
    /// </summary>
    internal void SetWindowHidden(bool hidden)
    {
        if (_windowHidden == hidden)
            return;
        _windowHidden = hidden;
        if (!hidden && IsLiveRefreshDue)
            _ = SafeRefreshAsync();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsLiveRefreshDue || _refreshing)
            return;
        await SafeRefreshAsync();
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // page left / app closing
        }
        catch (Exception ex)
        {
            // Never let a refresh problem escape to the dispatcher; log each distinct problem once.
            if (_lastRefreshError != ex.Message)
            {
                _lastRefreshError = ex.Message;
                Log.Warn($"Процессы: не удалось обновить список: {ex.Message}");
            }
        }
    }

    // ================================================================ refresh pipeline

    /// <summary>Takes a snapshot in the background and applies it to the rows (in place). No overlapping refreshes.</summary>
    internal async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var needBaseline = !_sampler.HasFreshBaseline(_service.Clock());
            var delay = BaselineDelay;
            var (samples, cpu, memory) = await Task.Run(async () =>
            {
                if (needBaseline)
                {
                    _sampler.Reset();
                    _service.TakeSnapshot(_sampler);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                var list = _service.TakeSnapshot(_sampler);
                return (list, SystemStats.CpuUsagePercent(), SystemStats.Memory());
            }, ct);

            lock (_stateLock)
            {
                // A fresh baseline means a long pause since the last measurement: "consecutive" load starts over.
                if (needBaseline)
                    _streaks.BreakStreaks();
                _streaks.Update(samples.Select(s => (s.Key, s.CpuPercent)));
                SnapshotDiff.Apply(_rows, samples, s => new ProcessRow(s.Info) { Checks = Checked }, (row, s) =>
                {
                    row.Cpu = s.CpuPercent;
                    row.Memory = s.MemoryBytes;
                    row.ParentPid = ProcessesService.ParentOf(s);
                    row.SustainedHighCpu = _streaks.IsSustained(s.Key);
                });
                HasLoaded = true;
                RebuildAll();
            }
            UpdateSummary(cpu, memory);
            StartBackgroundWork();
            RelayCommand.Refresh(); // buttons depend on details that may change without any user input
            _lastRefreshError = null;

            if (!_loggedFirstLoad)
            {
                _loggedFirstLoad = true;
                Log.Info($"Процессы: {Format.Count(ProcessCount, "процесс", "процесса", "процессов")} в " +
                         $"{Format.Count(ProgramCount, "программе", "программах", "программах")}");
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Waits until command lines and signatures requested so far are known (used by the first load and previews).</summary>
    internal async Task WaitForBackgroundAsync(CancellationToken ct, bool reportProgress = false)
    {
        while (true)
        {
            Task[] tasks;
            lock (_pending)
                tasks = _pending.ToArray();
            if (tasks.Length == 0)
                break;
            if (reportProgress)
            {
                var total = _signaturesRequested;
                var done = Math.Min(_signaturesDone, total);
                if (total > 0)
                    ReportProgress(done * 100.0 / total, $"Проверяю цифровые подписи программ: {done} из {total}");
                else
                    ReportProgress(-1, "Загружаю командные строки и службы…");
            }
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(250, ct));
            ct.ThrowIfCancellationRequested();
        }
        ApplyDetailsNow();
    }

    private void Track(Task task)
    {
        lock (_pending)
            _pending.Add(task);
        task.ContinueWith(t =>
        {
            lock (_pending)
                _pending.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StartBackgroundWork()
    {
        RequestMissingSignatures();
        List<ProcessStaticInfo> need;
        lock (_stateLock)
            need = _rows.Values.Where(r => !r.Info.DetailsLoaded).Select(r => r.Info).ToList();
        if (need.Count == 0 || _detailsRunning)
            return;
        _detailsRunning = true;
        Track(LoadDetailsThenAsync(need));
    }

    private async Task LoadDetailsThenAsync(List<ProcessStaticInfo> need)
    {
        try
        {
            await _service.LoadDetailsAsync(need, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: не удалось загрузить командные строки: {ex.Message}");
        }
        finally
        {
            _detailsRunning = false;
        }
        // New paths (from service configuration) need signatures; request them before this task counts as finished.
        RequestMissingSignatures();
        OnDetailsChanged();
    }

    private void RequestMissingSignatures()
    {
        List<string> paths;
        lock (_stateLock)
        {
            paths = _rows.Values.Select(r => r.Info.Path).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !_requestedSignatures.Contains(p))
                .ToList();
            _requestedSignatures.UnionWith(paths);
        }
        foreach (var path in paths)
        {
            Interlocked.Increment(ref _signaturesRequested);
            Track(SignatureThenAsync(path));
        }
    }

    private async Task SignatureThenAsync(string path)
    {
        try
        {
            await _service.GetSignatureAsync(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: проверка подписи {path}: {ex.Message}");
        }
        Interlocked.Increment(ref _signaturesDone);
        OnDetailsChanged();
    }

    /// <summary>Coalesces bursts of background results into one UI update.</summary>
    private void OnDetailsChanged()
    {
        if (Interlocked.Exchange(ref _detailsDirty, 1) == 1)
            return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            ApplyDetailsNow();
        else
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyDetailsNow));
    }

    private void ApplyDetailsNow()
    {
        Interlocked.Exchange(ref _detailsDirty, 0);
        try
        {
            lock (_stateLock)
                RebuildAll();
            OnSuspiciousTextChanged();
            OnPropertyChanged(nameof(IsCheckingSignatures));
            RelayCommand.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: не удалось обновить сведения: {ex.Message}");
        }
    }

    private void RebuildAll()
    {
        SyncDetails();
        RebuildGroups();
        UpdateView();
        UpdateCounts();
    }

    /// <summary>Copies cached details into the rows and re-evaluates protection and suspicion where inputs changed.</summary>
    private void SyncDetails()
    {
        var byPid = new Dictionary<int, ProcessRow>(_rows.Count);
        foreach (var row in _rows.Values)
            byPid.TryAdd(row.Pid, row);

        // The cleaner and the helpers it starts for its checks (powershell.exe, reg.exe, netsh.exe...) are never judged.
        var selfPid = _service.SelfPid;
        var own = OwnProcesses.Find(_rows.Values, selfPid, r => r.Pid, r => r.ParentPid, r => r.CreateTime, r => r.Info.Name,
            r => r.Info.Path);

        foreach (var row in _rows.Values)
        {
            var info = row.Info;
            row.OwnRole = OwnProcesses.RoleOf(row, selfPid, r => r.Pid, own);
            row.Path = info.Path;
            row.AccessDenied = info.Path is null && !KnownWindowsProcesses.IsPseudo(info.Name);
            row.Description = info.Meta?.Description ?? (info.Path is null ? KnownWindowsProcesses.Describe(info.Name) : null);
            row.Company = info.Meta?.Company;
            row.CommandLine = info.CommandLine;
            row.DetailsLoaded = info.DetailsLoaded;
            row.Services = info.Services;
            row.Signature = _service.TryGetSignature(info.Path);
            row.IsTrusted = info.Path is not null && AppServices.Settings.IsTrusted(info.Path);
            row.Evaluate(AnalyzeRow, _trustVersion);

            var (verdict, reason) = ProcessGuard.Check(row.Pid, row.ExeName, row.Path, selfPid);
            row.SetProtection(verdict, reason);
            row.SetQuarantineBlockReason(QuarantineRules.BlockReason(row.Path, row.Signature, row.Suspicion, verdict, row.IsTrusted,
                isOwnHelper: row.OwnRole == ProcessOwnRole.Helper));
            row.ParentText = ParentText(row, byPid);
            ApplyDefenderState(row);
        }
    }

    private static SuspicionResult? AnalyzeRow(ProcessRow row)
    {
        if (row.IsOwn)
            return SuspicionResult.Clean; // the cleaner does not judge itself or the tools it runs
        if (row.Path is not null && row.Signature is null)
            return null; // wait for the signature, otherwise signed programs would flash as "suspicious"
        return ProcessesService.Evaluate(row.Path, row.Signature, row.CommandLine, row.Info.Meta, row.SustainedHighCpu);
    }

    private static string ParentText(ProcessRow row, Dictionary<int, ProcessRow> byPid)
    {
        if (row.ParentPid <= 0)
            return "—";
        if (byPid.TryGetValue(row.ParentPid, out var parent) && parent.CreateTime <= row.CreateTime)
            return $"{parent.ExeName} (PID {parent.Pid})";
        return $"PID {row.ParentPid} — уже завершён";
    }

    private void RebuildGroups()
    {
        var keys = ProcessGrouping.AssignKeys(_rows.Values, r => r.Info.Name, r => r.Path);
        var buckets = new Dictionary<string, List<ProcessRow>>();
        foreach (var (row, key) in keys)
        {
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<ProcessRow>();
            list.Add(row);
        }

        foreach (var (key, list) in buckets)
        {
            if (!_groups.TryGetValue(key, out var group))
                _groups[key] = group = new ProcessGroup(key, list[0].ExeName) { Checks = Checked };
            group.SetInstances(list);
            UpdateGroup(group);
        }
        foreach (var key in _groups.Keys.Where(k => !buckets.ContainsKey(k)).ToList())
            _groups.Remove(key);
    }

    private void UpdateGroup(ProcessGroup g)
    {
        var (cpu, memory, _) = ProcessGrouping.Aggregate(g.Instances);
        g.Cpu = cpu;
        g.Memory = memory;
        var source = g.Instances.FirstOrDefault(r => r.Path is not null) ?? g.Instances[0];
        g.Path = source.Path;
        g.AccessDenied = g.Path is null && g.Instances.Any(r => r.AccessDenied);
        g.Description = source.Description;
        g.Company = source.Company;
        g.Signature = _service.TryGetSignature(g.Path);
        g.IsTrusted = g.Path is not null && AppServices.Settings.IsTrusted(g.Path);
        g.SetOwnRoleFromInstances();
        g.Suspicion = g.Instances.Any(r => r.Path is not null && r.Suspicion is null)
            ? null
            : ProcessGrouping.Combine(g.Instances.Select(r => r.Suspicion));

        var worst = ProcessGuard.Worst(g.Instances.Select(r => r.Verdict));
        var reason = g.Instances.FirstOrDefault(r => r.Verdict == worst)?.ProtectionReason;
        g.SetProtection(worst, reason);
        g.SetQuarantineBlockReason(QuarantineRules.BlockReason(g.Path, g.Signature, g.Suspicion, worst, g.IsTrusted,
            isOwnHelper: g.OwnRole == ProcessOwnRole.Helper));
        ApplyDefenderState(g);
        g.InvalidateSearchText();
    }

    /// <summary>Builds the visible list (filter + sort + grouping) and applies it to <see cref="Items"/> in place.</summary>
    private void UpdateView()
    {
        var filter = new ProcessFilter(SearchText, SuspiciousOnly, HideMicrosoft);
        var comparer = new ProcessComparer(_sortColumn, _sortDescending);
        var desired = new List<ProcessItem>(_rows.Count);
        if (GroupByProgram)
        {
            foreach (var group in _groups.Values.Where(g => g.Count > 0 && filter.Matches(g)).OrderBy(g => (ProcessItem)g, comparer))
            {
                group.IsChild = false;
                desired.Add(group);
                if (!group.HasChildren || !group.IsExpanded)
                    continue;
                foreach (var row in group.Instances.Where(r => filter.ShowChild(group, r)).OrderBy(r => (ProcessItem)r, comparer))
                {
                    row.IsFlat = false;
                    row.IsChild = true;
                    desired.Add(row);
                }
            }
        }
        else
        {
            foreach (var row in _rows.Values.Where(filter.Matches).OrderBy(r => (ProcessItem)r, comparer))
            {
                row.IsChild = false;
                row.IsFlat = true;
                desired.Add(row);
            }
        }

        var selected = _selectedItem;
        _selectionDisturbed = false;
        _syncingItems = true;
        try
        {
            CollectionSync.SyncTo(Items, desired);
        }
        finally
        {
            _syncingItems = false;
        }

        var resolved = ResolveSelection(selected, desired);
        if (!ReferenceEquals(resolved, _selectedItem))
        {
            SelectedItem = resolved;
            Reveal(resolved);
        }
        else if (_selectionDisturbed)
            OnPropertyChanged(nameof(SelectedItem));
        VisibleCount = desired.Count;

        // Exited processes, and ones that can no longer be ended, leave the checks (a disabled box is never checked or
        // counted); the header follows the visible rows.
        Checked.KeepOnly(_rows.Values.Where(r => r.CanCheck).Select(r => r.Key));
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    private ProcessItem? ResolveSelection(ProcessItem? selected, List<ProcessItem> visible)
    {
        if (selected is null)
            return null;
        var set = new HashSet<ProcessItem>(visible, ReferenceEqualityComparer.Instance);
        if (set.Contains(selected))
            return selected;
        switch (selected)
        {
            case ProcessRow row:
                // Exited, collapsed or grouping switched on: fall back to its program.
                if (row.Group is { } program && set.Contains(program))
                    return program;
                break;
            case ProcessGroup group:
                // Grouping switched off: keep showing the same program's main process.
                if (group.MainRow is { } main && _rows.ContainsKey(main.Key))
                {
                    if (set.Contains(main))
                        return main;
                    if (main.Group is { } newGroup && set.Contains(newGroup))
                        return newGroup;
                }
                break;
        }
        return null;
    }

    private void UpdateCounts()
    {
        ProcessCount = _rows.Count;
        ProgramCount = _groups.Count;
        SuspiciousCount = _rows.Values.Count(r => r.IsSuspicious);
        SuspiciousProgramCount = _groups.Values.Count(g => g.Instances.Any(r => r.IsSuspicious));
        InaccessibleCount = _rows.Values.Count(r => r.AccessDenied);
        OnPropertyChanged(nameof(ProcessCountText));
        OnPropertyChanged(nameof(ProgramCountText));
        OnSuspiciousTextChanged();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowResetFilters));
    }

    private void UpdateSummary(double cpu, MemoryStatus memory)
    {
        CpuPercent = cpu;
        CpuText = $"{Math.Round(cpu):0}%";
        if (memory.TotalBytes > 0)
        {
            MemoryPercent = memory.LoadPercent;
            MemoryText = $"{memory.LoadPercent}%";
            MemoryDetails = UsedOfTotal(memory.UsedBytes, memory.TotalBytes);
        }
    }

    /// <summary>"18,5 из 47,9 ГБ" (the unit is written once when both numbers share it).</summary>
    internal static string UsedOfTotal(ulong used, ulong total)
    {
        var u = Format.Bytes(used);
        var t = Format.Bytes(total);
        var unit = t[(t.LastIndexOf(' ') + 1)..];
        return u.EndsWith(" " + unit, StringComparison.Ordinal) ? $"{u[..^(unit.Length + 1)]} из {t}" : $"{u} из {t}";
    }

    private void UpdateViewSafe()
    {
        try
        {
            lock (_stateLock)
                UpdateView();
            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: не удалось обновить список: {ex.Message}");
        }
    }

    // ================================================================ list interaction

    /// <summary>Header click: same column toggles the direction; a new column starts with the natural order.</summary>
    public void SortBy(string? key)
    {
        if (!Enum.TryParse<ProcessSortColumn>(key, out var column))
            return;
        if (column == _sortColumn)
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = column;
            _sortDescending = column != ProcessSortColumn.Name;
        }
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(SortDescending));
        UpdateViewSafe();
    }

    public void ToggleExpand(ProcessItem? item)
    {
        switch (item)
        {
            case ProcessGroup { HasChildren: true } group:
                SetExpanded(group, !group.IsExpanded);
                break;
            case ProcessRow { IsChild: true, Group: { } parent }:
                SetExpanded(parent, false);
                SelectedItem = parent;
                break;
        }
    }

    public void SetExpanded(ProcessItem? item, bool expanded)
    {
        if (item is ProcessRow { IsChild: true, Group: { } parent } && !expanded)
        {
            SelectedItem = parent;
            return;
        }
        if (item is not ProcessGroup { HasChildren: true } group || group.IsExpanded == expanded)
            return;
        group.IsExpanded = expanded;
        UpdateViewSafe();
    }

    private void ResetFilters()
    {
        _searchText = "";
        _suspiciousOnly = false;
        _hideMicrosoft = false;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SuspiciousOnly));
        OnPropertyChanged(nameof(HideMicrosoft));
        UpdateViewSafe();
    }

    private static void ToggleCheck(ProcessItem? item)
    {
        if (item is not null)
            item.IsChecked = item.IsChecked != true;
    }

    private void ToggleAllVisible()
    {
        Checked.SetAll(VisibleCheckKeys(), AllVisibleChecked != true);
        // Also when nothing changed: the clicked header checkbox has toggled itself and must show the real state again.
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    private void OnChecksChanged()
    {
        // Hidden rows read the set again when they are shown, so only the listed ones are told.
        foreach (var item in Items)
            item.OnCheckedChanged();
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AllVisibleChecked));
        RelayCommand.Refresh();
    }

    // ================================================================ actions

    private static List<ProcessRow> InstancesOf(ProcessItem item)
        => item is ProcessGroup g ? g.Instances.ToList() : new List<ProcessRow> { (ProcessRow)item };

    private static KillTarget ToTarget(ProcessRow r) => new(r.Pid, r.CreateTime, r.ExeName, r.Path);

    private async Task KillAsync(ProcessItem? item, bool tree)
    {
        if (item is null)
            return;
        var roots = InstancesOf(item);
        if (roots.Count == 0)
            return;
        if (!item.CanKill || roots.Any(r => !r.CanKill))
        {
            Dialogs.Info("Процесс защищён", item.ProtectionText ?? ProcessGuard.CriticalText);
            return;
        }

        List<ProcessRow> victims;
        var skipped = new List<ProcessRow>();
        if (tree)
        {
            List<ProcessRow> all;
            lock (_stateLock)
                all = _rows.Values.ToList();
            var collected = ProcessTree.Collect(all, roots, r => r.Pid, r => r.ParentPid, r => r.CreateTime);
            victims = collected.Where(r => r.CanKill).ToList();
            skipped = collected.Where(r => !r.CanKill).ToList();
        }
        else
        {
            victims = roots;
        }

        var title = tree ? "Завершить дерево процессов?" : victims.Count > 1 ? "Завершить процессы?" : "Завершить процесс?";
        if (!Dialogs.Confirm(title, BuildKillMessage(item, roots, victims, skipped, tree)))
            return;

        var targets = victims.Select(ToTarget).ToList();
        var report = await Task.Run(() => _service.Kill(targets));

        if (report.Done == 0 && report.AccessDenied && !Admin.IsElevated)
            Admin.RequireElevation($"Завершение процесса {item.ExeName}");
        else if (report.Failed.Count > 0)
            Dialogs.Error("Не всё получилось", BuildKillReport(item, report));
        else
            Dialogs.Info("Готово", BuildKillReport(item, report));

        await SafeRefreshAsync();
    }

    internal static string BuildKillMessage(ProcessItem item, List<ProcessRow> roots, List<ProcessRow> victims,
        List<ProcessRow> skipped, bool tree)
    {
        var sb = new StringBuilder();
        var name = item.FriendlyName;
        var exe = item.ExeName;
        var nameText = string.Equals(name, exe, StringComparison.OrdinalIgnoreCase) ? $"«{exe}»" : $"«{name}» ({exe})";

        if (tree)
        {
            var rootPids = string.Join(", ", roots.Select(r => r.Pid));
            sb.Append($"Будут принудительно завершены {nameText} (PID {rootPids}) и все запущенные им процессы — ");
            sb.AppendLine($"всего {victims.Count}:");
            foreach (var r in victims.Take(12))
                sb.AppendLine($"  • {r.ExeName} (PID {r.Pid})");
            if (victims.Count > 12)
                sb.AppendLine($"  • …и ещё {victims.Count - 12}");
            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Защищённые процессы из этого дерева затронуты не будут: " +
                              string.Join(", ", skipped.Take(6).Select(r => $"{r.ExeName} (PID {r.Pid})")) +
                              (skipped.Count > 6 ? " и другие" : "") + ".");
            }
        }
        else if (victims.Count == 1)
        {
            sb.AppendLine($"Будет принудительно завершён процесс {nameText}, PID {victims[0].Pid}.");
        }
        else
        {
            var pids = string.Join(", ", victims.Take(15).Select(r => r.Pid)) + (victims.Count > 15 ? ", …" : "");
            sb.AppendLine($"Будут принудительно завершены все процессы программы {nameText} — {victims.Count} шт. (PID {pids}).");
        }

        sb.AppendLine();
        sb.Append("Несохранённые данные в программе будут потеряны. Если программа отвечает, лучше закройте её обычным способом.");
        if (item.HasKillWarning || victims.Any(v => v.HasKillWarning))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Внимание: ").Append(ProcessGuard.ExplorerText);
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    private static string BuildKillReport(ProcessItem item, KillReport report)
    {
        var sb = new StringBuilder();
        var total = report.Done + report.Failed.Count;
        if (report.Failed.Count == 0)
        {
            if (report.Killed.Count == 0)
                sb.Append(total > 1 ? "Эти процессы уже завершились сами." : "Процесс уже завершился сам.");
            else if (total == 1)
                sb.Append($"Процесс «{item.ExeName}» завершён.");
            else
                sb.Append($"Завершено процессов: {report.Done}.");
            return sb.ToString();
        }

        sb.AppendLine($"Завершено процессов: {report.Done} из {total}.");
        sb.AppendLine("Не удалось завершить:");
        foreach (var (target, error) in report.Failed.Take(10))
            sb.AppendLine($"  • {target.Name} (PID {target.Pid}) — {error}");
        if (report.AccessDenied && !Admin.IsElevated)
        {
            sb.AppendLine();
            sb.Append("Чтобы завершить их, запустите WaterCa7s PC Cleaner от имени администратора (кнопка внизу слева).");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>What "Завершить отмеченные" is about to do, taken from the checks right before the question.</summary>
    private sealed class CheckedKillPlan
    {
        /// <summary>The checked processes that may be ended, each once.</summary>
        public List<ProcessRow> Victims { get; } = new();

        /// <summary>
        /// Checked processes the list does not show right now (search, filters, or inside a collapsed program that is only
        /// partly checked), one line per program with all their PIDs. They are ended too, so the question names them first.
        /// </summary>
        public List<string> HiddenLines { get; } = new();

        /// <summary>What the list shows as checked, in list order: a fully checked program is one line.</summary>
        public List<string> VisibleLines { get; } = new();

        /// <summary>Checked processes that can no longer be ended (protection changed since the last list update).</summary>
        public List<ProcessRow> Skipped { get; } = new();
    }

    private async Task KillCheckedAsync()
    {
        var plan = PlanCheckedKill();
        // A disabled box is never checked: processes that became protected leave the checks right away.
        Checked.SetAll(plan.Skipped.Select(r => r.Key), false);
        if (plan.Victims.Count == 0)
        {
            if (plan.Skipped.Count > 0)
                ShowInfo("Процесс защищён", "Отмеченные процессы теперь защищены, завершать их нельзя:\n" +
                                            BulkText.BulletList(plan.Skipped.Select(ProcessLine)));
            return;
        }
        var title = plan.Victims.Count > 1 ? "Завершить отмеченные процессы?" : "Завершить отмеченный процесс?";
        if (!Confirm(title, BuildCheckedKillMessage(plan)))
            return;

        var targets = plan.Victims.Select(ToTarget).ToList();
        var kill = KillOverride ?? _service.Kill;
        var report = await Task.Run(() => kill(targets));

        // Ended (or already gone) processes leave the checks; those that could not be ended stay checked.
        Checked.SetAll(report.Killed.Concat(report.AlreadyExited).Select(t => new ProcessKey(t.Pid, t.CreateTime)), false);

        // BuildKillReport names the item only when there was a single process, which is then Victims[0].
        if (report.Done == 0 && report.AccessDenied && !IsElevated())
            RequireElevation("Завершение отмеченных процессов");
        else if (report.Failed.Count > 0)
            Dialogs.Error("Не всё получилось", BuildKillReport(plan.Victims[0], report));
        else
            ShowInfo("Готово", BuildKillReport(plan.Victims[0], report));

        await SafeRefreshAsync();
    }

    private CheckedKillPlan PlanCheckedKill()
    {
        var plan = new CheckedKillPlan();
        lock (_stateLock)
        {
            foreach (var key in Checked.Keys)
            {
                if (!_rows.TryGetValue(key, out var row))
                    continue; // exited: the next list update drops the check
                (row.CanCheck ? plan.Victims : plan.Skipped).Add(row);
            }
            var victims = plan.Victims.Select(r => r.Key).ToHashSet();

            // What the list shows as checked: a fully checked program, or a checked process row.
            var shown = new HashSet<ProcessKey>();
            foreach (var item in Items)
            {
                IEnumerable<ProcessRow> rows = item switch
                {
                    ProcessGroup { IsChecked: true } program => program.Instances,
                    ProcessRow r => new[] { r },
                    _ => Array.Empty<ProcessRow>(),
                };
                var listed = rows.Where(r => victims.Contains(r.Key) && shown.Add(r.Key)).ToList();
                if (listed.Count > 0)
                    plan.VisibleLines.Add(CheckedLine(item, listed, allPids: false));
            }

            plan.HiddenLines.AddRange(plan.Victims.Where(r => !shown.Contains(r.Key))
                .GroupBy(r => (ProcessItem?)r.Group ?? r)
                .Select(g => CheckedLine(g.Key, g.OrderBy(r => r.Pid).ToList(), allPids: true))
                .OrderBy(line => line, StringComparer.CurrentCultureIgnoreCase));
        }
        return plan;
    }

    /// <summary>
    /// "notepad.exe, PID 4242" or "Google Chrome (chrome.exe) — 3 процесса (PID 1, 2, 3)"; like the single kill's question,
    /// a long PID list is cut after 15 unless <paramref name="allPids"/>.
    /// </summary>
    private static string CheckedLine(ProcessItem item, List<ProcessRow> rows, bool allPids)
    {
        var name = string.Equals(item.FriendlyName, item.ExeName, StringComparison.OrdinalIgnoreCase)
            ? item.ExeName
            : $"{item.FriendlyName} ({item.ExeName})";
        if (rows.Count == 1)
            return $"{name}, PID {rows[0].Pid}";
        var cut = !allPids && rows.Count > 15;
        var pids = string.Join(", ", rows.Take(cut ? 15 : rows.Count).Select(r => r.Pid)) + (cut ? ", …" : "");
        return $"{name} — {Format.Count(rows.Count, "процесс", "процесса", "процессов")} (PID {pids})";
    }

    private static string ProcessLine(ProcessRow row) => CheckedLine(row, new List<ProcessRow> { row }, allPids: true);

    private static string BuildCheckedKillMessage(CheckedKillPlan plan)
    {
        var sb = new StringBuilder();
        var total = plan.Victims.Count == 1
            ? "Будет принудительно завершён 1 процесс"
            : $"Будут принудительно завершены {Format.Count(plan.Victims.Count, "процесс", "процесса", "процессов")}";
        if (plan.HiddenLines.Count == 0)
        {
            sb.AppendLine(total + ":");
            sb.AppendLine(BulkText.BulletList(plan.VisibleLines));
        }
        else
        {
            // They are ended too, so they come first and are never folded into "…и ещё N".
            sb.AppendLine(total + ".");
            sb.AppendLine();
            sb.AppendLine("Не видны в списке сейчас:");
            sb.AppendLine(BulkText.BulletList(plan.HiddenLines, plan.HiddenLines.Count));
            if (plan.VisibleLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("В списке:");
                sb.AppendLine(BulkText.BulletList(plan.VisibleLines));
            }
        }
        if (plan.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Эти отмеченные процессы теперь защищены и затронуты не будут:");
            sb.AppendLine(BulkText.BulletList(plan.Skipped.Select(ProcessLine)));
        }
        sb.AppendLine();
        sb.Append("Несохранённые данные в этих программах будут потеряны. Если программа отвечает, лучше закройте её обычным способом.");
        if (plan.Victims.Any(v => v.HasKillWarning))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Внимание: ").Append(ProcessGuard.ExplorerText);
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    private async Task QuarantineAsync(ProcessItem? item)
    {
        if (item?.Path is not { } path)
            return;
        if (!item.CanQuarantine)
        {
            Dialogs.Info("Карантин недоступен", item.QuarantineToolTip);
            return;
        }

        List<ProcessRow> running;
        lock (_stateLock)
            running = _rows.Values.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (running.Any(r => !r.CanKill))
        {
            Dialogs.Info("Карантин недоступен", ProcessGuard.CriticalText);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Файл «{item.ExeName}» будет перемещён в карантин:");
        sb.AppendLine(path);
        sb.AppendLine();
        if (running.Count > 0)
            sb.AppendLine($"Сначала будут завершены его процессы ({running.Count} шт.) — несохранённые данные в программе будут потеряны.");
        sb.AppendLine("В карантине файл переименовывается и не может запуститься. Вернуть его можно в разделе «Настройки» → «Карантин».");
        sb.AppendLine();
        sb.AppendLine("Почему файл вызывает подозрение:");
        foreach (var reason in item.Reasons)
            sb.AppendLine($"  • {reason}");
        sb.AppendLine();
        sb.Append("Поместить в карантин?");
        if (IsBusy)
        {
            // RunBusyAsync would silently skip the work while the first scan is still running.
            Dialogs.Info("Подождите немного", "Сейчас идёт проверка процессов. Повторите действие через несколько секунд.");
            return;
        }
        if (!Dialogs.Confirm("Поместить в карантин?", sb.ToString()))
            return;
        if (IsBusy)
        {
            Dialogs.Info("Подождите немного", "Сейчас идёт проверка процессов. Ничего не изменено — повторите действие через несколько секунд.");
            return;
        }

        QuarantineOutcome? outcome = null;
        var reasonText = "Процессы: " + string.Join("; ", item.Reasons);
        var targets = running.Select(ToTarget).ToList();
        await RunBusyAsync($"Помещаю «{item.ExeName}» в карантин…", async ct =>
        {
            outcome = await _service.QuarantineAsync(path, targets, reasonText, ct);
        });
        if (outcome is null)
            return;

        if (outcome.Success)
            Dialogs.Info("Готово",
                $"«{item.ExeName}» помещён в карантин. Завершено процессов: {outcome.Report.Done}.\n\n" +
                "Если программа снова появится после перезагрузки, загляните в раздел «Автозагрузка» — " +
                "там может остаться запись о её запуске.");
        else if (outcome.NeedsElevation)
            Admin.RequireElevation($"Помещение «{item.ExeName}» в карантин");
        else if (outcome.Cancelled)
            Dialogs.Info("Карантин отменён", outcome.Message);
        else
            Dialogs.Error("Карантин", outcome.Message);
        await SafeRefreshAsync();
    }

    private void ToggleTrust(ProcessItem? item)
    {
        if (item?.Path is not { } path)
            return;
        if (!item.CanTrust)
        {
            // Also guards programmatic calls that bypass CanExecute (e.g. a Microsoft-signed script host).
            Dialogs.Info("Доверие недоступно", item.TrustToolTip);
            return;
        }
        if (!item.IsTrusted)
        {
            // Neutral question: trusting changes only the app's own list, nothing on the system.
            var ok = Dialogs.Ask("Доверять программе?",
                $"«{item.FriendlyName}» больше не будет помечаться как подозрительная — ни здесь, ни в других разделах.\n\n" +
                $"{path}\n\nДоверяйте только программам, в которых уверены. Отменить можно здесь же кнопкой «Не доверять».");
            if (!ok)
                return;
        }
        ProcessesService.SetTrusted(path, !item.IsTrusted);
        _trustVersion++;
        ApplyDetailsNow();
    }

    private static void OpenLocation(ProcessItem? item)
    {
        if (item?.Path is { } path)
            ProcessRunner.ShowInExplorer(path);
    }

    private static void CopyPath(ProcessItem? item)
    {
        if (item?.Path is not { } path)
            return;
        try
        {
            Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось скопировать путь в буфер обмена: {ex.Message}");
        }
    }

    private static void SearchWeb(ProcessItem? item)
    {
        if (item is null)
            return;
        var publisher = item.Signature?.Publisher ?? item.Company;
        FileTools.SearchWeb(string.IsNullOrWhiteSpace(publisher) ? $"{item.ExeName} что это за процесс" : $"{item.ExeName} {publisher}");
    }

    private static async Task VirusTotalAsync(ProcessItem? item)
    {
        if (item?.Path is { } path)
            await FileTools.OpenVirusTotalAsync(path);
    }

    private async Task ScanWithDefenderAsync(ProcessItem? item)
    {
        if (item?.Path is not { } path || _defenderRunning.Contains(path))
            return;
        _defenderRunning.Add(path);
        SetDefenderStatus(path, "Защитник Windows проверяет файл… Это может занять до минуты.");
        string result;
        try
        {
            result = await FileTools.ScanWithDefenderAsync(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: проверка Защитником {path}: {ex.Message}");
            result = $"Не удалось запустить проверку: {ErrorText.For(ex)}.";
        }
        finally
        {
            _defenderRunning.Remove(path);
        }
        Log.Info($"Процессы: проверка Защитником {path}: {result}");
        SetDefenderStatus(path, $"Защитник Windows, {DateTime.Now:HH:mm}: {result}");
        RelayCommand.Refresh();
    }

    private void SetDefenderStatus(string path, string text)
    {
        _defenderResults[path] = text;
        lock (_stateLock)
        {
            foreach (var row in _rows.Values)
                ApplyDefenderState(row);
            foreach (var group in _groups.Values)
                ApplyDefenderState(group);
        }
    }

    private void ApplyDefenderState(ProcessItem item)
    {
        if (item.Path is null)
            return;
        item.DefenderStatus = _defenderResults.TryGetValue(item.Path, out var text) ? text : null;
        item.IsDefenderScanning = _defenderRunning.Contains(item.Path);
    }

    // ================================================================ test hooks

    internal bool IsRefreshing => _refreshing;

    internal IReadOnlyCollection<ProcessRow> Rows
    {
        get
        {
            lock (_stateLock)
                return _rows.Values.ToList();
        }
    }

    internal IReadOnlyCollection<ProcessGroup> Groups
    {
        get
        {
            lock (_stateLock)
                return _groups.Values.ToList();
        }
    }
}
