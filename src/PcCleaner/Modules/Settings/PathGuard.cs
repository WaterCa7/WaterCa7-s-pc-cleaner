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

    /// <summary>
    /// True only if <paramref name="path"/> is a file directly in <paramref name="folder"/> (not in a subfolder).
    /// The app stores backups and quarantined files only at the top level, so anything deeper — for example behind a
    /// junction planted in the folder — is refused.
    /// </summary>
    public static bool IsDirectChild(string? path, string? folder)
    {
        if (!IsInside(path, folder))
            return false;
        try
        {
            var full = Path.GetFullPath(path!).TrimEnd('\\');
            var parent = Path.GetDirectoryName(full);
            return parent is not null
                   && string.Equals(parent.TrimEnd('\\'), Path.GetFullPath(folder!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return false;
        }
    }
}
