using System.IO;

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
        DownloadKind.Installers => "Старые установщики программ",
        DownloadKind.Archives => "Старые архивы",
        _ => "Старые образы дисков",
    };

    public static string GroupDescription(DownloadKind kind, int days) => kind switch
    {
        DownloadKind.Installers =>
            $"Установочные файлы (.exe, .msi) в «Загрузках», которые скачаны больше {days} дней назад. Программы из них " +
            "обычно уже установлены, а свежую версию всегда можно скачать снова.",
        DownloadKind.Archives =>
            $"Архивы (.zip, .rar, .7z) в «Загрузках» старше {days} дней. Если вы их уже распаковали, сами архивы больше не нужны.",
        _ => $"Образы дисков (.iso) в «Загрузках» старше {days} дней — обычно это дистрибутивы, которые уже использовали.",
    };

    public static string GroupIcon(DownloadKind kind) => kind switch
    {
        DownloadKind.Installers => "",
        DownloadKind.Archives => "",
        _ => "",
    };
}
