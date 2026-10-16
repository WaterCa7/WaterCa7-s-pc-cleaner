using System.IO;

namespace PcCleaner.Modules.Settings;

/// <summary>Makes sure file operations of this page stay inside the app's own folders.</summary>
internal static class PathGuard
{
    /// <summary>
    /// True only if <paramref name="path"/> resolves (after "..", "/" etc. are normalized) to something strictly inside
    /// <paramref name="folder"/>. "C:\Data\BackupsEvil\x" is NOT inside "C:\Data\Backups".
    /// </summary>
    public static bool IsInside(string? path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder);
            if (!root.EndsWith('\\'))
                root += '\\';
            return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return false;
        }
    }
}
