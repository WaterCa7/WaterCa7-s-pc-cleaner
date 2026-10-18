using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

/// <summary>How a category is sized and cleaned.</summary>
public enum CategoryKind
{
    /// <summary>Plain files under one or more root folders.</summary>
    Files,

    /// <summary>The Windows Recycle Bin (SHQueryRecycleBin / SHEmptyRecycleBin).</summary>
    RecycleBin,

    /// <summary>Delivery Optimization cache (PowerShell Delete-DeliveryOptimizationCache).</summary>
    DeliveryOptimization,
}

/// <summary>Which minimum file age applies to a category.</summary>
public enum AgeRule
{
    /// <summary>Every file may be deleted regardless of age.</summary>
    None,

    /// <summary>Uses AppSettings.CleanerMinFileAgeHours (protects running installers).</summary>
    UserSetting,

    /// <summary>Uses <see cref="CleanerCategory.FixedMinAge"/>.</summary>
    Fixed,
}

/// <summary>A folder (or a single file) that a category cleans.</summary>
public sealed class CleanRoot
{
    public required string Path { get; init; }

    /// <summary>True when <see cref="Path"/> is one file (e.g. C:\Windows\MEMORY.DMP) instead of a folder.</summary>
    public bool IsSingleFile { get; init; }

    /// <summary>Descend into sub-folders (never into junctions/symlinks).</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>File-name wildcards (e.g. "thumbcache_*.db"); null = every file.</summary>
    public IReadOnlyList<string>? Patterns { get; init; }

    /// <summary>Deleting here needs administrator rights (sizing may still work).</summary>
    public bool RequiresAdmin { get; init; }

    public static CleanRoot Folder(string path, bool requiresAdmin = false)
        => new() { Path = path, RequiresAdmin = requiresAdmin };

    public static CleanRoot Files(string folder, bool recursive, bool requiresAdmin, params string[] patterns)
        => new() { Path = folder, Recursive = recursive, RequiresAdmin = requiresAdmin, Patterns = patterns };

    public static CleanRoot File(string path, bool requiresAdmin)
        => new() { Path = path, IsSingleFile = true, Recursive = false, RequiresAdmin = requiresAdmin };

    public override string ToString() => Path;
}

/// <summary>One line of the cleaner list ("Временные файлы пользователя", "Google Chrome", ...).</summary>
public sealed class CleanerCategory
{
    public const string GroupWindows = "Windows";
    public const string GroupBrowsers = "Браузеры";
    public const string GroupApps = "Приложения и игры";

    public required string Id { get; init; }

    public required string Group { get; init; }

    public required string Name { get; init; }

    /// <summary>One plain-language sentence: what this is and whether it is safe.</summary>
    public required string Description { get; init; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "\uE8B7";

    public CategoryKind Kind { get; init; } = CategoryKind.Files;

    public IReadOnlyList<CleanRoot> Roots { get; init; } = Array.Empty<CleanRoot>();

    public bool DefaultChecked { get; init; } = true;

    /// <summary>Optional plain-language explanation of a technical term (tooltip of the name).</summary>
    public string? Hint { get; init; }

    /// <summary>Why the category is not selected by default (tooltip of the "Не рекомендуется" badge).</summary>
    public string? Warning { get; init; }

    public AgeRule Age { get; init; } = AgeRule.None;

    public TimeSpan FixedMinAge { get; init; }

    /// <summary>Human-readable age limit, e.g. "моложе 24 часов".</summary>
    public string? AgeNote { get; init; }

    /// <summary>Remove sub-folders that became empty (never the root itself).</summary>
    public bool RemoveEmptyDirectories { get; init; } = true;

    /// <summary>Process names (without .exe) that keep these files busy.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();

    /// <summary>True when one of <see cref="ProcessNames"/> was running when the catalog was built.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Folder opened by "Открыть папку" when the category has no folder roots.</summary>
    public string? OpenFolder { get; init; }

    public bool IsBrowser => Group == GroupBrowsers;

    /// <summary>Cleaning is impossible without administrator rights.</summary>
    public bool RequiresAdmin => Kind == CategoryKind.DeliveryOptimization
                                 || (Kind == CategoryKind.Files && Roots.Count > 0 && Roots.All(r => r.RequiresAdmin));

    /// <summary>Some (not all) folders need administrator rights.</summary>
    public bool PartlyRequiresAdmin => Kind == CategoryKind.Files && !RequiresAdmin && Roots.Any(r => r.RequiresAdmin);

    /// <summary>
    /// This category limited to the folders that need administrator rights, or null when it has none. The dashboard fix of
    /// the system junk uses it: the user's own folders of the same category (reports, crash dumps) belong to the other fix.
    /// </summary>
    internal CleanerCategory? OnlyAdminFolders()
    {
        if (Kind == CategoryKind.DeliveryOptimization)
            return this;
        if (Kind != CategoryKind.Files)
            return null;
        var admin = Roots.Where(r => r.RequiresAdmin).ToList();
        if (admin.Count == 0)
            return null;
        if (admin.Count == Roots.Count)
            return this;
        return new CleanerCategory
        {
            Id = Id,
            Group = Group,
            Name = Name,
            Description = Description,
            Glyph = Glyph,
            Kind = Kind,
            Roots = admin,
            DefaultChecked = DefaultChecked,
            Hint = Hint,
            Warning = Warning,
            Age = Age,
            FixedMinAge = FixedMinAge,
            AgeNote = AgeNote,
            RemoveEmptyDirectories = RemoveEmptyDirectories,
            ProcessNames = ProcessNames,
            IsRunning = IsRunning,
            OpenFolder = OpenFolder,
        };
    }

    /// <summary>Existing folders to show in the details view.</summary>
    public IReadOnlyList<string> DisplayFolders
    {
        get
        {
            var list = new List<string>();
            foreach (var r in Roots)
            {
                var folder = r.IsSingleFile ? System.IO.Path.GetDirectoryName(r.Path) : r.Path;
                if (string.IsNullOrEmpty(folder) || list.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (r.IsSingleFile ? File.Exists(r.Path) : Directory.Exists(folder))
                        list.Add(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // not shown
                }
            }
            if (list.Count == 0 && OpenFolder is not null)
                list.Add(OpenFolder);
            return list;
        }
    }

    public override string ToString() => $"{Id} ({Name})";
}

/// <summary>Rules applied to every file at scan and clean time.</summary>
public sealed class CleanerRules
{
    /// <summary>
    /// Upper limit for the minimum file age (10 years). settings.json can be edited by hand; a huge value would
    /// otherwise overflow TimeSpan/DateTime and break every analysis.
    /// </summary>
    internal const int MaxMinFileAgeHours = 24 * 3650;

    public CleanerRules(TimeSpan minFileAge, IEnumerable<string>? exclusions, bool isElevated, DateTime? nowUtc = null)
    {
        var max = TimeSpan.FromHours(MaxMinFileAgeHours);
        MinFileAge = minFileAge < TimeSpan.Zero ? TimeSpan.Zero : minFileAge > max ? max : minFileAge;
        Exclusions = NormalizeExclusions(exclusions);
        IsElevated = isElevated;
        NowUtc = nowUtc ?? DateTime.UtcNow;
    }

    /// <summary>From AppSettings.CleanerMinFileAgeHours (user temp folders, Windows Temp, update cache).</summary>
    public TimeSpan MinFileAge { get; }

    /// <summary>Normalized prefixes the cleaner never touches (case-insensitive prefix match).</summary>
    public IReadOnlyList<string> Exclusions { get; }

    public bool IsElevated { get; }

    public DateTime NowUtc { get; }

    /// <summary>Rules from the user's settings plus the app's own folders.</summary>
    public static CleanerRules FromSettings(AppSettings settings, bool isElevated)
    {
        var exclusions = new List<string>(settings.CleanerExclusions ?? new List<string>());
        exclusions.Add(AppPaths.DataDir);
        exclusions.Add(AppContext.BaseDirectory);
        var hours = Math.Clamp(settings.CleanerMinFileAgeHours, 0, MaxMinFileAgeHours);
        return new CleanerRules(TimeSpan.FromHours(hours), exclusions, isElevated);
    }

    public TimeSpan MinAgeFor(CleanerCategory category) => category.Age switch
    {
        AgeRule.UserSetting => MinFileAge,
        AgeRule.Fixed => category.FixedMinAge,
        _ => TimeSpan.Zero,
    };

    /// <summary>Files whose newest timestamp is after this moment are too young to delete (null = no limit).</summary>
    public DateTime? CutoffFor(CleanerCategory category)
    {
        var age = MinAgeFor(category);
        return age <= TimeSpan.Zero ? null : NowUtc - age;
    }

    public bool IsExcluded(string path)
    {
        foreach (var e in Exclusions)
        {
            if (path.StartsWith(e, StringComparison.OrdinalIgnoreCase))
                return true;
            // "C:\Keep\" also protects the folder C:\Keep itself: it is neither walked nor removed when empty.
            if (e.Length == path.Length + 1 && e[^1] == '\\' && e.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<string> NormalizeExclusions(IEnumerable<string>? exclusions)
    {
        var list = new List<string>();
        if (exclusions is null)
            return list;
        foreach (var raw in exclusions)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).Replace('/', '\\');
            // Same form as the paths the walker produces: 8.3 names expanded (%TEMP% is often C:\Users\NAME~1\...),
            // ".." resolved. Otherwise such an exclusion would silently never match and its files would be deleted.
            var full = SafePathTools.Normalize(value);
            if (full is not null)
                value = value.EndsWith('\\') && !full.EndsWith('\\') ? full + "\\" : full;
            if (value.Length > 0 && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
                list.Add(value);
        }
        return list;
    }
}

/// <summary>A file found by the scan (used for the "largest files" details).</summary>
public readonly record struct ScannedFile(string Path, long Size, DateTime LastWriteUtc);

/// <summary>What a scan found in one category. Read-only: nothing is changed by scanning.</summary>
public sealed class CategoryScanResult
{
    public required string CategoryId { get; init; }

    /// <summary>Total size of matching files that could be read.</summary>
    public long Bytes { get; set; }

    public long Files { get; set; }

    /// <summary>Part of <see cref="Bytes"/> located in folders that need admin rights to clean.</summary>
    public long AdminBytes { get; set; }

    public long AdminFiles { get; set; }

    /// <summary>Folders that could not be read (usually no rights).</summary>
    public int DeniedDirectories { get; set; }

    /// <summary>Files skipped because they are younger than the minimum age.</summary>
    public long TooYoungFiles { get; set; }

    public long ExcludedEntries { get; set; }

    /// <summary>Junctions/symlinks that were not followed.</summary>
    public long ReparsePointsSkipped { get; set; }

    /// <summary>Roots refused by the safety guard.</summary>
    public int RejectedRoots { get; set; }

    /// <summary>Largest files first.</summary>
    public IReadOnlyList<ScannedFile> Largest { get; set; } = Array.Empty<ScannedFile>();

    /// <summary>Set when the size could not be determined (friendly Russian text).</summary>
    public string? Error { get; set; }

    public TimeSpan Duration { get; set; }

    public long CleanableBytes(bool elevated) => elevated ? Bytes : Math.Max(0, Bytes - AdminBytes);

    public long CleanableFiles(bool elevated) => elevated ? Files : Math.Max(0, Files - AdminFiles);
}

/// <summary>What cleaning one category did.</summary>
public sealed class CategoryCleanResult
{
    public required string CategoryId { get; init; }

    public required string Name { get; init; }

    public long FreedBytes { get; set; }

    public long DeletedFiles { get; set; }

    public long DeletedDirectories { get; set; }

    /// <summary>Files in use by another program (sharing violation).</summary>
    public long LockedFiles { get; set; }

    /// <summary>Files we were not allowed to delete.</summary>
    public long DeniedFiles { get; set; }

    /// <summary>Roots skipped because the app is not elevated.</summary>
    public int SkippedAdminRoots { get; set; }

    /// <summary>Roots refused by the safety guard (should never happen).</summary>
    public int RejectedRoots { get; set; }

    public bool Cancelled { get; set; }

    /// <summary>Extra Russian message for special categories (Recycle Bin, Delivery Optimization).</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Nothing was done because the contents grew after the user confirmed (Recycle Bin: files were deleted into it
    /// after its size was shown, and the user did not agree to empty it with them).
    /// </summary>
    public bool ChangedAfterConfirmation { get; set; }

    public List<string> Errors { get; } = new();

    public long SkippedFiles => LockedFiles + DeniedFiles;
}

/// <summary>The Recycle Bin as the user saw it in a confirmation: total size and number of objects.</summary>
public readonly record struct RecycleBinSnapshot(long Bytes, long Items)
{
    /// <summary>
    /// True when <paramref name="now"/> holds more than this snapshot — something was deleted into the bin since then,
    /// and the user has never seen it. (A smaller bin is fine: the user restored or removed something themselves.)
    /// </summary>
    public bool IsExceededBy(RecycleBinSnapshot now) => now.Items > Items || now.Bytes > Bytes;

    /// <summary>"8,8 КБ, 2 объекта".</summary>
    public string Text => $"{Format.Bytes(Bytes)}, {CleanerText.Objects(Items)}";
}
