using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

public enum ScanState
{
    NotScanned,
    Scanning,
    Done,
}

/// <summary>One category row: checkbox, size, badges and expandable details.</summary>
public sealed class CategoryItemViewModel : ObservableObject
{
    private const int MaxVisibleFolders = 6;

    private readonly Action<CategoryItemViewModel> _checkedChanged;
    private readonly Action<string>? _exclude;
    private bool _isChecked;
    private bool _isExpanded;
    private bool _isRunning;
    private ScanState _state;
    private CategoryScanResult? _result;
    private IReadOnlyList<LargeFileViewModel> _largestFiles = Array.Empty<LargeFileViewModel>();
    private IReadOnlyList<FolderLinkViewModel> _folders;

    public CategoryItemViewModel(CleanerCategory category, IReadOnlyList<string> folders, bool isChecked, bool isElevated,
        Action<CategoryItemViewModel> checkedChanged, Action<string>? exclude = null)
    {
        Category = category;
        IsElevated = isElevated;
        _isChecked = isChecked;
        _isRunning = category.IsRunning;
        _checkedChanged = checkedChanged;
        _exclude = exclude;
        _folders = folders.Select(f => new FolderLinkViewModel(f)).ToList();
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenRecycleBinCommand = new RelayCommand(() => ProcessRunner.ShellOpen("shell:RecycleBinFolder"));
    }

    public CleanerCategory Category { get; private set; }

    public string Id => Category.Id;

    public string Name => Category.Name;

    public string Description => Category.Description;

    public string Glyph => Category.Glyph;

    public string? Warning => Category.Warning;

    public string? Hint => Category.Hint;

    public bool IsElevated { get; }

    public bool IsRecycleBin => Category.Kind == CategoryKind.RecycleBin;

    /// <summary>Cannot be cleaned at all without admin rights (shown with a lock instead of a checkbox).</summary>
    public bool NeedsAdmin => Category.RequiresAdmin && !IsElevated;

    /// <summary>Some folders are skipped without admin rights.</summary>
    public bool PartlyNeedsAdmin => Category.PartlyRequiresAdmin && !IsElevated;

    public bool ShowAdminBadge => NeedsAdmin || PartlyNeedsAdmin;

    public bool CanClean => !NeedsAdmin;

    public string AdminBadgeText => NeedsAdmin ? "Нужны права администратора" : "Частично — нужны права администратора";

    public string AdminTip => NeedsAdmin
        ? "Это системная папка: очистить её можно только с правами администратора. Размер показан для справки."
        : "Часть папок системная: без прав администратора они будут пропущены, остальное очистится как обычно.";

    public bool NotRecommended => !Category.DefaultChecked;

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public string RunningText => Category.IsBrowser ? "Браузер запущен" : "Программа запущена";

    public string RunningTip => Category.IsBrowser
        ? "Закройте браузер для полной очистки: пока он открыт, часть файлов кэша занята и будет пропущена."
        : "Закройте программу для полной очистки: пока она открыта, часть файлов занята и будет пропущена.";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
            {
                OnPropertyChanged(nameof(IsSelectedForCleaning));
                _checkedChanged(this);
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public ScanState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
                RaiseResultProperties();
        }
    }

    public bool IsScanning => State == ScanState.Scanning;

    public CategoryScanResult? Result => _result;

    public long Bytes => _result?.Bytes ?? 0;

    public long Files => _result?.Files ?? 0;

    /// <summary>What can be freed right now (admin-only folders excluded when not elevated).</summary>
    public long CleanableBytes => _result?.CleanableBytes(IsElevated) ?? 0;

    public long CleanableFiles => _result?.CleanableFiles(IsElevated) ?? 0;

    /// <summary>Found in folders that only an administrator can clean.</summary>
    public long AdminOnlyBytes => IsElevated || _result is null ? 0 : _result.AdminBytes;

    public bool HasResult => State == ScanState.Done && _result is not null;

    public bool IsEmpty => HasResult && Bytes == 0 && Files == 0 && _result!.Error is null;

    public bool IsSelectedForCleaning => IsChecked && CanClean && HasResult && (CleanableBytes > 0 || CleanableFiles > 0);

    public string SizeText => State switch
    {
        ScanState.Scanning => "Анализ…",
        ScanState.Done when _result?.Error is not null && Bytes == 0 => "—",
        ScanState.Done when Bytes == 0 && Files == 0 && _result!.DeniedDirectories > 0 => "Нет доступа",
        ScanState.Done when Bytes == 0 && Files == 0 => "Пусто",
        ScanState.Done => Format.Bytes(Bytes),
        _ => "—",
    };

    public bool IsSizeMuted => State != ScanState.Done || (Bytes == 0 && Files == 0) || NeedsAdmin;

    public string FilesText
    {
        get
        {
            if (!HasResult)
                return "";
            if (_result!.Error is not null && Files == 0)
                return _result.Error;
            if (Files == 0)
                return "";
            return IsRecycleBin ? CleanerText.Objects(Files) : CleanerText.Files(Files);
        }
    }

    public IReadOnlyList<FolderLinkViewModel> Folders
    {
        get => _folders;
        private set => Set(ref _folders, value);
    }

    public bool HasFolders => Folders.Count > 0;

    /// <summary>When every folder lies inside the program's own folder, only that folder is shown (with sub-folder names).</summary>
    public string? FolderBase
    {
        get
        {
            var baseDir = Category.OpenFolder;
            if (baseDir is null || Folders.Count < 2)
                return null;
            return Folders.All(f => SafePathTools.IsStrictlyUnder(f.Path, baseDir)) ? baseDir : null;
        }
    }

    /// <summary>Folders with an "Открыть папку" link (at most six).</summary>
    public IReadOnlyList<FolderLinkViewModel> VisibleFolders
        => FolderBase is { } baseDir ? new[] { new FolderLinkViewModel(baseDir) } : Folders.Take(MaxVisibleFolders).ToList();

    public string? SubfoldersText
    {
        get
        {
            var baseDir = FolderBase;
            if (baseDir is null)
                return null;
            var prefixLength = baseDir.TrimEnd('\\').Length + 1;
            var names = Folders.Select(f => f.Path[prefixLength..]);
            return "Очищаются только папки кэша внутри неё: " + string.Join(", ", names) + ".";
        }
    }

    public string? MoreFoldersText => FolderBase is null && Folders.Count > MaxVisibleFolders
        ? $"…и ещё {Format.Count(Folders.Count - MaxVisibleFolders, "папка", "папки", "папок")}"
        : null;

    public IReadOnlyList<LargeFileViewModel> LargestFiles
    {
        get => _largestFiles;
        private set => Set(ref _largestFiles, value);
    }

    public bool HasLargestFiles => LargestFiles.Count > 0;

    public string LargestCaption => Files > LargestFiles.Count
        ? $"Самые большие файлы — {Format.Number(LargestFiles.Count)} из {Format.Number(Files)}"
        : "Файлы";

    /// <summary>Extra facts about the last scan (fresh files kept, junctions skipped, no access...).</summary>
    public string DetailsNote
    {
        get
        {
            if (_result is null)
                return "";
            var parts = new List<string>();
            if (_result.TooYoungFiles > 0)
            {
                parts.Add(Category.Age == AgeRule.Fixed
                    ? $"Журналы моложе 30 дней не удаляются (таких файлов: {Format.Number(_result.TooYoungFiles)})."
                    : $"Свежие файлы{(Category.AgeNote is null ? "" : $" ({Category.AgeNote})")} не удаляются — ими ещё могут " +
                      $"пользоваться программы (таких файлов: {Format.Number(_result.TooYoungFiles)}).");
            }
            if (_result.AdminBytes > 0 && !IsElevated && PartlyNeedsAdmin)
                parts.Add($"{Format.Bytes(_result.AdminBytes)} находится в системных папках — их можно очистить только с правами администратора.");
            if (_result.DeniedDirectories > 0)
                parts.Add(IsElevated
                    ? $"Нет доступа к {Format.Count(_result.DeniedDirectories, "папке", "папкам", "папкам")} — они пропущены."
                    : $"Нет доступа к {Format.Count(_result.DeniedDirectories, "папке", "папкам", "папкам")}: с правами администратора будет видно больше.");
            if (_result.ReparsePointsSkipped > 0)
                parts.Add($"Ссылок на другие папки (junction/symlink) пропущено: {Format.Number(_result.ReparsePointsSkipped)} — по ним очистка никогда не переходит.");
            if (_result.ExcludedEntries > 0)
                parts.Add($"В исключениях: {Format.Number(_result.ExcludedEntries)} — они не удаляются.");
            return string.Join(" ", parts);
        }
    }

    public string DetailsEmptyText => Category.Kind switch
    {
        CategoryKind.RecycleBin => "Чтобы посмотреть, что лежит в корзине, откройте её в Проводнике.",
        CategoryKind.DeliveryOptimization =>
            "Этими файлами управляет служба оптимизации доставки Windows, поэтому они очищаются её собственной командой.",
        _ when State == ScanState.Scanning => "Идёт анализ…",
        _ when HasResult && _result!.Error is not null => _result.Error + ".",
        _ when HasResult => "Удалять здесь нечего — папки уже чистые.",
        _ => "Нажмите «Анализ», чтобы увидеть файлы.",
    };

    public ICommand ToggleExpandCommand { get; }

    public ICommand OpenRecycleBinCommand { get; }

    internal void BeginScan() => State = ScanState.Scanning;

    internal void ApplyResult(CategoryScanResult result)
    {
        _result = result;
        var roots = Category.Roots
            .Select(r => SafePathTools.Normalize(r.IsSingleFile ? System.IO.Path.GetDirectoryName(r.Path) : r.Path))
            .Where(r => r is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        LargestFiles = result.Largest
            .Select(f => new LargeFileViewModel(f, _exclude, !roots.Contains(System.IO.Path.GetDirectoryName(f.Path) ?? "")))
            .ToList();
        State = ScanState.Done;
        RaiseResultProperties();
    }

    /// <summary>After a cancelled scan: back to the previous state (keeps an older result if there was one).</summary>
    internal void EndScanIfPending()
    {
        if (State == ScanState.Scanning)
            State = _result is null ? ScanState.NotScanned : ScanState.Done;
    }

    /// <summary>Replaces the category definition after a re-analysis (folders may have appeared/disappeared).</summary>
    internal void UpdateCategory(CleanerCategory category, IReadOnlyList<string> folders)
    {
        Category = category;
        Folders = folders.Select(f => new FolderLinkViewModel(f)).ToList();
        IsRunning = category.IsRunning;
        OnPropertyChanged(string.Empty);
    }

    internal void RefreshRunning() => IsRunning = Category.IsRunning;

    /// <summary>Sets the checkbox without notifying the page (used by "select all"-style batch actions).</summary>
    internal void SetCheckedSilently(bool value)
    {
        if (Set(ref _isChecked, value, nameof(IsChecked)))
            OnPropertyChanged(nameof(IsSelectedForCleaning));
    }

    private void RaiseResultProperties()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(CleanableBytes));
        OnPropertyChanged(nameof(AdminOnlyBytes));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSelectedForCleaning));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(IsSizeMuted));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(HasLargestFiles));
        OnPropertyChanged(nameof(LargestCaption));
        OnPropertyChanged(nameof(DetailsNote));
        OnPropertyChanged(nameof(DetailsEmptyText));
    }
}

/// <summary>A titled group of categories ("Windows", "Браузеры", "Приложения и игры").</summary>
public sealed class CategoryGroupViewModel : ObservableObject
{
    public CategoryGroupViewModel(string name, string glyph, string note, string emptyText,
        IReadOnlyList<CategoryItemViewModel> items)
    {
        Name = name;
        Glyph = glyph;
        Note = note;
        EmptyText = emptyText;
        Items = items;
    }

    public string Name { get; }

    public string Glyph { get; }

    public string Note { get; }

    public string EmptyText { get; }

    public IReadOnlyList<CategoryItemViewModel> Items { get; }

    public bool HasItems => Items.Count > 0;

    public string TotalText
    {
        get
        {
            var done = Items.Where(i => i.HasResult).ToList();
            return done.Count == 0 ? "" : Format.Bytes(done.Sum(i => i.Bytes));
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(TotalText));
}

/// <summary>A folder of a category with an "Открыть папку" link.</summary>
public sealed class FolderLinkViewModel
{
    public FolderLinkViewModel(string path)
    {
        Path = path;
        OpenCommand = new RelayCommand(() => ProcessRunner.ShowInExplorer(Path));
    }

    public string Path { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>One of the largest files of a category (details view) with a context menu.</summary>
public sealed class LargeFileViewModel
{
    /// <param name="canExcludeFolder">False when the file lies directly in the category folder (excluding it would disable the whole category).</param>
    public LargeFileViewModel(ScannedFile file, Action<string>? exclude, bool canExcludeFolder = true)
    {
        FullPath = file.Path;
        Size = file.Size;
        Modified = file.LastWriteUtc.ToLocalTime();
        ShowInExplorerCommand = new RelayCommand(() => ProcessRunner.ShowInExplorer(FullPath));
        CopyPathCommand = new RelayCommand(CopyPath);
        ExcludeFileCommand = new RelayCommand(() => exclude?.Invoke(FullPath), () => exclude is not null);
        ExcludeFolderCommand = new RelayCommand(
            () => exclude?.Invoke(System.IO.Path.GetDirectoryName(FullPath) + "\\"), () => exclude is not null && canExcludeFolder);
    }

    public string FullPath { get; }

    public string FileName => System.IO.Path.GetFileName(FullPath);

    public string Folder => System.IO.Path.GetDirectoryName(FullPath) ?? "";

    public long Size { get; }

    public DateTime Modified { get; }

    public string SizeText => Format.Bytes(Size);

    public string ToolTipText => $"{FullPath}\nРазмер: {Format.Bytes(Size)}\nИзменён: {Format.DateTime(Modified)}";

    public ICommand ShowInExplorerCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand ExcludeFileCommand { get; }

    public ICommand ExcludeFolderCommand { get; }

    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(FullPath);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            Log.Warn($"Не удалось скопировать путь в буфер обмена: {ex.Message}");
        }
    }
}

/// <summary>The "Готово" card shown after cleaning.</summary>
public sealed class CleanResultViewModel
{
    public CleanResultViewModel(IReadOnlyList<CategoryCleanResult> results, bool cancelled)
    {
        Freed = results.Sum(r => r.FreedBytes);
        Deleted = results.Sum(r => r.DeletedFiles);
        Skipped = results.Sum(r => r.SkippedFiles);
        Cancelled = cancelled || results.Any(r => r.Cancelled);
        Lines = results
            .OrderByDescending(r => r.FreedBytes)
            .Select(r => new CleanResultLine(r.Name, Format.Bytes(r.FreedBytes), LineNote(r)))
            .ToList();
        ErrorCount = results.Sum(r => r.Errors.Count);
        Time = DateTime.Now;
    }

    public long Freed { get; }

    public long Deleted { get; }

    public long Skipped { get; }

    public bool Cancelled { get; }

    public int ErrorCount { get; }

    public DateTime Time { get; }

    public string Headline => Cancelled
        ? $"Очистка прервана — освобождено {Format.Bytes(Freed)}"
        : Freed > 0 ? $"Готово! Освобождено {Format.Bytes(Freed)}" : "Готово, но место почти не освободилось";

    public string Summary
    {
        get
        {
            var text = CleanerText.Deleted(Deleted);
            text = char.ToUpper(text[0], Format.Ru) + text[1..];
            if (Skipped > 0)
                text += ", " + CleanerText.SkippedBusy(Skipped);
            text += ".";
            if (ErrorCount > 0)
                text += $" Ошибок: {Format.Number(ErrorCount)} (подробности в журнале).";
            return text;
        }
    }

    public string? Hint => Skipped > 0
        ? "Занятые файлы сейчас используются запущенными программами. Закройте их и повторите очистку, чтобы удалить остальное."
        : null;

    public IReadOnlyList<CleanResultLine> Lines { get; }

    private static string LineNote(CategoryCleanResult r)
    {
        var parts = new List<string>();
        if (r.DeletedFiles > 0)
            parts.Add(CleanerText.Deleted(r.DeletedFiles));
        if (r.SkippedFiles > 0)
            parts.Add(CleanerText.SkippedBusy(r.SkippedFiles));
        if (r.SkippedAdminRoots > 0)
            parts.Add("системные папки пропущены — нужны права администратора");
        if (r.Errors.Count > 0)
            parts.Add("были ошибки");
        if (parts.Count == 0)
            parts.Add(r.Message ?? "нечего удалять");
        return string.Join(", ", parts);
    }
}

public sealed record CleanResultLine(string Name, string FreedText, string Note);
