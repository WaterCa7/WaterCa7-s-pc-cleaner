using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Cleaner;

/// <summary>Phrases with correct number agreement for cleaning results (Russian forms; English "deleted 3 files").</summary>
public static class CleanerText
{
    /// <summary>"удалён 1 файл", "удалено 3 файла", "удалено 12 345 файлов".</summary>
    public static string Deleted(long files)
        => IsSingular(files) ? T.F("удалён {0}", Files(files)) : T.F("удалено {0}", Files(files));

    /// <summary>"удалён 1 объект", "удалено 8 объектов" (Recycle Bin items can be files or folders).</summary>
    public static string DeletedObjects(long items)
        => IsSingular(items) ? T.F("удалён {0}", Objects(items)) : T.F("удалено {0}", Objects(items));

    /// <summary>"пропущен 1 занятый", "пропущено 87 занятых" (files open in a running program).</summary>
    public static string SkippedBusy(long files)
        => IsSingular(files) ? T.F("пропущен {0} занятый", Format.Number(files)) : T.F("пропущено {0} занятых", Format.Number(files));

    /// <summary>"пропущен 1 файл без доступа", "пропущено 5 файлов без доступа" (no permission to delete).</summary>
    public static string SkippedDenied(long files)
        => IsSingular(files) ? T.F("пропущен {0} без доступа", Files(files)) : T.F("пропущено {0} без доступа", Files(files));

    /// <summary>
    /// What was skipped, busy files told apart from protected ones: "пропущено 87 занятых", "пропущено 5 файлов без доступа",
    /// "пропущено: 87 занятых, 5 без доступа". Empty when nothing was skipped.
    /// </summary>
    public static string Skipped(long busyFiles, long deniedFiles)
    {
        if (busyFiles > 0 && deniedFiles > 0)
            return IsSingular(busyFiles)
                ? T.F("пропущено: {0} занятый, {1} без доступа", Format.Number(busyFiles), Format.Number(deniedFiles))
                : T.F("пропущено: {0} занятых, {1} без доступа", Format.Number(busyFiles), Format.Number(deniedFiles));
        if (busyFiles > 0)
            return SkippedBusy(busyFiles);
        return deniedFiles > 0 ? SkippedDenied(deniedFiles) : "";
    }

    /// <summary>"Освобождено 3,2 ГБ, удалено 12 345 файлов, пропущено 87 занятых" (without the final period).</summary>
    public static string ResultLine(long freedBytes, long deletedFiles, long busyFiles, long deniedFiles = 0)
    {
        var text = T.F("Освобождено {0}, {1}", Format.Bytes(freedBytes), Deleted(deletedFiles));
        var skipped = Skipped(busyFiles, deniedFiles);
        if (skipped.Length > 0)
            text += ", " + skipped;
        return text;
    }

    /// <summary>"1 файл", "5 файлов".</summary>
    public static string Files(long n) => Format.Count(n, "файл", "файла", "файлов");

    /// <summary>"1 объект", "5 объектов".</summary>
    public static string Objects(long n) => Format.Count(n, "объект", "объекта", "объектов");

    /// <summary>The Russian rule for "удалён 21 файл" / "пропущен 1 занятый" (English does not use it: its plural comes from Format.Count).</summary>
    private static bool IsSingular(long n) => n % 10 == 1 && n % 100 != 11;
}
