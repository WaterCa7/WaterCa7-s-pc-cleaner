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

    internal enum DeleteOutcome
    {
        Deleted,
        Locked,
        Denied,
        Gone,
        Skipped,
    }

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

                if (root.IsSingleFile)
                {
                    CleanSingleFile(root, rootPath, rules, cutoff, result);
                    continue;
                }

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
                    Record(result, TryDeleteFile(file.FullPath, rootPath, root, rules, cutoff, out var size, out var error),
                        size, error);
                }

                if (category.RemoveEmptyDirectories && root.Recursive)
                    RemoveEmptyDirectories(rootPath, directories, rules, cutoff, result, ct);
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

    /// <summary>Re-checks one file and deletes it. Never throws.</summary>
    internal static DeleteOutcome TryDeleteFile(string path, string rootPath, CleanRoot root, CleanerRules rules,
        DateTime? cutoff, out long size, out string? error)
    {
        size = 0;
        error = null;
        FileInfo info;
        FileAttributes attributes;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return DeleteOutcome.Gone;
            attributes = info.Attributes;
            size = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return DeleteOutcome.Gone;
        }

        if (!SafePathTools.IsStrictlyUnder(path, rootPath))
            return DeleteOutcome.Skipped;
        if (!root.Recursive && !string.Equals(Path.GetDirectoryName(path), rootPath, StringComparison.OrdinalIgnoreCase))
            return DeleteOutcome.Skipped;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            return DeleteOutcome.Skipped;
        if (rules.IsExcluded(path) || !MatchesPatterns(root, info.Name))
            return DeleteOutcome.Skipped;
        if (cutoff is not null && Newest(info) > cutoff.Value)
            return DeleteOutcome.Skipped;

        return DeleteFile(path, attributes, out error);
    }

    private static DeleteOutcome DeleteFile(string path, FileAttributes attributes, out string? error)
    {
        error = null;
        try
        {
            File.Delete(path);
            return DeleteOutcome.Deleted;
        }
        catch (UnauthorizedAccessException) when ((attributes & FileAttributes.ReadOnly) != 0)
        {
            // Read-only temp files are common (unpacked installers): clear the flag and retry once.
            try
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return DeleteOutcome.Deleted;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.SetAttributes(path, attributes);
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    // file is gone or still locked
                }
                return ex is UnauthorizedAccessException ? DeleteOutcome.Denied : DeleteOutcome.Locked;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return DeleteOutcome.Denied;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DeleteOutcome.Gone;
        }
        catch (PathTooLongException ex)
        {
            error = $"{path}: {ex.Message}";
            return DeleteOutcome.Skipped;
        }
        catch (IOException)
        {
            return DeleteOutcome.Locked; // in use by another program
        }
        catch (SecurityException)
        {
            return DeleteOutcome.Denied;
        }
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
        if (dir is null || !IsDirectoryRealFolder(dir))
            return;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return;
            if (cutoff is not null && Newest(info) > cutoff.Value)
                return;
            var size = info.Length;
            Record(result, DeleteFile(filePath, info.Attributes, out var error), size, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            result.DeniedFiles++;
        }
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

    /// <summary>Removes sub-folders that are empty now, deepest first. The root itself is never removed.</summary>
    private static void RemoveEmptyDirectories(string rootPath, List<string> directories, CleanerRules rules,
        DateTime? cutoff, CategoryCleanResult result, CancellationToken ct)
    {
        // A child path is always longer than its parent, so this order removes children first.
        foreach (var dir in directories.OrderByDescending(d => d.Length))
        {
            if (ct.IsCancellationRequested)
                break;
            if (!SafePathTools.IsStrictlyUnder(dir, rootPath) || rules.IsExcluded(dir))
                continue;
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (cutoff is not null && info.CreationTimeUtc > cutoff.Value)
                    continue;
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    continue;
                Directory.Delete(dir, recursive: false);
                result.DeletedDirectories++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // in use or not allowed: keep it
            }
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

    private static DateTime Newest(FileInfo info)
    {
        var created = info.CreationTimeUtc;
        var written = info.LastWriteTimeUtc;
        return created > written ? created : written;
    }
}
