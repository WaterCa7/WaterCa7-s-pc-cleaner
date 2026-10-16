using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

public sealed class CleanerViewModel : PageViewModel
{
    private const long Mb = 1024L * 1024;

    private static readonly (string Name, string Glyph, string Note, string Empty)[] GroupInfo =
    {
        (CleanerCategory.GroupWindows, "",
            "Временные файлы и кэш системы. Всё это Windows при необходимости создаст заново.",
            "Системные папки не найдены."),
        (CleanerCategory.GroupBrowsers, "",
            "Очищается только кэш. Пароли, история, cookie, открытые вкладки, закладки и расширения не затрагиваются — " +
            "заново входить на сайты не придётся.",
            "Поддерживаемые браузеры (Chrome, Edge, Яндекс Браузер, Opera, Brave, Vivaldi, Firefox) не найдены."),
        (CleanerCategory.GroupApps, "",
            "Кэш программ и игр. Сами программы, сохранения игр, переписка и настройки не затрагиваются.",
            "Кэш известных программ и игр не найден."),
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
        LaunchDiskCleanupCommand = new RelayCommand(() => _service.LaunchDiskCleanup());
        RestartAsAdminCommand = new RelayCommand(() => Admin.RestartElevated(), () => !IsBusy && !_service.IsElevated);
        DismissResultCommand = new RelayCommand(() => LastResult = null);

        PropertyChanged += OnSelfPropertyChanged;
    }

    public override string Title => "Очистка";

    public override string Subtitle => "Временные файлы, кэш браузеров и программ, корзина — освободите место без риска";

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
                    ? "Ищем временные файлы, кэш и другой мусор…"
                    : "Нажмите «Анализ», чтобы узнать, сколько места занимает мусор.";
            if (TotalBytes < Mb)
                return "Мусора почти нет — здесь всё чисто.";
            return SelectedCount > 0
                ? $"Выбрано {Format.Bytes(SelectedBytes)} {CleanerText.InCategories(SelectedCount)}"
                : "Ничего не выбрано — отметьте категории в списке ниже.";
        }
    }

    public string ScannedAtText => _scannedAt is null ? "" : $"Проверено в {_scannedAt.Value:HH:mm}";

    /// <summary>Found in folders that need administrator rights (when not elevated).</summary>
    public long AdminOnlyBytes
    {
        get => _adminOnlyBytes;
        private set => Set(ref _adminOnlyBytes, value);
    }

    public bool ShowAdminHint => !IsElevated && _items.Any(i => i.NeedsAdmin || i.PartlyNeedsAdmin);

    public string AdminHintText => AdminOnlyBytes > Mb
        ? $"Ещё {Format.Bytes(AdminOnlyBytes)} лежит в системных папках (временные файлы Windows, кэш обновлений и т. п.) — " +
          "удалить их можно только с правами администратора."
        : "Системные папки (временные файлы Windows, кэш обновлений, журналы) можно очистить только с правами администратора.";

    public CleanResultViewModel? LastResult
    {
        get => _lastResult;
        private set => Set(ref _lastResult, value);
    }

    public string? DismStatus
    {
        get => _dismStatus;
        private set => Set(ref _dismStatus, value);
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
        _scanTask = RunBusyAsync("Ищем ненужные файлы…", async ct =>
        {
            await LoadCatalogAsync(ct);
            await ScanItemsAsync(_items.ToList(), ct);
            _scannedAt = DateTime.Now;
            HasScanned = true;
            OnPropertyChanged(nameof(ScannedAtText));
            Log.Info($"Очистка: анализ завершён, можно освободить {Format.Bytes(TotalBytes)}");
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
        Groups = GroupInfo
            .Select(g => new CategoryGroupViewModel(g.Name, g.Glyph, g.Note, g.Empty,
                _items.Where(i => i.Category.Group == g.Name).ToList()))
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
        ReportProgress(0, $"Анализ: проверено 0 из {items.Count}");
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
                    ReportProgress(n * 100.0 / items.Count, $"Анализ: {category.Name} — готово ({n} из {items.Count})");
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
            Log.Warn($"Очистка: не удалось проверить запущенные программы: {ex.Message}");
        }
    }

    // ===== Selection =====

    private void OnItemCheckedChanged(CategoryItemViewModel item)
    {
        if (_batchUpdate)
            return;
        var settings = _service.Settings;
        settings.CleanerDisabledCategories ??= new List<string>();
        var disabled = settings.CleanerDisabledCategories;
        if (item.IsChecked)
            disabled.RemoveAll(id => id == item.Id);
        else if (item.Category.DefaultChecked && !disabled.Contains(item.Id))
            disabled.Add(item.Id);
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
        settings.CleanerExclusions ??= new List<string>();
        if (!settings.CleanerExclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            settings.CleanerExclusions.Add(path);
            settings.Save();
        }
        Log.Info($"Добавлено в исключения очистки: {path}");
        var affected = _items
            .Where(i => i.LargestFiles.Any(f => f.FullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (affected.Count > 0 && !IsBusy)
            _ = RunBusyAsync("Обновляем список после исключения…", ct => ScanItemsAsync(affected, ct));
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
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
            OnPropertyChanged(nameof(SummaryText));
    }

    // ===== Cleaning =====

    private async Task CleanSelectedAsync()
    {
        var selected = _items.Where(i => i.IsSelectedForCleaning).ToList();
        if (selected.Count == 0)
        {
            Dialogs.Info("Очистка", "Отметьте хотя бы одну категорию, в которой найдены файлы.");
            return;
        }
        if (!Dialogs.Confirm("Очистка", BuildConfirmation(selected)))
        {
            Log.Info("Очистка отменена пользователем");
            return;
        }

        await RunBusyAsync("Очистка…", async ct =>
        {
            var categories = selected.Select(i => i.Category).ToList();
            var results = await _service.CleanAsync(categories,
                (index, category) => ReportProgress(index * 100.0 / categories.Count,
                    $"Очистка: {category.Name} ({index + 1} из {categories.Count})"),
                ct);
            LastResult = new CleanResultViewModel(results, ct.IsCancellationRequested);
            Log.Info("Очистка завершена: " + CleanerService.Summarize(results));

            // Show what is left in the cleaned categories (quick, so not cancellable).
            ReportProgress(-1, "Пересчитываем, сколько места осталось занято…");
            var cleanedIds = results.Select(r => r.CategoryId).ToHashSet(StringComparer.Ordinal);
            await ScanItemsAsync(selected.Where(i => cleanedIds.Contains(i.Id)).ToList(), CancellationToken.None);
        });
    }

    private string BuildConfirmation(IReadOnlyList<CategoryItemViewModel> selected)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Будут удалены ненужные файлы из выбранных категорий:");
        sb.AppendLine();
        foreach (var item in selected.Take(14))
            sb.AppendLine($"•  {item.Name} — {Format.Bytes(item.CleanableBytes)}");
        if (selected.Count > 14)
            sb.AppendLine($"•  и ещё {Format.Count(selected.Count - 14, "категория", "категории", "категорий")}");
        sb.AppendLine();
        sb.AppendLine($"Всего: около {Format.Bytes(selected.Sum(i => i.CleanableBytes))}.");
        sb.AppendLine();
        sb.AppendLine("Удаляются только временные файлы и кэш: ваши документы, пароли, история и cookie браузеров " +
                      "не затрагиваются. Файлы, занятые запущенными программами, будут пропущены, а программы при " +
                      "необходимости создадут кэш заново.");

        if (selected.Any(i => i.IsRecycleBin))
        {
            sb.AppendLine();
            sb.AppendLine("Корзина будет очищена — файлы из неё восстановить будет нельзя.");
        }
        var running = selected.Where(i => i.IsRunning).Select(i => i.Name).Distinct().ToList();
        if (running.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Сейчас запущены: {string.Join(", ", running)}. Их кэш очистится частично — " +
                          "для полной очистки закройте эти программы.");
        }
        if (selected.Any(i => i.PartlyNeedsAdmin))
        {
            sb.AppendLine();
            sb.AppendLine("Системные папки, для которых нужны права администратора, будут пропущены.");
        }
        sb.AppendLine();
        sb.Append("Удалённые файлы не попадают в корзину, поэтому отменить очистку будет нельзя. Продолжить?");
        return sb.ToString();
    }

    // ===== Deep cleaning =====

    private async Task RunComponentCleanupAsync()
    {
        if (!Admin.RequireElevation("Очистка компонентов Windows (DISM)"))
            return;
        var confirmed = Dialogs.Confirm("Очистка компонентов Windows",
            "Будет запущена встроенная команда Windows:\n" +
            "DISM /Online /Cleanup-Image /StartComponentCleanup\n\n" +
            "Она удаляет старые версии системных компонентов, которые остались после установки обновлений, " +
            "и обычно освобождает от нескольких сотен мегабайт до нескольких гигабайт.\n\n" +
            "•  Это займёт от 5 до 30 минут. Прерывать процесс нельзя, но компьютером можно пользоваться.\n" +
            "•  После очистки уже установленные обновления нельзя будет удалить (откатить) — это нормально.\n" +
            "•  Отменить саму очистку нельзя. Перед запуском будет создана точка восстановления (если это " +
            "включено в настройках).\n\nЗапустить?");
        if (!confirmed)
            return;

        await RunBusyAsync("Очистка компонентов Windows (DISM) — это может занять до 30 минут, не выключайте компьютер…",
            async ct =>
            {
                DismStatus = "Выполняется…";
                await RestorePoint.TryCreateAsync("Очистка компонентов Windows", ct);
                ReportProgress(-1, "Очистка компонентов Windows (DISM) — это может занять до 30 минут, не выключайте компьютер…");
                DismStatus = await _service.RunComponentCleanupAsync(ct);
                Dialogs.Info("Очистка компонентов Windows", DismStatus);
            });
        if (DismStatus == "Выполняется…")
            DismStatus = null;
    }
}
