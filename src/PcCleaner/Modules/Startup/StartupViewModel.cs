using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using PcCleaner.Core;
using ElevatedRestart = PcCleaner.Modules.Settings.ElevatedRestart;

namespace PcCleaner.Modules.Startup;

/// <summary>One segment of the source filter ("Все", "Реестр и папки", ...).</summary>
public sealed class SourceFilterOption : ObservableObject
{
    private int _count;

    public required StartupSourceFilter Value { get; init; }

    public required string Title { get; init; }

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}

/// <summary>The buttons of the bulk action bar.</summary>
internal enum StartupBulkAction
{
    Disable,
    Enable,
    Delete,
}

/// <summary>
/// What a bulk button is about to do with the checked entries, taken from the checks right before the question. Every checked entry
/// is in exactly one of the three lists, so the question can name all of them.
/// </summary>
internal sealed class StartupCheckedPlan
{
    public StartupCheckedPlan(StartupBulkAction action) => Action = action;

    public StartupBulkAction Action { get; }

    /// <summary>Checked entries the action changes and the list shows right now, in list order.</summary>
    public List<StartupItem> Listed { get; } = new();

    /// <summary>
    /// Checked entries the action changes but the list does not show right now (search, source tab, status card, the hidden-Windows
    /// toggles), by name. They are changed too, so the question names them first.
    /// </summary>
    public List<StartupItem> Hidden { get; } = new();

    /// <summary>Checked entries the action leaves alone, with the reason.</summary>
    public List<(StartupItem Item, string Reason)> Skipped { get; } = new();

    /// <summary>Everything the action changes: the listed entries, then the hidden ones.</summary>
    public IEnumerable<StartupItem> Items => Listed.Concat(Hidden);

    public int Count => Listed.Count + Hidden.Count;

    /// <summary>Moves the entries that match from the changed ones to the skipped ones.</summary>
    public void Skip(Func<StartupItem, bool> match, string reason)
    {
        foreach (var list in new[] { Listed, Hidden })
        {
            foreach (var item in list.Where(match).ToList())
            {
                list.Remove(item);
                Skipped.Add((item, reason));
            }
        }
    }
}

public sealed class StartupViewModel : PageViewModel
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);

    private readonly StartupService _service;
    private readonly ObservableCollection<StartupItem> _items = new();
    private StartupFilterOptions _options = new();
    private StartupItem? _selectedItem;
    private SourceFilterOption _selectedSource;
    private string _searchText = "";
    private bool _isLoaded;
    private bool _stale;
    private DateTime _loadedAt;
    private Task? _loadTask;
    private int _enabledCount, _disabledCount, _suspiciousCount, _brokenCount, _deletableBrokenCount;
    private int _windowsTaskCount, _microsoftServiceCount, _shownCount, _visibleTotal;
    private string? _message;
    private bool _messageIsError;

    public StartupViewModel(StartupService service)
    {
        _service = service;
        SourceFilters = new[]
        {
            new SourceFilterOption { Value = StartupSourceFilter.All, Title = "Все" },
            new SourceFilterOption { Value = StartupSourceFilter.RegistryAndFolders, Title = "Реестр и папки" },
            new SourceFilterOption { Value = StartupSourceFilter.Tasks, Title = "Планировщик" },
            new SourceFilterOption { Value = StartupSourceFilter.Services, Title = "Службы" },
        };
        _selectedSource = SourceFilters[0];

        ItemsView = CollectionViewSource.GetDefaultView(_items);
        ItemsView.Filter = o => o is StartupItem item && StartupFilter.Matches(item, _options);

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(), () => !IsBusy);
        ToggleCommand = new AsyncRelayCommand(p => ToggleAsync(ItemOf(p)), p => !IsBusy && ItemOf(p) is { CanToggle: true });
        DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(ItemOf(p)), p => !IsBusy && ItemOf(p) is { CanDelete: true });
        DeleteBrokenCommand = new AsyncRelayCommand(DeleteBrokenAsync, () => !IsBusy && DeletableBrokenCount > 0);
        OpenLocationCommand = new RelayCommand(p => OpenLocation(ItemOf(p)), p => ItemOf(p) is not null);
        CopyCommandCommand = new RelayCommand(p => CopyCommand(ItemOf(p)), p => ItemOf(p) is not null);
        SearchWebCommand = new RelayCommand(p => SearchWeb(ItemOf(p)), p => ItemOf(p) is not null);
        VirusTotalCommand = new AsyncRelayCommand(p => OpenVirusTotalAsync(ItemOf(p)), p => ItemOf(p) is { TargetExists: true });
        DefenderCommand = new AsyncRelayCommand(p => ScanWithDefenderAsync(ItemOf(p)), p => !IsBusy && ItemOf(p) is { TargetExists: true });
        TrustCommand = new RelayCommand(p => ToggleTrust(ItemOf(p)), p => !IsBusy && ItemOf(p) is not null);
        OpenTaskSchedulerCommand = new RelayCommand(p => OpenTaskScheduler(ItemOf(p)), p => ItemOf(p) is { IsTask: true });
        OpenServicesCommand = new RelayCommand(p => OpenServices(ItemOf(p)), p => ItemOf(p) is { IsService: true });
        SetStatusFilterCommand = new RelayCommand(p => SetStatusFilter(p));
        RestartAsAdminCommand = new RelayCommand(() => ElevatedRestart.RequireElevation("просмотр и изменение всех записей автозагрузки"),
            () => !Admin.IsElevated);
        DismissMessageCommand = new RelayCommand(() => Message = null);

        Checked.Changed += (_, _) => OnChecksChanged();
        DisableCheckedCommand = new AsyncRelayCommand(() => ChangeCheckedAsync(StartupBulkAction.Disable), () => !IsBusy && Checked.Count > 0);
        EnableCheckedCommand = new AsyncRelayCommand(() => ChangeCheckedAsync(StartupBulkAction.Enable), () => !IsBusy && Checked.Count > 0);
        DeleteCheckedCommand = new AsyncRelayCommand(() => ChangeCheckedAsync(StartupBulkAction.Delete), () => !IsBusy && Checked.Count > 0);
        ClearChecksCommand = new RelayCommand(Checked.Clear);
        ToggleCheckCommand = new RelayCommand(p => ToggleCheck(p as StartupItem), p => p is StartupItem { CanCheck: true });
        ToggleAllVisibleCommand = new RelayCommand(ToggleAllVisible, () => VisibleCheckable().Any());

        _service.ChangedExternally += () => _stale = true;
    }

    public override string Title => "Автозагрузка";

    public override string Subtitle => "Что запускается вместе с Windows: программы, задания планировщика и службы";

    public override string IconGlyph => "";

    // ===================== data =====================

    public ICollectionView ItemsView { get; }

    public IReadOnlyList<SourceFilterOption> SourceFilters { get; }

    public StartupItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public SourceFilterOption SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is not null && Set(ref _selectedSource, value))
                UpdateFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
                UpdateFilter();
        }
    }

    public StartupStatusFilter StatusFilter => _options.Status;

    public bool IsEnabledFilter => StatusFilter == StartupStatusFilter.Enabled;
    public bool IsDisabledFilter => StatusFilter == StartupStatusFilter.Disabled;
    public bool IsSuspiciousFilter => StatusFilter == StartupStatusFilter.Suspicious;
    public bool IsBrokenFilter => StatusFilter == StartupStatusFilter.Broken;

    public bool ShowWindowsTasks
    {
        get => _options.ShowWindowsTasks;
        set
        {
            if (_options.ShowWindowsTasks == value)
                return;
            _options = _options with { ShowWindowsTasks = value };
            OnPropertyChanged();
            UpdateFilter();
        }
    }

    public bool ShowMicrosoftServices
    {
        get => _options.ShowMicrosoftServices;
        set
        {
            if (_options.ShowMicrosoftServices == value)
                return;
            _options = _options with { ShowMicrosoftServices = value };
            OnPropertyChanged();
            UpdateFilter();
        }
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (Set(ref _isLoaded, value))
                OnPropertyChanged(nameof(IsEmptyStateVisible));
        }
    }

    public int EnabledCount { get => _enabledCount; private set => Set(ref _enabledCount, value); }
    public int DisabledCount { get => _disabledCount; private set => Set(ref _disabledCount, value); }
    public int SuspiciousCount { get => _suspiciousCount; private set => Set(ref _suspiciousCount, value); }
    public int BrokenCount { get => _brokenCount; private set => Set(ref _brokenCount, value); }

    public int DeletableBrokenCount
    {
        get => _deletableBrokenCount;
        private set
        {
            if (Set(ref _deletableBrokenCount, value))
            {
                OnPropertyChanged(nameof(DeleteBrokenText));
                RelayCommand.Refresh();
            }
        }
    }

    public int WindowsTaskCount { get => _windowsTaskCount; private set => Set(ref _windowsTaskCount, value); }
    public int MicrosoftServiceCount { get => _microsoftServiceCount; private set => Set(ref _microsoftServiceCount, value); }

    public int ShownCount
    {
        get => _shownCount;
        private set
        {
            if (Set(ref _shownCount, value))
            {
                OnPropertyChanged(nameof(IsEmptyStateVisible));
                OnPropertyChanged(nameof(ShownText));
            }
        }
    }

    public int VisibleTotal
    {
        get => _visibleTotal;
        private set
        {
            if (Set(ref _visibleTotal, value))
            {
                OnPropertyChanged(nameof(ShownText));
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyText));
                OnPropertyChanged(nameof(EmptyGlyph));
            }
        }
    }

    public string ShownText => ShownCount == VisibleTotal
        ? $"Записей: {Format.Number(VisibleTotal)}"
        : $"Показано {Format.Number(ShownCount)} из {Format.Number(VisibleTotal)}";

    public string DeleteBrokenText => $"Удалить битые записи ({DeletableBrokenCount})";

    public string WindowsTasksText => WindowsTaskCount > 0 ? $"Показать задания Windows ({WindowsTaskCount})" : "Показать задания Windows";

    public string MicrosoftServicesText => MicrosoftServiceCount > 0
        ? $"Показать службы Microsoft ({MicrosoftServiceCount})"
        : "Показать службы Microsoft";

    public bool IsEmptyStateVisible => IsLoaded && ShownCount == 0;

    public string EmptyTitle => VisibleTotal == 0 ? "Автозагрузка пуста" : "Ничего не найдено";

    public string EmptyGlyph => VisibleTotal == 0 ? "\uE73E" : "\uE721";

    public string EmptyText => VisibleTotal == 0
        ? "Вместе с Windows не запускается ни одной сторонней программы. Так держать!"
        : "Измените фильтр или строку поиска.";

    /// <summary>The service's view of the rights (<see cref="Admin.IsElevated"/>; the tests' sandbox replaces it).</summary>
    public bool IsElevated => _service.Env.IsElevated();

    /// <summary>Last action result shown in the banner above the list.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    // ===================== commands =====================

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand DeleteBrokenCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand CopyCommandCommand { get; }
    public RelayCommand SearchWebCommand { get; }
    public AsyncRelayCommand VirusTotalCommand { get; }
    public AsyncRelayCommand DefenderCommand { get; }
    public RelayCommand TrustCommand { get; }
    public RelayCommand OpenTaskSchedulerCommand { get; }
    public RelayCommand OpenServicesCommand { get; }
    public RelayCommand SetStatusFilterCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand DismissMessageCommand { get; }

    /// <summary>"Отключить" in the bulk bar: switches off the checked entries.</summary>
    public AsyncRelayCommand DisableCheckedCommand { get; }

    /// <summary>"Включить" in the bulk bar.</summary>
    public AsyncRelayCommand EnableCheckedCommand { get; }

    /// <summary>"Удалить" in the bulk bar.</summary>
    public AsyncRelayCommand DeleteCheckedCommand { get; }

    /// <summary>"Снять отметки".</summary>
    public RelayCommand ClearChecksCommand { get; }

    /// <summary>A row's checkbox click (parameter: the row).</summary>
    public RelayCommand ToggleCheckCommand { get; }

    /// <summary>Header checkbox click: checks every visible row that can be checked, or unchecks them when all are.</summary>
    public RelayCommand ToggleAllVisibleCommand { get; }

    /// <summary>
    /// Checked entries, by <see cref="StartupItem.Id"/> only: rows read <see cref="StartupItem.IsChecked"/> from here, so the checks
    /// survive a rescan (which builds new row objects) and what the buttons act on is exactly this set.
    /// </summary>
    internal CheckedSet<string> Checked { get; } = new();

    /// <summary>"Отмечено: N" of the bulk action bar.</summary>
    public int CheckedCount => Checked.Count;

    /// <summary>Header checkbox over the rows the list shows: true = all checked, false = none, null = some.</summary>
    public bool? AllVisibleChecked => Checked.StateOf(VisibleCheckable().Select(i => i.Id));

    /// <summary>The page's Yes/No question before the bulk actions (<see cref="Dialogs.Confirm"/>; tests replace it).</summary>
    internal Func<string, string, bool> Confirm { get; set; } = Dialogs.Confirm;

    /// <summary>The one message after a bulk action (tests replace it).</summary>
    internal Action<string, string> ShowInfo { get; set; } = Dialogs.Info;

    /// <summary>Offers the restart as administrator before a bulk action (tests replace it so nothing restarts).</summary>
    internal Func<string, bool> RequireElevation { get; set; } = ElevatedRestart.RequireElevation;

    /// <summary>Creates the restore point before services are changed (tests replace it so none is really created).</summary>
    internal Func<string, CancellationToken, Task<bool>> CreateRestorePoint { get; set; } =
        (description, ct) => RestorePoint.TryCreateAsync(description, ct);

    private StartupItem? ItemOf(object? parameter) => parameter as StartupItem ?? SelectedItem;

    // ===================== navigation / loading =====================

    public override Task OnNavigatedToAsync()
    {
        // First visit (or a failed/cancelled first load): load in the background.
        // Later visits: refresh only if something changed elsewhere or the data is old.
        var needLoad = !_isLoaded
            ? _loadTask is not { IsCompleted: false }
            : _stale || DateTime.Now - _loadedAt > RefreshAfter;
        if (needLoad)
            _ = LoadAsync();
        else if (_isLoaded && !IsBusy)
            RefreshTrust(); // the trusted list may have been edited on another page
        return Task.CompletedTask;
    }

    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        if (_loadTask is { IsCompleted: false })
            await _loadTask;
        else if (!_isLoaded || DateTime.Now - _loadedAt > TimeSpan.FromSeconds(30))
            await LoadAsync(ct);

        // Verification screenshots show the details panel of the first (most important) entry.
        SelectedItem ??= ItemsView.Cast<StartupItem>().FirstOrDefault();
    }

    private Task LoadAsync(CancellationToken external = default)
    {
        if (_loadTask is { IsCompleted: false })
            return _loadTask;
        if (IsBusy)
            return Task.CompletedTask;
        _loadTask = RunBusyAsync("Собираем список автозагрузки…", async ct =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, external);
            var items = await _service.ScanAsync((percent, text) => ReportProgress(percent, text), linked.Token);
            ApplyItems(items);
        });
        return _loadTask;
    }

    internal void ApplyItems(IReadOnlyList<StartupItem> items)
    {
        var selectedId = SelectedItem?.Id;
        _items.Clear();
        foreach (var item in items)
        {
            item.Checks = Checked; // a rescan builds new objects for the same entries: their checks come back by Id
            _items.Add(item);
        }
        _loadedAt = DateTime.Now;
        _stale = false;
        IsLoaded = true;
        RecomputeCounts();
        SelectedItem = selectedId is null ? null : _items.FirstOrDefault(i => i.Id == selectedId && StartupFilter.Matches(i, _options));
    }

    // ===================== filtering =====================

    private void SetStatusFilter(object? parameter)
    {
        var status = parameter is string s && Enum.TryParse<StartupStatusFilter>(s, out var parsed) ? parsed : StartupStatusFilter.All;
        if (status == _options.Status)
            status = StartupStatusFilter.All; // clicking the active card again resets the filter
        _options = _options with { Status = status };
        OnPropertyChanged(nameof(StatusFilter));
        OnPropertyChanged(nameof(IsEnabledFilter));
        OnPropertyChanged(nameof(IsDisabledFilter));
        OnPropertyChanged(nameof(IsSuspiciousFilter));
        OnPropertyChanged(nameof(IsBrokenFilter));
        UpdateFilter();
    }

    private void UpdateFilter()
    {
        _options = _options with { Source = SelectedSource.Value, Search = SearchText };
        ItemsView.Refresh();
        if (SelectedItem is not null && !StartupFilter.Matches(SelectedItem, _options))
            SelectedItem = null; // do not show details of a row that is no longer in the list
        RecomputeCounts();
    }

    private void RecomputeCounts()
    {
        var visible = _items.Where(i => StartupFilter.IsVisible(i, _options)).ToList();
        EnabledCount = visible.Count(i => i.IsEnabled);
        DisabledCount = visible.Count(i => !i.IsEnabled);
        SuspiciousCount = visible.Count(i => i.IsSuspicious);
        BrokenCount = visible.Count(i => i.IsBroken);
        DeletableBrokenCount = _items.Count(i => i.IsBroken && i.CanDelete);
        WindowsTaskCount = _items.Count(i => i.IsWindowsTask);
        MicrosoftServiceCount = _items.Count(i => i.IsMicrosoftService);
        foreach (var option in SourceFilters)
            option.Count = visible.Count(i => StartupFilter.MatchesSource(i, option.Value));
        VisibleTotal = visible.Count;
        ShownCount = _items.Count(i => StartupFilter.Matches(i, _options));
        OnPropertyChanged(nameof(WindowsTasksText));
        OnPropertyChanged(nameof(MicrosoftServicesText));
        SyncChecks();
    }

    /// <summary>
    /// Entries that are gone, and ones that can't be changed any more (a rescan found them protected), leave the checks: a disabled
    /// box is never checked or counted. The header follows the visible rows. Runs with every change of the list or its filter.
    /// </summary>
    private void SyncChecks()
    {
        Checked.KeepOnly(_items.Where(i => i.CanCheck).Select(i => i.Id));
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    /// <summary>
    /// After a state change: refresh the row and the counters. The row stays in the list even if it no longer matches
    /// the current quick filter (no jumping rows); the next filter change re-applies it.
    /// </summary>
    private void AfterItemChanged(StartupItem item)
    {
        item.NotifyStateChanged();
        RecomputeCounts();
        RelayCommand.Refresh();
    }

    /// <summary>The same for several entries at once: every row is refreshed, the counters are recomputed once.</summary>
    private void AfterItemsChanged(IEnumerable<StartupItem> items)
    {
        foreach (var item in items)
            item.NotifyStateChanged();
        RecomputeCounts();
        RelayCommand.Refresh();
    }

    private void ShowMessage(string text, bool isError = false)
    {
        MessageIsError = isError;
        Message = text;
    }

    // ===================== enable / disable =====================

    private async Task ToggleAsync(StartupItem? item)
    {
        if (item is null)
            return;
        var enable = !item.IsEnabled;
        try
        {
            if (!item.CanToggle)
            {
                ShowMessage(item.ToggleBlockedReason);
                return;
            }
            var action = enable ? $"включение «{item.DisplayName}»" : $"отключение «{item.DisplayName}»";
            if (item.RequiresAdmin && !Admin.IsElevated && !item.IsTask)
            {
                ElevatedRestart.RequireElevation(action);
                return;
            }
            if (NeedsToggleConfirm(item, enable) &&
                !Dialogs.Confirm(enable ? "Включить автозапуск?" : "Отключить автозапуск?", BuildToggleConfirm(item, enable)))
                return;

            StartupActionResult? result = null;
            await RunBusyAsync(enable ? $"Включаем «{item.DisplayName}»…" : $"Отключаем «{item.DisplayName}»…", async ct =>
            {
                if (item.IsService)
                {
                    ReportProgress(-1, "Создаём точку восстановления…");
                    await RestorePoint.TryCreateAsync($"Автозагрузка: служба {item.ServiceName}", ct);
                    ReportProgress(-1, enable ? $"Включаем «{item.DisplayName}»…" : $"Отключаем «{item.DisplayName}»…");
                }
                result = await _service.SetEnabledAsync(item, enable, ct);
            });

            if (result is null)
                return;
            if (result.NeedsAdmin)
            {
                ShowMessage(result.Message, isError: true);
                ElevatedRestart.RequireElevation(action);
                return;
            }
            ShowMessage(result.Message, !result.Success);
            if (result.Success && (item.Scope == StartupScope.AllUsers || item.IsService || item.IsTask))
                Dialogs.Info("Автозагрузка", result.Message);
        }
        finally
        {
            // Resets the one-way bound switch to the real state when the change was declined or failed.
            AfterItemChanged(item);
        }
    }

    /// <summary>Per-user Run/Startup-folder changes are reversible like in Task Manager and skip the question.</summary>
    internal static bool NeedsToggleConfirm(StartupItem item, bool enable) => item.Kind switch
    {
        StartupKind.Service or StartupKind.ScheduledTask => true,
        _ => item.Scope == StartupScope.AllUsers && !enable,
    };

    private static string BuildToggleConfirm(StartupItem item, bool enable)
    {
        var sb = new StringBuilder();
        switch (item.Kind)
        {
            case StartupKind.Service:
                if (enable)
                {
                    sb.AppendLine($"Вернуть службе «{item.DisplayName}» автоматический запуск?");
                    sb.AppendLine();
                    sb.AppendLine("Служба снова будет стартовать вместе с Windows.");
                }
                else
                {
                    sb.AppendLine($"Перевести службу «{item.DisplayName}» ({item.ServiceName}) в ручной режим запуска?");
                    sb.AppendLine();
                    sb.AppendLine("Служба перестанет стартовать вместе с Windows, но программы смогут запустить её, когда она понадобится. " +
                                  "Служба не удаляется.");
                    sb.AppendLine();
                    sb.AppendLine("Прежний режим запуска будет запомнен — кнопка «Включить» вернёт его.");
                }
                sb.AppendLine("Перед изменением будет создана точка восстановления Windows (если это разрешено в настройках).");
                break;

            case StartupKind.ScheduledTask:
                sb.AppendLine(enable
                    ? $"Включить задание планировщика «{item.DisplayName}»?"
                    : $"Отключить задание планировщика «{item.DisplayName}»?");
                sb.AppendLine();
                sb.AppendLine($"Путь: {item.TaskPath}");
                sb.AppendLine($"Команда: {Shorten(item.Command)}");
                sb.AppendLine();
                sb.AppendLine(enable
                    ? "Задание снова будет запускаться " + (item.TriggerText ?? "автоматически") + "."
                    : "Задание останется в планировщике, но перестанет запускаться. Включить его обратно можно здесь же.");
                break;

            default:
                sb.AppendLine($"Отключить автозапуск «{item.DisplayName}» для всех пользователей компьютера?");
                sb.AppendLine();
                sb.AppendLine($"Команда: {Shorten(item.Command)}");
                sb.AppendLine();
                sb.AppendLine("Запись не удаляется: Windows просто перестанет запускать программу при входе — так же, как в Диспетчере задач. " +
                              "Включить её обратно можно здесь в любой момент.");
                sb.AppendLine("Перед изменением сохраняется резервная копия раздела реестра.");
                break;
        }
        return sb.ToString().TrimEnd();
    }

    // ===================== delete =====================

    private async Task DeleteAsync(StartupItem? item)
    {
        if (item is null)
            return;
        if (!item.CanDelete)
        {
            ShowMessage(item.IsService
                ? "Службы не удаляются — их можно только перевести в ручной режим переключателем."
                : "Системные записи Windows программа не удаляет.");
            return;
        }
        if (item.RequiresAdmin && !Admin.IsElevated && !item.IsTask)
        {
            ElevatedRestart.RequireElevation($"удаление «{item.DisplayName}» из автозагрузки");
            return;
        }
        if (!Dialogs.Confirm("Удалить из автозагрузки?", BuildDeleteConfirm(item)))
            return;

        StartupActionResult? result = null;
        await RunBusyAsync($"Удаляем «{item.DisplayName}» из автозагрузки…", async ct =>
        {
            result = await _service.DeleteAsync(item, ct);
        });
        if (result is null)
            return;
        if (result.NeedsAdmin)
        {
            ShowMessage(result.Message, isError: true);
            ElevatedRestart.RequireElevation($"удаление «{item.DisplayName}» из автозагрузки");
            return;
        }
        ShowMessage(result.Message, !result.Success);
        if (result.Success)
        {
            RemoveItems(new[] { item });
            Dialogs.Info("Автозагрузка", result.Message);
        }
    }

    private static string BuildDeleteConfirm(StartupItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Удалить «{item.DisplayName}» из автозагрузки?");
        sb.AppendLine();
        sb.AppendLine($"Источник: {item.SourceText} ({item.ScopeText.ToLowerInvariant()})");
        sb.AppendLine($"Где: {item.LocationText}");
        sb.AppendLine($"Команда: {Shorten(item.Command)}");
        sb.AppendLine();
        sb.AppendLine("Сама программа не удаляется — исчезнет только запись автозапуска.");
        sb.AppendLine(item.Kind switch
        {
            StartupKind.StartupFolder => "Ярлык будет перемещён в Корзину — при необходимости его можно восстановить оттуда.",
            StartupKind.ScheduledTask => $"Перед удалением задание сохраняется в XML-файл ({AppPaths.BackupsDir}); " +
                                         "восстановить его можно через «Планировщик заданий» → «Импортировать задачу».",
            _ => $"Перед удалением сохраняется резервная копия раздела реестра ({AppPaths.BackupsDir}); " +
                 "чтобы вернуть запись, достаточно дважды щёлкнуть .reg-файл.",
        });
        if (!item.IsBroken && !item.IsRunOnce)
        {
            sb.AppendLine();
            sb.AppendLine("Совет: если вы просто не хотите, чтобы программа запускалась сама, её достаточно отключить — это обратимо.");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task DeleteBrokenAsync()
    {
        // The list may be minutes old: re-check every file so a program reinstalled meanwhile is never removed as "broken".
        // This quick check only shapes the confirmation; the service re-reads every entry (its current command, not only the
        // old file) right before deleting it, so an entry rewritten by an update is left alone too.
        var candidates = _items.Where(i => i.IsBroken && i.CanDelete).ToList();
        var broken = candidates.Where(StillBroken).ToList();
        var reload = broken.Count < candidates.Count;
        try
        {
            if (broken.Count == 0)
            {
                ShowMessage(candidates.Count == 0
                    ? "Битых записей нет."
                    : "Файлы этих записей снова на месте — удалять нечего. Список обновлён.");
                return;
            }
            reload |= await DeleteBrokenCoreAsync(broken);
        }
        finally
        {
            // Some entries came back or changed (program reinstalled or updated?): show their real state once the deletion has finished.
            if (reload)
                await LoadAsync();
        }
    }

    /// <summary>Confirms and deletes; returns true when some entries changed since the scan and the list must be reloaded.</summary>
    private async Task<bool> DeleteBrokenCoreAsync(IReadOnlyList<StartupItem> broken)
    {
        var allowed = Admin.IsElevated ? broken : broken.Where(i => !i.RequiresAdmin || i.IsTask).ToList();
        var skipped = broken.Count - allowed.Count;
        if (allowed.Count == 0)
        {
            ElevatedRestart.RequireElevation("удаление битых записей автозагрузки для всех пользователей");
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Удалить {Format.Count(allowed.Count, "битую запись", "битые записи", "битых записей")} из автозагрузки?");
        sb.AppendLine();
        foreach (var item in allowed.Take(15))
            sb.AppendLine($"• {item.DisplayName} — {item.SourceShort}: {Shorten(item.TargetFile ?? item.Command, 70)}");
        if (allowed.Count > 15)
            sb.AppendLine($"• …и ещё {allowed.Count - 15}");
        sb.AppendLine();
        sb.AppendLine("Эти записи ссылаются на файлы, которых больше нет, поэтому ничего не запускают. " +
                      "Перед удалением сохраняются резервные копии: реестр — в .reg-файлы, задания — в XML, ярлыки — в Корзину.");
        if (skipped > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{Format.Count(skipped, "запись", "записи", "записей")} для всех пользователей " +
                          $"{Format.Plural(skipped, "будет пропущена", "будут пропущены", "будут пропущены")} — " +
                          "для них нужны права администратора.");
        }
        if (!Dialogs.Confirm("Удалить битые записи?", sb.ToString().TrimEnd()))
            return false;

        StartupBulkResult? result = null;
        await RunBusyAsync("Удаляем битые записи…", async ct =>
        {
            result = await _service.DeleteBrokenAsync(allowed, ct);
        });
        if (result is null)
            return false;
        RemoveItems(result.Done);
        var summary = result.Summary;
        if (skipped > 0)
            summary += $"{Environment.NewLine}{Environment.NewLine}Пропущено (нужны права администратора): {skipped}.";
        ShowMessage(BrokenResultMessage(result), result.Failed.Count > 0);
        Dialogs.Info("Битые записи", summary);
        return result.Skipped.Count > 0;
    }

    /// <summary>Banner text after "Удалить битые".</summary>
    internal static string BrokenResultMessage(StartupBulkResult result)
    {
        string text;
        if (result.Failed.Count > 0)
            text = $"Удалено {result.Done.Count}, не удалось: {result.Failed.Count}.";
        else if (result.Done.Count == 0 && result.Skipped.Count > 0)
            text = "Удалять нечего.";
        else
            text = $"Битые записи удалены: {result.Done.Count}.";
        if (result.Skipped.Count > 0)
            text += $" Оставлено без изменений (запись изменилась после проверки): {result.Skipped.Count}. Список обновлён.";
        return text;
    }

    /// <summary>True only if the target file is still definitely missing right now.</summary>
    private static bool StillBroken(StartupItem item)
        => StartupNative.TargetExists(item.TargetFile, item.Is32Bit) == false;

    private void RemoveItems(IEnumerable<StartupItem> removed)
    {
        foreach (var item in removed.ToList())
        {
            if (ReferenceEquals(SelectedItem, item))
                SelectedItem = null;
            _items.Remove(item);
        }
        RecomputeCounts();
        RelayCommand.Refresh();
    }

    // ===================== check several entries =====================

    private IEnumerable<StartupItem> VisibleCheckable() => ItemsView.Cast<StartupItem>().Where(i => i.CanCheck);

    private static void ToggleCheck(StartupItem? item)
    {
        if (item is not null)
            item.IsChecked = !item.IsChecked;
    }

    private void ToggleAllVisible()
    {
        Checked.SetAll(VisibleCheckable().Select(i => i.Id), AllVisibleChecked != true);
        // Also when nothing changed: the clicked header checkbox has toggled itself and must show the real state again.
        OnPropertyChanged(nameof(AllVisibleChecked));
    }

    private void OnChecksChanged()
    {
        foreach (var item in _items)
            item.OnCheckedChanged();
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AllVisibleChecked));
        RelayCommand.Refresh();
    }

    private const string ProtectedReason = "системная запись Windows, программа её не меняет";

    private const string NeedsAdminReason = "нужны права администратора";

    /// <summary>Why a checkable entry is left alone by the action; null when the action changes it.</summary>
    private static string? NotApplicableReason(StartupItem item, StartupBulkAction action) => action switch
    {
        StartupBulkAction.Disable => !item.CanToggle
            ? "однократный запуск нельзя отключить, его можно только удалить"
            : item.IsEnabled ? null : "уже отключена",
        StartupBulkAction.Enable => !item.CanToggle
            ? "однократный запуск не бывает отключённым, включать нечего"
            : item.IsEnabled ? "уже включена" : null,
        _ => item.CanDelete ? null : "службы не удаляются, их можно только перевести в ручной режим",
    };

    /// <summary>
    /// Machine-wide entries and services need administrator rights. A scheduled task is not asked about: it may well be changeable
    /// without them (a task of the current user), and when it is not, the change reports it.
    /// </summary>
    private static bool NeedsAdminRights(StartupItem item) => item.RequiresAdmin && !item.IsTask;

    /// <summary>
    /// Takes the checks as they are right now: the checked entries the list shows are split from those it does not (search, tab,
    /// card), and each lands in the list the action changes or in the skipped one with its reason. Changes nothing.
    /// </summary>
    internal StartupCheckedPlan PlanChecked(StartupBulkAction action)
    {
        var plan = new StartupCheckedPlan(action);
        var listed = ItemsView.Cast<StartupItem>().ToList();
        var listedIds = listed.Select(i => i.Id).ToHashSet();
        var hidden = _items.Where(i => !listedIds.Contains(i.Id)).OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in listed.Concat(hidden).Where(i => Checked.IsChecked(i.Id)))
        {
            var reason = item.CanCheck ? NotApplicableReason(item, action) : ProtectedReason;
            if (reason is not null)
                plan.Skipped.Add((item, reason));
            else
                (listedIds.Contains(item.Id) ? plan.Listed : plan.Hidden).Add(item);
        }
        return plan;
    }

    /// <summary>What a bulk button did: filled while it runs, so a stop or an error half way still shows the real state.</summary>
    private sealed class CheckedOutcome
    {
        public bool Started { get; set; }

        public bool Finished { get; set; }

        /// <summary>The user pressed «Отмена»: the rest was left alone.</summary>
        public bool Cancelled { get; set; }

        public bool NeedsAdmin { get; set; }

        /// <summary>Entries a change was tried on (their rows are refreshed afterwards).</summary>
        public List<StartupItem> Attempted { get; } = new();

        public List<StartupItem> Done { get; } = new();

        public List<(StartupItem Item, string Error)> Failed { get; } = new();

        /// <summary>The result of the deletion; null if it was stopped or failed half way.</summary>
        public StartupBulkResult? Deleted { get; set; }
    }

    private async Task ChangeCheckedAsync(StartupBulkAction action)
    {
        if (IsBusy)
            return; // RunBusyAsync would silently skip the work
        var plan = PlanChecked(action);
        // A disabled box is never checked: entries found protected right now leave the checks at once.
        Checked.SetAll(plan.Skipped.Where(s => !s.Item.CanCheck).Select(s => s.Item.Id), false);

        if (!IsElevated && plan.Items.Any(NeedsAdminRights) && !RequireElevation(ElevationText(action)))
            plan.Skip(NeedsAdminRights, NeedsAdminReason);

        if (plan.Count == 0)
        {
            if (plan.Skipped.Count == 0)
                return;
            var nothing = action switch
            {
                StartupBulkAction.Disable => "Нечего отключать",
                StartupBulkAction.Enable => "Нечего включать",
                _ => "Нечего удалять",
            };
            ShowMessage($"{nothing}: ни одна из отмеченных записей не подходит для этого действия.");
            ShowInfo("Автозагрузка", $"{nothing}.\n\n{SkippedSection(plan.Skipped)}");
            return;
        }

        if (!Confirm(CheckedTitle(plan), BuildCheckedConfirm(plan)))
            return;

        var outcome = new CheckedOutcome();
        await RunBusyAsync(action switch
        {
            StartupBulkAction.Disable => "Отключаем отмеченные записи…",
            StartupBulkAction.Enable => "Включаем отмеченные записи…",
            _ => "Удаляем отмеченные записи из автозагрузки…",
        }, ct => RunCheckedAsync(plan, outcome, ct));

        // Whatever happened, the rows show the real state again (this also resets a switch after a failure or «Отмена»), and what was
        // done leaves the checks; what failed or was left alone stays checked.
        Checked.SetAll(outcome.Done.Select(i => i.Id), false);
        AfterItemsChanged(outcome.Attempted);
        if (!outcome.Started)
            return;
        if (action == StartupBulkAction.Delete && outcome.Deleted is null)
        {
            // Stopped or failed half way: some entries may be gone already, so show the real list.
            ShowMessage(outcome.Cancelled
                ? "Удаление остановлено. Часть записей могла быть удалена — список обновлён."
                : "Не удалось удалить отмеченные записи. Список обновлён.", isError: !outcome.Cancelled);
            await LoadAsync();
            return;
        }
        if (action != StartupBulkAction.Delete && !outcome.Finished)
            return; // an unexpected error: RunBusyAsync has shown it

        int done, failed;
        bool needsAdmin;
        string details;
        if (action == StartupBulkAction.Delete)
        {
            var result = outcome.Deleted!;
            RemoveItems(result.Done);
            (done, failed, needsAdmin) = (result.Done.Count, result.Failed.Count, result.NeedsAdmin);
            details = result.Summary;
            if (plan.Skipped.Count > 0)
                details += $"{Environment.NewLine}{Environment.NewLine}{SkippedSection(plan.Skipped)}";
        }
        else
        {
            (done, failed, needsAdmin) = (outcome.Done.Count, outcome.Failed.Count, outcome.NeedsAdmin);
            details = ToggleDetails(plan, outcome);
        }

        ShowMessage(CheckedBanner(action, done, failed, plan.Skipped.Count, outcome.Cancelled), isError: failed > 0);
        if (done == 0 && needsAdmin && !IsElevated)
            RequireElevation(ElevationText(action));
        else
            ShowInfo("Автозагрузка", details);
    }

    private async Task RunCheckedAsync(StartupCheckedPlan plan, CheckedOutcome outcome, CancellationToken ct)
    {
        outcome.Started = true;
        var items = plan.Items.ToList();
        try
        {
            if (plan.Action == StartupBulkAction.Delete)
            {
                outcome.Deleted = await _service.DeleteManyAsync(items, ct);
            }
            else
            {
                var enable = plan.Action == StartupBulkAction.Enable;
                var services = items.Count(i => i.IsService);
                if (services > 0)
                {
                    // One restore point for all of them, like the single switch makes one for its service.
                    ReportProgress(-1, "Создаём точку восстановления…");
                    await CreateRestorePoint($"Автозагрузка: {Format.Count(services, "служба", "службы", "служб")}", ct);
                }
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[i];
                    ReportProgress(i * 100.0 / items.Count,
                        $"{(enable ? "Включаем" : "Отключаем")} «{item.DisplayName}»… ({i + 1} из {items.Count})");
                    outcome.Attempted.Add(item);
                    var result = await _service.SetEnabledAsync(item, enable, ct);
                    if (result.Success)
                    {
                        outcome.Done.Add(item);
                        continue;
                    }
                    outcome.Failed.Add((item, result.Message));
                    outcome.NeedsAdmin |= result.NeedsAdmin;
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome.Cancelled = true; // keep what was done; the rest stays as it was
        }
        outcome.Finished = true;
    }

    private static string ElevationText(StartupBulkAction action) => action switch
    {
        StartupBulkAction.Disable => "Отключение отмеченных записей автозагрузки",
        StartupBulkAction.Enable => "Включение отмеченных записей автозагрузки",
        _ => "Удаление отмеченных записей из автозагрузки",
    };

    private static string CheckedTitle(StartupCheckedPlan plan)
    {
        var one = plan.Count == 1;
        return plan.Action switch
        {
            StartupBulkAction.Disable => one ? "Отключить отмеченную запись?" : "Отключить отмеченные записи?",
            StartupBulkAction.Enable => one ? "Включить отмеченную запись?" : "Включить отмеченные записи?",
            _ => one ? "Удалить отмеченную запись из автозагрузки?" : "Удалить отмеченные записи из автозагрузки?",
        };
    }

    /// <summary>One line per entry: its name and where it comes from, so two entries of the same name can be told apart.</summary>
    private static string EntryLine(StartupItem item) => item.Kind switch
    {
        StartupKind.ScheduledTask => $"{item.DisplayName} — Планировщик, {item.TaskPath}",
        StartupKind.Service => $"{item.DisplayName} — служба {item.ServiceName}",
        _ => $"{item.DisplayName} — {item.SourceShort}, {item.SourceDetail}",
    };

    private static string SkippedSection(IReadOnlyList<(StartupItem Item, string Reason)> skipped)
        => $"Не затронуты ({skipped.Count}):\n" + BulkText.BulletList(skipped.Select(s => $"{s.Item.DisplayName} — {s.Reason}"));

    /// <summary>The question before a bulk action: exactly the entries the action will change, hidden ones first.</summary>
    private static string BuildCheckedConfirm(StartupCheckedPlan plan)
    {
        var count = Format.Count(plan.Count, "запись", "записи", "записей");
        var total = plan.Action switch
        {
            StartupBulkAction.Disable => $"Отключить автозапуск ({count})",
            StartupBulkAction.Enable => $"Включить автозапуск ({count})",
            _ => $"Удалить из автозагрузки ({count})",
        };

        var sb = new StringBuilder();
        if (plan.Hidden.Count == 0)
        {
            sb.AppendLine(total + ":");
            sb.AppendLine(BulkText.BulletList(plan.Listed.Select(EntryLine)));
        }
        else
        {
            // They are changed too, so they come first and are never folded into "…и ещё N".
            sb.AppendLine(total + ".");
            sb.AppendLine();
            sb.AppendLine("Не видны в списке сейчас:");
            sb.AppendLine(BulkText.BulletList(plan.Hidden.Select(EntryLine), plan.Hidden.Count));
            if (plan.Listed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("В списке:");
                sb.AppendLine(BulkText.BulletList(plan.Listed.Select(EntryLine)));
            }
        }
        if (plan.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(SkippedSection(plan.Skipped));
        }

        var items = plan.Items.ToList();
        sb.AppendLine();
        switch (plan.Action)
        {
            case StartupBulkAction.Disable:
                sb.AppendLine("Записи не удаляются: Windows просто перестанет запускать их при входе — так же, как в Диспетчере задач. " +
                              "Включить их обратно можно здесь в любой момент.");
                if (items.Any(i => i.IsService))
                    sb.AppendLine("Службы будут переведены в ручной режим запуска и не удаляются: программы смогут запустить их, когда понадобится. " +
                                  "Прежний режим запоминается — «Включить» вернёт его. " +
                                  "Перед изменением будет создана точка восстановления Windows (если это разрешено в настройках).");
                if (items.Any(i => i.IsTask))
                    sb.AppendLine("Задания останутся в планировщике, но перестанут запускаться.");
                break;

            case StartupBulkAction.Enable:
                sb.AppendLine("Записи снова будут запускаться вместе с Windows.");
                if (items.Any(i => i.IsService))
                    sb.AppendLine("Службам вернётся прежний автоматический запуск. " +
                                  "Перед изменением будет создана точка восстановления Windows (если это разрешено в настройках).");
                break;

            default:
                sb.AppendLine("Сами программы не удаляются — исчезнут только записи автозапуска.");
                sb.AppendLine($"Перед удалением сохраняются резервные копии: реестр — в .reg-файлы, задания — в XML (папка {AppPaths.BackupsDir}), " +
                              "ярлыки перемещаются в Корзину.");
                if (items.Any(i => !i.IsBroken && !i.IsRunOnce))
                    sb.AppendLine("Совет: если вы просто не хотите, чтобы программы запускались сами, их достаточно отключить — это обратимо.");
                break;
        }
        sb.AppendLine();
        sb.Append("Продолжить?");
        return sb.ToString();
    }

    /// <summary>The page banner after a bulk action: "Отключено: 3, не удалось: 1, не затронуто: 2."</summary>
    private static string CheckedBanner(StartupBulkAction action, int done, int failed, int skipped, bool stopped)
    {
        var verb = action switch
        {
            StartupBulkAction.Disable => "Отключено",
            StartupBulkAction.Enable => "Включено",
            _ => "Удалено",
        };
        var text = $"{verb}: {done}";
        if (failed > 0)
            text += $", не удалось: {failed}";
        if (skipped > 0)
            text += $", не затронуто: {skipped}";
        return text + (stopped ? ". Остановлено — остальные записи не тронуты." : ".");
    }

    /// <summary>The one message after "Отключить" / "Включить" (<see cref="StartupBulkResult.Summary"/> does the same for the deletion).</summary>
    private static string ToggleDetails(StartupCheckedPlan plan, CheckedOutcome outcome)
    {
        var enable = plan.Action == StartupBulkAction.Enable;
        var lines = new List<string>
        {
            outcome.Done.Count > 0
                ? $"{(enable ? "Включено" : "Отключено")}: {Format.Count(outcome.Done.Count, "запись", "записи", "записей")}."
                : $"Ничего не {(enable ? "включено" : "отключено")}.",
        };
        if (outcome.Cancelled)
            lines.Add("Остановлено: остальные записи не тронуты.");
        if (outcome.Failed.Count > 0)
        {
            lines.Add("");
            lines.Add($"Не удалось изменить ({outcome.Failed.Count}):");
            lines.Add(BulkText.BulletList(outcome.Failed.Select(f => $"{f.Item.DisplayName} — {f.Error}"), 10));
        }
        if (plan.Skipped.Count > 0)
        {
            lines.Add("");
            lines.Add(SkippedSection(plan.Skipped));
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ===================== item tools =====================

    private void OpenLocation(StartupItem? item)
    {
        if (item is null)
            return;
        if (item.TargetExists == true && !string.IsNullOrWhiteSpace(item.TargetFile))
            ProcessRunner.ShowInExplorer(item.TargetFile);
        else if (!string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath))
            ProcessRunner.ShowInExplorer(item.FilePath);
        else
            Dialogs.Info("Файл не найден", $"Файл «{item.TargetFile ?? item.Command}» не найден на диске.\n\nЗапись находится здесь: {item.LocationText}");
    }

    private void CopyCommand(StartupItem? item)
    {
        if (item is null)
            return;
        if (TrySetClipboard(item.Command))
            ShowMessage("Команда скопирована в буфер обмена.");
    }

    private void SearchWeb(StartupItem? item)
    {
        if (item is not null)
            FileTools.SearchWeb(item.SearchQuery);
    }

    private async Task OpenVirusTotalAsync(StartupItem? item)
    {
        if (item?.TargetFile is null || item.TargetExists != true)
        {
            Dialogs.Info("VirusTotal", "Файл не найден — проверять нечего.");
            return;
        }
        ShowMessage("Открываем отчёт VirusTotal по контрольной сумме файла. Сам файл никуда не отправляется.");
        await FileTools.OpenVirusTotalAsync(item.TargetFile);
    }

    private async Task ScanWithDefenderAsync(StartupItem? item)
    {
        if (item?.TargetFile is null || item.TargetExists != true)
        {
            Dialogs.Info("Защитник Windows", "Файл не найден — проверять нечего.");
            return;
        }
        var target = item.TargetFile;
        await RunBusyAsync($"Защитник Windows проверяет {Path.GetFileName(target)}…", async ct =>
        {
            string message;
            try
            {
                message = await FileTools.ScanWithDefenderAsync(target, ct);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                Log.Warn($"Автозагрузка: не удалось запустить проверку Защитником: {ex.Message}");
                message = $"Не удалось запустить проверку: {ErrorText.For(ex)}.";
            }
            var threat = message.Contains("обнаружил", StringComparison.OrdinalIgnoreCase);
            ShowMessage($"Защитник Windows: {message}", threat);
            Dialogs.Info("Защитник Windows", $"{item.DisplayName}\n{target}\n\n{message}");
        });
    }

    private void ToggleTrust(StartupItem? item)
    {
        if (item is null)
            return;
        // Interpreters (powershell.exe, rundll32.exe…) are trusted per entry, never as a file: see StartupService.TrustKey.
        var trusted = StartupService.SetTrusted(item, !item.IsTrusted, _service.Settings);
        ShowMessage(trusted
            ? $"«{item.DisplayName}» отмечен как доверенный — предупреждения для него больше не показываются. " +
              "Убрать его из доверенных можно здесь же или в «Настройках»."
            : $"«{item.DisplayName}» больше не в списке доверенных — проверки снова включены.");

        // The same program file may be used by several entries (the «Процессы» page checks the same file key).
        RefreshTrust();
        AfterItemChanged(item);
    }

    /// <summary>
    /// Re-applies the shared trusted list to the entries whose trust changed (here, or on another page such as
    /// «Настройки» → «Доверенные»). Cheap: only those entries are re-analyzed.
    /// </summary>
    internal void RefreshTrust()
    {
        var settings = _service.Settings;
        var changed = false;
        foreach (var item in _items)
        {
            if (StartupService.IsTrustedNow(item, settings) == item.IsTrusted)
                continue;
            _service.Reanalyze(item);
            item.NotifyStateChanged();
            changed = true;
        }
        if (changed)
        {
            RecomputeCounts();
            RelayCommand.Refresh();
        }
    }

    private void OpenTaskScheduler(StartupItem? item)
    {
        ProcessRunner.ShellOpen("taskschd.msc");
        if (item?.TaskPath is { } path && TrySetClipboard(path))
            ShowMessage($"Открываем «Планировщик заданий». Путь задания скопирован в буфер обмена: {path}");
    }

    private void OpenServices(StartupItem? item)
    {
        ProcessRunner.ShellOpen("services.msc");
        if (item is not null)
            ShowMessage($"Открываем список служб Windows. Найдите в нём «{item.DisplayName}».");
    }

    private bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException)
        {
            Log.Warn($"Не удалось скопировать в буфер обмена: {ex.Message}");
            ShowMessage("Буфер обмена занят другой программой — попробуйте ещё раз.", true);
            return false;
        }
    }

    private static string Shorten(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
