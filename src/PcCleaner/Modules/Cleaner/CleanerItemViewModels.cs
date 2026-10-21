using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

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
        ExpandCommand = new RelayCommand(() => IsExpanded = true);
        CollapseCommand = new RelayCommand(() => IsExpanded = false);
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

    public string AdminBadgeText => NeedsAdmin ? T._("Нужны права администратора") : T._("Частично — нужны права администратора");

    public string AdminTip => NeedsAdmin
        ? T._("Это системная папка: очистить её можно только с правами администратора. Размер показан для справки.")
        : T._("Часть папок системная: без прав администратора они будут пропущены, остальное очистится как обычно.");

    public bool NotRecommended => !Category.DefaultChecked;

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public string RunningText => Category.IsBrowser
        ? T._("Закройте браузер для полной очистки")
        : T._("Закройте программу для полной очистки");

    public string RunningTip => Category.IsBrowser
        ? T._("Браузер сейчас запущен: пока он открыт, часть файлов кэша занята и будет пропущена.")
        : T._("Программа сейчас запущена: пока она открыта, часть её файлов занята и будет пропущена.");

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
        ScanState.Scanning => T._("Анализ…"),
        ScanState.Done when _result?.Error is not null && Bytes == 0 => "—",
        ScanState.Done when Bytes == 0 && Files == 0 && _result!.DeniedDirectories > 0 => T._("Нет доступа"),
        ScanState.Done when Bytes == 0 && Files == 0 => T._("Пусто"),
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
            return T.F("Очищаются только папки кэша внутри неё: {0}.", string.Join(", ", names));
        }
    }

    public string? MoreFoldersText => FolderBase is null && Folders.Count > MaxVisibleFolders
        ? T.F("…и ещё {0}", Format.Count(Folders.Count - MaxVisibleFolders, "папка", "папки", "папок"))
        : null;

    public IReadOnlyList<LargeFileViewModel> LargestFiles
    {
        get => _largestFiles;
        private set => Set(ref _largestFiles, value);
    }

    public bool HasLargestFiles => LargestFiles.Count > 0;

    public string LargestCaption => Files > LargestFiles.Count
        ? T.F("Самые большие файлы — {0} из {1}", Format.Number(LargestFiles.Count), Format.Number(Files))
        : T._("Файлы");

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
                var tooYoung = Format.Number(_result.TooYoungFiles);
                parts.Add(Category.Age == AgeRule.Fixed
                    ? T.F("Журналы моложе 30 дней не удаляются (таких файлов: {0}).", tooYoung)
                    : Category.AgeNote is null
                        ? T.F("Свежие файлы не удаляются — ими ещё могут пользоваться программы (таких файлов: {0}).", tooYoung)
                        : T.F("Свежие файлы ({0}) не удаляются — ими ещё могут пользоваться программы (таких файлов: {1}).", Category.AgeNote, tooYoung));
            }
            if (_result.AdminBytes > 0 && !IsElevated && PartlyNeedsAdmin)
                parts.Add(T.F("{0} находится в системных папках — их можно очистить только с правами администратора.", Format.Bytes(_result.AdminBytes)));
            if (_result.DeniedDirectories > 0)
            {
                var folders = Format.Count(_result.DeniedDirectories, "папке", "папкам", "папкам");
                parts.Add(IsElevated
                    ? T.F("Нет доступа к {0} — они пропущены.", folders)
                    : T.F("Нет доступа к {0}: с правами администратора будет видно больше.", folders));
            }
            if (_result.ReparsePointsSkipped > 0)
                parts.Add(T.F("Ссылок на другие папки (junction/symlink) пропущено: {0} — по ним очистка никогда не переходит.", Format.Number(_result.ReparsePointsSkipped)));
            if (_result.ExcludedEntries > 0)
                parts.Add(T.F("В исключениях: {0} — они не удаляются.", Format.Number(_result.ExcludedEntries)));
            return string.Join(" ", parts);
        }
    }

    public string DetailsEmptyText => Category.Kind switch
    {
        CategoryKind.RecycleBin => T._("Чтобы посмотреть, что лежит в корзине, откройте её в Проводнике."),
        CategoryKind.DeliveryOptimization =>
            T._("Этими файлами управляет служба оптимизации доставки Windows, поэтому они очищаются её собственной командой."),
        _ when State == ScanState.Scanning => T._("Идёт анализ…"),
        _ when HasResult && _result!.Error is not null => _result.Error + ".",
        _ when HasResult => T._("Удалять здесь нечего — папки уже чистые."),
        _ => T._("Нажмите «Анализ», чтобы увидеть файлы."),
    };

    /// <summary>Click on the row text, or Space/Enter on its chevron.</summary>
    public ICommand ToggleExpandCommand { get; }

    /// <summary>Right arrow while the row's checkbox or chevron has keyboard focus.</summary>
    public ICommand ExpandCommand { get; }

    /// <summary>Left arrow while the row's checkbox or chevron has keyboard focus.</summary>
    public ICommand CollapseCommand { get; }

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

/// <summary>A titled group of categories ("Windows", "Browsers", "Apps and games": the titles are texts of the UI language).</summary>
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

    public string ToolTipText => T.F("{0}\nРазмер: {1}\nИзменён: {2}", FullPath, Format.Bytes(Size), Format.DateTime(Modified));

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
            Log.Warn(T.F("Не удалось скопировать путь в буфер обмена: {0}", ex.Message));
        }
    }
}

/// <summary>The "Готово" card shown after cleaning.</summary>
public sealed class CleanResultViewModel
{
    public CleanResultViewModel(IReadOnlyList<CategoryCleanResult> results, bool cancelled, bool elevated = false)
    {
        Freed = results.Sum(r => r.FreedBytes);
        Deleted = results.Sum(r => r.DeletedFiles);
        Busy = results.Sum(r => r.LockedFiles);
        Denied = results.Sum(r => r.DeniedFiles);
        _elevated = elevated;
        Cancelled = cancelled || results.Any(r => r.Cancelled);
        Lines = results
            .OrderByDescending(r => r.FreedBytes)
            .Select(r => new CleanResultLine(r.Name, Format.Bytes(r.FreedBytes), LineNote(r)))
            .ToList();
        ErrorCount = results.Sum(r => r.Errors.Count);
        RecycleBinChanged = results.Any(r => r.ChangedAfterConfirmation);
        Time = DateTime.Now;
    }

    /// <summary>
    /// The Recycle Bin was left alone: files were deleted into it after the confirmation, and the user did not agree to
    /// empty it with them.
    /// </summary>
    public bool RecycleBinChanged { get; }

    public long Freed { get; }

    public long Deleted { get; }

    private readonly bool _elevated;

    /// <summary>Files skipped because a running program has them open.</summary>
    public long Busy { get; }

    /// <summary>Files skipped because there is no permission to delete them.</summary>
    public long Denied { get; }

    public long Skipped => Busy + Denied;

    public bool Cancelled { get; }

    public int ErrorCount { get; }

    public DateTime Time { get; }

    public string Headline => Cancelled
        ? T.F("Очистка прервана — освобождено {0}", Format.Bytes(Freed))
        : Freed > 0
            ? T.F("Готово! Освобождено {0}", Format.Bytes(Freed))
            : RecycleBinChanged
                ? T._("Корзина не очищена — в ней появились новые файлы")
                : T._("Готово, но место почти не освободилось");

    public string Summary
    {
        get
        {
            var text = CleanerText.Deleted(Deleted);
            text = char.ToUpper(text[0], T.Culture) + text[1..];
            if (Skipped > 0)
                text += ", " + CleanerText.Skipped(Busy, Denied);
            text += ".";
            if (ErrorCount > 0)
                text += " " + T.F("Ошибок: {0} (подробности в журнале).", Format.Number(ErrorCount));
            return text;
        }
    }

    /// <summary>What to do about the skipped files: busy ones and protected ones need different advice.</summary>
    public string? Hint
    {
        get
        {
            var parts = new List<string>();
            if (RecycleBinChanged)
                parts.Add(T._("Корзина не очищена: после подтверждения в неё попали новые файлы. Проверьте её и нажмите «Очистить выбранное» ещё раз — в подтверждении будет её новый размер."));
            if (Busy > 0)
                parts.Add(T._("Занятые файлы сейчас открыты в запущенных программах — закройте их и повторите очистку, чтобы удалить остальное."));
            if (Denied > 0)
                parts.Add(_elevated
                    ? T._("Файлы без доступа защищены Windows или принадлежат другой учётной записи — так и должно быть, их можно не трогать.")
                    : T._("Файлы без доступа принадлежат системе или другой учётной записи — часть из них можно удалить, запустив программу от имени администратора."));
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    public IReadOnlyList<CleanResultLine> Lines { get; }

    private static string LineNote(CategoryCleanResult r)
    {
        var parts = new List<string>();
        if (r.DeletedFiles > 0)
            parts.Add(r.CategoryId == CleanerCatalog.RecycleBinId
                ? CleanerText.DeletedObjects(r.DeletedFiles)
                : CleanerText.Deleted(r.DeletedFiles));
        if (r.SkippedFiles > 0)
            parts.Add(CleanerText.Skipped(r.LockedFiles, r.DeniedFiles));
        if (r.SkippedAdminRoots > 0)
            parts.Add(T._("системные папки пропущены — нужны права администратора"));
        if (r.Errors.Count > 0)
            parts.Add(T._("были ошибки"));
        if (parts.Count == 0)
            parts.Add(r.Message ?? T._("нечего удалять"));
        return string.Join(", ", parts);
    }
}

public sealed record CleanResultLine(string Name, string FreedText, string Note);
