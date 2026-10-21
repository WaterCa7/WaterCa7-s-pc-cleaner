using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Disk;

/// <summary>Small colored label shown next to titles.</summary>
public sealed record BadgeItem(string Text, Tone Tone, string? ToolTip = null);

/// <summary>"Label: value" pair shown as a stat chip.</summary>
public sealed record StatItem(string Label, string Value, Tone Tone = Tone.Neutral, string? ToolTip = null);

/// <summary>Drive card on the overview tab.</summary>
public sealed class DriveItem : ObservableObject
{
    private TrimState _trim;

    public DriveItem(DriveSummary model, TrimState trim)
    {
        Model = model;
        _trim = trim;
    }

    public DriveSummary Model { get; }

    public char Letter => Model.Letter;

    public string Title => string.IsNullOrWhiteSpace(Model.Label)
        ? T.F("Локальный диск ({0}:)", Model.Letter)
        : $"{Model.Label} ({Model.Letter}:)";

    public string SubTitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Model.FileSystem))
                parts.Add(Model.FileSystem);
            if (Model.Disk is not null)
                parts.Add(Model.Disk.Model);
            else if (Model.DiskNumber is not null)
                parts.Add(T.F("Диск {0}", Model.DiskNumber));
            return string.Join(" · ", parts);
        }
    }

    public string FreeText => Format.Bytes(Model.FreeBytes);

    public string TotalText => T.F("свободно из {0}", Format.Bytes(Model.TotalBytes));

    public string UsedText => T.F("Занято {0} ({1})", Format.Bytes(Model.UsedBytes), Format.Percent(Model.UsedPercent));

    public double UsedPercent => Model.UsedPercent;

    public DriveKind Kind => Model.Disk?.Kind ?? DriveKind.Unknown;

    public bool IsSolidState => StorageLabels.IsSolidState(Kind);

    public IReadOnlyList<BadgeItem> Badges
    {
        get
        {
            var list = new List<BadgeItem>();
            if (Model.Disk is not null && Kind != DriveKind.Unknown)
                list.Add(new BadgeItem(StorageLabels.KindLabel(Kind), Tone.Info, StorageLabels.KindDescription(Kind)));
            if (Model.Disk?.BusType == StorageLabels.BusUsb)
                list.Add(new BadgeItem("USB", Tone.Neutral, T._("Накопитель подключён по USB.")));
            if (Model.IsSystemDrive)
                list.Add(new BadgeItem(T._("Системный"), Tone.Neutral, T._("На этом диске установлена Windows.")));
            return list;
        }
    }

    public string HealthText => Model.Disk is null ? T._("Нет данных") : StorageLabels.HealthLabel(Model.Disk.Health);

    public Tone HealthTone => Model.Disk is null ? Tone.Neutral : StorageLabels.HealthTone(Model.Disk.Health);

    public string HealthToolTip => Model.Disk is null
        ? T._("Не удалось сопоставить этот том с физическим диском.")
        : StorageLabels.HealthDescription(Model.Disk.Health);

    public bool IsLowSpace => Model.FreePercent < 15 && Model.FreeBytes < (64L << 30);

    public string LowSpaceText => Model.FreePercent < 10
        ? T._("Очень мало свободного места — Windows может работать медленнее.")
        : T._("Свободного места становится мало.");

    public TrimState Trim
    {
        get => _trim;
        set
        {
            if (Set(ref _trim, value))
            {
                OnPropertyChanged(nameof(TrimText));
                OnPropertyChanged(nameof(TrimTone));
                OnPropertyChanged(nameof(CanEnableTrim));
            }
        }
    }

    public bool ShowTrim => IsSolidState;

    public string TrimText => _trim switch
    {
        TrimState.Enabled => T._("TRIM включён"),
        TrimState.Disabled => T._("TRIM выключен"),
        _ => T._("TRIM: нет данных"),
    };

    public Tone TrimTone => _trim switch
    {
        TrimState.Enabled => Tone.Accent,
        TrimState.Disabled => Tone.Warning,
        _ => Tone.Neutral,
    };

    public bool CanEnableTrim => IsSolidState && _trim == TrimState.Disabled;

    public string OptimizeText => T._("Оптимизировать");

    public string OptimizeToolTip => Kind switch
    {
        DriveKind.Hdd => T._("Дефрагментация: собирает части файлов в непрерывные участки, чтобы жёсткий диск читал их быстрее. Может занять от нескольких минут до нескольких часов. Нужны права администратора."),
        DriveKind.Ssd or DriveKind.NvmeSsd or DriveKind.Scm =>
            T._("Для SSD выполняется ReTrim: Windows сообщает накопителю обо всех свободных блоках. Быстро и безопасно. Нужны права администратора."),
        _ => T._("Windows сама выберет подходящий способ оптимизации для этого диска. Нужны права администратора."),
    };
}

/// <summary>Physical disk row on the overview tab.</summary>
public sealed class PhysicalDiskItem
{
    public PhysicalDiskItem(PhysicalDiskInfo model, TrimState trim, bool reliabilityRequested)
    {
        Model = model;
        var badges = new List<BadgeItem>();
        if (model.Kind != DriveKind.Unknown)
            badges.Add(new BadgeItem(StorageLabels.KindLabel(model.Kind), Tone.Info, StorageLabels.KindDescription(model.Kind)));
        if (model.BusType is not null && model.BusType != StorageLabels.BusNvme)
            badges.Add(new BadgeItem(StorageLabels.BusLabel(model.BusType), Tone.Neutral, T._("Интерфейс подключения накопителя.")));
        badges.Add(new BadgeItem(StorageLabels.HealthLabel(model.Health), StorageLabels.HealthTone(model.Health),
            StorageLabels.HealthDescription(model.Health)));
        Badges = badges;

        var details = new List<string>();
        if (model.Number is not null)
            details.Add(T.F("Диск {0}", model.Number));
        if (model.SizeBytes > 0)
            details.Add(Format.Bytes(model.SizeBytes));
        details.Add(model.Letters.Count switch
        {
            0 => T._("без буквы диска"),
            1 => T.F("том {0}:", model.Letters[0]),
            _ => T.F("тома {0}", string.Join(", ", model.Letters.Select(l => l + ":"))),
        });
        var rpm = StorageLabels.SpindleLabel(model.SpindleSpeed);
        if (rpm is not null)
            details.Add(rpm);
        Details = string.Join(" · ", details);

        var stats = new List<StatItem>
        {
            new(T._("Состояние"), model.OperationalStatus, model.OperationalOk ? Tone.Neutral : Tone.Warning,
                T._("Рабочее состояние, о котором сообщает Windows.")),
        };
        if (StorageLabels.IsSolidState(model.Kind))
        {
            stats.Add(new StatItem("TRIM", trim switch
                {
                    TrimState.Enabled => T._("включён"),
                    TrimState.Disabled => T._("выключен"),
                    _ => T._("нет данных"),
                }, trim == TrimState.Disabled ? Tone.Warning : trim == TrimState.Enabled ? Tone.Accent : Tone.Neutral,
                T._("TRIM сообщает SSD об удалённых данных, чтобы он не замедлялся со временем. Должен быть включён.")));
        }

        var r = model.Reliability;
        if (r is not null)
        {
            if (r.TemperatureC is { } t)
                stats.Add(new StatItem(T._("Температура"), $"{t} °C", t >= 60 ? Tone.Danger : t >= 50 ? Tone.Warning : Tone.Neutral,
                    T._("Текущая температура накопителя. Нормально — до 50 °C, выше 60 °C — стоит улучшить охлаждение.")));
            if (r.WearPercent is { } w)
                stats.Add(new StatItem(T._("Износ"), $"{w}%", w >= 90 ? Tone.Danger : w >= 70 ? Tone.Warning : Tone.Neutral,
                    T._("Сколько ресурса записи SSD уже израсходовано. 100% — ресурс исчерпан.")));
            if (r.PowerOnHours is { } h)
                stats.Add(new StatItem(T._("Наработка"), T.F("{0} ч", Format.Number(h)), Tone.Neutral,
                    T.F("Сколько часов накопитель проработал за всё время (≈ {0} дн.).", Format.Number(h / 24))));
            if (r.ReadErrorsUncorrected is { } e)
                stats.Add(new StatItem(T._("Ошибки чтения"), Format.Number(e), e > 0 ? Tone.Danger : Tone.Neutral,
                    T._("Неисправимые ошибки чтения. Если их больше нуля — сделайте резервную копию данных.")));
        }
        Stats = stats;
        ShowAdminHint = !reliabilityRequested && !Admin.IsElevated;
        ShowNoCounters = reliabilityRequested && (r is null || !r.HasAnyValue);
    }

    public PhysicalDiskInfo Model { get; }

    public string Title => Model.Model;

    public string Details { get; }

    public IReadOnlyList<BadgeItem> Badges { get; }

    public IReadOnlyList<StatItem> Stats { get; }

    public string Icon => Model.Kind == DriveKind.Hdd ? "" : "";

    public bool ShowAdminHint { get; }

    public bool ShowNoCounters { get; }
}

/// <summary>Analyser location in the ComboBox.</summary>
public sealed record LocationOption(string Title, string Path, string Icon, long? ExpectedBytes)
{
    public override string ToString() => Title;
}

/// <summary>Row of the folder list (a subfolder, a link, or the files directly in the current folder).</summary>
public sealed class FolderRow
{
    public FolderRow(FolderNode? node, string name, long bytes, long files, double percent, bool isFilesEntry)
    {
        Node = node;
        Name = name;
        Bytes = bytes;
        Percent = percent;
        IsFilesEntry = isFilesEntry;
        FilesText = node?.IsLink == true ? "—" : Format.Number(files);
    }

    public FolderNode? Node { get; }

    public string Name { get; }

    public long Bytes { get; }

    public string SizeText => IsLink ? "—" : Format.Bytes(Bytes);

    public double Percent { get; }

    public string PercentText => IsLink ? "" : Format.Percent(Percent);

    public string FilesText { get; }

    public bool IsFilesEntry { get; }

    public bool IsLink => Node?.IsLink == true;

    public bool IsInaccessible => Node is { IsInaccessible: true, IsLink: false };

    /// <summary>A subfolder that has subfolders of its own ("Файлы в этой папке" is the current folder itself).</summary>
    public bool CanOpen => !IsFilesEntry && Node is { IsLink: false } n && n.Children.Length > 0;

    public string Icon => IsFilesEntry ? "" : IsLink ? "" : "";

    public Tone IconTone => IsFilesEntry || IsLink ? Tone.Neutral : Tone.Warning;

    public string Path => Node?.FullPath ?? "";

    public string ToolTip => IsLink
        ? T._("Это ссылка (junction или символическая ссылка) на другую папку. Её содержимое не учитывается здесь, чтобы не посчитать одни и те же файлы дважды.")
        : IsFilesEntry
            ? T._("Файлы, которые лежат прямо в этой папке (не во вложенных).")
            : IsInaccessible
                ? T.F("{0}\nЧасть содержимого не прочитана: нужны права администратора.", Path)
                : Path;
}

public sealed record BreadcrumbItem(string Name, FolderNode Node, bool IsLast);

public sealed class CategoryRow
{
    public CategoryRow(CategoryInfo info, long bytes, long files, double percent)
    {
        Info = info;
        Bytes = bytes;
        Files = files;
        Percent = percent;
        Brush = new SolidColorBrush(info.Color);
        Brush.Freeze();
    }

    public CategoryInfo Info { get; }

    public string Name => Info.Name;

    public string Icon => Info.Icon;

    public string Description => Info.Description;

    public SolidColorBrush Brush { get; }

    public long Bytes { get; }

    public long Files { get; }

    public double Percent { get; }

    public string SizeText => Format.Bytes(Bytes);

    public string PercentText => Format.Percent(Percent);

    public string CountText => Format.Count(Files, "файл", "файла", "файлов");
}

/// <summary>Row of the largest files list.</summary>
public sealed class LargeFileRow : ObservableObject
{
    private bool _isChecked;

    public LargeFileRow(FileHit hit, bool isProtected, string? riskNote)
    {
        Hit = hit;
        IsProtected = isProtected;
        RiskNote = riskNote;
        var info = FileClassifier.Info(hit.Category);
        CategoryName = info.Name;
        Icon = info.Icon;
        Brush = new SolidColorBrush(info.Color);
        Brush.Freeze();
    }

    public FileHit Hit { get; }

    public string Path => Hit.Path;

    public string Name => Hit.Name;

    public string Folder => Hit.Directory;

    public long Size => Hit.Size;

    public string SizeText => Format.Bytes(Hit.Size);

    public string ModifiedText => Format.Date(Hit.ModifiedUtc.ToLocalTime());

    public string CategoryName { get; }

    public string Icon { get; }

    public SolidColorBrush Brush { get; }

    public bool IsProtected { get; }

    public bool CanSelect => !IsProtected;

    public string? RiskNote { get; }

    public bool IsExecutable => Hit.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                Hit.Path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                Hit.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    public string ToolTip => IsProtected
        ? T.F("{0}\nСистемный файл или файл программы — удалять его отсюда нельзя.", Hit.Path)
        : RiskNote is null ? Hit.Path : $"{Hit.Path}\n{RiskNote}";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value && CanSelect))
                CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;
}

/// <summary>Action button on a suggestion card.</summary>
public sealed record SuggestionAction(string Text, ICommand Command, object? Parameter, bool IsDanger = false,
    bool IsAccent = false, string? ToolTip = null, string? Icon = null);

/// <summary>File inside a suggestion (old download, big video).</summary>
public sealed class SuggestionFile : ObservableObject
{
    private bool _isChecked;

    public SuggestionFile(FoundFile file, string? note)
    {
        File = file;
        Note = note;
    }

    public FoundFile File { get; }

    public string Path => File.Path;

    public string Name => File.Name;

    public string Folder => System.IO.Path.GetDirectoryName(File.Path) ?? "";

    public long Size => File.Size;

    public string SizeText => Format.Bytes(File.Size);

    public string? Note { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;
}

public enum SuggestionKind
{
    OldDownloads,
    BigVideos,
    Hibernation,
    PageFile,
    WindowsOld,
    ShadowStorage,
    RecycleBin,
}

/// <summary>Card on the "Советы по месту" tab.</summary>
public sealed class SuggestionItem : ObservableObject
{
    private bool _isExpanded;
    private long _checkedBytes;
    private int _checkedCount;

    public SuggestionItem(SuggestionKind kind, string icon, Tone tone, string title, string description, long bytes)
    {
        Kind = kind;
        Icon = icon;
        Tone = tone;
        Title = title;
        Description = description;
        Bytes = bytes;
    }

    public SuggestionKind Kind { get; }

    public string Icon { get; }

    public Tone Tone { get; }

    public string Title { get; }

    public string Description { get; set; }

    public long Bytes { get; private set; }

    public string? SizeText => Bytes > 0 ? (IsAtLeast ? T.F("не меньше {0}", Format.Bytes(Bytes)) : Format.Bytes(Bytes)) : null;

    /// <summary>The size is a lower bound (the measurement was cut short): "at least …".</summary>
    public bool IsAtLeast { get; private set; }

    /// <summary>Updates the size (e.g. after a slow background measurement finished).</summary>
    public void SetSize(long bytes, bool atLeast)
    {
        Bytes = bytes;
        IsAtLeast = atLeast;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(IsAtLeast));
        OnPropertyChanged(nameof(SizeText));
    }

    /// <summary>Counted in "можно освободить" at the top of the tab.</summary>
    public bool CountsAsReclaimable { get; init; }

    /// <summary>Short lowercase name for the overview teaser ("большие видео").</summary>
    public string ShortName { get; init; } = "";

    public string? Note { get; init; }

    public ObservableCollection<SuggestionFile> Files { get; } = new();

    public bool HasFiles => Files.Count > 0;

    public ObservableCollection<SuggestionAction> Actions { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                OnPropertyChanged(nameof(ExpandText));
        }
    }

    public string ExpandText => IsExpanded
        ? T._("Скрыть файлы")
        : T.F("Показать {0}", Format.Count(Files.Count, "файл", "файла", "файлов"));

    public string CheckedText => _checkedCount == 0
        ? T._("Ничего не отмечено")
        : T.F("Отмечено {0} — {1}", Format.Count(_checkedCount, "файл", "файла", "файлов"), Format.Bytes(_checkedBytes));

    public bool HasChecked => _checkedCount > 0;

    public void AddFiles(IEnumerable<SuggestionFile> files)
    {
        foreach (var f in files)
        {
            f.CheckedChanged += (_, _) => RecountChecked();
            Files.Add(f);
        }
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(ExpandText));
    }

    public void RemoveFiles(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files.Where(f => set.Contains(f.Path)).ToList())
            Files.Remove(f);
        Bytes = Files.Sum(f => f.Size);
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(ExpandText));
        RecountChecked();
    }

    public void SetAllChecked(bool value)
    {
        foreach (var f in Files)
            f.IsChecked = value;
    }

    private void RecountChecked()
    {
        _checkedCount = Files.Count(f => f.IsChecked);
        _checkedBytes = Files.Where(f => f.IsChecked).Sum(f => f.Size);
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(HasChecked));
    }
}
