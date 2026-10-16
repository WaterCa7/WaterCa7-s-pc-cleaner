using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

/// <summary>Measures a folder without following junctions/symlinks. Read-only, never throws.</summary>
internal static class FolderSize
{
    /// <summary>
    /// Total size of files under <paramref name="folder"/>. Reparse points are skipped (not followed, not counted).
    /// Returns null if the folder does not exist or could not be read at all. Stops after <paramref name="maxEntries"/>.
    /// Sub-folders listed in <paramref name="exclude"/> (install folders of other programs) are not counted.
    /// </summary>
    public static long? Measure(string folder, CancellationToken ct, int maxEntries = 400_000, ISet<string>? exclude = null)
    {
        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(folder);
            if (!root.Exists || FileTools.IsReparsePoint(root))
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }

        long total = 0;
        var entries = 0;
        var readAny = false;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var info in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (++entries > maxEntries)
                        return total;
                    if (FileTools.IsReparsePoint(info))
                        continue;
                    if (info is FileInfo f)
                        total += f.Length;
                    else if (info is DirectoryInfo d && (exclude is null || !exclude.Contains(d.FullName.TrimEnd(Path.DirectorySeparatorChar))))
                        stack.Push(d);
                }
                readAny = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // skip unreadable folders
            }
        }
        return readAny ? total : null;
    }
}
