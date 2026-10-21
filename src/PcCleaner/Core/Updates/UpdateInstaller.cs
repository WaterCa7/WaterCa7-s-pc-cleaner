using System.IO;
using System.Security.Cryptography;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>
/// Puts a staged exe in place of the running one. The staged file lives in a user-writable folder, so it is opened with
/// writes denied and the very bytes that are copied are the bytes that are hashed: nothing can swap the file between the
/// check and the install, even when the program runs as administrator.
/// </summary>
public static class UpdateInstaller
{
    public const string NewSuffix = ".new";
    public const string OldSuffix = ".old";

    /// <summary>
    /// Replaces <paramref name="exePath"/> with the staged file; the current exe becomes "*.old" (Windows allows renaming a
    /// running exe). On any failure the original exe stays in place or is put back.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The program folder is not writable.</exception>
    /// <exception cref="UpdateException">The staged file does not match update.json.</exception>
    /// <exception cref="IOException">A file is locked.</exception>
    public static void Install(StagedUpdate staged, string exePath)
    {
        var newPath = exePath + NewSuffix;
        var oldPath = exePath + OldSuffix;
        var movedAway = false;
        try
        {
            if (File.Exists(newPath))
                File.Delete(newPath);
            CopyVerified(staged, newPath);
            if (!TryDelete(oldPath))
                oldPath = $"{exePath}.{Guid.NewGuid():N}{OldSuffix}";
            File.Move(exePath, oldPath);
            movedAway = true;
            File.Move(newPath, exePath);
        }
        catch
        {
            if (movedAway && !File.Exists(exePath))
                File.Move(oldPath, exePath);
            TryDelete(newPath);
            throw;
        }
    }

    /// <summary>Run by the new version: deletes the previous exe(s) next to it, retrying while the old process exits.</summary>
    public static async Task CleanupAsync(string exePath, TimeSpan timeout, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var pattern = Path.GetFileName(exePath) + "*" + OldSuffix;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            TryDelete(exePath + NewSuffix);
            var left = Directory.EnumerateFiles(dir, pattern).Count(file => !TryDelete(file));
            if (left == 0 || DateTime.UtcNow >= deadline)
                return;
            await Task.Delay(500, ct);
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyVerified(StagedUpdate staged, string newPath)
    {
        var expected = staged.Manifest.Download;
        using var source = new FileStream(staged.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > expected.Size)
                throw new UpdateException(T._("подготовленный файл больше, чем указано в update.json"));
            hash.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
        }
        if (total != expected.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expected.Sha256)
            throw new UpdateException(T._("подготовленный файл не совпадает с update.json"));
        target.Flush(flushToDisk: true);
    }
}
