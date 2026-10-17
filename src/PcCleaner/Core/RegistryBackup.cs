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
    /// Under load reg.exe now and then fails to write its file ("Не удается выполнить запись в файл"), so a failed export
    /// is tried again. A timed-out one is not: retrying it would only keep the user waiting.
    /// </summary>
    internal const int ExportAttempts = 3;

    /// <summary>
    /// Exports a key, e.g. @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run".
    /// Returns the backup file path, or null if the export failed.
    /// </summary>
    public static Task<string?> ExportAsync(string fullKeyPath, string? reason = null, CancellationToken ct = default)
        => ExportAsync(fullKeyPath, reason,
            (file, token) => ProcessRunner.RunAsync("reg.exe", $"export \"{fullKeyPath}\" \"{file}\" /y", TimeSpan.FromSeconds(30), token),
            ct);

    /// <summary><see cref="ExportAsync(string, string?, CancellationToken)"/> with the reg.exe run (into the given file) replaceable for tests.</summary>
    internal static async Task<string?> ExportAsync(string fullKeyPath, string? reason,
        Func<string, CancellationToken, Task<ProcessResult>> runExport, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.BackupsDir);
        var safeName = Regex.Replace(fullKeyPath, @"[^\w\-]+", "_");
        if (safeName.Length > 80)
            safeName = safeName[^80..];
        // A short random suffix keeps names unique when two backups happen in the same millisecond.
        var file = Path.Combine(AppPaths.BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}_{safeName}.reg");

        using (var existing = OpenKey(fullKeyPath))
        {
            if (existing is null)
            {
                Log.Warn($"Резервная копия не создана: раздел реестра не найден или нет доступа — {fullKeyPath}");
                return null;
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            var result = await runExport(file, ct);
            if (result.Success && File.Exists(file))
                break;
            if (result.TimedOut || attempt == ExportAttempts)
            {
                Log.Warn($"Не удалось сохранить резервную копию {fullKeyPath}: {result.StdErr.Trim()}");
                return null;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
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

    /// <summary>
    /// Imports a .reg backup (HKLM keys need admin) exactly as the file is at the moment reg.exe reads it.
    /// Files in the Backups folder can be rewritten by any process of the user, so a caller that has inspected the file
    /// or shown its contents in a confirmation must not import that path afterwards: use
    /// <see cref="ImportCheckedAsync"/>, which imports the very snapshot it verified, or pass a private copy that the
    /// caller keeps locked against writes until the import ends.
    /// </summary>
    public static async Task<bool> ImportAsync(string regFile, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("reg.exe", $"import \"{regFile}\"", TimeSpan.FromSeconds(60), ct);
        if (result.Success)
        {
            Log.Action($"Восстановлено из резервной копии: {Path.GetFileName(regFile)}");
            return true;
        }
        Log.Warn($"Не удалось восстановить {Path.GetFileName(regFile)}: {result.StdErr.Trim()}");
        return false;
    }

    /// <summary>
    /// Imports a .reg file exactly as it was checked. The file is read once into a private snapshot
    /// (elevated: %WINDIR%\Temp with an admin-only DACL, see <see cref="SecureTemp"/>), <paramref name="verify"/> inspects
    /// that snapshot (its path is passed), and only that snapshot is imported. A non-elevated process can rewrite files in
    /// the user-writable Backups folder at any moment, so checking the original file and then importing it would let the
    /// contents change between the check (and the user's confirmation) and the elevated import.
    /// Returns false if the snapshot could not be made, <paramref name="verify"/> refused it, or reg.exe failed.
    /// </summary>
    public static async Task<bool> ImportCheckedAsync(string regFile, Func<string, bool> verify, CancellationToken ct = default)
    {
        string? snapshot = null;
        try
        {
            try
            {
                var content = await File.ReadAllBytesAsync(regFile, ct);
                var dir = Admin.IsElevated ? SecureTemp.ElevatedDir : AppPaths.EnsureDir(Path.Combine(AppPaths.DataDir, "Temp"));
                snapshot = SecureTemp.Create(dir, "WaterCa7sPcCleaner-reg", ".reg", content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Не удалось восстановить {Path.GetFileName(regFile)}: не удалось прочитать файл — {ErrorText.For(ex)}");
                return false;
            }

            if (!verify(snapshot))
            {
                Log.Warn($"Восстановление из {Path.GetFileName(regFile)} отменено: файл не прошёл повторную проверку");
                return false;
            }

            var result = await ProcessRunner.RunAsync("reg.exe", $"import \"{snapshot}\"", TimeSpan.FromSeconds(60), ct);
            if (result.Success)
            {
                Log.Action($"Восстановлено из резервной копии: {Path.GetFileName(regFile)}");
                return true;
            }
            Log.Warn($"Не удалось восстановить {Path.GetFileName(regFile)}: {result.StdErr.Trim()}");
            return false;
        }
        finally
        {
            SecureTemp.TryDelete(snapshot);
        }
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
            Log.Warn($"Нет доступа к {fullKeyPath}: {ErrorText.For(ex)}");
            return null;
        }
    }
}
