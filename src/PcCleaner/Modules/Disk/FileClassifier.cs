using System.IO;
using System.Windows.Media;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Disk;

/// <summary>Space breakdown categories ("По типам").</summary>
public enum FileCategory
{
    Video,
    Music,
    Images,
    Archives,
    DiskImages,
    Installers,
    Documents,
    Programs,
    System,
    Other,
}

/// <summary>Context inherited by everything below a folder (affects classification).</summary>
[Flags]
internal enum DirContext : byte
{
    None = 0,

    /// <summary>Downloads / Desktop: .exe and .msi files there are installers.</summary>
    InstallerFolder = 1,

    /// <summary>Program Files, Steam, Epic and other game libraries: everything counts as programs and games.</summary>
    Programs = 2,

    /// <summary>Windows folder and system service folders.</summary>
    System = 4,
}

public sealed record CategoryInfo(FileCategory Category, string Name, string Icon, Color Color, string Description);

/// <summary>
/// Classifies files into <see cref="FileCategory"/> by extension and folder context without allocating
/// (the extension is packed into a 64-bit key).
/// </summary>
internal static class FileClassifier
{
    public static readonly int CategoryCount = Enum.GetValues<FileCategory>().Length;

    private static readonly Dictionary<ulong, FileCategory> ByExtension = new();
    private static readonly ulong ExeKey;
    private static readonly ulong SysKey;

    private sealed record CategoryTable(string Language, IReadOnlyList<CategoryInfo> Items);

    private static volatile CategoryTable? _categories;

    /// <summary>The categories with names and descriptions in the UI language (rebuilt when the language changes).</summary>
    public static IReadOnlyList<CategoryInfo> Categories
    {
        get
        {
            var table = _categories;
            if (table is null || table.Language != T.Language)
                _categories = table = new CategoryTable(T.Language, BuildCategories());
            return table.Items;
        }
    }

    private static IReadOnlyList<CategoryInfo> BuildCategories() => new[]
    {
        new CategoryInfo(FileCategory.Video, T._("Видео"), "", Color.FromRgb(0xF4, 0x72, 0xB6),
            T._("Фильмы, записи экрана и другие видеофайлы (.mp4, .mkv, .avi…).")),
        new CategoryInfo(FileCategory.Music, T._("Музыка"), "", Color.FromRgb(0xA7, 0x8B, 0xFA),
            T._("Музыка и другие звуковые файлы (.mp3, .flac, .wav…).")),
        new CategoryInfo(FileCategory.Images, T._("Изображения"), "", Color.FromRgb(0x60, 0xA5, 0xFA),
            T._("Фотографии, картинки и макеты (.jpg, .png, .psd, RAW…).")),
        new CategoryInfo(FileCategory.Archives, T._("Архивы"), "", Color.FromRgb(0xFB, 0xBF, 0x24),
            T._("Сжатые архивы (.zip, .rar, .7z…). Часто их уже распаковали и они больше не нужны.")),
        new CategoryInfo(FileCategory.DiskImages, T._("Образы дисков"), "", Color.FromRgb(0xFB, 0x92, 0x3C),
            T._("Образы дисков и виртуальных машин (.iso, .img, .vhdx…).")),
        new CategoryInfo(FileCategory.Installers, T._("Установщики"), "", Color.FromRgb(0xF8, 0x71, 0x71),
            T._("Установочные файлы программ (.msi, а также .exe в «Загрузках» и на рабочем столе). После установки их обычно можно удалить.")),
        new CategoryInfo(FileCategory.Documents, T._("Документы"), "", Color.FromRgb(0x34, 0xD3, 0x99),
            T._("Документы, таблицы, презентации, PDF и книги.")),
        new CategoryInfo(FileCategory.Programs, T._("Игры и программы"), "", Color.FromRgb(0x22, 0xD3, 0xEE),
            T._("Установленные программы и игры (Program Files, Steam, Epic Games и другие библиотеки игр). Удаляйте их через раздел «Программы».")),
        new CategoryInfo(FileCategory.System, T._("Системные"), "", Color.FromRgb(0x94, 0xA3, 0xB8),
            T._("Папка Windows, файлы подкачки и гибернации, служебные папки системы. Удалять их вручную нельзя.")),
        new CategoryInfo(FileCategory.Other, T._("Прочее"), "", Color.FromRgb(0x64, 0x74, 0x8B),
            T._("Всё остальное: данные программ, кэш, сохранения игр, файлы без расширения и т. п.")),
    };

    private static readonly string[] SystemRootFolders =
    {
        "System Volume Information", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset", "$GetCurrent",
        "Windows.old", "Recovery", "Config.Msi", "PerfLogs", "$Recycle.Bin",
    };

    private static readonly string[] ProgramFolderNames =
    {
        "steamapps", "SteamLibrary", "Steam", "Epic Games", "XboxGames", "GOG Games", "GOG Galaxy", "Riot Games",
        "Ubisoft Game Launcher", "EA Games", "Origin Games", "Battle.net", "WindowsApps", "ModifiableWindowsApps",
        "Games", "Rockstar Games", "Battlestate Games", "Wargaming.net", "Lesta Games", "VK Play", "VKPlay", "MyGames",
        "GameCenter", "Paradox Interactive", "Bethesda.net Launcher", "Blizzard", "Ubisoft",
    };

    static FileClassifier()
    {
        void Add(FileCategory category, params string[] extensions)
        {
            foreach (var ext in extensions)
            {
                var key = ExtensionKey(ext);
                if (key != 0)
                    ByExtension[key] = category;
            }
        }

        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts", "m2ts",
            "mts", "3gp", "vob", "ogv", "f4v", "divx", "rm", "rmvb", "asf");
        Add(FileCategory.Music, "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus", "ape", "alac", "aiff", "aif",
            "mid", "midi", "dsf", "dff", "wv", "amr", "mka");
        Add(FileCategory.Images, "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif",
            "avif", "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "psd", "psb", "svg", "ico", "xcf", "kra",
            "exr", "hdr", "tga", "dds");
        Add(FileCategory.Archives, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "lz",
            "lzma", "cab", "arj", "z", "001");
        Add(FileCategory.DiskImages, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "wim", "esd", "dmg", "nrg",
            "mdf", "mds", "avhdx", "swm");
        Add(FileCategory.Installers, "msi", "msix", "msixbundle", "appx", "appxbundle", "msp", "msu");
        Add(FileCategory.Documents, "pdf", "doc", "docx", "docm", "dot", "dotx", "xls", "xlsx", "xlsm", "xlsb", "ppt",
            "pptx", "pps", "ppsx", "odt", "ods", "odp", "rtf", "txt", "md", "csv", "epub", "fb2", "mobi", "azw3", "djvu",
            "djv", "xps", "oxps", "one", "pages", "numbers", "key", "tex", "log");
        Add(FileCategory.Programs, "dll", "jar", "apk", "pak", "vpk", "bsa", "ba2", "ucas", "utoc", "assets",
            "bundle", "forge", "rpf", "upk", "big");

        ExeKey = ExtensionKey("exe");
        SysKey = ExtensionKey("sys");
    }

    public static CategoryInfo Info(FileCategory category) => Categories[(int)category];

    /// <summary>Category of a file by its name and the context of its folder.</summary>
    public static FileCategory Classify(ReadOnlySpan<char> fileName, DirContext context)
    {
        if ((context & DirContext.System) != 0)
            return FileCategory.System;
        if ((context & DirContext.Programs) != 0)
            return FileCategory.Programs;

        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
            return FileCategory.Other;
        var key = ExtensionKey(fileName[(dot + 1)..]);
        if (key == 0)
            return FileCategory.Other;
        if (key == ExeKey)
            return (context & DirContext.InstallerFolder) != 0 ? FileCategory.Installers : FileCategory.Programs;
        if (key == SysKey)
            return FileCategory.System;
        return ByExtension.TryGetValue(key, out var category) ? category : FileCategory.Other;
    }

    public static FileCategory Classify(string fileName, DirContext context) => Classify(fileName.AsSpan(), context);

    /// <summary>
    /// Packs an extension (without dot) of up to 10 chars [a-z0-9_-] into 6 bits per char. Returns 0 if not packable.
    /// </summary>
    internal static ulong ExtensionKey(ReadOnlySpan<char> ext)
    {
        if (ext.Length is 0 or > 10)
            return 0;
        ulong key = 0;
        foreach (var ch in ext)
        {
            ulong code;
            if (ch is >= 'a' and <= 'z')
                code = (ulong)(ch - 'a' + 1);
            else if (ch is >= 'A' and <= 'Z')
                code = (ulong)(ch - 'A' + 1);
            else if (ch is >= '0' and <= '9')
                code = (ulong)(ch - '0' + 27);
            else if (ch == '_')
                code = 37;
            else if (ch == '-')
                code = 38;
            else
                return 0;
            key = (key << 6) | code;
        }
        return key;
    }

    internal static ulong ExtensionKey(string ext) => ExtensionKey(ext.TrimStart('.').AsSpan());

    internal static bool IsSystemRootFolderName(string name)
        => SystemRootFolders.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    internal static bool IsProgramFolderName(ReadOnlySpan<char> name)
    {
        foreach (var n in ProgramFolderNames)
        {
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Decides the <see cref="DirContext"/> of folders. Roots are injectable for tests.
/// </summary>
internal sealed class PathRules
{
    private readonly HashSet<string> _systemDirs;
    private readonly HashSet<string> _programDirs;
    private readonly HashSet<string> _installerDirs;

    public PathRules(IEnumerable<string?> systemDirs, IEnumerable<string?> programDirs, IEnumerable<string?> installerDirs)
    {
        _systemDirs = Normalize(systemDirs);
        _programDirs = Normalize(programDirs);
        _installerDirs = Normalize(installerDirs);
    }

    public static PathRules Default { get; } = CreateDefault();

    private static PathRules CreateDefault()
    {
        var programs = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        };
        var installers = new List<string?>
        {
            KnownFolders.Downloads,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return new PathRules(new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows) }, programs, installers);
    }

    private static HashSet<string> Normalize(IEnumerable<string?> dirs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            if (string.IsNullOrWhiteSpace(d))
                continue;
            try
            {
                set.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // ignore bad paths
            }
        }
        return set;
    }

    /// <summary>Context of the folder the analysis starts at (looks at all of its ancestors).</summary>
    public DirContext ForRoot(string fullPath)
    {
        string path;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DirContext.None;
        }

        var context = DirContext.None;
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var parent = Path.GetDirectoryName(current);
            var name = Path.GetFileName(current);
            var isRootChild = parent is not null && Path.GetDirectoryName(parent) is null;
            context |= Own(current, name, isRootChild);
        }
        return Effective(context);
    }

    /// <summary>Context of a subfolder: inherited from the parent, otherwise decided by its own path/name.</summary>
    public DirContext ForChild(DirContext parent, string childFullPath, ReadOnlySpan<char> childName, bool isDriveRootChild)
    {
        if (parent is DirContext.System or DirContext.Programs)
            return parent;
        return Effective(parent | Own(childFullPath, childName, isDriveRootChild));
    }

    private DirContext Own(string fullPath, ReadOnlySpan<char> name, bool isDriveRootChild)
    {
        if (_systemDirs.Contains(fullPath))
            return DirContext.System;
        if (isDriveRootChild && FileClassifier.IsSystemRootFolderName(name.ToString()))
            return DirContext.System;
        if (_programDirs.Contains(fullPath) || FileClassifier.IsProgramFolderName(name))
            return DirContext.Programs;
        if (_installerDirs.Contains(fullPath))
            return DirContext.InstallerFolder;
        return DirContext.None;
    }

    private static DirContext Effective(DirContext context)
    {
        if ((context & DirContext.System) != 0)
            return DirContext.System;
        if ((context & DirContext.Programs) != 0)
            return DirContext.Programs;
        return context;
    }
}
