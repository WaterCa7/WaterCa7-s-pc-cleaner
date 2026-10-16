using System.IO;
using System.Text.Json;

namespace PcCleaner.Core;

public sealed record QuarantineItem(string Id, string OriginalPath, string StoredPath, DateTime Time, string Reason)
{
    public string FileName => Path.GetFileName(OriginalPath);
}

/// <summary>
/// Moves suspicious files into %LOCALAPPDATA%\WaterCa7sPcCleaner\Quarantine (renamed, so they can't run)
/// instead of deleting them. Items can be restored from the Settings page.
/// </summary>
public static class Quarantine
{
    private static string IndexFile => Path.Combine(AppPaths.QuarantineDir, "index.json");
    private static readonly object Lock = new();

    public static IReadOnlyList<QuarantineItem> List()
    {
        lock (Lock)
        {
            try
            {
                return File.Exists(IndexFile)
                    ? JsonSerializer.Deserialize<List<QuarantineItem>>(File.ReadAllText(IndexFile)) ?? new()
                    : new List<QuarantineItem>();
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось прочитать список карантина: {ex.Message}");
                return new List<QuarantineItem>();
            }
        }
    }

    /// <summary>Moves the file to quarantine. Caller must have confirmed with the user. Throws on failure.</summary>
    public static QuarantineItem Add(string path, string reason)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Файл не найден", full);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Suspicion.IsUnder(full, windows))
            throw new InvalidOperationException("Системные файлы Windows нельзя помещать в карантин.");

        lock (Lock)
        {
            Directory.CreateDirectory(AppPaths.QuarantineDir);
            var id = Guid.NewGuid().ToString("N");
            var stored = Path.Combine(AppPaths.QuarantineDir, id + ".quarantine");
            File.Move(full, stored);
            var item = new QuarantineItem(id, full, stored, DateTime.Now, reason);
            var list = List().ToList();
            list.Add(item);
            SaveIndex(list);
            Log.Action($"Файл помещён в карантин: {full}");
            return item;
        }
    }

    public static void Restore(QuarantineItem item) => Restore(item, item.OriginalPath);

    /// <summary>Restores the file to <paramref name="targetPath"/> (e.g. a new name when the original path is taken).</summary>
    public static void Restore(QuarantineItem item, string targetPath)
    {
        lock (Lock)
        {
            var target = Path.GetFullPath(targetPath);
            if (File.Exists(target))
                throw new IOException($"По этому пути уже есть файл: {target}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(item.StoredPath, target);
            SaveIndex(List().Where(i => i.Id != item.Id).ToList());
            Log.Action($"Файл восстановлен из карантина: {target}");
        }
    }

    /// <summary>Permanently deletes a quarantined file. Caller must have confirmed with the user.</summary>
    public static void Delete(QuarantineItem item)
    {
        lock (Lock)
        {
            if (File.Exists(item.StoredPath))
                File.Delete(item.StoredPath);
            SaveIndex(List().Where(i => i.Id != item.Id).ToList());
            Log.Action($"Файл из карантина удалён навсегда: {item.OriginalPath}");
        }
    }

    private static void SaveIndex(List<QuarantineItem> items)
    {
        Directory.CreateDirectory(AppPaths.QuarantineDir);
        File.WriteAllText(IndexFile, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    }
}
