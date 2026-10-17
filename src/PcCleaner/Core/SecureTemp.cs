using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PcCleaner.Core;

/// <summary>
/// Temporary files that an elevated program hands to another elevated program (a PowerShell script, a .reg file).
/// <para>
/// The app's own data folder (%LOCALAPPDATA%) is writable by every non-elevated process of the same user. A file the
/// elevated app writes there and then passes to powershell.exe / reg.exe could be swapped in between by such a process,
/// and its contents would run as administrator. So, when elevated, these files go to %WINDIR%\Temp (users can create
/// files there but cannot rename or delete the folder, and cannot open other accounts' files) and are created with a
/// protected DACL that grants access only to SYSTEM and Administrators.
/// </para>
/// </summary>
internal static class SecureTemp
{
    /// <summary>%WINDIR%\Temp — the folder for files of the elevated app.</summary>
    public static string ElevatedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");

    /// <summary>
    /// Creates a new file "&lt;prefix&gt;-&lt;guid&gt;&lt;extension&gt;" in <paramref name="directory"/> with the admin-only DACL
    /// (set atomically at creation) and writes <paramref name="content"/>. Throws IOException/UnauthorizedAccessException.
    /// </summary>
    public static string Create(string directory, string prefix, string extension, byte[] content)
    {
        var path = Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}{extension}");
        using (var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize,
                   FileShare.None, 4096, FileOptions.None, AdminOnlySecurity()))
        {
            stream.Write(content, 0, content.Length);
        }
        return path;
    }

    /// <summary>
    /// SYSTEM and Administrators: full control; the owner (if it is the user, by policy): read only, so it cannot
    /// rewrite the DACL either. Inherited entries of the folder are not applied.
    /// </summary>
    internal static FileSecurity AdminOnlySecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        // "OWNER RIGHTS": replaces the implicit READ_CONTROL | WRITE_DAC of the owner.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
            FileSystemRights.Read, AccessControlType.Allow));
        return security;
    }

    public static void TryDelete(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
