using System.Globalization;

namespace PcCleaner.Core;

public static class Format
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };

    /// <summary>1536 -> "1,5 КБ".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "-" + Bytes(-bytes);
        if (bytes < 1024)
            return $"{bytes} {Units[0]}";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, Ru) + " " + Units[unit];
    }

    public static string Bytes(ulong bytes) => Bytes(bytes > long.MaxValue ? long.MaxValue : (long)bytes);

    public static string Percent(double value) => value.ToString("0.#", Ru) + "%";

    public static string Number(long value) => value.ToString("N0", Ru);

    /// <summary>TimeSpan -> "2 д 3 ч", "5 ч 12 мин", "42 с" (the standard abbreviation, as in "1,6 с" elsewhere).</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} д {span.Hours} ч";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} ч {span.Minutes} мин";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} мин {span.Seconds} с";
        return $"{Math.Max(0, (int)span.TotalSeconds)} с";
    }

    public static string Date(DateTime? date) => date is null ? "—" : date.Value.ToString("dd.MM.yyyy", Ru);

    public static string DateTime(DateTime? date) => date is null ? "—" : date.Value.ToString("dd.MM.yyyy HH:mm", Ru);

    /// <summary>Russian plural: Plural(5, "файл", "файла", "файлов") -> "файлов".</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
            return many;
        return mod10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>"5 файлов".</summary>
    public static string Count(long n, string one, string few, string many) => $"{Number(n)} {Plural(n, one, few, many)}";
}
