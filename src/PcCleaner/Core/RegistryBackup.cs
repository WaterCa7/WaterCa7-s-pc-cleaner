using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PcCleaner.Core;

/// <summary>
/// Exports registry keys to .reg files before they are modified, and imports them back.
/// Every registry deletion in the app MUST call <see cref="ExportAsync"/> first and abort if it fails.
/// </summary>
public static class RegistryBackup
{
    /// <summary>
    /// Exports a key, e.g. @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run".
    /// Returns the backup file path, or null if the export failed.
    /// </summary>
    public static async Task<string?> ExportAsync(string fullKeyPath, string? reason = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.BackupsDir);
        var safeName = Regex.Replace(fullKeyPath, @"[^\w\-]+", "_");
        if (safeName.Length > 80)
            safeName = safeName[^80..];
        var file = Path.Combine(AppPaths.BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{safeName}.reg");

        var result = await ProcessRunner.RunAsync("reg.exe", $"export \"{fullKeyPath}\" \"{file}\" /y",
            TimeSpan.FromSeconds(30), ct);
        if (!result.Success || !File.Exists(file))
        {
            Log.Error($"Не удалось сохранить резервную копию {fullKeyPath}: {result.StdErr.Trim()}");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            try
            {
                File.WriteAllText(file + ".txt", reason);
            }
            catch
            {
                // description is optional
            }
        }
        Log.Info($"Резервная копия реестра: {Path.GetFileName(file)}");
        return file;
    }

    /// <summary>Imports a .reg backup (HKLM keys need admin).</summary>
    public static async Task<bool> ImportAsync(string regFile, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("reg.exe", $"import \"{regFile}\"", TimeSpan.FromSeconds(60), ct);
        if (result.Success)
        {
            Log.Action($"Восстановлено из резервной копии: {Path.GetFileName(regFile)}");
            return true;
        }
        Log.Error($"Не удалось восстановить {Path.GetFileName(regFile)}: {result.StdErr.Trim()}");
        return false;
    }

    public static IEnumerable<FileInfo> ListBackups()
    {
        var dir = new DirectoryInfo(AppPaths.BackupsDir);
        return dir.Exists
            ? dir.EnumerateFiles("*.reg").OrderByDescending(f => f.CreationTime)
            : Enumerable.Empty<FileInfo>();
    }

    /// <summary>Opens "HKLM\..." / "HKEY_CURRENT_USER\..." as a RegistryKey (view: 64-bit).</summary>
    public static RegistryKey? OpenKey(string fullKeyPath, bool writable = false, RegistryView view = RegistryView.Registry64)
    {
        var idx = fullKeyPath.IndexOf('\\');
        var hiveName = idx < 0 ? fullKeyPath : fullKeyPath[..idx];
        var sub = idx < 0 ? "" : fullKeyPath[(idx + 1)..];
        RegistryHive? hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => null,
        };
        if (hive is null)
            return null;
        try
        {
            var root = RegistryKey.OpenBaseKey(hive.Value, view);
            return sub.Length == 0 ? root : root.OpenSubKey(sub, writable);
        }
        catch (Exception ex)
        {
            Log.Warn($"Нет доступа к {fullKeyPath}: {ex.Message}");
            return null;
        }
    }
}
