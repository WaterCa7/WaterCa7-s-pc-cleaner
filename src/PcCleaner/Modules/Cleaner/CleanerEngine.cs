using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

/// <summary>
/// Sizing and deleting for file-based categories. Pure logic on top of <see cref="SafeFileWalker"/> and
/// <see cref="RootGuard"/>, so it can be tested against temp folders.
/// </summary>
internal static class CleanerEngine
{
    /// <summary>How many of the largest files are remembered per category for the details view.</summary>
    public const int DefaultSampleSize = 200;

    /// <summary>Read-only: counts matching files and remembers the largest ones.</summary>
    public static CategoryScanResult Scan(CleanerCategory category, CleanerRules rules, RootGuard guard, int sampleSize,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new CategoryScanResult { CategoryId = category.Id };
        var top = sampleSize > 0 ? new PriorityQueue<ScannedFile, long>(sampleSize + 1) : null;
        var cutoff = rules.CutoffFor(category);
        var stats = new WalkStats();

        foreach (var root in category.Roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!guard.IsSafeRoot(root, out var reason))
            {
                result.RejectedRoots++;
                Log.Warn($"Очистка: «{root.Path}» пропущено ради безопасности — {reason}");
                continue;
            }
            var rootPath = SafePathTools.Normalize(root.Path)!;
            if (rules.IsExcluded(rootPath))
            {
                stats.Excluded++;
                continue;
            }
            if (guard.IsRedirected(rootPath, root.IsSingleFile))
            {
                // A folder on the way to the root is a junction/symlink: the files physically live elsewhere.
                stats.ReparsePointsSkipped++;
                continue;
            }
            foreach (var file in EnumerateCandidates(root, rootPath, rules, cutoff, stats, null, ct))
            {
                result.Bytes += file.Length;
                result.Files++;
                if (root.RequiresAdmin)
                {
                    result.AdminBytes += file.Length;
                    result.AdminFiles++;
                }
                if (top is not null)
                {
                    top.Enqueue(new ScannedFile(file.FullPath, file.Length, file.LastWriteUtc), file.Length);
                    if (top.Count > sampleSize)
                        top.Dequeue();
                }
            }
        }

        result.DeniedDirectories = stats.DeniedDirectories;
        result.ReparsePointsSkipped = stats.ReparsePointsSkipped;
        result.ExcludedEntries = stats.Excluded;
        result.TooYoungFiles = stats.TooYoung;
        if (top is not null)
        {
            var largest = new List<ScannedFile>(top.Count);
            while (top.Count > 0)
                largest.Add(top.Dequeue());
            largest.Reverse();
            result.Largest = largest;
        }
        result.Duration = sw.Elapsed;
        return result;
    }

    /// <summary>
    /// Deletes matching files of the category. Every file is re-checked right before deletion
    /// (inside the root, no reparse point on the way, not excluded, still old enough).
    /// Busy files are skipped and counted; empty sub-folders are removed bottom-up, never the root.
    /// Cancellation stops early and returns what was done so far.
    /// </summary>
    public static CategoryCleanResult Clean(CleanerCategory category, CleanerRules rules, RootGuard guard,
        CancellationToken ct)
    {
        var result = new CategoryCleanResult { CategoryId = category.Id, Name = category.Name };
        var cutoff = rules.CutoffFor(category);
        try
        {
            foreach (var root in category.Roots)
            {
                ct.ThrowIfCancellationRequested();
                if (root.RequiresAdmin && !rules.IsElevated)
                {
                    result.SkippedAdminRoots++;
                    continue;
                }
                if (!guard.IsSafeRoot(root, out var reason))
                {
                    result.RejectedRoots++;
                    Log.Warn($"Очистка: «{root.Path}» пропущено ради безопасности — {reason}");
                    continue;
                }
                var rootPath = SafePathTools.Normalize(root.Path)!;
                if (rules.IsExcluded(rootPath))
                    continue;
                if (guard.IsRedirected(rootPath, root.IsSingleFile))
                {
                    Log.Info($"Очистка: «{root.Path}» пропущено — путь к папке проходит через ссылку (junction/symlink)");
                    continue;
                }

                if (root.IsSingleFile)
                {
                    CleanSingleFile(root, rootPath, rules, cutoff, result);
                    continue;
                }

                // Where the root really is; every file must turn out to be exactly below it (checked through its handle).
                var rootReal = SafeDelete.GetRealFolderPath(rootPath);
                if (rootReal is null)
                    continue; // missing, itself a junction, or not accessible
                var directories = new List<string>();
                var stats = new WalkStats();
                string? verifiedDir = null;
                foreach (var file in EnumerateCandidates(root, rootPath, rules, cutoff, stats, directories.Add, ct))
                {
                    var dir = Path.GetDirectoryName(file.FullPath);
                    if (dir is null)
                        continue;
                    if (!string.Equals(dir, verifiedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsChainFreeOfReparsePoints(dir, rootPath))
                            continue;
                        verifiedDir = dir;
                    }
                    Record(result,
                        TryDeleteFile(file.FullPath, rootPath, rootReal, root, rules, cutoff, out var size, out var error),
                        size, error);
                }

                if (category.RemoveEmptyDirectories && root.Recursive)
                    RemoveEmptyDirectories(rootPath, rootReal, directories, rules, cutoff, result, ct);
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        return result;
    }

    internal static bool MatchesPatterns(CleanRoot root, string fileName)
    {
        if (root.Patterns is null || root.Patterns.Count == 0)
            return true;
        foreach (var pattern in root.Patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Re-checks one file and deletes it. The path checks (inside the root, not excluded, pattern) are done on the
    /// path; type, age and the real location (no junction/symlink on the way) are checked through the handle
    /// that deletes the file, so they cannot change in between. Read-only files are deleted too. Never throws.
    /// </summary>
    /// <param name="rootReal">The real path of <paramref name="rootPath"/> (<see cref="SafeDelete.GetRealFolderPath"/>).</param>
    internal static DeleteOutcome TryDeleteFile(string path, string rootPath, string rootReal, CleanRoot root,
        CleanerRules rules, DateTime? cutoff, out long size, out string? error)
    {
        size = 0;
        error = null;
        if (!SafePathTools.IsStrictlyUnder(path, rootPath))
            return DeleteOutcome.Skipped;
        if (!root.Recursive && !string.Equals(Path.GetDirectoryName(path), rootPath, StringComparison.OrdinalIgnoreCase))
            return DeleteOutcome.Skipped;
        if (rules.IsExcluded(path) || !MatchesPatterns(root, Path.GetFileName(path)))
            return DeleteOutcome.Skipped;

        var expectedReal = rootReal.TrimEnd('\\') + path[rootPath.Length..];
        return SafeDelete.Delete(path, expectedReal, isDirectory: false,
            info => cutoff is null || info.NewestUtc <= cutoff.Value, out size, out error);
    }

    private static void Record(CategoryCleanResult result, DeleteOutcome outcome, long size, string? error)
    {
        switch (outcome)
        {
            case DeleteOutcome.Deleted:
                result.DeletedFiles++;
                result.FreedBytes += size;
                break;
            case DeleteOutcome.Locked:
                result.LockedFiles++;
                break;
            case DeleteOutcome.Denied:
                result.DeniedFiles++;
                break;
        }
        if (error is not null && result.Errors.Count < 50)
            result.Errors.Add(error);
    }

    private static void CleanSingleFile(CleanRoot root, string filePath, CleanerRules rules, DateTime? cutoff,
        CategoryCleanResult result)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is null || !IsDirectoryRealFolder(dir) || rules.IsExcluded(filePath))
            return;
        var dirReal = SafeDelete.GetRealFolderPath(dir);
        if (dirReal is null)
            return;
        var expectedReal = dirReal.TrimEnd('\\') + "\\" + Path.GetFileName(filePath);
        var outcome = SafeDelete.Delete(filePath, expectedReal, isDirectory: false,
            info => cutoff is null || info.NewestUtc <= cutoff.Value, out var size, out var error);
        Record(result, outcome, size, error);
    }

    private static IEnumerable<WalkedEntry> EnumerateCandidates(CleanRoot root, string rootPath, CleanerRules rules,
        DateTime? cutoff, WalkStats stats, Action<string>? onDirectory, CancellationToken ct)
    {
        if (root.IsSingleFile)
        {
            var single = ReadSingleFile(rootPath, stats);
            if (single is null)
                yield break;
            if (cutoff is not null && single.Value.NewestUtc > cutoff.Value)
            {
                stats.TooYoung++;
                yield break;
            }
            yield return single.Value;
            yield break;
        }

        var options = new WalkOptions
        {
            Recursive = root.Recursive,
            IsExcluded = rules.IsExcluded,
            OnDirectory = onDirectory,
        };
        foreach (var entry in SafeFileWalker.EnumerateFiles(rootPath, options, stats, ct))
        {
            if (!MatchesPatterns(root, entry.Name))
                continue;
            if (cutoff is not null && entry.NewestUtc > cutoff.Value)
            {
                stats.TooYoung++;
                continue;
            }
            yield return entry;
        }
    }

    private static WalkedEntry? ReadSingleFile(string path, WalkStats stats)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                stats.ReparsePointsSkipped++;
                return null;
            }
            return new WalkedEntry(info.FullName, info.Name, info.Length, info.Attributes, info.CreationTimeUtc,
                info.LastWriteTimeUtc);
        }
        catch (UnauthorizedAccessException)
        {
            stats.DeniedDirectories++;
            return null;
        }
        catch (Exception ex) when (ex is IOException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes sub-folders that are empty now, deepest first. The root itself is never removed. Like files, each
    /// folder is removed through its own handle after confirming its real location and age; Windows refuses to
    /// delete a folder that is not empty, so nothing inside can be lost.
    /// </summary>
    private static void RemoveEmptyDirectories(string rootPath, string rootReal, List<string> directories,
        CleanerRules rules, DateTime? cutoff, CategoryCleanResult result, CancellationToken ct)
    {
        // A child path is always longer than its parent, so this order removes children first.
        foreach (var dir in directories.OrderByDescending(d => d.Length))
        {
            if (ct.IsCancellationRequested)
                break;
            if (!SafePathTools.IsStrictlyUnder(dir, rootPath) || rules.IsExcluded(dir))
                continue;
            var expectedReal = rootReal.TrimEnd('\\') + dir[rootPath.Length..];
            var outcome = SafeDelete.Delete(dir, expectedReal, isDirectory: true,
                info => cutoff is null || info.CreationUtc <= cutoff.Value, out _, out _);
            if (outcome == DeleteOutcome.Deleted)
                result.DeletedDirectories++;
        }
    }

    /// <summary>True if no folder between <paramref name="dir"/> and the root (inclusive) is a junction/symlink.</summary>
    internal static bool IsChainFreeOfReparsePoints(string dir, string rootPath)
    {
        var current = dir;
        while (true)
        {
            if (!IsDirectoryRealFolder(current))
                return false;
            if (string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!SafePathTools.IsStrictlyUnder(current, rootPath))
                return false;
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                return false;
            current = parent;
        }
    }

    private static bool IsDirectoryRealFolder(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
