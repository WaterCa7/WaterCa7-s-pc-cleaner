using System.IO;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Disk;

/// <summary>Finds forgotten installers, archives and disk images in the Downloads folder.</summary>
internal static class DownloadsAdvisor
{
    public const int DefaultAgeDays = 90;

    private static readonly string[] InstallerExtensions = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };
    private static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" };
    private static readonly string[] ImageExtensions = { ".iso", ".img" };

    public static DownloadKind? KindOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext))
            return null;
        if (InstallerExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return DownloadKind.Installers;
        if (ArchiveExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return DownloadKind.Archives;
        if (ImageExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return DownloadKind.DiskImages;
        return null;
    }

    /// <summary>
    /// True if the file was neither created (downloaded/copied) nor modified during the last <paramref name="days"/> days.
    /// </summary>
    public static bool IsOlderThan(FoundFile file, DateTime now, int days) => now - file.NewestTimeLocal >= TimeSpan.FromDays(days);

    /// <summary>
    /// Drops .exe files that are part of an unpacked (portable) program rather than a downloaded installer: files in a
    /// subfolder of Downloads that <paramref name="isProgramFolder"/> recognises (it also holds .dll files or other
    /// programs). Deleting such a file would break the program, so it is never suggested. Files directly in Downloads
    /// are kept.
    /// </summary>
    public static List<FoundFile> ExcludeUnpackedPrograms(IEnumerable<FoundFile> files, string downloadsRoot,
        Func<string, bool> isProgramFolder)
    {
        var root = Path.TrimEndingDirectorySeparator(downloadsRoot);
        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FoundFile>();
        foreach (var file in files)
        {
            if (!file.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
                continue;
            }
            var dir = Path.GetDirectoryName(file.Path);
            if (dir is null || string.Equals(Path.TrimEndingDirectorySeparator(dir), root, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
                continue;
            }
            if (!verdicts.TryGetValue(dir, out var isProgram))
                verdicts[dir] = isProgram = isProgramFolder(dir);
            if (!isProgram)
                result.Add(file);
        }
        return result;
    }

    /// <summary>Groups old installers / archives / disk images; each group is sorted by size (largest first).</summary>
    public static List<FileGroup> GroupOld(IEnumerable<FoundFile> files, DateTime now, int days = DefaultAgeDays)
    {
        return files
            .Where(f => IsOlderThan(f, now, days))
            .Select(f => (File: f, Kind: KindOf(f.Path)))
            .Where(x => x.Kind is not null)
            .GroupBy(x => x.Kind!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new FileGroup(g.Key, g.Select(x => x.File).OrderByDescending(f => f.Size).ToList()))
            .ToList();
    }

    public static string GroupTitle(DownloadKind kind) => kind switch
    {
        DownloadKind.Installers => T._("Старые установщики программ"),
        DownloadKind.Archives => T._("Старые архивы"),
        _ => T._("Старые образы дисков"),
    };

    /// <summary>The title of an old-downloads suggestion card on the Disks page.</summary>
    public static string GroupTitleInDownloads(DownloadKind kind) => kind switch
    {
        DownloadKind.Installers => T._("Старые установщики программ в «Загрузках»"),
        DownloadKind.Archives => T._("Старые архивы в «Загрузках»"),
        _ => T._("Старые образы дисков в «Загрузках»"),
    };

    public static string GroupDescription(DownloadKind kind, int days) => kind switch
    {
        DownloadKind.Installers =>
            T.F("Установочные файлы (.exe, .msi) в «Загрузках», которые скачаны больше {0} дней назад. Программы из них обычно уже установлены, а свежую версию всегда можно скачать снова.", days),
        DownloadKind.Archives =>
            T.F("Архивы (.zip, .rar, .7z) в «Загрузках» старше {0} дней. Если вы их уже распаковали, сами архивы больше не нужны.", days),
        _ => T.F("Образы дисков (.iso) в «Загрузках» старше {0} дней — обычно это дистрибутивы, которые уже использовали.", days),
    };

    public static string GroupIcon(DownloadKind kind) => kind switch
    {
        DownloadKind.Installers => "",
        DownloadKind.Archives => "",
        _ => "",
    };
}
