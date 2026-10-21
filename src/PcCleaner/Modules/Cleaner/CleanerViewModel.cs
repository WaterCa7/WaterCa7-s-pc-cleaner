using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using ElevatedRestart = PcCleaner.Modules.Settings.ElevatedRestart;

namespace PcCleaner.Modules.Cleaner;

public sealed class CleanerViewModel : PageViewModel
{
    private const long Mb = 1024L * 1024;

    /// <summary>The three groups: the key of <see cref="CleanerCategory.Group"/>, the title, the glyph, the note and the empty-state text.</summary>
    private static (string Key, string Name, string Glyph, string Note, string Empty)[] GroupInfo() => new[]
    {
        (CleanerCategory.GroupWindows, "Windows", "",
            T._("Временные файлы и кэш системы. Всё это Windows при необходимости создаст заново."),
            T._("Системные папки не найдены.")),
        (CleanerCategory.GroupBrowsers, T._("Браузеры"), "",
            T._("Очищается только кэш. Пароли, история, cookie, открытые вкладки, закладки и расширения не затрагиваются — заново входить на сайты не придётся."),
            T._("Поддерживаемые браузеры (Chrome, Edge, Яндекс Браузер, Opera, Brave, Vivaldi, Firefox) не найдены.")),
        (CleanerCategory.GroupApps, T._("Приложения и игры"), "",
            T._("Кэш программ и игр. Сами программы, сохранения игр, переписка и настройки не затрагиваются."),
            T._("Кэш известных программ и игр не найден.")),
    };

    private readonly CleanerService _service;
    private readonly List<CategoryItemViewModel> _items = new();
    private IReadOnlyList<CategoryGroupViewModel> _groups = Array.Empty<CategoryGroupViewModel>();
    private bool _visited;
    private bool _isCatalogLoaded;
    private bool _hasScanned;
    private bool _batchUpdate;
    private Task? _scanTask;
    private DateTime? _scannedAt;
    private long _totalBytes;
    private long _adminOnlyBytes;
    private long _selectedBytes;
    private int _selectedCount;
    private CleanResultViewModel? _lastResult;
    private string? _dismStatus;

    public CleanerViewModel(CleanerService service)
    {
        _service = service;
        AnalyzeCommand = new AsyncRelayCommand(() => StartScan(), () => !IsBusy);
        CleanCommand = new AsyncRelayCommand(CleanSelectedAsync, () => !IsBusy && SelectedCount > 0);
        SelectRecommendedCommand = new RelayCommand(SelectRecommended, () => !IsBusy && _items.Count > 0);
        UnselectAllCommand = new RelayCommand(UnselectAll, () => !IsBusy && _items.Count > 0);
        RunDismCommand = new AsyncRelayCommand(RunComponentCleanupAsync, () => !IsBusy);
        LaunchDiskCleanupCommand = new AsyncRelayCommand(LaunchDiskCleanupAsync);
        RestartAsAdminCommand = new RelayCommand(() => ElevatedRestart.Request(), () => !IsBusy && !_service.IsElevated);
        DismissResultCommand = new RelayCommand(() => LastResult = null);

        PropertyChanged += OnSelfPropertyChanged;
    }

    public override string Title => T._("Очистка");

    public override string Subtitle => T._("Временные файлы, кэш браузеров и программ, корзина — освободите место без риска");

    public override string IconGlyph => "";

    // ===== State =====

    public IReadOnlyList<CategoryGroupViewModel> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public bool IsElevated => _service.IsElevated;

    public bool IsCatalogLoaded
    {
        get => _isCatalogLoaded;
        private set => Set(ref _isCatalogLoaded, value);
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (Set(ref _hasScanned, value))
                RaiseSummary();
        }
    }

    /// <summary>Everything found that can be cleaned right now.</summary>
    public long TotalBytes
    {
        get => _totalBytes;
        private set => Set(ref _totalBytes, value);
    }

    public string TotalText => HasScanned || TotalBytes > 0 ? Format.Bytes(TotalBytes) : "—";

    public long SelectedBytes
    {
        get => _selectedBytes;
        private set => Set(ref _selectedBytes, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set => Set(ref _selectedCount, value);
    }

    public string SummaryText
    {
        get
        {
            if (!HasScanned)
                return IsBusy
                    ? T._("Ищем временные файлы, кэш и другой мусор…")
                    : T._("Нажмите «Анализ», чтобы узнать, сколько места занимает мусор.");
            if (TotalBytes < Mb)
                return T._("Мусора почти нет — здесь всё чисто.");
            return SelectedCount > 0
                ? T.F("Выбрано {0} в {1}", Format.Bytes(SelectedBytes), Format.Count(SelectedCount, "категории", "категориях", "категориях"))
                : T._("Ничего не выбрано — отметьте категории в списке ниже.");
        }
    }

    public string ScannedAtText => _scannedAt is null ? "" : T.F("Проверено в {0:HH:mm}", _scannedAt.Value);

    /// <summary>Found in folders that need administrator rights (when not elevated).</summary>
    public long AdminOnlyBytes
    {
        get => _adminOnlyBytes;
        private set => Set(ref _adminOnlyBytes, value);
    }

    public bool ShowAdminHint => !IsElevated && _items.Any(i => i.NeedsAdmin || i.PartlyNeedsAdmin);

    public string AdminHintText => AdminOnlyBytes > Mb
        ? T.F("Ещё {0} лежит в системных папках (временные файлы Windows, кэш обновлений и т. п.) — удалить их можно только с правами администратора.", Format.Bytes(AdminOnlyBytes))
        : T._("Системные папки (временные файлы Windows, кэш обновлений, журналы) можно очистить только с правами администратора.");

    public CleanResultViewModel? LastResult
    {
        get => _lastResult;
        private set => Set(ref _lastResult, value);
    }

    public string? DismStatus
    {
        get => _dismStatus;
        private set
        {
            if (Set(ref _dismStatus, value))
                RaiseTips(); // while DISM runs, the busy tooltips say that it cannot be stopped
        }
    }

    internal static string DismRunning => T._("Выполняется…");

    // ===== Button tooltips (also shown on disabled buttons: they say why the button cannot be pressed) =====

    internal static string BusyTip => T._("Сейчас идёт анализ или очистка — дождитесь окончания или нажмите «Отмена» над страницей");

    /// <summary>DISM is not cancelled on purpose (see CleanerService.RunComponentCleanupAsync): "Отмена" would not help.</summary>
    internal static string DismBusyTip => T._("Идёт очистка компонентов Windows (DISM) — дождитесь её окончания, прерывать её нельзя");

    private string CurrentBusyTip => BusyTipFor(DismStatus);

    internal static string BusyTipFor(string? dismStatus) => dismStatus == DismRunning ? DismBusyTip : BusyTip;

    public string AnalyzeTip => IsBusy
        ? CurrentBusyTip
        : HasScanned
            ? T._("Проверить ещё раз (ничего не удаляется)")
            : T._("Найти временные файлы, кэш и другой мусор (ничего не удаляется)");

    public string CleanTip => IsBusy ? CurrentBusyTip : IdleCleanTip(SelectedCount, HasScanned, TotalBytes);

    /// <summary>"Очистить выбранное" while nothing runs. It is enabled exactly when something is selected.</summary>
    internal static string IdleCleanTip(int selectedCount, bool hasScanned, long totalBytes)
    {
        // Checked first: rows sized by an analysis that was stopped half-way can already be cleaned.
        if (selectedCount > 0)
            return T._("Удалить файлы в отмеченных категориях (перед удалением появится подтверждение)");
        if (!hasScanned)
            return T._("Сначала выполните анализ — кнопка станет доступна, когда будет понятно, что удалять");
        return totalBytes < Mb
            ? T._("Удалять нечего: мусора почти нет")
            : T._("Отметьте в списке ниже хотя бы одну категорию, в которой найдены файлы");
    }

    public string SelectRecommendedTip => IsBusy
        ? CurrentBusyTip
        : _items.Count == 0
            ? T._("Список категорий ещё не готов — дождитесь анализа")
            : T._("Отметить безопасные категории, которые мы рекомендуем очищать");

    public string UnselectAllTip => IsBusy
        ? CurrentBusyTip
        : _items.Count == 0
            ? T._("Список категорий ещё не готов — дождитесь анализа")
            : T._("Снять отметки со всех категорий");

    public string RestartAsAdminTip => IsBusy
        ? CurrentBusyTip
        : T._("Программа перезапустится с правами администратора — тогда можно будет очистить и системные папки");

    public string RunDismTip => IsBusy
        ? CurrentBusyTip
        : T._("Перед запуском появится подтверждение со всеми подробностями");

    private void RaiseTips()
    {
        OnPropertyChanged(nameof(AnalyzeTip));
        OnPropertyChanged(nameof(CleanTip));
        OnPropertyChanged(nameof(SelectRecommendedTip));
        OnPropertyChanged(nameof(UnselectAllTip));
        OnPropertyChanged(nameof(RestartAsAdminTip));
        OnPropertyChanged(nameof(RunDismTip));
    }

    // ===== Commands =====

    public ICommand AnalyzeCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand SelectRecommendedCommand { get; }

    public ICommand UnselectAllCommand { get; }

    public ICommand RunDismCommand { get; }

    public ICommand LaunchDiskCleanupCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand DismissResultCommand { get; }

    // ===== Navigation =====

    public override Task OnNavigatedToAsync()
    {
        // Also when an earlier (preview) scan was cancelled before the list was built.
        if (!_visited || (!IsBusy && !IsCatalogLoaded))
        {
            _visited = true;
            _ = StartScan(); // non-blocking: the page fills in as categories finish
        }
        else if (!IsBusy && IsCatalogLoaded)
        {
            _ = RefreshRunningAsync();
        }
        return Task.CompletedTask;
    }

    /// <summary>Full read-only analysis (screenshot mode / verification). Never deletes anything.</summary>
    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() => Ui.Post(() =>
        {
            if (CancelCommand.CanExecute(null))
                CancelCommand.Execute(null);
        }));
        _visited = true; // the first visit afterwards shows these results instead of scanning again
        if (_scanTask is { IsCompleted: false })
            await _scanTask;
        if (!HasScanned && !ct.IsCancellationRequested)
            await StartScan();
    }

    // ===== Analysis =====

    private Task StartScan()
    {
        _scanTask = RunBusyAsync(T._("Ищем ненужные файлы…"), async ct =>
        {
            await LoadCatalogAsync(ct);
            await ScanItemsAsync(_items.ToList(), ct);
            _scannedAt = DateTime.Now;
            HasScanned = true;
            OnPropertyChanged(nameof(ScannedAtText));
            Log.Info(T.F("Очистка: анализ завершён, можно освободить {0}", Format.Bytes(TotalBytes)));
        });
        return _scanTask;
    }

    /// <summary>Builds the category list (first time) or refreshes the definitions (folders may have changed).</summary>
    private async Task LoadCatalogAsync(CancellationToken ct)
    {
        var settings = _service.Settings;
        var disabled = new HashSet<string>(settings.CleanerDisabledCategories ?? new List<string>(), StringComparer.Ordinal);
        var elevated = _service.IsElevated;
        var catalog = await Task.Run(() => _service.LoadCategories()
            .Select(c => (Category: c, Folders: c.DisplayFolders))
            .ToList(), ct);

        if (IsCatalogLoaded && catalog.Select(c => c.Category.Id).SequenceEqual(_items.Select(i => i.Id)))
        {
            for (var i = 0; i < catalog.Count; i++)
                _items[i].UpdateCategory(catalog[i].Category, catalog[i].Folders);
            OnPropertyChanged(nameof(ShowAdminHint));
            return;
        }

        var previous = _items.ToDictionary(i => i.Id);
        var items = catalog.Select(c =>
        {
            var isChecked = previous.TryGetValue(c.Category.Id, out var old)
                ? old.IsChecked
                : c.Category.DefaultChecked && !disabled.Contains(c.Category.Id);
            return new CategoryItemViewModel(c.Category, c.Folders, isChecked, elevated, OnItemCheckedChanged, ExcludePath);
        }).ToList();

        _items.Clear();
        _items.AddRange(items);
        Groups = GroupInfo()
            .Select(g => new CategoryGroupViewModel(g.Name, g.Glyph, g.Note, g.Empty,
                _items.Where(i => i.Category.Group == g.Key).ToList()))
            .ToList();
        IsCatalogLoaded = true;
        OnPropertyChanged(nameof(ShowAdminHint));
        Recalculate();
    }

    /// <summary>Sizes the given rows in parallel, updating each one as soon as it is done.</summary>
    private async Task ScanItemsAsync(IReadOnlyList<CategoryItemViewModel> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return;
        foreach (var item in items)
            item.BeginScan();
        Recalculate();

        var byId = items.ToDictionary(i => i.Id);
        var done = 0;
        ReportProgress(0, T.F("Анализ: проверено 0 из {0}", items.Count));
        try
        {
            await _service.ScanAsync(items.Select(i => i.Category).ToList(), CleanerEngine.DefaultSampleSize,
                (category, result) =>
                {
                    var n = Interlocked.Increment(ref done);
                    Ui.Post(() =>
                    {
                        if (byId.TryGetValue(category.Id, out var item))
                            item.ApplyResult(result);
                        Recalculate();
                    });
                    ReportProgress(n * 100.0 / items.Count, T.F("Анализ: {0} — готово ({1} из {2})", category.Name, n, items.Count));
                }, ct);
        }
        finally
        {
            Ui.Invoke(() =>
            {
                foreach (var item in items)
                    item.EndScanIfPending();
                Recalculate();
            });
        }
    }

    private async Task RefreshRunningAsync()
    {
        try
        {
            var categories = _items.Select(i => i.Category).ToList();
            await Task.Run(() => _service.RefreshRunning(categories));
            foreach (var item in _items)
                item.RefreshRunning();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Warn(T.F("Очистка: не удалось проверить запущенные программы: {0}", ErrorText.For(ex)), ex);
        }
    }

    // ===== Selection =====

    private void OnItemCheckedChanged(CategoryItemViewModel item)
    {
        if (_batchUpdate)
            return;
        var settings = _service.Settings;
        // Copy-on-write: the Settings page may be reading the list (e.g. while resetting it).
        var disabled = new List<string>(settings.CleanerDisabledCategories ?? new List<string>());
        if (item.IsChecked)
            disabled.RemoveAll(id => id == item.Id);
        else if (item.Category.DefaultChecked && !disabled.Contains(item.Id))
            disabled.Add(item.Id);
        settings.CleanerDisabledCategories = disabled;
        settings.Save();
        Recalculate();
    }

    private void SelectRecommended()
    {
        _batchUpdate = true;
        try
        {
            foreach (var item in _items)
                item.SetCheckedSilently(item.Category.DefaultChecked);
        }
        finally
        {
            _batchUpdate = false;
        }
        var settings = _service.Settings;
        settings.CleanerDisabledCategories = new List<string>();
        settings.Save();
        Recalculate();
    }

    private void UnselectAll()
    {
        _batchUpdate = true;
        try
        {
            foreach (var item in _items)
                item.SetCheckedSilently(false);
        }
        finally
        {
            _batchUpdate = false;
        }
        var settings = _service.Settings;
        var disabled = new List<string>(settings.CleanerDisabledCategories ?? new List<string>());
        foreach (var item in _items.Where(i => i.Category.DefaultChecked && !disabled.Contains(i.Id)))
            disabled.Add(item.Id);
        settings.CleanerDisabledCategories = disabled;
        settings.Save();
        Recalculate();
    }

    /// <summary>Adds a file/folder to the cleaner exclusions (from the details context menu) and re-sizes its category.</summary>
    private void ExcludePath(string path)
    {
        var settings = _service.Settings;
        var exclusions = settings.CleanerExclusions ?? new List<string>();
        if (!exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            // Copy-on-write: a scan on a background thread may be enumerating the current list right now.
            settings.CleanerExclusions = new List<string>(exclusions) { path };
            settings.Save();
        }
        Log.Info(T.F("Добавлено в исключения очистки: {0}", path));
        var affected = _items
            .Where(i => i.LargestFiles.Any(f => f.FullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (affected.Count > 0 && !IsBusy)
            _ = RunBusyAsync(T._("Обновляем список после исключения…"), ct => ScanItemsAsync(affected, ct));
    }

    private void Recalculate()
    {
        var done = _items.Where(i => i.HasResult).ToList();
        TotalBytes = done.Sum(i => i.CleanableBytes);
        AdminOnlyBytes = done.Sum(i => i.AdminOnlyBytes);
        var selected = _items.Where(i => i.IsSelectedForCleaning).ToList();
        SelectedBytes = selected.Sum(i => i.CleanableBytes);
        SelectedCount = selected.Count;
        foreach (var g in Groups)
            g.Refresh();
        RaiseSummary();
        OnPropertyChanged(nameof(AdminHintText));
        RelayCommand.Refresh();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SummaryText));
        RaiseTips();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(SummaryText));
            RaiseTips();
        }
    }

    // ===== Cleaning =====

    /// <summary>The page's Yes/No question before cleaning (<see cref="Dialogs.Confirm"/>; tests replace it).</summary>
    internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    private async Task CleanSelectedAsync()
    {
        var selected = _items.Where(i => i.IsSelectedForCleaning).ToList();
        if (selected.Count == 0)
        {
            Dialogs.Info(T._("Очистка"), T._("Отметьте хотя бы одну категорию, в которой найдены файлы."));
            return;
        }
        if (selected.FirstOrDefault(i => i.IsRecycleBin) is { } binItem)
        {
            // The analysis may be old, and files may have been deleted into the Recycle Bin since: the confirmation
            // shows what is in it now. The service checks once more right before emptying it and asks again if it grew.
            await RefreshRecycleBinAsync(binItem);
            if (IsBusy)
                return;
            selected = _items.Where(i => i.IsSelectedForCleaning).ToList();
            if (selected.Count == 0)
            {
                // A bin whose contents cannot be read is never emptied (and is not "already empty").
                Dialogs.Info(T._("Очистка"), binItem.Result?.Error is not null
                    ? T._("Не удалось узнать, что сейчас лежит в корзине, поэтому она не будет очищена, а других отмеченных категорий с файлами нет. Попробуйте ещё раз чуть позже.")
                    : T._("Корзина уже пуста, а других отмеченных категорий с файлами нет — удалять нечего."));
                return;
            }
        }
        var shownBin = ShownRecycleBin(selected);
        if (!Confirm(T._("Очистка"), BuildConfirmation(selected)))
        {
            Log.Info(T._("Очистка отменена пользователем"));
            return;
        }

        await RunBusyAsync(T._("Очистка…"), async ct =>
        {
            var categories = selected.Select(i => i.Category).ToList();
            var results = await _service.CleanAsync(categories,
                (index, category) => ReportProgress(index * 100.0 / categories.Count,
                    T.F("Очистка: {0} ({1} из {2})", category.Name, index + 1, categories.Count)),
                ct, confirmedRecycleBin: shownBin);
            LastResult = new CleanResultViewModel(results, ct.IsCancellationRequested, _service.IsElevated);
            Log.Info(T.F("Очистка завершена: {0}", CleanerService.Summarize(results)));

            // Show what is left in the cleaned categories (quick, so not cancellable).
            ReportProgress(-1, T._("Пересчитываем, сколько места осталось занято…"));
            var cleanedIds = results.Select(r => r.CategoryId).ToHashSet(StringComparer.Ordinal);
            await ScanItemsAsync(selected.Where(i => cleanedIds.Contains(i.Id)).ToList(), CancellationToken.None);
        });
    }

    /// <summary>Re-reads the size of the Recycle Bin (read-only) and shows it on its row.</summary>
    private async Task RefreshRecycleBinAsync(CategoryItemViewModel binItem)
    {
        // SHQueryRecycleBin walks the bins of every drive: keep it off the UI thread.
        var result = await Task.Run(() => _service.ScanCategoryAsync(binItem.Category, 0, CancellationToken.None));
        Ui.Invoke(() =>
        {
            binItem.ApplyResult(result);
            Recalculate();
        });
    }

    /// <summary>The Recycle Bin size and object count the confirmation shows (null when the bin is not selected).</summary>
    internal static RecycleBinSnapshot? ShownRecycleBin(IEnumerable<CategoryItemViewModel> selected)
        => selected.FirstOrDefault(i => i.IsRecycleBin)?.Result is { } r ? new RecycleBinSnapshot(r.Bytes, r.Files) : null;

    private string BuildConfirmation(IReadOnlyList<CategoryItemViewModel> selected)
    {
        var sb = new StringBuilder();
        sb.AppendLine(T._("Будут удалены ненужные файлы из выбранных категорий:"));
        sb.AppendLine();
        foreach (var item in selected.Take(14))
        {
            sb.AppendLine(item.IsRecycleBin
                ? $"•  {item.Name} — {Format.Bytes(item.CleanableBytes)} ({CleanerText.Objects(item.Files)})"
                : $"•  {item.Name} — {Format.Bytes(item.CleanableBytes)}");
        }
        if (selected.Count > 14)
            sb.AppendLine(T.F("•  и ещё {0}", Format.Count(selected.Count - 14, "категория", "категории", "категорий")));
        sb.AppendLine();
        sb.AppendLine(T.F("Всего: около {0}.", Format.Bytes(selected.Sum(i => i.CleanableBytes))));
        sb.AppendLine();
        sb.AppendLine(T._("Удаляются только временные файлы и кэш: ваши документы, пароли, история и cookie браузеров не затрагиваются. Файлы, занятые запущенными программами, будут пропущены, а программы при необходимости создадут кэш заново."));

        if (ShownRecycleBin(selected) is { } bin)
        {
            sb.AppendLine();
            sb.AppendLine(T.F("Корзина будет очищена полностью (сейчас в ней {0}) — файлы из неё восстановить будет нельзя. Если к началу её очистки в корзину попадут новые файлы, программа спросит ещё раз.", bin.Text));
        }
        var running = selected.Where(i => i.IsRunning).Select(i => i.Name).Distinct().ToList();
        if (running.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(T.F("Сейчас запущены: {0}. Их кэш очистится частично — для полной очистки закройте эти программы.", string.Join(", ", running)));
        }
        if (selected.Any(i => i.PartlyNeedsAdmin))
        {
            sb.AppendLine();
            sb.AppendLine(T._("Системные папки, для которых нужны права администратора, будут пропущены."));
        }
        sb.AppendLine();
        sb.Append(T._("Удалённые файлы не попадают в корзину, поэтому отменить очистку будет нельзя. Продолжить?"));
        return sb.ToString();
    }

    // ===== Deep cleaning =====

    /// <summary>
    /// How long "Открыть" stays disabled after the wizard was started: its window takes a moment to appear, and a double
    /// click must not open a second one.
    /// </summary>
    internal TimeSpan DiskCleanupCooldown { get; set; } = TimeSpan.FromSeconds(3);

    private async Task LaunchDiskCleanupAsync()
    {
        // Off the UI thread: starting a process can take a moment. AsyncRelayCommand ignores clicks until this returns.
        var result = await Task.Run(_service.LaunchDiskCleanup);
        switch (result)
        {
            case DiskCleanupLaunch.Started:
                await Task.Delay(DiskCleanupCooldown);
                break;
            case DiskCleanupLaunch.AlreadyOpen:
                Dialogs.Info(T._("Очистка диска"),
                    T._("Мастер очистки диска Windows уже открыт — переключитесь на его окно через панель задач."));
                break;
            default:
                Dialogs.Error(T._("Очистка диска"),
                    T._("Не удалось открыть встроенную утилиту «Очистка диска». Подробности — в журнале (раздел «Настройки»)."));
                break;
        }
    }

    private async Task RunComponentCleanupAsync()
    {
        if (!ElevatedRestart.RequireElevation(T._("Очистка компонентов Windows (DISM)")))
            return;
        var confirmed = Dialogs.Confirm(T._("Очистка компонентов Windows"),
            T._("Будет запущена встроенная команда Windows:\nDISM /Online /Cleanup-Image /StartComponentCleanup\n\nОна удаляет старые версии системных компонентов, которые остались после установки обновлений, и обычно освобождает от нескольких сотен мегабайт до нескольких гигабайт.\n\n•  Это займёт от 5 до 30 минут. Прерывать процесс нельзя, но компьютером можно пользоваться.\n•  После очистки уже установленные обновления нельзя будет удалить (откатить) — это нормально.\n•  Отменить саму очистку нельзя. Перед запуском будет создана точка восстановления (если это включено в настройках).\n\nЗапустить?"));
        if (!confirmed)
            return;

        await RunBusyAsync(T._("Очистка компонентов Windows (DISM) — это может занять до 30 минут, не выключайте компьютер…"),
            async ct =>
            {
                DismStatus = DismRunning;
                await RestorePoint.TryCreateAsync(T._("Очистка компонентов Windows"), ct);
                ReportProgress(-1, T._("Очистка компонентов Windows (DISM) — это может занять до 30 минут, не выключайте компьютер…"));
                DismStatus = await _service.RunComponentCleanupAsync(ct);
                Dialogs.Info(T._("Очистка компонентов Windows"), DismStatus);
            });
        if (DismStatus == DismRunning)
            DismStatus = null;
    }
}
