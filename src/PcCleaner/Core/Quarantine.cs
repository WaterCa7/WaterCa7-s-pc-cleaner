using System.IO;
using System.Text.Json;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

public sealed record QuarantineItem(string Id, string OriginalPath, string StoredPath, DateTime Time, string Reason)
{
    public string FileName => Path.GetFileName(OriginalPath);
}

/// <summary>
/// Moves suspicious files into %LOCALAPPDATA%\WaterCa7sPcCleaner\Quarantine (renamed, so they can't run)
/// instead of deleting them. Items can be restored from the Settings page.
/// <para>
/// index.json (the original path of every stored file) is written atomically and the previous version is kept as
/// index.json.bak. If the index cannot be read, changes are refused rather than overwriting it — otherwise the
/// records of files already in quarantine would be lost and they could no longer be restored. If writing the index
/// fails right after a file was moved in, the file is moved back.
/// </para>
/// </summary>
public static class Quarantine
{
    private static string IndexFile => Path.Combine(AppPaths.QuarantineDir, "index.json");
    private static string BackupIndexFile => IndexFile + ".bak";
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = true };

    public static IReadOnlyList<QuarantineItem> List()
    {
        lock (Lock)
        {
            try
            {
                return ReadIndex(forUpdate: false);
            }
            catch (Exception ex)
            {
                Log.Warn(T.F("Не удалось прочитать список карантина: {0}", ErrorText.For(ex)));
                return new List<QuarantineItem>();
            }
        }
    }

    /// <summary>Moves the file to quarantine. Caller must have confirmed with the user. Throws on failure.</summary>
    public static QuarantineItem Add(string path, string reason)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException(T._("Файл не найден"), full);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Suspicion.IsUnder(full, windows))
            throw ErrorText.Own(new InvalidOperationException(T._("Системные файлы Windows нельзя помещать в карантин.")));

        lock (Lock)
        {
            // Read the index BEFORE moving anything: if it is unreadable, nothing is changed.
            var list = ReadIndex(forUpdate: true);
            Directory.CreateDirectory(AppPaths.QuarantineDir);
            var id = Guid.NewGuid().ToString("N");
            var stored = Path.Combine(AppPaths.QuarantineDir, id + ".quarantine");
            File.Move(full, stored);
            var item = new QuarantineItem(id, full, stored, DateTime.Now, reason);
            list.Add(item);
            try
            {
                SaveIndex(list);
            }
            catch (Exception ex)
            {
                // Without its record the file could never be restored: put it back where it was.
                try
                {
                    File.Move(stored, full);
                    Log.Warn(T.F("Карантин: не удалось записать список карантина ({0}) — файл возвращён на место: {1}", ErrorText.For(ex), full));
                }
                catch (Exception back)
                {
                    Log.Error(T.F("Карантин: не удалось записать список карантина и вернуть файл на место. Файл {0} сейчас хранится как {1}",
                        full, stored), back);
                }
                throw ErrorText.Own(new InvalidOperationException(
                    T.F("Не удалось записать список карантина: {0}. Файл оставлен на месте.", ErrorText.For(ex)), ex));
            }
            Log.Action(T.F("Файл помещён в карантин: {0}", full));
            return item;
        }
    }

    public static void Restore(QuarantineItem item) => Restore(item, item.OriginalPath);

    /// <summary>Restores the file to <paramref name="targetPath"/> (e.g. a new name when the original path is taken).</summary>
    public static void Restore(QuarantineItem item, string targetPath)
    {
        lock (Lock)
        {
            var list = ReadIndex(forUpdate: true);
            var target = Path.GetFullPath(targetPath);
            if (File.Exists(target))
                throw new IOException(T.F("По этому пути уже есть файл: {0}", target));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(item.StoredPath, target);
            SaveIndex(list.Where(i => i.Id != item.Id).ToList());
            Log.Action(T.F("Файл восстановлен из карантина: {0}", target));
        }
    }

    /// <summary>Permanently deletes a quarantined file. Caller must have confirmed with the user.</summary>
    public static void Delete(QuarantineItem item)
    {
        lock (Lock)
        {
            var list = ReadIndex(forUpdate: true);
            if (File.Exists(item.StoredPath))
                File.Delete(item.StoredPath);
            SaveIndex(list.Where(i => i.Id != item.Id).ToList());
            Log.Action(T.F("Файл из карантина удалён навсегда: {0}", item.OriginalPath));
        }
    }

    /// <summary>
    /// Reads index.json. If it cannot be read: for display, the previous version (index.json.bak) is shown when
    /// possible; for a change (<paramref name="forUpdate"/>) InvalidOperationException is thrown and the index is left
    /// untouched — rewriting it would drop the records it still holds (even the .bak may miss the latest ones).
    /// </summary>
    private static List<QuarantineItem> ReadIndex(bool forUpdate)
    {
        if (!File.Exists(IndexFile))
            return new List<QuarantineItem>();

        try
        {
            return Read(IndexFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (forUpdate)
            {
                Log.Warn(T.F("Список карантина не читается ({0}) — изменения в карантине отменены", ErrorText.For(ex)));
                throw ErrorText.Own(new InvalidOperationException(
                    T._("Список карантина (index.json в папке карантина) не читается, поэтому ничего не изменено: иначе пропали бы записи о файлах, которые уже лежат в карантине, и их нельзя было бы восстановить."), ex));
            }
            if (TryRead(BackupIndexFile) is { } previous)
            {
                Log.Warn(T.F("Список карантина не читается ({0}) — показана его предыдущая копия", ErrorText.For(ex)));
                return previous;
            }
            throw;
        }
    }

    private static List<QuarantineItem> Read(string file)
        => (JsonSerializer.Deserialize<List<QuarantineItem>>(ReadText(file)) ?? new List<QuarantineItem>())
            .Where(i => i is not null)
            .ToList();

    /// <summary>An antivirus may hold the file open for a moment: retry briefly before calling it unreadable.</summary>
    private static string ReadText(string file)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (IOException) when (attempt < 3 && File.Exists(file))
            {
                Thread.Sleep(100);
            }
        }
    }

    private static List<QuarantineItem>? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? Read(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Atomic: writes index.json.tmp, then replaces index.json keeping the previous version as index.json.bak.</summary>
    private static void SaveIndex(List<QuarantineItem> items)
    {
        Directory.CreateDirectory(AppPaths.QuarantineDir);
        var tmp = IndexFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items, IndexJson));
        if (!File.Exists(IndexFile))
        {
            File.Move(tmp, IndexFile);
            return;
        }
        try
        {
            File.Replace(tmp, IndexFile, BackupIndexFile, ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            // e.g. the .bak is locked by an antivirus scan: still replace the index atomically, just without a copy.
            File.Move(tmp, IndexFile, overwrite: true);
        }
    }
}
