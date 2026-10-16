using System.IO;
using System.IO.Enumeration;
using System.Security;

namespace PcCleaner.Modules.Cleaner;

/// <summary>A file or folder entry read from disk by <see cref="SafeFileWalker"/>.</summary>
internal readonly record struct WalkedEntry(
    string FullPath,
    string Name,
    long Length,
    FileAttributes Attributes,
    DateTime CreationUtc,
    DateTime LastWriteUtc)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>The newest of creation/modification time (copied installer files keep an old write time).</summary>
    public DateTime NewestUtc => CreationUtc > LastWriteUtc ? CreationUtc : LastWriteUtc;
}

internal sealed class WalkOptions
{
    public bool Recursive { get; init; } = true;

    /// <summary>Paths for which this returns true are neither returned nor descended into.</summary>
    public Func<string, bool>? IsExcluded { get; init; }

    /// <summary>Called for every sub-folder that will be walked (used to remove empty folders later).</summary>
    public Action<string>? OnDirectory { get; init; }
}

internal sealed class WalkStats
{
    public int DeniedDirectories { get; set; }

    public long ReparsePointsSkipped { get; set; }

    public long Excluded { get; set; }

    /// <summary>Files skipped by the caller because they are younger than the minimum age.</summary>
    public long TooYoung { get; set; }

    /// <summary>True if the root itself was a junction/symlink and therefore not walked.</summary>
    public bool RootIsReparsePoint { get; set; }
}

/// <summary>
/// Enumerates files under a root without ever following or descending into reparse points
/// (junctions, symlinks, mount points). Inaccessible folders are skipped and counted.
/// </summary>
internal static class SafeFileWalker
{
    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>Lazily yields every regular file (not reparse point, not excluded) under <paramref name="root"/>.</summary>
    public static IEnumerable<WalkedEntry> EnumerateFiles(string root, WalkOptions options, WalkStats? stats,
        CancellationToken ct)
    {
        stats ??= new WalkStats();
        if (!IsWalkableRoot(root, stats))
            yield break;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            var entries = ReadDirectory(dir, stats);
            foreach (var entry in entries)
            {
                if (entry.IsReparsePoint)
                {
                    stats.ReparsePointsSkipped++;
                    continue;
                }
                if (options.IsExcluded?.Invoke(entry.FullPath) == true)
                {
                    stats.Excluded++;
                    continue;
                }
                if (entry.IsDirectory)
                {
                    if (options.Recursive)
                    {
                        options.OnDirectory?.Invoke(entry.FullPath);
                        pending.Push(entry.FullPath);
                    }
                    continue;
                }
                yield return entry;
            }
        }
    }

    /// <summary>True if the root exists, is a real folder and not a junction/symlink.</summary>
    public static bool IsWalkableRoot(string root, WalkStats? stats = null)
    {
        try
        {
            var info = new DirectoryInfo(root);
            if (!info.Exists)
                return false;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (stats is not null)
                {
                    stats.RootIsReparsePoint = true;
                    stats.ReparsePointsSkipped++; // shown in the details ("junction/symlink skipped")
                }
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads one folder; errors (no access, folder vanished) end the listing of that folder only.</summary>
    private static List<WalkedEntry> ReadDirectory(string dir, WalkStats stats)
    {
        var list = new List<WalkedEntry>();
        try
        {
            var enumerable = new FileSystemEnumerable<WalkedEntry>(dir,
                (ref FileSystemEntry e) => new WalkedEntry(
                    e.ToFullPath(),
                    e.FileName.ToString(),
                    e.IsDirectory ? 0 : e.Length,
                    e.Attributes,
                    e.CreationTimeUtc.UtcDateTime,
                    e.LastWriteTimeUtc.UtcDateTime),
                DirectoryOptions);
            foreach (var entry in enumerable)
                list.Add(entry);
        }
        catch (UnauthorizedAccessException)
        {
            stats.DeniedDirectories++;
        }
        catch (SecurityException)
        {
            stats.DeniedDirectories++;
        }
        catch (IOException)
        {
            // Folder disappeared, path too long, device not ready: skip it.
        }
        return list;
    }
}
