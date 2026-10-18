using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using ElevatedRestart = PcCleaner.Modules.Settings.ElevatedRestart;

namespace PcCleaner.Modules.Disk;

public sealed class DiskViewModel : PageViewModel
{
    public const int TabOverview = 0;
    public const int TabAnalyzer = 1;
    public const int TabLargest = 2;
    public const int TabSuggestions = 3;

    private static readonly TimeSpan PreviewAnalysisLimit = TimeSpan.FromSeconds(40);

    private readonly DiskService _service;
    private readonly LocationOption _profileLocation;

    private bool _loadedOnce;
    private bool _overviewAttempted;
    private CancellationTokenSource? _refineCts;
    private DateTime _overviewLoadedAt = DateTime.MinValue;
    private Task? _overviewTask;
    private Task? _suggestionsTask;
    private int _selectedTab;

    // Overview
    private bool _isOverviewLoading;
    private TrimState _trim = TrimState.Unknown;
    private bool _hasSolidState;
    private ObservableCollection<DriveItem> _drives = new();
    private ObservableCollection<PhysicalDiskItem> _physicalDisks = new();

    // Analyzer
    private ObservableCollection<LocationOption> _locations = new();
    private LocationOption? _selectedLocation;
    private SpaceAnalysisResult? _result;
    private string? _analyzedPath;
    private long[] _categoryBytes = Array.Empty<long>();
    private long[] _categoryFiles = Array.Empty<long>();
    private FolderNode? _current;
    private ObservableCollection<FolderRow> _folderRows = new();
    private ObservableCollection<BreadcrumbItem> _breadcrumbs = new();
    private IReadOnlyList<CategoryRow> _categories = Array.Empty<CategoryRow>();
    private ObservableCollection<LargeFileRow> _largestFiles = new();
    private string _analysisSummary = "";
    private IReadOnlyList<BadgeItem> _analysisNotes = Array.Empty<BadgeItem>();
    private string _selectedFilesText = "";
    private bool _hasSelectedFiles;

    // Suggestions
    private bool _isSuggestionsLoading;
    private ObservableCollection<SuggestionItem> _suggestions = new();
    private string _suggestionsSummary = "";
    private string _suggestionsTeaser = "";

    public DiskViewModel(DiskService service)
    {
        _service = service;
        _profileLocation = new LocationOption("Папка пользователя", KnownFolders.UserProfile, "\uE77B", null);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeSelectedAsync, () => !IsBusy && SelectedLocation is not null);
        AnalyzeDriveCommand = new AsyncRelayCommand(p => p is DriveItem d ? AnalyzeDriveAsync(d) : Task.CompletedTask, _ => !IsBusy);
        PickFolderCommand = new AsyncRelayCommand(PickFolderAsync, () => !IsBusy);
        GoToAnalyzerCommand = new RelayCommand(() => SelectedTab = TabAnalyzer);
        GoToSuggestionsCommand = new RelayCommand(() => SelectedTab = TabSuggestions);

        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is FolderRow { CanOpen: true, Node: { } node })
                NavigateTo(node);
        });
        UpCommand = new RelayCommand(() =>
        {
            if (_current?.Parent is { } parent)
                NavigateTo(parent);
        }, () => _current?.Parent is not null);
        BreadcrumbCommand = new RelayCommand(p =>
        {
            if (p is BreadcrumbItem b)
                NavigateTo(b.Node);
        });

        ShowInExplorerCommand = new RelayCommand(p =>
        {
            var path = PathOf(p);
            if (!string.IsNullOrEmpty(path))
                SystemTools.ShowInExplorer(path);
        });
        OpenPathCommand = new RelayCommand(p =>
        {
            if (p is string path && Directory.Exists(path))
                SystemTools.Open(path);
        });
        CopyPathCommand = new RelayCommand(p => CopyPath(PathOf(p)));
        SearchWebCommand = new RelayCommand(p =>
        {
            var path = PathOf(p);
            if (string.IsNullOrEmpty(path))
                return;
            var isFolder = p is FolderRow or BreadcrumbItem;
            FileTools.SearchWeb($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))} что это за {(isFolder ? "папка" : "файл")}");
        });
        VirusTotalCommand = new AsyncRelayCommand(p => p is LargeFileRow row ? FileTools.OpenVirusTotalAsync(row.Path) : Task.CompletedTask);

        RecycleCheckedFilesCommand = new AsyncRelayCommand(RecycleCheckedFilesAsync, () => !IsBusy && HasSelectedFiles);
        RecycleFileCommand = new AsyncRelayCommand(p => p is LargeFileRow row ? RecycleRowsAsync(new[] { row }) : Task.CompletedTask,
            p => !IsBusy && p is LargeFileRow { CanSelect: true });
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (var row in LargestFiles)
                row.IsChecked = false;
        }, () => HasSelectedFiles);

        ToggleExpandCommand = new RelayCommand(p =>
        {
            if (p is SuggestionItem s)
                s.IsExpanded = !s.IsExpanded;
        });
        CheckAllCommand = new RelayCommand(p =>
        {
            if (p is SuggestionItem s)
                s.SetAllChecked(!s.Files.All(f => f.IsChecked));
        });
        RecycleSuggestionCommand = new AsyncRelayCommand(
            p => p is SuggestionItem s ? RecycleSuggestionAsync(s) : Task.CompletedTask, _ => !IsBusy);
        DisableHibernationCommand = new AsyncRelayCommand(DisableHibernationAsync, () => !IsBusy);
        DiskCleanupCommand = new AsyncRelayCommand(() => OpenToolAsync(_service.OpenDiskCleanup, "«Очистка диска»"));
        SystemProtectionCommand = new AsyncRelayCommand(() => OpenToolAsync(
            () => _service.OpenTool(SystemTools.Exe("SystemPropertiesProtection.exe"), "", "SystemPropertiesProtection"),
            "«Защита системы»"));
        OpenRecycleBinCommand = new RelayCommand(() => SystemTools.Open("shell:RecycleBinFolder"));

        OptimizeDriveCommand = new AsyncRelayCommand(p => p is DriveItem d ? OptimizeDriveAsync(d) : Task.CompletedTask, _ => !IsBusy);
        EnableTrimCommand = new AsyncRelayCommand(EnableTrimAsync, () => !IsBusy);
        OpenDriveCommand = new RelayCommand(p =>
        {
            if (p is DriveItem d)
                SystemTools.Open(d.Model.RootPath);
        });
        RestartAsAdminCommand = new RelayCommand(() => ElevatedRestart.Request(), () => !Admin.IsElevated);
    }

    public override string Title => T._("Диски");

    public override string Subtitle => T._("Свободное место, что его занимает, советы по очистке и здоровье накопителей");

    public override string IconGlyph => "\uEDA2";

    // ------------------------------------------------------------------ commands

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand AnalyzeDriveCommand { get; }
    public AsyncRelayCommand PickFolderCommand { get; }
    public RelayCommand GoToAnalyzerCommand { get; }
    public RelayCommand GoToSuggestionsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand BreadcrumbCommand { get; }
    public RelayCommand ShowInExplorerCommand { get; }
    public RelayCommand OpenPathCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand SearchWebCommand { get; }
    public AsyncRelayCommand VirusTotalCommand { get; }
    public AsyncRelayCommand RecycleCheckedFilesCommand { get; }
    public AsyncRelayCommand RecycleFileCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ToggleExpandCommand { get; }
    public RelayCommand CheckAllCommand { get; }
    public AsyncRelayCommand RecycleSuggestionCommand { get; }
    public AsyncRelayCommand DisableHibernationCommand { get; }
    public AsyncRelayCommand DiskCleanupCommand { get; }
    public AsyncRelayCommand SystemProtectionCommand { get; }

    /// <summary>
    /// How long a tool button stays disabled after it opened its window (the window takes a moment to appear,
    /// so a double click must not open a second one).
    /// </summary>
    internal TimeSpan ToolLaunchCooldown { get; set; } = TimeSpan.FromSeconds(3);
    public RelayCommand OpenRecycleBinCommand { get; }
    public AsyncRelayCommand OptimizeDriveCommand { get; }
    public AsyncRelayCommand EnableTrimCommand { get; }
    public RelayCommand OpenDriveCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }

    // ------------------------------------------------------------------ common state

    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    public bool IsAdmin => Admin.IsElevated;

    // ------------------------------------------------------------------ overview

    public bool IsOverviewLoading
    {
        get => _isOverviewLoading;
        private set
        {
            if (Set(ref _isOverviewLoading, value))
                OnDrivesStateChanged();
        }
    }

    public ObservableCollection<DriveItem> Drives
    {
        get => _drives;
        private set
        {
            if (Set(ref _drives, value))
            {
                OnPropertyChanged(nameof(DriveColumns));
                OnDrivesStateChanged();
            }
        }
    }

    public int DriveColumns => Drives.Count <= 1 ? 1 : 2;

    public bool HasDrives => Drives.Count > 0;

    /// <summary>"Loading…" placeholder: no drive cards yet and the first load has not finished.</summary>
    public bool ShowDrivesLoading => !HasDrives && (IsOverviewLoading || !_overviewAttempted);

    /// <summary>Empty state: the load finished but found no ready local drive (or failed).</summary>
    public bool ShowNoDrives => !HasDrives && _overviewAttempted && !IsOverviewLoading;

    private void OnDrivesStateChanged()
    {
        OnPropertyChanged(nameof(HasDrives));
        OnPropertyChanged(nameof(ShowDrivesLoading));
        OnPropertyChanged(nameof(ShowNoDrives));
    }

    public ObservableCollection<PhysicalDiskItem> PhysicalDisks
    {
        get => _physicalDisks;
        private set
        {
            if (Set(ref _physicalDisks, value))
                OnPropertyChanged(nameof(HasPhysicalDisks));
        }
    }

    public bool HasPhysicalDisks => PhysicalDisks.Count > 0;

    public bool ShowTrimWarning => _hasSolidState && _trim == TrimState.Disabled;

    public bool ShowReliabilityAdminHint => !Admin.IsElevated;

    // ------------------------------------------------------------------ analyzer

    public ObservableCollection<LocationOption> Locations
    {
        get => _locations;
        private set => Set(ref _locations, value);
    }

    public LocationOption? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (Set(ref _selectedLocation, value))
                RelayCommand.Refresh();
        }
    }

    public bool HasAnalysis => _result is not null;

    public string AnalyzedPath => _analyzedPath ?? "";

    public string AnalysisSummary
    {
        get => _analysisSummary;
        private set => Set(ref _analysisSummary, value);
    }

    public IReadOnlyList<BadgeItem> AnalysisNotes
    {
        get => _analysisNotes;
        private set => Set(ref _analysisNotes, value);
    }

    public bool IsPartial => _result?.IsPartial == true;

    public ObservableCollection<FolderRow> FolderRows
    {
        get => _folderRows;
        private set => Set(ref _folderRows, value);
    }

    public ObservableCollection<BreadcrumbItem> Breadcrumbs
    {
        get => _breadcrumbs;
        private set => Set(ref _breadcrumbs, value);
    }

    public string CurrentFolderSize => _current is null
        ? ""
        : $"{Format.Bytes(_current.TotalBytes)} · {Format.Count(_current.TotalFiles, "файл", "файла", "файлов")}";

    public bool CanGoUp => _current?.Parent is not null;

    public IReadOnlyList<CategoryRow> Categories
    {
        get => _categories;
        private set => Set(ref _categories, value);
    }

    public ObservableCollection<LargeFileRow> LargestFiles
    {
        get => _largestFiles;
        private set
        {
            if (Set(ref _largestFiles, value))
                OnPropertyChanged(nameof(HasLargestFiles));
        }
    }

    public bool HasLargestFiles => LargestFiles.Count > 0;

    public string LargestFilesTitle => _result is null
        ? "Самые большие файлы"
        : $"{Format.Count(LargestFiles.Count, "самый большой файл", "самых больших файла", "самых больших файлов")} в {AnalyzedPath}";

    public string SelectedFilesText
    {
        get => _selectedFilesText;
        private set => Set(ref _selectedFilesText, value);
    }

    public bool HasSelectedFiles
    {
        get => _hasSelectedFiles;
        private set
        {
            if (Set(ref _hasSelectedFiles, value))
                RelayCommand.Refresh();
        }
    }

    // ------------------------------------------------------------------ suggestions

    public bool IsSuggestionsLoading
    {
        get => _isSuggestionsLoading;
        private set => Set(ref _isSuggestionsLoading, value);
    }

    public ObservableCollection<SuggestionItem> Suggestions
    {
        get => _suggestions;
        private set
        {
            if (Set(ref _suggestions, value))
                OnPropertyChanged(nameof(HasSuggestions));
        }
    }

    public bool HasSuggestions => Suggestions.Count > 0;

    public string SuggestionsSummary
    {
        get => _suggestionsSummary;
        private set => Set(ref _suggestionsSummary, value);
    }

    /// <summary>One line for the overview tab: what the biggest suggestions are about.</summary>
    public string SuggestionsTeaser
    {
        get => _suggestionsTeaser;
        private set
        {
            if (Set(ref _suggestionsTeaser, value))
                OnPropertyChanged(nameof(HasSuggestionsTeaser));
        }
    }

    public bool HasSuggestionsTeaser => !string.IsNullOrEmpty(SuggestionsTeaser);

    // ------------------------------------------------------------------ navigation / loading

    public override Task OnNavigatedToAsync()
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            RebuildLocations(Array.Empty<DriveSummary>());
            _ = LoadOverviewAsync();
            _ = LoadSuggestionsAsync();
        }
        else if (DateTime.Now - _overviewLoadedAt > TimeSpan.FromSeconds(30))
        {
            _ = LoadOverviewAsync();
        }
        return Task.CompletedTask;
    }

    public override async Task PreviewScanAsync(CancellationToken ct)
    {
        if (!_loadedOnce)
            await OnNavigatedToAsync();
        await LoadOverviewAsync().WaitAsync(ct);
        var suggestions = LoadSuggestionsAsync();

        var location = Locations.FirstOrDefault(l => string.Equals(l.Path, _profileLocation.Path, StringComparison.OrdinalIgnoreCase))
                       ?? _profileLocation;
        if (Directory.Exists(location.Path))
        {
            SelectedLocation = location;
            await RunBusyAsync($"Анализ папки {location.Path}", async busyCt =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, busyCt);
                linked.CancelAfter(PreviewAnalysisLimit);
                await RunAnalysisAsync(location, linked.Token);
            });
        }
        await suggestions.WaitAsync(ct);
    }

    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadOverviewAsync(), LoadSuggestionsAsync());
    }

    private Task LoadOverviewAsync()
    {
        if (_overviewTask is { IsCompleted: false })
            return _overviewTask;
        return _overviewTask = LoadOverviewCoreAsync();
    }

    private async Task LoadOverviewCoreAsync()
    {
        IsOverviewLoading = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (Drives.Count == 0)
            {
                var quick = await _service.GetDrivesAsync(CancellationToken.None);
                Drives = new ObservableCollection<DriveItem>(quick.Select(d => new DriveItem(d, TrimState.Unknown)));
                RebuildLocations(quick);
            }

            var overview = await _service.GetOverviewAsync(includeReliability: true, CancellationToken.None);
            _trim = overview.Trim;
            _hasSolidState = overview.Disks.Any(d => StorageLabels.IsSolidState(d.Kind));
            Drives = new ObservableCollection<DriveItem>(overview.Drives.Select(d => new DriveItem(d, overview.Trim)));
            PhysicalDisks = new ObservableCollection<PhysicalDiskItem>(
                overview.Disks.Select(d => new PhysicalDiskItem(d, overview.Trim, overview.ReliabilityRequested)));
            OnPropertyChanged(nameof(ShowTrimWarning));
            RebuildLocations(overview.Drives);
            _overviewLoadedAt = DateTime.Now;
            Log.Info($"Диски: сведения о {Format.Count(overview.Drives.Count, "диске", "дисках", "дисках")} получены за {sw.Elapsed.TotalSeconds:0.0} с");
        }
        catch (Exception ex)
        {
            Log.Error("Диски: не удалось загрузить сведения о дисках", ex);
        }
        finally
        {
            _overviewAttempted = true;
            IsOverviewLoading = false;
            OnDrivesStateChanged();
        }
    }

    private Task LoadSuggestionsAsync()
    {
        if (_suggestionsTask is { IsCompleted: false })
            return _suggestionsTask;
        return _suggestionsTask = LoadSuggestionsCoreAsync();
    }

    private async Task LoadSuggestionsCoreAsync()
    {
        IsSuggestionsLoading = true;
        if (Suggestions.Count == 0)
            SuggestionsSummary = "Ищем, что можно освободить…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await _service.GetSuggestionsAsync(CancellationToken.None);
            Suggestions = new ObservableCollection<SuggestionItem>(BuildSuggestions(data));
            UpdateSuggestionsSummary();
            Log.Info($"Диски: советы по месту подготовлены за {sw.Elapsed.TotalSeconds:0.0} с");
        }
        catch (Exception ex)
        {
            Log.Error("Диски: не удалось подготовить советы по месту", ex);
            if (Suggestions.Count == 0)
                SuggestionsSummary = "Не удалось подготовить советы — нажмите «Обновить»";
        }
        finally
        {
            IsSuggestionsLoading = false;
        }
    }

    private void RebuildLocations(IReadOnlyList<DriveSummary> drives)
    {
        var selectedPath = SelectedLocation?.Path;
        var list = new List<LocationOption>();
        foreach (var d in drives)
        {
            var name = string.IsNullOrWhiteSpace(d.Label) ? "Локальный диск" : d.Label;
            list.Add(new LocationOption($"Диск {d.Letter}: — {name}", d.RootPath, "\uEDA2", d.UsedBytes));
        }
        list.Add(_profileLocation);
        AddFolder(list, "Загрузки", KnownFolders.Downloads, "\uE896");
        AddFolder(list, "Документы", KnownFolders.Documents, "\uE8A5");
        AddFolder(list, "Рабочий стол", KnownFolders.Desktop, "\uE7F4");
        AddFolder(list, "Видео", KnownFolders.Videos, "\uE714");
        foreach (var custom in Locations.Where(l => l.Icon == "\uE838"))
        {
            if (list.All(l => !string.Equals(l.Path, custom.Path, StringComparison.OrdinalIgnoreCase)))
                list.Add(custom);
        }

        Locations = new ObservableCollection<LocationOption>(list);
        SelectedLocation = list.FirstOrDefault(l => string.Equals(l.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                           ?? list.FirstOrDefault();
    }

    private static void AddFolder(List<LocationOption> list, string title, string? path, string icon)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
            list.All(l => !string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase)))
            list.Add(new LocationOption(title, path, icon, null));
    }

    // ------------------------------------------------------------------ analyzer

    private async Task AnalyzeSelectedAsync()
    {
        var location = SelectedLocation;
        if (location is null)
            return;
        if (!Directory.Exists(location.Path))
        {
            Dialogs.Info("Папка не найдена", $"Папка больше не существует или недоступна:\n{location.Path}");
            return;
        }
        SelectedTab = TabAnalyzer;
        await RunBusyAsync($"Анализ {location.Path}", ct => RunAnalysisAsync(location, ct));
    }

    private async Task AnalyzeDriveAsync(DriveItem drive)
    {
        var location = Locations.FirstOrDefault(l => string.Equals(l.Path, drive.Model.RootPath, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return;
        SelectedLocation = location;
        await AnalyzeSelectedAsync();
    }

    private async Task PickFolderAsync()
    {
        var owner = IntPtr.Zero;
        try
        {
            if (Application.Current?.MainWindow is { } window)
                owner = new WindowInteropHelper(window).Handle;
        }
        catch (InvalidOperationException)
        {
            // no window (tests)
        }

        var path = FolderPicker.Pick(owner, "Выберите папку для анализа", SelectedLocation?.Path);
        if (string.IsNullOrWhiteSpace(path))
            return;

        var existing = Locations.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new LocationOption($"Папка: {ShortName(path)}", path, "\uE838", null);
            Locations.Add(existing);
        }
        SelectedLocation = existing;
        await AnalyzeSelectedAsync();
    }

    private async Task RunAnalysisAsync(LocationOption location, CancellationToken ct)
    {
        var analyzer = new SpaceAnalyzer();
        var task = analyzer.RunAsync(location.Path, ct);
        var cancelReported = false;
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(300));
            if (task.IsCompleted)
                break;
            if (ct.IsCancellationRequested)
            {
                if (!cancelReported)
                    ReportProgress(-1, "Останавливаем анализ…");
                cancelReported = true;
                continue;
            }
            var bytes = analyzer.BytesScanned;
            double percent = location.ExpectedBytes is > 0 ? Math.Min(99, 100.0 * bytes / location.ExpectedBytes.Value) : -1;
            var current = analyzer.CurrentPath;
            ReportProgress(percent,
                $"Просмотрено {Format.Count(analyzer.FilesScanned, "файл", "файла", "файлов")} ({Format.Bytes(bytes)})" +
                (current is null ? "" : $" — {current}"));
        }
        var result = await task;
        // The protection check resolves links (file system calls), so it runs off the UI thread.
        var protectedFlags = await Task.Run(() => result.LargestFiles.Select(h => _service.IsProtectedPath(h.Path)).ToArray());
        ApplyAnalysis(result, protectedFlags);
    }

    private void ApplyAnalysis(SpaceAnalysisResult result, bool[] protectedFlags)
    {
        _result = result;
        _analyzedPath = result.Root.FullPath;
        _categoryBytes = new long[FileClassifier.CategoryCount];
        _categoryFiles = new long[FileClassifier.CategoryCount];
        foreach (var c in result.Categories)
        {
            _categoryBytes[(int)c.Category] = c.Bytes;
            _categoryFiles[(int)c.Category] = c.Files;
        }

        var rows = result.LargestFiles
            .Select((h, i) => new LargeFileRow(h, i >= protectedFlags.Length || protectedFlags[i], RiskNoteFor(h)))
            .ToList();
        foreach (var row in rows)
            row.CheckedChanged += (_, _) => UpdateSelectedFiles();
        LargestFiles = new ObservableCollection<LargeFileRow>(rows);
        UpdateSelectedFiles();

        var notes = new List<BadgeItem>();
        if (result.IsPartial)
            notes.Add(new BadgeItem("Анализ остановлен — данные неполные", Tone.Warning,
                "Анализ был остановлен до завершения, поэтому размеры папок показаны не полностью. Запустите его снова, чтобы увидеть всё."));
        if (result.InaccessibleFolders > 0)
            notes.Add(new BadgeItem($"Нет доступа: {Format.Count(result.InaccessibleFolders, "папка", "папки", "папок")}", Tone.Warning,
                "Эти папки не удалось прочитать, и их размер не учтён." +
                (Admin.IsElevated ? "" : " С правами администратора программа увидит больше.")));
        if (result.SkippedLinks > 0)
            notes.Add(new BadgeItem($"Ссылок пропущено: {Format.Number(result.SkippedLinks)}", Tone.Neutral,
                "Ссылки (junction/symlink) ведут в другие папки или на другие диски. Их содержимое здесь не учитывается, " +
                "чтобы не посчитать одни и те же файлы дважды."));
        if (result.CloudOnlyFiles > 0)
            notes.Add(new BadgeItem($"Только в облаке: {Format.Count(result.CloudOnlyFiles, "файл", "файла", "файлов")}", Tone.Info,
                "Файлы OneDrive, которые хранятся только в облаке, не занимают место на этом диске и не учитываются."));
        AnalysisNotes = notes;

        UpdateAnalysisSummary();
        RebuildCategories();
        NavigateTo(result.Root);
        Log.Info($"Диски: анализ {result.Root.FullPath} — {Format.Bytes(result.TotalBytes)}, " +
                 $"{Format.Count(result.FileCount, "файл", "файла", "файлов")} за {Format.Duration(result.Elapsed)}" +
                 (result.IsPartial ? " (остановлен, данные неполные)" : ""));

        OnPropertyChanged(nameof(HasAnalysis));
        OnPropertyChanged(nameof(AnalyzedPath));
        OnPropertyChanged(nameof(IsPartial));
        OnPropertyChanged(nameof(LargestFilesTitle));
    }

    private string? RiskNoteFor(FileHit hit)
        => _service.RiskNote(hit.Path) ?? (hit.Category == FileCategory.Programs
            ? "Файл установленной игры или программы — после удаления она может перестать работать. Игры и программы лучше удалять в разделе «Программы» или в их лаунчере."
            : null);

    private void UpdateAnalysisSummary()
    {
        if (_result is null)
            return;
        var root = _result.Root;
        AnalysisSummary =
            $"{Format.Bytes(root.TotalBytes)} · {Format.Count(root.TotalFiles, "файл", "файла", "файлов")} · " +
            $"{Format.Count(root.TotalFolders, "папка", "папки", "папок")} · проверено за {Format.Duration(_result.Elapsed)}";
    }

    private void RebuildCategories()
    {
        var total = _categoryBytes.Sum();
        Categories = Enumerable.Range(0, _categoryBytes.Length)
            .Where(i => _categoryBytes[i] > 0)
            .OrderByDescending(i => _categoryBytes[i])
            .Select(i => new CategoryRow(FileClassifier.Info((FileCategory)i), _categoryBytes[i], _categoryFiles[i],
                total > 0 ? 100.0 * _categoryBytes[i] / total : 0))
            .ToList();
    }

    private void NavigateTo(FolderNode node)
    {
        _current = node;
        var total = Math.Max(1, node.TotalBytes);
        var rows = node.Children
            .Select(c => new FolderRow(c, c.Name, c.TotalBytes, c.TotalFiles, 100.0 * c.TotalBytes / total, false))
            .ToList();
        if (node.OwnFiles > 0)
            rows.Add(new FolderRow(node, "Файлы в этой папке", node.OwnBytes, node.OwnFiles, 100.0 * node.OwnBytes / total, true));
        rows = rows.OrderBy(r => r.IsLink).ThenByDescending(r => r.Bytes).ToList();
        FolderRows = new ObservableCollection<FolderRow>(rows);

        var chain = new List<FolderNode>();
        for (var n = node; n is not null; n = n.Parent)
            chain.Insert(0, n);
        Breadcrumbs = new ObservableCollection<BreadcrumbItem>(
            chain.Select((n, i) => new BreadcrumbItem(i == 0 ? n.FullPath : n.Name, n, i == chain.Count - 1)));

        OnPropertyChanged(nameof(CurrentFolderSize));
        OnPropertyChanged(nameof(CanGoUp));
        RelayCommand.Refresh();
    }

    private void UpdateSelectedFiles()
    {
        var selected = LargestFiles.Where(r => r.IsChecked).ToList();
        HasSelectedFiles = selected.Count > 0;
        SelectedFilesText = selected.Count == 0
            ? "Отметьте файлы, которые больше не нужны"
            : $"Отмечено {Format.Count(selected.Count, "файл", "файла", "файлов")} — {Format.Bytes(selected.Sum(r => r.Size))}";
    }

    // ------------------------------------------------------------------ recycle

    private Task RecycleCheckedFilesAsync() => RecycleRowsAsync(LargestFiles.Where(r => r.IsChecked).ToList());

    private async Task RecycleRowsAsync(IReadOnlyList<LargeFileRow> rows)
    {
        var files = rows.Where(r => r.CanSelect).Select(r => new RecycleCandidate(r.Path, r.Size, r.RiskNote)).ToList();
        await RecycleWithConfirmAsync(files);
    }

    private async Task RecycleSuggestionAsync(SuggestionItem suggestion)
    {
        var files = suggestion.Files.Where(f => f.IsChecked)
            .Select(f => new RecycleCandidate(f.Path, f.Size, _service.RiskNote(f.Path)))
            .ToList();
        if (files.Count == 0)
        {
            suggestion.IsExpanded = true;
            Dialogs.Info("Ничего не отмечено",
                "Отметьте галочками файлы, которые больше не нужны (или нажмите «Отметить все»), а затем нажмите «Удалить в корзину».");
            return;
        }
        await RecycleWithConfirmAsync(files);
    }

    private sealed record RecycleCandidate(string Path, long Size, string? RiskNote);

    private async Task RecycleWithConfirmAsync(IReadOnlyList<RecycleCandidate> files)
    {
        if (IsBusy)
            return; // another operation is running; the buttons are disabled anyway
        if (files.Count == 0)
        {
            Dialogs.Info("Ничего не отмечено", "Отметьте галочками файлы, которые больше не нужны.");
            return;
        }
        // Synchronous on purpose (at most a few hundred metadata look-ups): no await before the confirmation, so no
        // other action can start in between.
        var blocked = files.Where(f => _service.IsProtectedPath(f.Path)).ToList();
        var allowed = files.Except(blocked).ToList();
        if (allowed.Count == 0)
        {
            Dialogs.Info("Удаление недоступно",
                "Эти файлы относятся к Windows или установленным программам — удалять их отсюда нельзя.");
            return;
        }

        var total = allowed.Sum(f => f.Size);
        var message = new StringBuilder();
        message.AppendLine($"Переместить в корзину {Format.Count(allowed.Count, "файл", "файла", "файлов")} общим размером {Format.Bytes(total)}?");
        message.AppendLine();
        foreach (var f in allowed.Take(12))
            message.AppendLine($"• {Path.GetFileName(f.Path)} — {Format.Bytes(f.Size)}");
        if (allowed.Count > 12)
            message.AppendLine($"… и ещё {Format.Count(allowed.Count - 12, "файл", "файла", "файлов")}");
        var risky = allowed.Count(f => f.RiskNote is not null);
        if (risky > 0)
        {
            message.AppendLine();
            message.AppendLine($"Внимание: {Format.Count(risky, "файл относится", "файла относятся", "файлов относятся")} к установленным программам, " +
                               "играм или их данным (AppData) — после удаления они могут работать неправильно.");
        }
        if (blocked.Count > 0)
        {
            message.AppendLine();
            message.AppendLine($"{Format.Count(blocked.Count, "системный файл", "системных файла", "системных файлов")} " +
                               $"{Format.Plural(blocked.Count, "будет пропущен", "будут пропущены", "будет пропущено")}.");
        }
        message.AppendLine();
        message.AppendLine("Файлы можно будет вернуть из корзины, пока она не очищена. " +
                           "Место на диске освободится после очистки корзины.");
        if (!Dialogs.Confirm("Удалить в корзину", message.ToString()))
            return;

        RecycleResult? result = null;
        await RunBusyAsync("Перемещаем файлы в корзину…", async _ =>
        {
            var batch = allowed.Select(f => (f.Path, f.Size)).ToList();
            result = await SystemTools.RunSta(() => _service.RecycleFiles(batch));
        });
        if (result is null)
            return;

        if (result.Recycled.Count > 0)
            Log.Action($"Диски: в корзину перемещено {Format.Count(result.Recycled.Count, "файл", "файла", "файлов")} " +
                       $"({Format.Bytes(result.RecycledBytes)})");
        OnFilesRecycled(result.Recycled);

        var summary = new StringBuilder();
        summary.AppendLine(result.Recycled.Count > 0
            ? $"Перемещено в корзину: {Format.Count(result.Recycled.Count, "файл", "файла", "файлов")} ({Format.Bytes(result.RecycledBytes)})."
            : "Ни один файл не был перемещён в корзину.");
        if (result.Skipped.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine($"Не перемещено: {Format.Count(result.Skipped.Count, "файл", "файла", "файлов")}");
            foreach (var (path, reason) in result.Skipped.Take(6))
                summary.AppendLine($"• {Path.GetFileName(path)} — {reason}");
            if (result.Skipped.Count > 6)
                summary.AppendLine($"… и ещё {Format.Count(result.Skipped.Count - 6, "файл", "файла", "файлов")}");
        }
        if (result.Recycled.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("Чтобы место на диске действительно освободилось, очистите корзину. " +
                               "Передумали — восстановите файлы из корзины.");
        }
        Dialogs.Info("Готово", summary.ToString().TrimEnd());
    }

    /// <summary>Removes recycled files from all lists and subtracts them from the analysis.</summary>
    private void OnFilesRecycled(IReadOnlyList<(string Path, long Size)> recycled)
    {
        if (recycled.Count == 0)
            return;
        var paths = new HashSet<string>(recycled.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

        var removedRows = LargestFiles.Where(r => paths.Contains(r.Path)).ToList();
        foreach (var row in removedRows)
            LargestFiles.Remove(row);
        OnPropertyChanged(nameof(HasLargestFiles));
        OnPropertyChanged(nameof(LargestFilesTitle));
        UpdateSelectedFiles();

        foreach (var s in Suggestions.Where(s => s.HasFiles).ToList())
        {
            s.RemoveFiles(paths);
            if (!s.HasFiles)
                Suggestions.Remove(s);
        }
        OnPropertyChanged(nameof(HasSuggestions));
        UpdateSuggestionsSummary();

        if (_result is null || _current is null)
            return;
        var rootPath = _result.Root.FullPath;
        var context = PathRules.Default;
        foreach (var (path, size) in recycled)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null || !DiskService.IsUnder(dir, rootPath))
                continue;
            var node = FindNode(_result.Root, dir);
            if (node is null)
                continue;
            node.OwnBytes -= size;
            node.OwnFiles--;
            for (var n = node; n is not null; n = n.Parent)
            {
                n.TotalBytes -= size;
                n.TotalFiles--;
                SpaceAnalyzer.Resort(n);
            }
            var hit = removedRows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))?.Hit;
            var category = hit?.Category ?? FileClassifier.Classify(Path.GetFileName(path), context.ForRoot(dir));
            _categoryBytes[(int)category] = Math.Max(0, _categoryBytes[(int)category] - size);
            _categoryFiles[(int)category] = Math.Max(0, _categoryFiles[(int)category] - 1);
        }
        UpdateAnalysisSummary();
        RebuildCategories();
        NavigateTo(_current);
    }

    private static FolderNode? FindNode(FolderNode root, string dir)
    {
        var rootPath = Path.TrimEndingDirectorySeparator(root.FullPath);
        var relative = dir.Length > rootPath.Length ? dir[rootPath.Length..] : "";
        var node = root;
        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(c => !c.IsLink && string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                return null;
        }
        return node;
    }

    // ------------------------------------------------------------------ suggestions

    private List<SuggestionItem> BuildSuggestions(SpaceSuggestions data)
    {
        var list = new List<SuggestionItem>();
        var now = DateTime.Now;

        foreach (var group in data.OldDownloads)
        {
            var item = new SuggestionItem(SuggestionKind.OldDownloads, DownloadsAdvisor.GroupIcon(group.Kind), Tone.Warning,
                DownloadsAdvisor.GroupTitle(group.Kind) + " в «Загрузках»",
                DownloadsAdvisor.GroupDescription(group.Kind, DownloadsAdvisor.DefaultAgeDays), group.TotalBytes)
            {
                CountsAsReclaimable = true,
                ShortName = group.Kind switch
                {
                    DownloadKind.Installers => "старые установщики",
                    DownloadKind.Archives => "старые архивы",
                    _ => "старые образы дисков",
                },
            };
            item.AddFiles(group.Files.Select(f => new SuggestionFile(f, AgeText(f, now))));
            item.Actions.Add(new SuggestionAction("Удалить отмеченные в корзину", RecycleSuggestionCommand, item, IsDanger: true,
                ToolTip: "Отмеченные файлы будут перемещены в корзину — их можно восстановить."));
            if (!string.IsNullOrEmpty(data.DownloadsPath))
                item.Actions.Add(new SuggestionAction("Открыть «Загрузки»", OpenPathCommand, data.DownloadsPath));
            list.Add(item);
        }

        if (data.BigVideos.Count > 0)
        {
            var item = new SuggestionItem(SuggestionKind.BigVideos, "\uE714", Tone.Info, "Большие видеофайлы",
                "Фильмы и записи больше 1 ГБ в папках «Видео», «Загрузки», «Документы» и на рабочем столе. " +
                "Если вы их уже посмотрели, удалите их или перенесите на другой диск.",
                data.BigVideos.Sum(v => v.Size))
            {
                CountsAsReclaimable = true,
                ShortName = "большие видео",
            };
            item.AddFiles(data.BigVideos.Select(f => new SuggestionFile(f, $"изменён {Format.Date(f.ModifiedLocal)}")));
            item.Actions.Add(new SuggestionAction("Удалить отмеченные в корзину", RecycleSuggestionCommand, item, IsDanger: true,
                ToolTip: "Отмеченные файлы будут перемещены в корзину — их можно восстановить."));
            list.Add(item);
        }

        var systemRoot = _service.SystemDriveRoot;
        var hiber = data.SystemFiles.FirstOrDefault(f => f.Name == "hiberfil.sys" &&
                                                          f.Path.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase));
        if (hiber is not null)
        {
            var item = new SuggestionItem(SuggestionKind.Hibernation, "\uE708", Tone.Info, "Файл гибернации hiberfil.sys",
                $"В нём Windows сохраняет содержимое оперативной памяти для гибернации и «Быстрого запуска». Если вы не пользуетесь " +
                $"гибернацией, её можно отключить — освободится {Format.Bytes(hiber.Size)}. Обычный спящий режим продолжит работать.",
                hiber.Size)
            {
                CountsAsReclaimable = true,
                ShortName = "файл гибернации",
                Note = "Вернуть гибернацию можно командой «powercfg /h on» от имени администратора.",
            };
            item.Actions.Add(new SuggestionAction("Отключить гибернацию", DisableHibernationCommand, null, IsAccent: true,
                ToolTip: "Выполнит «powercfg /h off». Нужны права администратора."));
            list.Add(item);
        }

        if (data.WindowsOldPath is not null)
        {
            var item = new SuggestionItem(SuggestionKind.WindowsOld, "\uE777", Tone.Warning, "Папка Windows.old — прежняя версия Windows",
                "Осталась после обновления Windows. Если всё работает нормально, удалите её через «Очистку диска»: нажмите " +
                "«Очистить системные файлы» и отметьте «Предыдущие установки Windows». После этого вернуться к прежней версии будет нельзя.",
                data.WindowsOldBytes)
            {
                CountsAsReclaimable = true,
                ShortName = "папка Windows.old",
                Note = "Сама программа эту папку не трогает — удаляет её только «Очистка диска» Windows.",
            };
            item.SetSize(data.WindowsOldBytes, data.WindowsOldPartial ? "не меньше " : "");
            item.Actions.Add(new SuggestionAction("Открыть «Очистку диска»", DiskCleanupCommand, null, IsAccent: true));
            item.Actions.Add(new SuggestionAction("Показать в проводнике", ShowInExplorerCommand, data.WindowsOldPath));
            list.Add(item);
            if (data.WindowsOldPartial)
                StartRefineFolderSize(item, data.WindowsOldPath);
        }

        if (data.RecycleBinBytes > 0)
        {
            var item = new SuggestionItem(SuggestionKind.RecycleBin, "\uE74D", Tone.Neutral, "Корзина",
                $"В корзине {Format.Count(data.RecycleBinItems, "объект", "объекта", "объектов")}. Удалённые файлы продолжают занимать " +
                "место, пока корзина не очищена. Посмотрите, нет ли там нужного, и очистите её (это можно сделать в разделе «Очистка»).",
                data.RecycleBinBytes)
            {
                CountsAsReclaimable = true,
                ShortName = "корзина",
            };
            item.Actions.Add(new SuggestionAction("Открыть корзину", OpenRecycleBinCommand, null));
            list.Add(item);
        }

        var pageFiles = data.SystemFiles.Where(f => f.Name is "pagefile.sys" or "swapfile.sys").ToList();
        if (pageFiles.Count > 0)
        {
            var details = string.Join(", ", pageFiles.Select(f => $"{f.Path} — {Format.Bytes(f.Size)}"));
            var item = new SuggestionItem(SuggestionKind.PageFile, "\uE964", Tone.Neutral, "Файл подкачки",
                "Windows использует pagefile.sys как продолжение оперативной памяти, а swapfile.sys — для приложений из Microsoft Store. " +
                "Удалять или отключать их не нужно: без файла подкачки программы могут вылетать при нехватке памяти. " +
                "Размером Windows управляет сама.",
                pageFiles.Sum(f => f.Size))
            {
                Note = details,
            };
            list.Add(item);
        }

        if (data.ShadowStorage.Count > 0 || data.ShadowStorageNeedsAdmin)
        {
            var used = data.ShadowStorage.Sum(s => s.UsedBytes ?? 0);
            var parts = data.ShadowStorage
                .Select(s => $"{s.Volume} — занято {(s.UsedBytes is { } u ? Format.Bytes(u) : "?")}" +
                             (s.MaxBytes is { } m ? $" из {Format.Bytes(m)} разрешённых" : ", без ограничения"))
                .ToList();
            var description = "Точки восстановления позволяют откатить Windows, если после установки программы или драйвера что-то " +
                              "сломалось. Уменьшить лимит места или удалить старые точки можно в окне «Защита системы» → «Настроить».";
            var item = new SuggestionItem(SuggestionKind.ShadowStorage, "\uE81C", Tone.Neutral, "Точки восстановления системы",
                description, used)
            {
                Note = data.ShadowStorageNeedsAdmin
                    ? "Сколько места они занимают, видно только при запуске программы от имени администратора."
                    : parts.Count > 0 ? string.Join("; ", parts) : "Точек восстановления пока нет.",
            };
            item.Actions.Add(new SuggestionAction("Открыть «Защиту системы»", SystemProtectionCommand, null));
            list.Add(item);
        }

        return list
            .OrderByDescending(s => s.CountsAsReclaimable)
            .ThenByDescending(s => s.Bytes)
            .ToList();
    }

    /// <summary>
    /// Measures a big folder (Windows.old) completely in the background and updates its card. A newer refresh cancels
    /// the previous measurement, so repeated "Обновить" clicks never stack several full scans.
    /// </summary>
    private void StartRefineFolderSize(SuggestionItem item, string path)
    {
        _refineCts?.Cancel();
        _refineCts?.Dispose();
        _refineCts = new CancellationTokenSource();
        _ = RefineFolderSizeAsync(item, path, _refineCts.Token);
    }

    private async Task RefineFolderSizeAsync(SuggestionItem item, string path, CancellationToken ct)
    {
        try
        {
            var (bytes, complete) = await DiskService.MeasureFolderAsync(path, TimeSpan.FromMinutes(3), ct);
            if (bytes > item.Bytes || complete)
                item.SetSize(Math.Max(bytes, item.Bytes), complete ? "" : "не меньше ");
            UpdateSuggestionsSummary();
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer refresh
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Диски: не удалось измерить {path}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"Диски: ошибка при измерении {path}", ex);
        }
    }

    private static string AgeText(FoundFile f, DateTime now)
    {
        var days = (int)(now - f.NewestTimeLocal).TotalDays;
        return days >= 365
            ? $"скачан {Format.Date(f.NewestTimeLocal)} (больше года назад)"
            : $"скачан {Format.Date(f.NewestTimeLocal)} ({Format.Count(days, "день", "дня", "дней")} назад)";
    }

    private void UpdateSuggestionsSummary()
    {
        var reclaimable = Suggestions.Where(s => s.CountsAsReclaimable).ToList();
        var total = reclaimable.Sum(s => s.Bytes);
        SuggestionsSummary = total > 0
            ? $"Можно освободить до {Format.Bytes(total)}"
            : "Крупных лишних файлов не найдено";

        var names = reclaimable.Where(s => s.Bytes > 0 && s.ShortName.Length > 0)
            .OrderByDescending(s => s.Bytes)
            .Select(s => s.ShortName)
            .Distinct()
            .ToList();
        if (total <= 0 || names.Count == 0)
        {
            SuggestionsTeaser = "";
            return;
        }
        var shown = string.Join(", ", names.Take(3));
        shown = char.ToUpper(shown[0], Format.Ru) + shown[1..];
        SuggestionsTeaser = names.Count > 3
            ? $"{shown} и ещё {Format.Count(names.Count - 3, "совет", "совета", "советов")}."
            : shown + ".";
    }

    // ------------------------------------------------------------------ system changes

    private async Task DisableHibernationAsync()
    {
        if (!ElevatedRestart.RequireElevation("Отключить гибернацию"))
            return;
        var hiber = DiskService.FindSystemFilesIn(_service.SystemDriveRoot).FirstOrDefault(f => f.Name == "hiberfil.sys");
        var size = hiber is null ? "" : $" ({Format.Bytes(hiber.Size)})";
        if (!Dialogs.Confirm("Отключить гибернацию",
                "Что произойдёт:\n" +
                $"• Windows удалит файл hiberfil.sys{size} с диска {_service.SystemDriveRoot.TrimEnd('\\')}.\n" +
                "• Режим гибернации и «Быстрый запуск» станут недоступны. Обычный спящий режим продолжит работать.\n" +
                "• Включение компьютера после полного выключения может стать на несколько секунд дольше.\n\n" +
                "Как вернуть: выполните от имени администратора команду powercfg /h on.\n\nПродолжить?"))
            return;

        string? message = null;
        await RunBusyAsync("Отключаем гибернацию…", async ct => message = await _service.DisableHibernationAsync(ct));
        if (message is null)
            return;
        Dialogs.Info("Гибернация", message);
        _ = LoadSuggestionsAsync();
        _ = LoadOverviewAsync();
    }

    private async Task EnableTrimAsync()
    {
        if (!ElevatedRestart.RequireElevation("Включить TRIM"))
            return;
        if (!Dialogs.Confirm("Включить TRIM",
                "Windows снова начнёт сообщать SSD, какие блоки освободились после удаления файлов. Это сохраняет скорость " +
                "записи и продлевает жизнь накопителя. Для обычных жёстких дисков это безопасно — они просто игнорируют команду.\n\n" +
                "Будет выполнена команда: fsutil behavior set DisableDeleteNotify 0\n" +
                "Как вернуть: fsutil behavior set DisableDeleteNotify 1 (не рекомендуется).\n\nПродолжить?"))
            return;

        string? message = null;
        await RunBusyAsync("Включаем TRIM…", async ct => message = await _service.EnableTrimAsync(ct));
        if (message is null)
            return;
        Dialogs.Info("TRIM", message);
        _ = LoadOverviewAsync();
    }

    private async Task OptimizeDriveAsync(DriveItem drive)
    {
        var letter = drive.Letter;
        if (!ElevatedRestart.RequireElevation($"Оптимизировать диск {letter}:"))
            return;

        var text = drive.Kind switch
        {
            DriveKind.Hdd =>
                $"Дефрагментировать диск {letter}: (HDD)?\n\n" +
                "Части файлов на жёстком диске будут собраны в непрерывные участки — так он читает их быстрее. " +
                "Это может занять от нескольких минут до нескольких часов (зависит от размера и заполненности диска). " +
                "Файлы не удаляются и не меняются. Работать за компьютером можно, но он может притормаживать.\n\n" +
                "Остановить можно в любой момент кнопкой «Отмена» — уже сделанная работа сохранится.",
            DriveKind.Ssd or DriveKind.NvmeSsd or DriveKind.Scm =>
                $"Оптимизировать диск {letter}: (SSD)?\n\n" +
                "Windows выполнит ReTrim: сообщит накопителю обо всех свободных блоках, чтобы он не замедлялся. " +
                "Обычно это занимает от нескольких секунд до пары минут. Файлы не удаляются и не меняются.",
            _ =>
                $"Оптимизировать диск {letter}:?\n\nWindows сама выберет подходящий способ (дефрагментация для HDD, ReTrim для SSD). " +
                "Файлы не удаляются и не меняются.",
        };
        text += "\n\nWindows и так оптимизирует диски по расписанию раз в неделю — вручную это нужно, только если вы давно не " +
                "включали компьютер надолго или заметили замедление.\n\nБудет выполнена команда: defrag " + letter + ": /O";
        if (!Dialogs.Confirm("Оптимизация диска", text))
            return;

        OptimizeOutcome? outcome = null;
        var cancelled = false;
        await RunBusyAsync($"Оптимизация диска {letter}:…", async ct =>
        {
            double lastPercent = -1;
            try
            {
                outcome = await _service.OptimizeDriveAsync(letter, line =>
                {
                    // Lines without "NN%" keep the last known progress instead of flickering to "indeterminate".
                    var percent = ToolOutputParsers.ParseProgressPercent(line);
                    if (percent is not null)
                        lastPercent = percent.Value;
                    ReportProgress(lastPercent, $"Диск {letter}: {line.Trim()}");
                }, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });

        if (outcome is null)
        {
            // Errors were already reported by RunBusyAsync; only a user cancel needs its own message.
            if (cancelled)
                Dialogs.Info("Оптимизация остановлена",
                    $"Оптимизация диска {letter}: остановлена. Уже выполненная часть работы сохранилась — её можно продолжить в любой момент.");
            return;
        }
        var details = string.IsNullOrWhiteSpace(outcome.Output) ? "" : "\n\n" + LastLines(outcome.Output, 14);
        Dialogs.Info(outcome.Success ? "Оптимизация завершена" : "Оптимизация не выполнена", outcome.Summary + details);
        _ = LoadOverviewAsync();
    }

    /// <summary>Opens a Windows tool once: not again while its window is open, and not twice on a double click.</summary>
    private async Task OpenToolAsync(Func<ToolLaunchResult> open, string displayName)
    {
        var result = await Task.Run(open);
        switch (result)
        {
            case ToolLaunchResult.Started:
                Log.Info($"Открыто окно {displayName}");
                // The command stays busy (its buttons disabled) while the window appears.
                await Task.Delay(ToolLaunchCooldown);
                break;
            case ToolLaunchResult.AlreadyOpen:
                Dialogs.Info("Окно уже открыто", $"Окно {displayName} уже открыто — переключитесь на него через панель задач.");
                break;
            case ToolLaunchResult.Cancelled:
                // The user answered "No" in the UAC prompt: nothing to report (already logged).
                break;
            default:
                Dialogs.Error("Не удалось открыть", $"Не удалось открыть окно {displayName}. Подробности — в журнале (раздел «Настройки»).");
                break;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string? PathOf(object? item) => item switch
    {
        string s => s,
        FolderRow r => r.Path,
        LargeFileRow f => f.Path,
        SuggestionFile s => s.Path,
        DriveItem d => d.Model.RootPath,
        BreadcrumbItem b => b.Node.FullPath,
        _ => null,
    };

    private static void CopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            Clipboard.SetText(path);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn($"Не удалось скопировать путь в буфер обмена: {ex.Message}");
        }
    }

    private static string ShortName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string LastLines(string text, int count)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        return string.Join("\n", lines.TakeLast(count));
    }
}
