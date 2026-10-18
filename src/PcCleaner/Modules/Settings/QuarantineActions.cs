using System.IO;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

/// <summary>Quarantine item prepared for display.</summary>
public sealed class QuarantineRow
{
    public QuarantineRow(QuarantineItem item, long sizeBytes, bool storedExists)
    {
        Item = item;
        SizeBytes = sizeBytes;
        StoredExists = storedExists;
    }

    public QuarantineItem Item { get; }

    public string FileName => Item.FileName;

    public string OriginalPath => Item.OriginalPath;

    public string Folder => Path.GetDirectoryName(Item.OriginalPath) ?? Item.OriginalPath;

    public DateTime Time => Item.Time;

    public string TimeText => Format.DateTime(Item.Time);

    public string Reason => string.IsNullOrWhiteSpace(Item.Reason) ? T._("Причина не указана") : Item.Reason.Trim();

    public long SizeBytes { get; }

    public bool StoredExists { get; }

    public string SizeText => StoredExists ? Format.Bytes(SizeBytes) : T._("нет файла");

    public string ToolTipText =>
        T.F("{0}\n\nПричина: {1}\nПомещён в карантин: {2}", Item.OriginalPath, Reason, TimeText) +
        (StoredExists ? "" : "\n\n" + T._("Файл в папке карантина не найден — возможно, его удалили вручную."));
}

internal enum QuarantineRestoreStatus
{
    Restored,
    RestoredAs,
    Conflict,
    MissingStoredFile,
    AccessDenied,
    Failed,
}

internal sealed record QuarantineRestoreResult(QuarantineRestoreStatus Status, string? TargetPath, string Message)
{
    public bool Success => Status is QuarantineRestoreStatus.Restored or QuarantineRestoreStatus.RestoredAs;
}

/// <summary>Restore/delete wrappers around Core.Quarantine with friendly results and path guards.</summary>
internal static class QuarantineActions
{
    public static List<QuarantineRow> LoadRows()
    {
        var rows = new List<QuarantineRow>();
        // A hand-edited or damaged index.json may contain "null" entries.
        foreach (var item in Quarantine.List().Where(i => i is not null))
        {
            long size = 0;
            var exists = false;
            try
            {
                var info = new FileInfo(item.StoredPath);
                exists = info.Exists;
                if (exists)
                    size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                Log.Warn(T.F("Карантин: не удалось прочитать {0}: {1}", item.StoredPath, ex.Message));
            }
            rows.Add(new QuarantineRow(item, size, exists));
        }
        return rows.OrderByDescending(r => r.Time).ToList();
    }

    /// <summary>"C:\x\tool.exe" → "C:\x\tool (восстановлен).exe", then "(восстановлен 2)", ...</summary>
    public static string AlternativePath(string originalPath, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var dir = Path.GetDirectoryName(originalPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        for (var i = 1; i < 1000; i++)
        {
            var suffix = i == 1 ? " " + T._("(восстановлен)") : " " + T.F("(восстановлен {0})", i);
            var candidate = Path.Combine(dir, name + suffix + ext);
            if (!exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// Moves the file back. If something already exists at the original path, returns Conflict
    /// unless <paramref name="renameIfExists"/> is set — then the file is restored next to it under a new name
    /// and the existing file is left untouched.
    /// </summary>
    public static QuarantineRestoreResult Restore(QuarantineItem item, bool renameIfExists)
    {
        if (!IsOwnStoredFile(item))
        {
            Log.Warn(T.F("Карантин: запись указывает на файл вне папки карантина ({0}) — пропущено", item.StoredPath));
            return new(QuarantineRestoreStatus.Failed, null,
                T._("Запись карантина повреждена: файл находится вне папки карантина."));
        }
        if (string.IsNullOrWhiteSpace(item.OriginalPath) || !Path.IsPathFullyQualified(item.OriginalPath)
                                                         || Path.GetFileName(item.OriginalPath).Length == 0)
        {
            Log.Warn(T.F("Карантин: в записи нет полного исходного пути ({0}) — пропущено", item.OriginalPath));
            return new(QuarantineRestoreStatus.Failed, null,
                T._("Запись карантина повреждена: не указан полный исходный путь файла."));
        }
        if (!File.Exists(item.StoredPath))
            return new(QuarantineRestoreStatus.MissingStoredFile, null,
                T._("Файл в папке карантина не найден — возможно, его удалили вручную или антивирусом."));

        var target = item.OriginalPath;
        if (File.Exists(target) || Directory.Exists(target))
        {
            if (!renameIfExists)
                return new(QuarantineRestoreStatus.Conflict, target, T.F("По исходному пути уже есть файл: {0}", target));
            target = AlternativePath(item.OriginalPath);
        }

        try
        {
            Quarantine.Restore(item, target);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn(T.F("Карантин: нет доступа для восстановления {0}: {1}", target, ex.Message));
            return new(QuarantineRestoreStatus.AccessDenied, target,
                T._("Нет доступа к исходной папке. Попробуйте запустить программу от имени администратора."));
        }
        catch (IOException ex) when (File.Exists(target) || Directory.Exists(target))
        {
            Log.Warn(T.F("Карантин: {0}", ex.Message));
            return new(QuarantineRestoreStatus.Conflict, target, T.F("По исходному пути уже есть файл: {0}", target));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException or InvalidOperationException)
        {
            // InvalidOperationException: the quarantine list (index.json) cannot be read, so Core changed nothing.
            Log.Warn(T.F("Карантин: не удалось восстановить {0}: {1}", item.OriginalPath, ex.Message));
            return new(QuarantineRestoreStatus.Failed, target, T.F("Не удалось восстановить файл: {0}.", ErrorPhrase(ex)));
        }

        return string.Equals(target, item.OriginalPath, StringComparison.OrdinalIgnoreCase)
            ? new(QuarantineRestoreStatus.Restored, target, T.F("Восстановлен: {0}", target))
            : new(QuarantineRestoreStatus.RestoredAs, target, T.F("Восстановлен под новым именем: {0}", target));
    }

    /// <summary>Permanently deletes a quarantined file (caller has confirmed).</summary>
    public static (bool Ok, string Message) Delete(QuarantineItem item)
    {
        if (!IsOwnStoredFile(item))
        {
            Log.Warn(T.F("Карантин: запись указывает на файл вне папки карантина ({0}) — удаление отменено", item.StoredPath));
            return (false, T.F("{0}: запись повреждена, файл вне папки карантина — удаление отменено.", item.FileName));
        }
        try
        {
            Quarantine.Delete(item);
            return (true, T.F("{0}: удалён навсегда.", item.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Карантин: не удалось удалить {0}: {1}", item.StoredPath, ex.Message));
            return (false, T.F("{0}: не удалось удалить — {1}.", item.FileName, ErrorPhrase(ex)));
        }
    }

    /// <summary>
    /// Russian reason for a result line ("файл занят другой программой"): <see cref="ErrorText.For"/>, starting with a
    /// lower-case letter because it follows a colon or a dash.
    /// </summary>
    internal static string ErrorPhrase(Exception ex)
    {
        var text = ErrorText.For(ex);
        return text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1])
            ? char.ToLower(text[0], Format.Ru) + text[1..]
            : text;
    }

    /// <summary>
    /// Quarantine.Add stores files directly in the quarantine folder; an index entry pointing anywhere else
    /// (outside, or into a subfolder that could be a junction) must never be moved or deleted.
    /// The index file itself is not a quarantined file either.
    /// </summary>
    internal static bool IsOwnStoredFile(QuarantineItem item)
        => PathGuard.IsDirectChild(item.StoredPath, AppPaths.QuarantineDir)
           && !string.Equals(Path.GetFileName(item.StoredPath), "index.json", StringComparison.OrdinalIgnoreCase);
}
