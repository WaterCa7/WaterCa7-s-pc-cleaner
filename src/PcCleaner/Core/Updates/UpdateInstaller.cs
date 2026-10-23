using System.IO;
using System.Security.Cryptography;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>
/// Gets a downloaded setup ready to run. The staged file lives in a user-writable folder, so it is opened with writes denied and
/// the very bytes that are copied into the program's own Updates folder are the bytes that are hashed. The copy is then held open
/// (<see cref="PreparedSetup"/>) with writes, renames and deletes denied until the process has been created from it, and its bytes
/// are checked again through that handle. That only stops direct changes of the file for that moment: the real protection is that
/// <see cref="UpdateService"/> installs automatically only from Program Files, where only administrators can change the folder
/// (anything else could swap the Updates folder or the file once the handle is closed, while the setup still runs as administrator).
/// </summary>
public static class UpdateInstaller
{
    /// <summary>The setup's switches for an update: no windows, no questions, no reboot, and where we came from.</summary>
    public static List<string> SetupArgs(Version from)
        => new() { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATEDFROM=" + from.ToString(3) };

    /// <summary>
    /// Copies the staged setup into <paramref name="setupDir"/> and returns the copy, held open against writes, renames and deletes.
    /// Dispose the result after the process has been created from it. Nothing is left behind on failure.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The program folder is not writable.</exception>
    /// <exception cref="UpdateException">The staged file does not match update.json.</exception>
    /// <exception cref="IOException">A file is locked.</exception>
    public static PreparedSetup PrepareSetup(StagedUpdate staged, string setupDir)
    {
        Directory.CreateDirectory(setupDir);
        var target = Path.Combine(setupDir, staged.Manifest.Download.Name);
        if (File.Exists(target))
            File.Delete(target);
        try
        {
            CopyVerified(staged, target);
            // Read-only handle that others may read (process creation needs that) but not write, rename or delete. Whatever
            // happened to the file between the copy and this open is caught by hashing it through the handle.
            var lease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                VerifyStream(lease, staged.Manifest.Download);
                return new PreparedSetup(target, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>Run by the new version: removes the setup folder, retrying while the setup that started it exits.</summary>
    public static async Task CleanupSetupAsync(string setupDir, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Directory.Exists(setupDir))
        {
            try
            {
                Directory.Delete(setupDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Log.Warn(T.F("Обновление: не удалось удалить {0}: {1}", setupDir, ErrorText.For(ex)), ex);
                    return;
                }
            }
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
        using var source = new FileStream(staged.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        ReadAndCheck(source, target, staged.Manifest.Download);
        target.Flush(flushToDisk: true);
    }

    /// <summary>Hashes the stream from its start and compares size and SHA-256 with update.json.</summary>
    private static void VerifyStream(FileStream stream, UpdateFile expected)
    {
        stream.Position = 0;
        ReadAndCheck(stream, null, expected);
        stream.Position = 0;
    }

    /// <summary>Reads <paramref name="source"/> to its end (copying to <paramref name="copy"/> when given) and checks size and SHA-256.</summary>
    private static void ReadAndCheck(Stream source, Stream? copy, UpdateFile expected)
    {
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
            copy?.Write(buffer, 0, read);
        }
        if (total != expected.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expected.Sha256)
            throw new UpdateException(T._("подготовленный файл не совпадает с update.json"));
    }
}

/// <summary>
/// The verified copy of the setup, held open with writes and deletes denied (but readable, so a process can be created from it).
/// Dispose it once the setup has been started.
/// </summary>
public sealed class PreparedSetup : IDisposable
{
    private readonly FileStream _lease;

    internal PreparedSetup(string path, FileStream lease)
    {
        Path = path;
        _lease = lease;
    }

    public string Path { get; }

    public void Dispose() => _lease.Dispose();
}
