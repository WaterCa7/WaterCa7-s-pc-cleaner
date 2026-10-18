using System.Globalization;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>Numbers, sizes, durations and dates for the UI, in its language (<see cref="T.Culture"/>).</summary>
public static class Format
{
    /// <summary>Russian culture for parsing and for texts that must stay Russian; UI text follows <see cref="T.Culture"/>.</summary>
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] RussianUnits = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
    private static readonly string[] EnglishUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>1536 -> "1,5 КБ" ("1.5 KB" in English).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "-" + Bytes(-bytes);
        var units = T.IsEnglish ? EnglishUnits : RussianUnits;
        if (bytes < 1024)
            return $"{bytes} {units[0]}";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, T.Culture) + " " + units[unit];
    }

    public static string Bytes(ulong bytes) => Bytes(bytes > long.MaxValue ? long.MaxValue : (long)bytes);

    public static string Percent(double value) => value.ToString("0.#", T.Culture) + "%";

    public static string Number(long value) => value.ToString("N0", T.Culture);

    /// <summary>
    /// TimeSpan -> "2 д 3 ч", "5 ч 12 мин", "42 с" (the standard abbreviation, as in "1,6 с" elsewhere);
    /// English "2 d 3 h", "5 h 12 min", "42 s".
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        var (days, hours, minutes, seconds) = T.IsEnglish ? ("d", "h", "min", "s") : ("д", "ч", "мин", "с");
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} {days} {span.Hours} {hours}";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} {hours} {span.Minutes} {minutes}";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} {minutes} {span.Seconds} {seconds}";
        return $"{Math.Max(0, (int)span.TotalSeconds)} {seconds}";
    }

    /// <summary>"18.10.2026"; English "Oct 18, 2026".</summary>
    public static string Date(DateTime? date)
        => date is null ? "—" : date.Value.ToString(T.IsEnglish ? "MMM d, yyyy" : "dd.MM.yyyy", T.Culture);

    /// <summary>"18.10.2026 14:05"; English "Oct 18, 2026 14:05".</summary>
    public static string DateTime(DateTime? date)
        => date is null ? "—" : date.Value.ToString(T.IsEnglish ? "MMM d, yyyy HH:mm" : "dd.MM.yyyy HH:mm", T.Culture);

    /// <summary>"18.10.2026 14:05:07"; English "Oct 18, 2026 14:05:07".</summary>
    public static string DateTimeSeconds(DateTime? date)
        => date is null ? "—" : date.Value.ToString(T.IsEnglish ? "MMM d, yyyy HH:mm:ss" : "dd.MM.yyyy HH:mm:ss", T.Culture);

    /// <summary>Plural(5, "файл", "файла", "файлов") -> "файлов"; in English the en.json entry of "файл" or "файлов" (<see cref="T.Plural"/>).</summary>
    public static string Plural(long n, string one, string few, string many) => T.Plural(n, one, few, many);

    /// <summary>"5 файлов".</summary>
    public static string Count(long n, string one, string few, string many) => $"{Number(n)} {Plural(n, one, few, many)}";
}
