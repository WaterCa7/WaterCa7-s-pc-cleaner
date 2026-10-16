using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using PcCleaner.Core;
using ElevatedRestart = PcCleaner.Modules.Settings.ElevatedRestart;

namespace PcCleaner.Modules.Programs;

public enum ProgramFilter
{
    All,
    Unwanted,
    Recent,
    Large,
    Broken,
}

public enum StoreFilter
{
    All,
    Removable,
}

/// <summary>A filter "chip" with a live counter.</summary>
public sealed class FilterOption<T> : ObservableObject
{
    private readonly Action<T> _onSelected;
    private bool _isSelected;
    private int _count;

    public FilterOption(T value, string title, string toolTip, Action<T> onSelected, bool selected = false)
    {
        Value = value;
        Title = title;
        ToolTip = toolTip;
        _onSelected = onSelected;
        _isSelected = selected;
    }

    public T Value { get; }

    public string Title { get; }

    public string ToolTip { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => $"{Title} · {Format.Number(Count)}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
                _onSelected(Value);
        }
    }
}

public sealed class ProgramsViewModel : PageViewModel
{
    private static readonly TimeSpan EnrichBudget = TimeSpan.FromSeconds(90);
    private static readonly Regex FamilyNameRegex = new(@"^[A-Za-z0-9._\-~]+$", RegexOptions.Compiled);

    private readonly ProgramsService _service;

    private readonly RangeObservableCollection<InstalledProgram> _programs = new();
    private readonly RangeObservableCollection<StoreApp> _storeApps = new();
    private readonly ListCollectionView _programsView;
    private readonly ListCollectionView _storeView;
    private InstalledProgram? _selectedProgram;
    private StoreApp? _selectedStoreApp;
    private string _searchText = "";
    private string _storeSearchText = "";
    private ProgramFilter _filter = ProgramFilter.All;
    private StoreFilter _storeFilter = StoreFilter.All;
    private int _selectedTabIndex;
    private bool _loaded;
    private DateTime _lastLoad;
    private Task? _loadTask;
    private Task? _enrichTask;
    private CancellationTokenSource? _enrichCts;
    private string? _storeError;
    private int _unreadableEntries;
    private string _totalSizeText = "";
    private int _pupCount;
    private int _brokenCount;
    private int _bloatCount;

    public ProgramsViewModel(ProgramsService service)
    {
        _service = service;
        _programsView = CreateProgramsView(_programs);
        _storeView = CreateStoreView(_storeApps);

        Filters = new ObservableCollection<FilterOption<ProgramFilter>>
        {
            new(ProgramFilter.All, "Все", "Все установленные программы", SetFilter, selected: true),
            new(ProgramFilter.Unwanted, "Нежелательные", "Программы, похожие на рекламные, «оптимизаторы» и другие потенциально нежелательные", SetFilter),
            new(ProgramFilter.Recent, "Недавние", $"Установлены или обновлены за последние {ProgramRules.RecentDays} дней", SetFilter),
            new(ProgramFilter.Large, "Большие", "Занимают больше 1 ГБ", SetFilter),
            new(ProgramFilter.Broken, "Битые", "Программа уже удалена, а запись в списке осталась", SetFilter),
        };
        StoreFilters = new ObservableCollection<FilterOption<StoreFilter>>
        {
            new(StoreFilter.All, "Все", "Все приложения Microsoft Store вашей учётной записи", SetStoreFilter, selected: true),
            new(StoreFilter.Removable, "Можно удалить", "Предустановленные приложения, которые можно безопасно удалить", SetStoreFilter),
        };

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(), () => !IsBusy);
        UninstallCommand = new AsyncRelayCommand(() => UninstallAsync(SelectedProgram, quiet: false),
            () => !IsBusy && SelectedProgram is { CanUninstall: true });
        QuietUninstallCommand = new AsyncRelayCommand(() => UninstallAsync(SelectedProgram, quiet: true),
            () => !IsBusy && SelectedProgram is { ShowQuietUninstall: true });
        ToggleDetailsCommand = new RelayCommand(() => IsDetailsOpen = !IsDetailsOpen);
        DeleteEntryCommand = new AsyncRelayCommand(() => DeleteEntryAsync(SelectedProgram),
            () => !IsBusy && SelectedProgram is { IsBroken: true });
        CleanBrokenCommand = new AsyncRelayCommand(CleanBrokenAsync, () => !IsBusy && BrokenCount > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedProgram is { InstallLocationExists: true });
        SearchWebCommand = new RelayCommand(SearchWeb, () => SelectedProgram is not null);
        ToggleTrustCommand = new RelayCommand(ToggleTrust, () => !IsBusy && SelectedProgram is not null);
        VirusTotalCommand = new AsyncRelayCommand(CheckVirusTotalAsync, () => SelectedProgram?.MainExecutable is not null);
        CopyNameCommand = new RelayCommand(CopyName, () => SelectedProgram is not null);
        ShowFilterCommand = new RelayCommand(p => ShowFilter(p), _ => true);
        ShowRemovableAppsCommand = new RelayCommand(ShowRemovableApps);

        RemoveStoreAppCommand = new AsyncRelayCommand(() => RemoveStoreAppAsync(SelectedStoreApp),
            () => !IsBusy && SelectedStoreApp is { CanRemove: true });
        RemoveAllBloatCommand = new AsyncRelayCommand(RemoveAllBloatAsync, () => !IsBusy && BloatCount > 0);
        OpenStorePageCommand = new RelayCommand(OpenStorePage, () => SelectedStoreApp?.PackageFamilyName is not null);
        StoreSearchWebCommand = new RelayCommand(StoreSearchWeb, () => SelectedStoreApp is not null);
        OpenStoreFolderCommand = new RelayCommand(OpenStoreFolder, () => !string.IsNullOrWhiteSpace(SelectedStoreApp?.InstallLocation));
    }

    public override string Title => "Программы";

    public override string Subtitle => "Установленные программы и приложения Store: что можно удалить, нежелательное ПО и «битые» записи";

    public override string IconGlyph => "\uE71D";

    // ===================================================================== bindable state

    public ObservableCollection<FilterOption<ProgramFilter>> Filters { get; }

    public ObservableCollection<FilterOption<StoreFilter>> StoreFilters { get; }

    public ListCollectionView ProgramsView => _programsView;

    public ListCollectionView StoreAppsView => _storeView;

    public IReadOnlyList<InstalledProgram> Programs => _programs;

    public IReadOnlyList<StoreApp> StoreApps => _storeApps;

    public InstalledProgram? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (Set(ref _selectedProgram, value))
            {
                OnPropertyChanged(nameof(HasSelectedProgram));
                OnPropertyChanged(nameof(TrustButtonText));
                OnPropertyChanged(nameof(NeedsAdminForEntry));
                OnPropertyChanged(nameof(UninstallMenuText));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasSelectedProgram => SelectedProgram is not null;

    /// <summary>Context menu text: runtimes say right away that removing them is not recommended.</summary>
    public string UninstallMenuText => SelectedProgram?.IsRuntime == true
        ? "Удалить компонент… (не рекомендуется)"
        : "Удалить программу…";

    /// <summary>The details panel on the right; the user can fold it into a slim strip to give the list more room.</summary>
    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        set => Set(ref _isDetailsOpen, value);
    }

    private bool _isDetailsOpen = true;

    public StoreApp? SelectedStoreApp
    {
        get => _selectedStoreApp;
        set
        {
            if (Set(ref _selectedStoreApp, value))
            {
                OnPropertyChanged(nameof(HasSelectedStoreApp));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasSelectedStoreApp => SelectedStoreApp is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
                RefreshProgramsView();
        }
    }

    public string StoreSearchText
    {
        get => _storeSearchText;
        set
        {
            if (Set(ref _storeSearchText, value ?? ""))
                RefreshStoreView();
        }
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => Set(ref _selectedTabIndex, value);
    }

    public bool IsLoaded => _loaded;

    public int TotalCount => _programs.Count;

    public string TotalCountText => _loaded ? Format.Number(TotalCount) : "—";

    public string TotalSizeText
    {
        get => _totalSizeText;
        private set => Set(ref _totalSizeText, value);
    }

    public int PupCount
    {
        get => _pupCount;
        private set
        {
            if (Set(ref _pupCount, value))
            {
                OnPropertyChanged(nameof(PupCountText));
                OnPropertyChanged(nameof(HasPups));
            }
        }
    }

    public bool HasPups => PupCount > 0;

    public string PupCountText => _loaded ? Format.Number(PupCount) : "—";

    public string PupHint => PupCount == 0 ? "не найдено — отлично" : "стоит проверить";

    public int BrokenCount
    {
        get => _brokenCount;
        private set
        {
            if (Set(ref _brokenCount, value))
            {
                OnPropertyChanged(nameof(BrokenCountText));
                OnPropertyChanged(nameof(HasBroken));
            }
        }
    }

    public bool HasBroken => BrokenCount > 0;

    public string BrokenCountText => _loaded ? Format.Number(BrokenCount) : "—";

    public int BloatCount
    {
        get => _bloatCount;
        private set
        {
            if (Set(ref _bloatCount, value))
                OnPropertyChanged(nameof(BloatCountText));
        }
    }

    public string BloatCountText => _loaded ? Format.Number(BloatCount) : "—";

    public string BloatHint => BloatCount == 0 ? "лишних не найдено" : "можно удалить";

    public string StoreTabHeader => _loaded ? $"Приложения Microsoft Store · {Format.Number(_storeApps.Count)}" : "Приложения Microsoft Store";

    public string ProgramsTabHeader => _loaded ? $"Программы · {Format.Number(_programs.Count)}" : "Программы";

    public string? StoreError
    {
        get => _storeError;
        private set
        {
            if (Set(ref _storeError, value))
                OnPropertyChanged(nameof(HasStoreError));
        }
    }

    public bool HasStoreError => !string.IsNullOrWhiteSpace(StoreError);

    public bool IsNotElevated => !Admin.IsElevated;

    /// <summary>Selected entry is a machine-wide broken entry and the app runs without admin rights.</summary>
    public bool NeedsAdminForEntry => SelectedProgram is { IsBroken: true, IsMachineWide: true } && !Admin.IsElevated;

    public bool HasUnreadableEntries => _unreadableEntries > 0;

    public string UnreadableEntriesText => _unreadableEntries == 0
        ? ""
        : $"{Format.Count(_unreadableEntries, "запись", "записи", "записей")} не удалось прочитать — запустите программу от имени администратора, чтобы увидеть всё.";

    public string TrustButtonText => SelectedProgram?.IsTrusted == true ? "Убрать из доверенных" : "Доверять";

    public bool IsProgramsEmpty => _programsView.IsEmpty;

    public string ProgramsEmptyTitle
    {
        get
        {
            if (!_loaded)
                return "Загружаем список программ…";
            if (_programs.Count == 0)
                return "Программы не найдены";
            if (!string.IsNullOrWhiteSpace(SearchText))
                return $"По запросу «{SearchText.Trim()}» ничего не найдено";
            return _filter switch
            {
                ProgramFilter.Unwanted => "Нежелательных программ не найдено",
                ProgramFilter.Recent => "Недавно установленных программ нет",
                ProgramFilter.Large => "Больших программ не найдено",
                ProgramFilter.Broken => "Битых записей нет",
                _ => "Список пуст",
            };
        }
    }

    public string ProgramsEmptyText
    {
        get
        {
            if (!_loaded)
                return "Это займёт несколько секунд.";
            if (_programs.Count == 0)
                return "Не удалось прочитать список установленных программ из реестра.";
            if (!string.IsNullOrWhiteSpace(SearchText))
                return "Проверьте написание или выберите фильтр «Все».";
            return _filter switch
            {
                ProgramFilter.Unwanted => "Рекламных программ, «оптимизаторов» и браузеров «в нагрузку» не обнаружено. Отлично!",
                ProgramFilter.Recent => $"За последние {ProgramRules.RecentDays} дней ничего не устанавливалось и не обновлялось.",
                ProgramFilter.Large => "Ни одна программа не занимает больше 1 ГБ (или размер ещё вычисляется).",
                ProgramFilter.Broken => "Все записи в списке программ ведут к существующим программам.",
                _ => "",
            };
        }
    }

    /// <summary>Icon of the empty list: loading, nothing found by search, or good news (no PUPs / broken entries).</summary>
    public string ProgramsEmptyGlyph => !_loaded ? "\uE72C"
        : _programs.Count > 0 && !string.IsNullOrWhiteSpace(SearchText) ? "\uE721"
        : ProgramsEmptyIsGood ? "\uE73E" : "\uE946";

    public bool ProgramsEmptyIsGood => _loaded && _programs.Count > 0 && string.IsNullOrWhiteSpace(SearchText) &&
                                       _filter is ProgramFilter.Unwanted or ProgramFilter.Broken;

    public bool IsStoreEmpty => _storeView.IsEmpty;

    public string StoreEmptyGlyph => !_loaded ? "\uE72C"
        : _storeApps.Count > 0 && !string.IsNullOrWhiteSpace(StoreSearchText) ? "\uE721"
        : StoreEmptyIsGood ? "\uE73E" : "\uE946";

    public bool StoreEmptyIsGood => _loaded && _storeApps.Count > 0 && string.IsNullOrWhiteSpace(StoreSearchText) &&
                                    _storeFilter == StoreFilter.Removable;

    public string StoreEmptyTitle
    {
        get
        {
            if (!_loaded)
                return "Загружаем приложения…";
            if (_storeApps.Count == 0)
                return HasStoreError ? "Список приложений недоступен" : "Приложений Microsoft Store не найдено";
            if (!string.IsNullOrWhiteSpace(StoreSearchText))
                return $"По запросу «{StoreSearchText.Trim()}» ничего не найдено";
            return _storeFilter == StoreFilter.Removable ? "Лишних предустановленных приложений нет" : "Список пуст";
        }
    }

    public string StoreEmptyText => _storeFilter == StoreFilter.Removable && _storeApps.Count > 0 && string.IsNullOrWhiteSpace(StoreSearchText)
        ? "Все известные «лишние» приложения уже удалены. Отлично!"
        : "";

    // ===================================================================== commands

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand QuietUninstallCommand { get; }
    public AsyncRelayCommand DeleteEntryCommand { get; }
    public AsyncRelayCommand CleanBrokenCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand SearchWebCommand { get; }
    public RelayCommand ToggleTrustCommand { get; }
    public RelayCommand ToggleDetailsCommand { get; }
    public AsyncRelayCommand VirusTotalCommand { get; }
    public RelayCommand CopyNameCommand { get; }
    public RelayCommand ShowFilterCommand { get; }
    public RelayCommand ShowRemovableAppsCommand { get; }
    public AsyncRelayCommand RemoveStoreAppCommand { get; }
    public AsyncRelayCommand RemoveAllBloatCommand { get; }
    public RelayCommand OpenStorePageCommand { get; }
    public RelayCommand StoreSearchWebCommand { get; }
    public RelayCommand OpenStoreFolderCommand { get; }

    // ===================================================================== navigation

    public override Task OnNavigatedToAsync()
    {
        var firstVisit = _loadTask is null;
        var previousFailed = !_loaded && _loadTask is { IsCompleted: true } && !IsBusy;
        var stale = _loaded && !IsBusy && DateTime.Now - _lastLoad > TimeSpan.FromMinutes(2);
        if (firstVisit || previousFailed || stale)
            _loadTask = LoadAsync();
        else if (_loaded && !IsBusy)
            RefreshTrust(); // the trusted list may have been edited on another page (e.g. «Настройки»)
        return Task.CompletedTask;
    }

    /// <summary>The settings with the shared trusted list (tests use a private instance).</summary>
    internal Func<AppSettings> Settings { get; set; } = () => AppServices.Settings;

    /// <summary>Test seam: shows a ready list (on the calling thread) as if it had just been loaded.</summary>
    internal void ShowProgramsForTests(IReadOnlyList<InstalledProgram> programs)
    {
        ApplyPrograms(new ProgramScanResult(programs, 0));
        _loaded = true;
        _lastLoad = DateTime.Now;
        _loadTask = Task.CompletedTask;
        RaiseAllSummary();
    }

    /// <summary>Re-applies the shared trusted list to the programs whose trust changed since they were checked.</summary>
    internal void RefreshTrust()
    {
        var settings = Settings();
        var changed = false;
        foreach (var p in _programs)
        {
            if (settings.IsTrusted(p.DisplayName) == p.IsTrusted)
                continue;
            ProgramsService.ApplyTrust(p, settings);
            changed = true;
        }
        if (!changed)
            return;
        OnPropertyChanged(nameof(TrustButtonText));
        UpdateSummary();
        if (_filter == ProgramFilter.Unwanted)
            RefreshProgramsView();
    }

    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        if (_loadTask is { IsCompleted: false })
            await _loadTask;
        if (!_loaded)
        {
            _loadTask = LoadAsync();
            await _loadTask;
        }

        var enrich = _enrichTask;
        if (enrich is not null && !enrich.IsCompleted)
            await Task.WhenAny(enrich, Task.Delay(TimeSpan.FromSeconds(25), ct));
        UpdateSummary();

        // Show the details panel with real data in the preview.
        SelectedProgram ??= _programsView.Cast<InstalledProgram>().FirstOrDefault(p => p.IsPup)
                            ?? _programsView.Cast<InstalledProgram>().FirstOrDefault(p => p.IsBroken)
                            ?? _programsView.Cast<InstalledProgram>().FirstOrDefault();
        SelectedStoreApp ??= _storeView.Cast<StoreApp>().FirstOrDefault(a => a.IsBloatware)
                             ?? _storeView.Cast<StoreApp>().FirstOrDefault();
    }

    // ===================================================================== loading

    private Task LoadAsync() => RunBusyAsync("Поиск установленных программ…", async ct =>
    {
        var scan = await _service.ScanProgramsAsync(ct);
        // Show the programs right away; the Store query (PowerShell) takes a few more seconds.
        ApplyPrograms(scan);
        _loaded = true;
        _lastLoad = DateTime.Now;
        RaiseAllSummary();
        try
        {
            ReportProgress(-1, "Получаем список приложений Microsoft Store…");
            var store = await _service.ScanStoreAppsAsync(ct);
            ApplyStoreApps(store);
            RaiseAllSummary();
        }
        finally
        {
            StartEnrichment();
        }
        Log.Info($"Программы: найдено {Format.Count(_programs.Count, "программа", "программы", "программ")} и " +
                 $"{Format.Count(_storeApps.Count, "приложение", "приложения", "приложений")} Microsoft Store");
    });

    private async Task RescanProgramsAsync()
    {
        var scan = await _service.ScanProgramsAsync(CancellationToken.None);
        ApplyPrograms(scan);
        RaiseAllSummary();
        StartEnrichment();
    }

    private async Task RescanStoreAppsAsync()
    {
        var store = await _service.ScanStoreAppsAsync(CancellationToken.None);
        ApplyStoreApps(store);
        RaiseAllSummary();
        StartEnrichment();
    }

    private void ApplyPrograms(ProgramScanResult scan)
    {
        var selectedKey = SelectedProgram?.RegistryKeyPath;
        _programs.ReplaceAll(scan.Programs.OrderBy(p => p.DisplayName, NameComparer));
        _unreadableEntries = scan.UnreadableEntries;
        SelectedProgram = selectedKey is null
            ? null
            : _programs.FirstOrDefault(p => string.Equals(p.RegistryKeyPath, selectedKey, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(Programs));
    }

    private void ApplyStoreApps(StoreAppsResult store)
    {
        var selected = SelectedStoreApp?.PackageFullName;
        _storeApps.ReplaceAll(store.Apps.OrderBy(a => a.DisplayName, NameComparer));
        StoreError = store.Error;
        SelectedStoreApp = selected is null
            ? null
            : _storeApps.FirstOrDefault(a => string.Equals(a.PackageFullName, selected, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(StoreApps));
    }

    /// <summary>Invariant (ICU root) order puts Latin before Cyrillic, like the Windows "Apps" list.</summary>
    private static readonly StringComparer NameComparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

    private ListCollectionView CreateProgramsView(RangeObservableCollection<InstalledProgram> source)
        => new(source) { Filter = FilterProgram, Culture = CultureInfo.InvariantCulture };

    private ListCollectionView CreateStoreView(RangeObservableCollection<StoreApp> source)
        => new(source) { Filter = FilterStoreApp, Culture = CultureInfo.InvariantCulture };

    private void StartEnrichment()
    {
        _enrichCts?.Cancel();
        _enrichCts?.Dispose();
        _enrichCts = new CancellationTokenSource();
        var programs = _programs.ToList();
        var apps = _storeApps.ToList();
        _enrichTask = EnrichAsync(programs, apps, _enrichCts.Token);
    }

    private async Task EnrichAsync(IReadOnlyList<InstalledProgram> programs, IReadOnlyList<StoreApp> apps, CancellationToken ct)
    {
        try
        {
            var lastSummary = System.Diagnostics.Stopwatch.StartNew();
            await _service.EnrichAsync(programs, apps, EnrichBudget, ct, () => Ui.Post(() =>
            {
                // Keep the "занимают" total in step with sizes measured in the background (at most every 1.5 s).
                if (lastSummary.Elapsed < TimeSpan.FromSeconds(1.5) || ct.IsCancellationRequested)
                    return;
                lastSummary.Restart();
                UpdateSummary(refreshFilteredView: false);
            }));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Программы: не удалось вычислить размеры или значки: {ex.Message}");
        }
        if (ReferenceEquals(programs.FirstOrDefault(), _programs.FirstOrDefault()))
            UpdateSummary();
    }

    // ===================================================================== filtering & summary

    private void SetFilter(ProgramFilter filter)
    {
        _filter = filter;
        RefreshProgramsView();
    }

    private void SetStoreFilter(StoreFilter filter)
    {
        _storeFilter = filter;
        RefreshStoreView();
    }

    private bool FilterProgram(object o)
    {
        if (o is not InstalledProgram p)
            return false;
        if (!MatchesSearch(p.SearchText, SearchText))
            return false;
        return _filter switch
        {
            ProgramFilter.Unwanted => p.IsPup,
            ProgramFilter.Recent => p.IsRecent,
            ProgramFilter.Large => p.IsLarge,
            ProgramFilter.Broken => p.IsBroken,
            _ => true,
        };
    }

    private bool FilterStoreApp(object o)
    {
        if (o is not StoreApp a)
            return false;
        if (!MatchesSearch(a.SearchText, StoreSearchText))
            return false;
        return _storeFilter != StoreFilter.Removable || a.IsBloatware;
    }

    internal static bool MatchesSearch(string haystack, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RefreshProgramsView()
    {
        _programsView.Refresh();
        OnPropertyChanged(nameof(IsProgramsEmpty));
        OnPropertyChanged(nameof(ProgramsEmptyTitle));
        OnPropertyChanged(nameof(ProgramsEmptyText));
        OnPropertyChanged(nameof(ProgramsEmptyGlyph));
        OnPropertyChanged(nameof(ProgramsEmptyIsGood));
    }

    private void RefreshStoreView()
    {
        _storeView.Refresh();
        OnPropertyChanged(nameof(IsStoreEmpty));
        OnPropertyChanged(nameof(StoreEmptyTitle));
        OnPropertyChanged(nameof(StoreEmptyText));
        OnPropertyChanged(nameof(StoreEmptyGlyph));
        OnPropertyChanged(nameof(StoreEmptyIsGood));
    }

    /// <summary>Recounts cards and filter chips. The filtered list is re-applied only when asked (it resets scrolling).</summary>
    private void UpdateSummary(bool refreshFilteredView = true)
    {
        PupCount = _programs.Count(p => p.IsPup);
        BrokenCount = _programs.Count(p => p.IsBroken);
        BloatCount = _storeApps.Count(a => a.IsBloatware);

        // Registry estimates + measured folders (each folder counted once).
        long total = _programs.Where(p => !p.IsBroken).Sum(p => p.EstimatedSizeBytes);
        total += _programs.Where(p => !p.IsBroken && p.EstimatedSizeBytes == 0 && p.ComputedSizeBytes > 0)
            .GroupBy(p => p.InstallLocation ?? p.RegistryKeyPath, StringComparer.OrdinalIgnoreCase)
            .Sum(g => g.First().ComputedSizeBytes);
        TotalSizeText = total > 0 ? $"занимают ≈ {Format.Bytes(total)}" : "размер вычисляется…";

        foreach (var f in Filters)
        {
            f.Count = f.Value switch
            {
                ProgramFilter.Unwanted => PupCount,
                ProgramFilter.Recent => _programs.Count(p => p.IsRecent),
                ProgramFilter.Large => _programs.Count(p => p.IsLarge),
                ProgramFilter.Broken => BrokenCount,
                _ => _programs.Count,
            };
        }
        foreach (var f in StoreFilters)
            f.Count = f.Value == StoreFilter.Removable ? BloatCount : _storeApps.Count;

        OnPropertyChanged(nameof(PupHint));
        OnPropertyChanged(nameof(BloatHint));
        if (refreshFilteredView && _filter == ProgramFilter.Large)
            RefreshProgramsView();
        RelayCommand.Refresh();
    }

    private void RaiseAllSummary()
    {
        UpdateSummary();
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(PupCountText));
        OnPropertyChanged(nameof(BrokenCountText));
        OnPropertyChanged(nameof(BloatCountText));
        OnPropertyChanged(nameof(ProgramsTabHeader));
        OnPropertyChanged(nameof(StoreTabHeader));
        OnPropertyChanged(nameof(HasUnreadableEntries));
        OnPropertyChanged(nameof(UnreadableEntriesText));
        RefreshProgramsView();
        RefreshStoreView();
    }

    private void ShowFilter(object? parameter)
    {
        var filter = parameter switch
        {
            ProgramFilter f => f,
            string s when Enum.TryParse<ProgramFilter>(s, true, out var parsed) => parsed,
            _ => ProgramFilter.All,
        };
        SelectedTabIndex = 0;
        foreach (var f in Filters)
            f.IsSelected = f.Value == filter;
    }

    private void ShowRemovableApps()
    {
        SelectedTabIndex = 1;
        foreach (var f in StoreFilters)
            f.IsSelected = f.Value == StoreFilter.Removable;
    }

    // ===================================================================== classic programs: actions

    private async Task UninstallAsync(InstalledProgram? p, bool quiet)
    {
        if (p is null || IsBusy)
            return;
        if (!p.CanUninstall)
        {
            Dialogs.Info("Удаление программы", p.IsBroken
                ? "Деинсталлятор этой программы не найден — скорее всего, она уже удалена. Уберите запись из списка кнопкой «Убрать запись из списка»."
                : "У программы не указан деинсталлятор. Попробуйте удалить её через «Параметры» → «Приложения».");
            return;
        }

        if (quiet && p.IsRuntime)
            return; // never offered for runtimes (see InstalledProgram.ShowQuietUninstall)

        if (!Dialogs.Confirm(p.IsRuntime ? "Удаление компонента Microsoft — не рекомендуется" : "Удаление программы",
                BuildUninstallConfirmation(p, quiet)))
            return;
        if (quiet && !Dialogs.Confirm("Тихое удаление",
                $"Тихое удаление пройдёт без вопросов деинсталлятора, и остановить его на полпути не получится.\n\n" +
                $"Точно удалить «{p.DisplayName}» в тихом режиме?"))
            return;

        UninstallOutcome? outcome = null;
        var stoppedWaiting = false;
        await RunBusyAsync($"Удаление «{p.DisplayName}»…", async ct =>
        {
            ReportProgress(-1, "Создаём точку восстановления…");
            await RestorePoint.TryCreateAsync($"Удаление {p.DisplayName}", ct);
            ReportProgress(-1, $"Ждём завершения деинсталлятора «{p.DisplayName}». Если его окно уже закрыто, нажмите «Отмена».");
            try
            {
                outcome = await _service.RunUninstallerAsync(p, quiet,
                    exe => ReportProgress(-1, $"Ждём завершения деинсталлятора ({exe}). Если его окно уже закрыто, нажмите «Отмена»."), ct);
            }
            catch (OperationCanceledException)
            {
                stoppedWaiting = true;
                Log.Info($"Удаление «{p.DisplayName}»: ожидание деинсталлятора прервано пользователем");
            }
            ReportProgress(-1, "Обновляем список программ…");
            await RescanProgramsAsync();
        });

        var stillThere = _programs.Any(x => string.Equals(x.RegistryKeyPath, p.RegistryKeyPath, StringComparison.OrdinalIgnoreCase));
        if (stoppedWaiting)
        {
            Dialogs.Info("Удаление программы", stillThere
                ? $"Ожидание остановлено. «{p.DisplayName}» пока остаётся в списке — если деинсталлятор ещё работает, " +
                  "дождитесь его завершения и нажмите «Обновить»."
                : $"Программа «{p.DisplayName}» удалена.");
            return;
        }
        if (outcome is null)
            return;
        switch (outcome.Status)
        {
            case UninstallStatus.Removed:
                Dialogs.Info("Программа удалена", p.SizeBytes > 0
                    ? $"«{p.DisplayName}» удалена. Освобождено примерно {Format.Bytes(p.SizeBytes)}."
                    : $"«{p.DisplayName}» удалена.");
                break;
            case UninstallStatus.StillInstalled:
                Dialogs.Info("Удаление программы", stillThere ? outcome.Message : $"Программа «{p.DisplayName}» удалена.");
                break;
            default:
                Dialogs.Info("Удаление программы", outcome.Message);
                break;
        }
    }

    /// <summary>
    /// The uninstall confirmation. For Microsoft runtimes it opens with a strong warning: other programs and games stop
    /// starting without them, and several versions side by side are normal.
    /// </summary>
    internal static string BuildUninstallConfirmation(InstalledProgram p, bool quiet)
    {
        var launch = quiet ? p.QuietLaunch : p.Launch;
        var sb = new StringBuilder();
        if (p.IsRuntime)
        {
            sb.AppendLine($"Внимание! «{p.DisplayName}» — компонент Microsoft, на котором работают другие программы и игры.");
            sb.AppendLine();
            sb.AppendLine("После его удаления они могут перестать запускаться — с ошибками вроде «Не найден файл MSVCP140.dll» " +
                          "или «Для запуска нужна платформа .NET». Несколько версий одного компонента — это нормально: " +
                          "каждая нужна своим программам.");
            sb.AppendLine();
            sb.AppendLine("Удаляйте его, только если точно знаете, что он больше не нужен (например, так советует служба поддержки). " +
                          "Вернуть компонент можно, скачав его установщик с сайта Microsoft.");
            sb.AppendLine();
        }
        sb.AppendLine(p.IsRuntime ? $"Всё равно удалить «{p.DisplayName}»?" : $"Удалить программу «{p.DisplayName}»?");
        sb.AppendLine();
        sb.AppendLine($"Издатель: {p.PublisherText}");
        if (!string.IsNullOrWhiteSpace(p.Version))
            sb.AppendLine($"Версия: {p.Version}");
        if (p.SizeBytes > 0)
            sb.AppendLine($"Размер: {Format.Bytes(p.SizeBytes)}");
        sb.AppendLine();
        sb.AppendLine(quiet
            ? "Программа будет удалена в тихом режиме — без окон и вопросов. Будет выполнена команда её установщика:"
            : "Откроется стандартный деинсталлятор программы — дальше следуйте его шагам. Будет запущено:");
        sb.AppendLine(launch?.DisplayText ?? p.UninstallString ?? "—");
        sb.AppendLine();
        sb.AppendLine("Перед удалением будет создана точка восстановления Windows (если это включено в настройках и есть права администратора).");
        sb.Append(p.IsRuntime
            ? "Отменить удаление нельзя, но компонент можно установить заново."
            : "Отменить удаление нельзя, но программу можно установить заново.");
        return sb.ToString();
    }

    private async Task DeleteEntryAsync(InstalledProgram? p)
    {
        if (p is null || !p.IsBroken || IsBusy)
            return;
        if (p.IsMachineWide && !ElevatedRestart.RequireElevation("Удаление записи из списка программ"))
            return;
        if (!Dialogs.Confirm("Удаление записи из списка",
                $"Убрать «{p.DisplayName}» из списка установленных программ?\n\n" +
                "Сама программа, судя по всему, уже удалена: нет ни её деинсталлятора, ни папки. Будет удалена только запись в реестре:\n" +
                $"{p.RegistryKeyPath}\n\n" +
                "Перед удалением сохранится резервная копия (.reg-файл) — запись можно вернуть, открыв этот файл."))
            return;

        EntryDeletionResult? result = null;
        await RunBusyAsync("Удаляем запись из списка программ…", async ct =>
        {
            result = await _service.DeleteEntryAsync(p, ct);
            await RescanProgramsAsync();
        });
        if (result is null)
            return;
        if (result.Success)
            Dialogs.Info("Запись удалена", $"«{p.DisplayName}» больше не показывается в списке программ.\n\n" +
                                           $"Резервная копия: {result.BackupFile}");
        else
            Dialogs.Error("Запись не удалена", result.Message);
    }

    private async Task CleanBrokenAsync()
    {
        if (IsBusy)
            return;
        var broken = _programs.Where(p => p.IsBroken).ToList();
        if (broken.Count == 0)
            return;

        var machine = broken.Where(p => p.IsMachineWide).ToList();
        var targets = broken;
        var skippedNote = "";
        if (machine.Count > 0 && !Admin.IsElevated)
        {
            targets = broken.Where(p => !p.IsMachineWide).ToList();
            if (targets.Count == 0)
            {
                ElevatedRestart.RequireElevation("Очистка битых записей в списке программ");
                return;
            }
            skippedNote = $"\n\nЕщё {Format.Count(machine.Count, "запись", "записи", "записей")} для всех пользователей можно удалить " +
                          "только с правами администратора — они будут пропущены.";
        }

        var names = string.Join("\n", targets.Take(12).Select(t => "• " + t.DisplayName));
        if (targets.Count > 12)
            names += $"\n• … и ещё {targets.Count - 12}";
        if (!Dialogs.Confirm("Очистка битых записей",
                $"Убрать из списка программ {Format.Count(targets.Count, "запись", "записи", "записей")} об уже удалённых программах?\n\n" +
                $"{names}\n\nУдаляются только строки в реестре; для каждой сначала сохраняется резервная копия (.reg-файл), " +
                $"поэтому любую запись можно вернуть.{skippedNote}"))
            return;

        var deleted = 0;
        IReadOnlyList<string> failures = Array.Empty<string>();
        await RunBusyAsync("Очищаем битые записи…", async ct =>
        {
            var progress = new Progress<double>(v => ReportProgress(v));
            (deleted, failures) = await _service.DeleteEntriesAsync(targets, progress, ct);
            await RescanProgramsAsync();
        });

        if (deleted == 0 && failures.Count == 0)
            return; // cancelled before anything was done
        var text = deleted > 0
            ? $"Удалено {Format.Count(deleted, "запись", "записи", "записей")}. Резервные копии сохранены в папке:\n{AppPaths.BackupsDir}"
            : "Ни одна запись не удалена.";
        if (failures.Count > 0)
            text += "\n\nНе удалось удалить:\n" + string.Join("\n", failures.Take(8));
        if (skippedNote.Length > 0)
            text += $"\n\nПропущено {Format.Count(machine.Count, "запись", "записи", "записей")} — нужны права администратора.";
        Dialogs.Info("Очистка битых записей", text);
    }

    private void OpenFolder()
    {
        var p = SelectedProgram;
        if (p?.InstallLocation is not null && p.InstallLocationExists)
            ProcessRunner.ShowInExplorer(p.InstallLocation);
    }

    private void SearchWeb()
    {
        var p = SelectedProgram;
        if (p is not null)
            FileTools.SearchWeb($"{p.DisplayName} нужна ли программа");
    }

    private void ToggleTrust()
    {
        var p = SelectedProgram;
        if (p is null)
            return;
        var trusted = ProgramsService.SetTrusted(Settings(), p.DisplayName, !p.IsTrusted);
        // Every entry with the same name (e.g. 32- and 64-bit) follows the shared list.
        RefreshTrust();
        Dialogs.Info("Доверенные программы", trusted
            ? $"«{p.DisplayName}» отмечена как доверенная: она больше не будет помечаться как нежелательная. " +
              "Убрать её из доверенных можно здесь же или в «Настройках»."
            : $"«{p.DisplayName}» убрана из доверенных: проверки снова включены.");
    }

    private async Task CheckVirusTotalAsync()
    {
        var exe = SelectedProgram?.MainExecutable;
        if (exe is not null)
            await FileTools.OpenVirusTotalAsync(exe);
    }

    private void CopyName()
    {
        var p = SelectedProgram;
        if (p is null)
            return;
        try
        {
            Clipboard.SetText(p.DisplayName);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Log.Warn($"Не удалось скопировать в буфер обмена: {ex.Message}");
        }
    }

    // ===================================================================== Store apps: actions

    private async Task RemoveStoreAppAsync(StoreApp? app)
    {
        if (app is null || IsBusy)
            return;
        if (!app.CanRemove)
        {
            Dialogs.Info("Удаление приложения", app.ProtectReason ?? "Это приложение нужно Windows — удалять его не стоит.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Удалить приложение «{app.DisplayName}»?");
        sb.AppendLine();
        if (app.HasDescription)
            sb.AppendLine(app.Description);
        sb.AppendLine($"Издатель: {app.PublisherDisplay}");
        if (app.SizeBytes > 0)
            sb.AppendLine($"Размер: {Format.Bytes(app.SizeBytes)}");
        sb.AppendLine();
        sb.AppendLine("Приложение будет удалено только для вашей учётной записи (Remove-AppxPackage).");
        sb.Append(app.UndoText);
        if (!Dialogs.Confirm("Удаление приложения Microsoft Store", sb.ToString()))
            return;

        (bool Success, string Message)? result = null;
        await RunBusyAsync($"Удаляем «{app.DisplayName}»…", async ct =>
        {
            result = await _service.RemoveStoreAppAsync(app, ct);
            ReportProgress(-1, "Обновляем список приложений…");
            await RescanStoreAppsAsync();
        });
        if (result is null)
            return;
        if (result.Value.Success)
            Dialogs.Info("Приложение удалено", app.IsFromStore
                ? $"«{app.DisplayName}» удалено. Вернуть его можно через Microsoft Store."
                : $"«{app.DisplayName}» удалено.");
        else
            Dialogs.Error("Не удалось удалить приложение", $"«{app.DisplayName}»: {result.Value.Message}");
    }

    private async Task RemoveAllBloatAsync()
    {
        if (IsBusy)
            return;
        var targets = _storeApps.Where(a => a.IsBloatware).ToList();
        if (targets.Count == 0)
            return;

        var names = string.Join("\n", targets.Take(15).Select(t => "• " + t.DisplayName));
        if (targets.Count > 15)
            names += $"\n• … и ещё {targets.Count - 15}";
        if (!Dialogs.Confirm("Удаление предустановленных приложений",
                $"Удалить {Format.Count(targets.Count, "предустановленное приложение", "предустановленных приложения", "предустановленных приложений")}?\n\n" +
                $"{names}\n\nОни удаляются только для вашей учётной записи. " +
                (targets.All(t => t.IsFromStore)
                    ? "Любое из них можно вернуть, установив заново из Microsoft Store. "
                    : "Приложения из Microsoft Store можно вернуть, установив заново оттуда же; остальные — только переустановив программу, которая их поставила. ") +
                "Перед удалением будет создана точка восстановления (если это включено в настройках и есть права администратора).\n\n" +
                "Если какое-то из них вам нужно — нажмите «Нет» и удаляйте приложения по одному."))
            return;

        var removed = new List<string>();
        var failed = new List<string>();
        await RunBusyAsync("Удаляем предустановленные приложения…", async ct =>
        {
            ReportProgress(-1, "Создаём точку восстановления…");
            await RestorePoint.TryCreateAsync("Удаление предустановленных приложений", ct);
            for (var i = 0; i < targets.Count && !ct.IsCancellationRequested; i++)
            {
                ReportProgress(100.0 * i / targets.Count, $"Удаляем «{targets[i].DisplayName}» ({i + 1} из {targets.Count})…");
                (bool Success, string Message) r;
                try
                {
                    r = await _service.RemoveStoreAppAsync(targets[i], ct);
                }
                catch (OperationCanceledException)
                {
                    break; // "Отмена": stop here, but still refresh the list and report what was removed
                }
                (r.Success ? removed : failed).Add(r.Success ? targets[i].DisplayName : $"{targets[i].DisplayName}: {r.Message}");
            }
            ReportProgress(-1, "Обновляем список приложений…");
            await RescanStoreAppsAsync();
        });

        if (removed.Count == 0 && failed.Count == 0)
            return;
        var text = $"Удалено {Format.Count(removed.Count, "приложение", "приложения", "приложений")}.";
        if (failed.Count > 0)
            text += "\n\nНе удалось удалить:\n" + string.Join("\n", failed.Take(8));
        if (removed.Count > 0)
            text += "\n\nПриложения из Microsoft Store можно вернуть через Microsoft Store.";
        Dialogs.Info("Удаление предустановленных приложений", text);
    }

    private void OpenStorePage()
    {
        var pfn = SelectedStoreApp?.PackageFamilyName;
        if (pfn is not null && FamilyNameRegex.IsMatch(pfn))
            ProcessRunner.ShellOpen($"ms-windows-store://pdp/?PFN={pfn}");
    }

    private void StoreSearchWeb()
    {
        var a = SelectedStoreApp;
        if (a is not null)
            FileTools.SearchWeb($"{a.DisplayName} {a.Name} можно ли удалить");
    }

    private void OpenStoreFolder()
    {
        var dir = SelectedStoreApp?.InstallLocation;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            ProcessRunner.ShowInExplorer(dir);
    }
}
