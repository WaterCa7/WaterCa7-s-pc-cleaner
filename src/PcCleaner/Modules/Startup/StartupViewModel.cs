using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
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

public sealed partial class StartupViewModel : PageViewModel
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
            new SourceFilterOption { Value = StartupSourceFilter.All, Title = T._("Все") },
            new SourceFilterOption { Value = StartupSourceFilter.RegistryAndFolders, Title = T._("Реестр и папки") },
            new SourceFilterOption { Value = StartupSourceFilter.Tasks, Title = T._("Планировщик") },
            new SourceFilterOption { Value = StartupSourceFilter.Services, Title = T._("Службы") },
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
        RestartAsAdminCommand = new RelayCommand(() => ElevatedRestart.RequireElevation(T._("просмотр и изменение всех записей автозагрузки")),
            () => !IsElevated);
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

    public override string Title => T._("Автозагрузка");

    public override string Subtitle => T._("Что запускается вместе с Windows: программы, задания планировщика и службы");

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
        ? T.F("Записей: {0}", Format.Number(VisibleTotal))
        : T.F("Показано {0} из {1}", Format.Number(ShownCount), Format.Number(VisibleTotal));

    public string DeleteBrokenText => T.F("Удалить битые записи ({0})", DeletableBrokenCount);

    public string WindowsTasksText => WindowsTaskCount > 0 ? T.F("Показать задания Windows ({0})", WindowsTaskCount) : T._("Показать задания Windows");

    public string MicrosoftServicesText => MicrosoftServiceCount > 0
        ? T.F("Показать службы Microsoft ({0})", MicrosoftServiceCount)
        : T._("Показать службы Microsoft");

    public bool IsEmptyStateVisible => IsLoaded && ShownCount == 0;

    public string EmptyTitle => VisibleTotal == 0 ? T._("Автозагрузка пуста") : T._("Ничего не найдено");

    public string EmptyGlyph => VisibleTotal == 0 ? "\uE73E" : "\uE721";

    public string EmptyText => VisibleTotal == 0
        ? T._("Вместе с Windows не запускается ни одной сторонней программы. Так держать!")
        : T._("Измените фильтр или строку поиска.");

    /// <summary>
    /// The page's one source of truth for the rights: the service's (<see cref="Admin.IsElevated"/> in the app, the sandbox's in tests),
    /// so every action here and the service itself agree.
    /// </summary>
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
        _loadTask = RunBusyAsync(T._("Собираем список автозагрузки…"), async ct =>
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
    /// After a state change: refresh the row and the counters. The row stays in the list even if it no longer matches
    /// the current quick filter (no jumping rows); the next filter change re-applies it.
    /// </summary>
    private void AfterItemChanged(StartupItem item)
    {
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
            var action = enable ? T.F("включение {0}", T.Quote(item.DisplayName)) : T.F("отключение {0}", T.Quote(item.DisplayName));
            if (item.RequiresAdmin && !IsElevated && !item.IsTask)
            {
                ElevatedRestart.RequireElevation(action);
                return;
            }
            if (NeedsToggleConfirm(item, enable) &&
                !Dialogs.Confirm(enable ? T._("Включить автозапуск?") : T._("Отключить автозапуск?"), BuildToggleConfirm(item, enable)))
                return;

            StartupActionResult? result = null;
            await RunBusyAsync(enable ? T.F("Включаем {0}…", T.Quote(item.DisplayName)) : T.F("Отключаем {0}…", T.Quote(item.DisplayName)), async ct =>
            {
                if (item.IsService)
                {
                    ReportProgress(-1, T._("Создаём точку восстановления…"));
                    await RestorePoint.TryCreateAsync(T.F("Автозагрузка: служба {0}", item.ServiceName), ct);
                    ReportProgress(-1, enable ? T.F("Включаем {0}…", T.Quote(item.DisplayName)) : T.F("Отключаем {0}…", T.Quote(item.DisplayName)));
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
                Dialogs.Info(T._("Автозагрузка"), result.Message);
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
                    sb.AppendLine(T.F("Вернуть службе {0} автоматический запуск?", T.Quote(item.DisplayName)));
                    sb.AppendLine();
                    sb.AppendLine(T._("Служба снова будет стартовать вместе с Windows."));
                }
                else
                {
                    sb.AppendLine(T.F("Перевести службу {0} ({1}) в ручной режим запуска?", T.Quote(item.DisplayName), item.ServiceName));
                    sb.AppendLine();
                    sb.AppendLine(T._("Служба перестанет стартовать вместе с Windows, но программы смогут запустить её, когда она понадобится. Служба не удаляется."));
                    sb.AppendLine();
                    sb.AppendLine(T._("Прежний режим запуска будет запомнен — кнопка «Включить» вернёт его."));
                }
                sb.AppendLine(T._("Перед изменением будет создана точка восстановления Windows (если это разрешено в настройках)."));
                break;

            case StartupKind.ScheduledTask:
                sb.AppendLine(enable
                    ? T.F("Включить задание планировщика {0}?", T.Quote(item.DisplayName))
                    : T.F("Отключить задание планировщика {0}?", T.Quote(item.DisplayName)));
                sb.AppendLine();
                sb.AppendLine(T.F("Путь: {0}", item.TaskPath));
                sb.AppendLine(T.F("Команда: {0}", Shorten(item.Command)));
                sb.AppendLine();
                sb.AppendLine(enable
                    ? T.F("Задание снова будет запускаться {0}.", item.TriggerText ?? T._("автоматически"))
                    : T._("Задание останется в планировщике, но перестанет запускаться. Включить его обратно можно здесь же."));
                break;

            default:
                sb.AppendLine(T.F("Отключить автозапуск {0} для всех пользователей компьютера?", T.Quote(item.DisplayName)));
                sb.AppendLine();
                sb.AppendLine(T.F("Команда: {0}", Shorten(item.Command)));
                sb.AppendLine();
                sb.AppendLine(T._("Запись не удаляется: Windows просто перестанет запускать программу при входе — так же, как в Диспетчере задач. Включить её обратно можно здесь в любой момент."));
                sb.AppendLine(T._("Перед изменением сохраняется резервная копия раздела реестра."));
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
                ? T._("Службы не удаляются — их можно только перевести в ручной режим переключателем.")
                : T._("Системные записи Windows программа не удаляет."));
            return;
        }
        if (item.RequiresAdmin && !IsElevated && !item.IsTask)
        {
            ElevatedRestart.RequireElevation(T.F("удаление {0} из автозагрузки", T.Quote(item.DisplayName)));
            return;
        }
        if (!Dialogs.Confirm(T._("Удалить из автозагрузки?"), BuildDeleteConfirm(item)))
            return;

        StartupActionResult? result = null;
        await RunBusyAsync(T.F("Удаляем {0} из автозагрузки…", T.Quote(item.DisplayName)), async ct =>
        {
            result = await _service.DeleteAsync(item, ct);
        });
        if (result is null)
            return;
        if (result.NeedsAdmin)
        {
            ShowMessage(result.Message, isError: true);
            ElevatedRestart.RequireElevation(T.F("удаление {0} из автозагрузки", T.Quote(item.DisplayName)));
            return;
        }
        ShowMessage(result.Message, !result.Success);
        if (result.Success)
        {
            RemoveItems(new[] { item });
            Dialogs.Info(T._("Автозагрузка"), result.Message);
        }
    }

    private static string BuildDeleteConfirm(StartupItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine(T.F("Удалить {0} из автозагрузки?", T.Quote(item.DisplayName)));
        sb.AppendLine();
        sb.AppendLine(T.F("Источник: {0} ({1})", item.SourceText, item.ScopeText.ToLowerInvariant()));
        sb.AppendLine(T.F("Где: {0}", item.LocationText));
        sb.AppendLine(T.F("Команда: {0}", Shorten(item.Command)));
        sb.AppendLine();
        sb.AppendLine(T._("Сама программа не удаляется — исчезнет только запись автозапуска."));
        sb.AppendLine(item.Kind switch
        {
            StartupKind.StartupFolder => T._("Ярлык будет перемещён в Корзину — при необходимости его можно восстановить оттуда."),
            StartupKind.ScheduledTask => T.F("Перед удалением задание сохраняется в XML-файл ({0}); восстановить его можно через «Планировщик заданий» → «Импортировать задачу».", AppPaths.BackupsDir),
            _ => T.F("Перед удалением сохраняется резервная копия раздела реестра ({0}); чтобы вернуть запись, достаточно дважды щёлкнуть .reg-файл.", AppPaths.BackupsDir),
        });
        if (!item.IsBroken && !item.IsRunOnce)
        {
            sb.AppendLine();
            sb.AppendLine(T._("Совет: если вы просто не хотите, чтобы программа запускалась сама, её достаточно отключить — это обратимо."));
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
                    ? T._("Битых записей нет.")
                    : T._("Файлы этих записей снова на месте — удалять нечего. Список обновлён."));
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
        var allowed = IsElevated ? broken : broken.Where(i => !i.RequiresAdmin || i.IsTask).ToList();
        var skipped = broken.Count - allowed.Count;
        if (allowed.Count == 0)
        {
            ElevatedRestart.RequireElevation(T._("удаление битых записей автозагрузки для всех пользователей"));
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine(T.F("Удалить {0} из автозагрузки?", Format.Count(allowed.Count, "битую запись", "битые записи", "битых записей")));
        sb.AppendLine();
        foreach (var item in allowed.Take(15))
            sb.AppendLine($"• {item.DisplayName} — {item.SourceShort}: {Shorten(item.TargetFile ?? item.Command, 70)}");
        if (allowed.Count > 15)
            sb.AppendLine("• " + T.F("…и ещё {0}", allowed.Count - 15));
        sb.AppendLine();
        sb.AppendLine(T._("Эти записи ссылаются на файлы, которых больше нет, поэтому ничего не запускают. Перед удалением сохраняются резервные копии: реестр — в .reg-файлы, задания — в XML, ярлыки — в Корзину."));
        if (skipped > 0)
        {
            sb.AppendLine();
            sb.AppendLine(T.F("{0} для всех пользователей {1} — для них нужны права администратора.",
                Format.Count(skipped, "запись", "записи", "записей"), Format.Plural(skipped, "будет пропущена", "будут пропущены", "будут пропущены")));
        }
        if (!Dialogs.Confirm(T._("Удалить битые записи?"), sb.ToString().TrimEnd()))
            return false;

        StartupBulkResult? result = null;
        await RunBusyAsync(T._("Удаляем битые записи…"), async ct =>
        {
            result = await _service.DeleteBrokenAsync(allowed, ct);
        });
        if (result is null)
            return false;
        RemoveItems(result.Done);
        var summary = result.Summary;
        if (skipped > 0)
            summary += Environment.NewLine + Environment.NewLine + T.F("Пропущено (нужны права администратора): {0}.", skipped);
        ShowMessage(BrokenResultMessage(result), result.Failed.Count > 0);
        Dialogs.Info(T._("Битые записи"), summary);
        return result.Skipped.Count > 0;
    }

    /// <summary>Banner text after "Удалить битые".</summary>
    internal static string BrokenResultMessage(StartupBulkResult result)
    {
        string text;
        if (result.Failed.Count > 0)
            text = T.F("Удалено {0}, не удалось: {1}.", result.Done.Count, result.Failed.Count);
        else if (result.Done.Count == 0 && result.Skipped.Count > 0)
            text = T._("Удалять нечего.");
        else
            text = T.F("Битые записи удалены: {0}.", result.Done.Count);
        if (result.Skipped.Count > 0)
            text += " " + T.F("Оставлено без изменений (запись изменилась после проверки): {0}. Список обновлён.", result.Skipped.Count);
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
            Dialogs.Info(T._("Файл не найден"), T.F("Файл {0} не найден на диске.\n\nЗапись находится здесь: {1}", T.Quote(item.TargetFile ?? item.Command), item.LocationText));
    }

    private void CopyCommand(StartupItem? item)
    {
        if (item is null)
            return;
        if (TrySetClipboard(item.Command))
            ShowMessage(T._("Команда скопирована в буфер обмена."));
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
            Dialogs.Info("VirusTotal", T._("Файл не найден — проверять нечего."));
            return;
        }
        ShowMessage(T._("Открываем отчёт VirusTotal по контрольной сумме файла. Сам файл никуда не отправляется."));
        await FileTools.OpenVirusTotalAsync(item.TargetFile);
    }

    /// <summary>Test seam: the Defender scan of one file (<see cref="FileTools.ScanWithDefenderAsync"/>).</summary>
    internal Func<string, CancellationToken, Task<DefenderScanResult>> DefenderScan { get; set; } =
        (path, ct) => FileTools.ScanWithDefenderAsync(path, ct);

    private async Task ScanWithDefenderAsync(StartupItem? item)
    {
        if (item?.TargetFile is null || item.TargetExists != true)
        {
            Dialogs.Info(T._("Защитник Windows"), T._("Файл не найден — проверять нечего."));
            return;
        }
        var target = item.TargetFile;
        await RunBusyAsync(T.F("Защитник Windows проверяет {0}…", Path.GetFileName(target)), async ct =>
        {
            DefenderScanResult scan;
            try
            {
                scan = await DefenderScan(target, ct);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                Log.Warn(T.F("Автозагрузка: не удалось запустить проверку Защитником: {0}", ErrorText.For(ex)), ex);
                scan = new DefenderScanResult(T.F("Не удалось запустить проверку: {0}.", ErrorText.For(ex)), ThreatFound: false);
            }
            // The verdict is the scan's own flag, never a word of its text (the text is Russian or English).
            ShowMessage(T.F("Защитник Windows: {0}", scan.Text), scan.ThreatFound);
            Dialogs.Info(T._("Защитник Windows"), $"{item.DisplayName}\n{target}\n\n{scan.Text}");
        });
    }

    private void ToggleTrust(StartupItem? item)
    {
        if (item is null)
            return;
        // Interpreters (powershell.exe, rundll32.exe…) are trusted per entry, never as a file: see StartupService.TrustKey.
        var trusted = StartupService.SetTrusted(item, !item.IsTrusted, _service.Settings);
        ShowMessage(trusted
            ? T.F("{0} отмечен как доверенный — предупреждения для него больше не показываются. Убрать его из доверенных можно здесь же или в «Настройках».", T.Quote(item.DisplayName))
            : T.F("{0} больше не в списке доверенных — проверки снова включены.", T.Quote(item.DisplayName)));

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
            ShowMessage(T.F("Открываем «Планировщик заданий». Путь задания скопирован в буфер обмена: {0}", path));
    }

    private void OpenServices(StartupItem? item)
    {
        ProcessRunner.ShellOpen("services.msc");
        if (item is not null)
            ShowMessage(T.F("Открываем список служб Windows. Найдите в нём {0}.", T.Quote(item.DisplayName)));
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
            Log.Warn(T.F("Не удалось скопировать в буфер обмена: {0}", ErrorText.For(ex)), ex);
            ShowMessage(T._("Буфер обмена занят другой программой — попробуйте ещё раз."), true);
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
