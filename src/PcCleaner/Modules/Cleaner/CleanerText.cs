using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

/// <summary>Russian phrases with correct number agreement for cleaning results.</summary>
public static class CleanerText
{
    /// <summary>"удалён 1 файл", "удалено 3 файла", "удалено 12 345 файлов".</summary>
    public static string Deleted(long files)
        => $"{(IsSingular(files) ? "удалён" : "удалено")} {Format.Count(files, "файл", "файла", "файлов")}";

    /// <summary>"пропущен 1 занятый", "пропущено 87 занятых".</summary>
    public static string SkippedBusy(long files)
        => IsSingular(files) ? $"пропущен {Format.Number(files)} занятый" : $"пропущено {Format.Number(files)} занятых";

    /// <summary>"Освобождено 3,2 ГБ, удалено 12 345 файлов, пропущено 87 занятых" (without the final period).</summary>
    public static string ResultLine(long freedBytes, long deletedFiles, long skippedFiles)
    {
        var text = $"Освобождено {Format.Bytes(freedBytes)}, {Deleted(deletedFiles)}";
        if (skippedFiles > 0)
            text += ", " + SkippedBusy(skippedFiles);
        return text;
    }

    /// <summary>"1 файл", "5 файлов".</summary>
    public static string Files(long n) => Format.Count(n, "файл", "файла", "файлов");

    /// <summary>"1 объект", "5 объектов".</summary>
    public static string Objects(long n) => Format.Count(n, "объект", "объекта", "объектов");

    /// <summary>"в 1 категории", "в 5 категориях".</summary>
    public static string InCategories(long n) => $"в {Format.Number(n)} {(IsSingular(n) ? "категории" : "категориях")}";

    private static bool IsSingular(long n) => n % 10 == 1 && n % 100 != 11;
}
