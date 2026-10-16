using System.IO;
using System.IO.Enumeration;

namespace PcCleaner.Modules.Disk;

/// <summary>
/// Read-only recursive file search that never follows junctions / symbolic links and silently skips
/// inaccessible folders.
/// </summary>
internal static class FileFinder
{
    public static List<FoundFile> Find(string root, Func<string, long, bool> filter, int maxDepth, CancellationToken ct)
    {
        var result = new List<FoundFile>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return result;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = Math.Max(0, maxDepth),
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        var enumerable = new FileSystemEnumerable<FoundFile>(root,
            (ref FileSystemEntry e) => new FoundFile(
                e.ToFullPath(),
                e.Length,
                e.CreationTimeUtc.LocalDateTime,
                e.LastWriteTimeUtc.LocalDateTime),
            options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) => !ct.IsCancellationRequested && !IsLink(ref e),
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && !IsCloudOnly(ref e) && filter(e.FileName.ToString(), e.Length),
        };

        try
        {
            foreach (var file in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                result.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // root vanished or became unreadable: return what we have
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static bool IsLink(ref FileSystemEntry e)
        => (e.Attributes & FileAttributes.ReparsePoint) != 0 && ReparsePoints.IsLinkOrUnknown(e.ToFullPath());

    private static bool IsCloudOnly(ref FileSystemEntry e)
        => ((int)e.Attributes & (0x00400000 | 0x00001000)) != 0;
}
