using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using PcCleaner.Core;

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
        RestartAsAdminCommand = new RelayCommand(() => Admin.RequireElevation("просмотр и изменение всех записей автозагрузки"),
            () => !Admin.IsElevated);
        DismissMessageCommand = new RelayCommand(() => Message = null);

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

    public string WindowsTasksText => WindowsTaskCount > 0 ? $"Показать задачи Windows ({WindowsTaskCount})" : "Показать задачи Windows";

    public string MicrosoftServicesText => MicrosoftServiceCount > 0
        ? $"Показать службы Microsoft ({MicrosoftServiceCount})"
        : "Показать службы Microsoft";

    public bool IsEmptyStateVisible => IsLoaded && ShownCount == 0;

    public string EmptyTitle => VisibleTotal == 0 ? "Автозагрузка пуста" : "Ничего не найдено";

    public string EmptyGlyph => VisibleTotal == 0 ? "\uE73E" : "\uE721";

    public string EmptyText => VisibleTotal == 0
        ? "Вместе с Windows не запускается ни одной сторонней программы. Так держать!"
        : "Измените фильтр или строку поиска.";

    public bool IsElevated => Admin.IsElevated;

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
            _items.Add(item);
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
            var action = enable ? $"включение «{item.DisplayName}»" : $"отключение «{item.DisplayName}»";
            if (item.RequiresAdmin && !Admin.IsElevated && !item.IsTask)
            {
                Admin.RequireElevation(action);
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
                Admin.RequireElevation(action);
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
            Admin.RequireElevation($"удаление «{item.DisplayName}» из автозагрузки");
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
            Admin.RequireElevation($"удаление «{item.DisplayName}» из автозагрузки");
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
        var broken = _items.Where(i => i.IsBroken && i.CanDelete).ToList();
        if (broken.Count == 0)
        {
            ShowMessage("Битых записей нет.");
            return;
        }
        var allowed = Admin.IsElevated ? broken : broken.Where(i => !i.RequiresAdmin || i.IsTask).ToList();
        var skipped = broken.Count - allowed.Count;
        if (allowed.Count == 0)
        {
            Admin.RequireElevation("удаление битых записей автозагрузки для всех пользователей");
            return;
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
            sb.AppendLine($"{Format.Count(skipped, "запись", "записи", "записей")} для всех пользователей будут пропущены — " +
                          "для них нужны права администратора.");
        }
        if (!Dialogs.Confirm("Удалить битые записи?", sb.ToString().TrimEnd()))
            return;

        StartupBulkResult? result = null;
        await RunBusyAsync("Удаляем битые записи…", async ct =>
        {
            result = await _service.DeleteManyAsync(allowed, ct);
        });
        if (result is null)
            return;
        RemoveItems(result.Done);
        var summary = result.Summary;
        if (skipped > 0)
            summary += $"{Environment.NewLine}{Environment.NewLine}Пропущено (нужны права администратора): {skipped}.";
        ShowMessage(result.Failed.Count == 0
            ? $"Битые записи удалены: {result.Done.Count}."
            : $"Удалено {result.Done.Count}, не удалось: {result.Failed.Count}.", result.Failed.Count > 0);
        Dialogs.Info("Битые записи", summary);
    }

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
                message = $"Не удалось запустить проверку: {ex.Message}";
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
        var settings = AppServices.Settings;
        if (item.IsTrusted)
        {
            settings.TrustedItems.RemoveAll(t =>
                string.Equals(t, item.TargetFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, item.Id, StringComparison.OrdinalIgnoreCase));
            ShowMessage($"«{item.DisplayName}» больше не в списке доверенных.");
            Log.Info($"Автозагрузка: «{item.DisplayName}» убран из доверенных");
        }
        else
        {
            settings.TrustedItems.Add(string.IsNullOrWhiteSpace(item.TargetFile) ? item.Id : item.TargetFile);
            ShowMessage($"«{item.DisplayName}» отмечен как доверенный — предупреждения для него больше не показываются.");
            Log.Info($"Автозагрузка: «{item.DisplayName}» добавлен в доверенные");
        }
        settings.Save();

        // The same file may be used by several entries.
        foreach (var other in _items.Where(i => ReferenceEquals(i, item) ||
                                                (item.TargetFile is not null &&
                                                 string.Equals(i.TargetFile, item.TargetFile, StringComparison.OrdinalIgnoreCase))))
            _service.Reanalyze(other);
        AfterItemChanged(item);
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
