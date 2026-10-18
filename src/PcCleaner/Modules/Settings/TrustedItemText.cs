using System.Globalization;
using System.IO;
using System.Windows.Data;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

/// <summary>
/// Readable text for entries of the shared trusted list. The list stores matching keys as they are:
/// file paths (Процессы, Автозагрузка), program names (Программы) and, for startup entries started through an
/// interpreter, the entry's internal id ("reg|HKCU|Software\...\Run|Updater", "task|\Vendor\Task", "svc|Name",
/// "lnk|C:\...\x.lnk"). Only the text shown in Settings changes; the stored key is kept for matching and removal.
/// </summary>
internal static class TrustedItemText
{
    /// <summary>One line for the list, e.g. "Автозагрузка: Updater (реестр, текущий пользователь)".</summary>
    public static string Label(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return "";
        var s = item.Trim();
        var bar = s.IndexOf('|');
        if (bar <= 0)
            return IsPath(s) ? s : T.F("Программа: {0}", s);

        var kind = s[..bar];
        var rest = s[(bar + 1)..];
        switch (kind.ToLowerInvariant())
        {
            case "reg":
            {
                // reg|<hive>|<key path>|<value name> (the value name may itself contain "|").
                var parts = rest.Split('|', 3);
                if (parts.Length == 3 && parts[2].Length > 0)
                    return T.F("Автозагрузка: {0} ({1})", parts[2], RegistryPlace(parts[0], parts[1]));
                break;
            }
            case "task" when rest.Trim('\\').Length > 0:
                return T.F("Задание планировщика: {0}", rest.Trim('\\'));
            case "svc" when rest.Length > 0:
                return T.F("Служба: {0}", rest);
            case "lnk" when rest.Length > 0:
            {
                var name = Path.GetFileNameWithoutExtension(rest);
                return T.F("Автозагрузка: {0} (папка «Автозагрузка»)", (name.Length > 0 ? name : rest));
            }
        }

        // Unknown or older id format ("RegistryRun|HKCU|Updater"): the last part is the entry name.
        var last = s.Split('|').LastOrDefault(p => p.Trim().Length > 0)?.Trim();
        return string.IsNullOrEmpty(last) ? s : T.F("Запись автозагрузки: {0}", last);
    }

    /// <summary>Tooltip: the readable label plus the stored key when they differ.</summary>
    public static string ToolTip(string? item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return "";
        var label = Label(item);
        return string.Equals(label, item.Trim(), StringComparison.Ordinal) ? label : $"{label}\n{item.Trim()}";
    }

    private static string RegistryPlace(string hive, string keyPath)
    {
        var scope = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => T._("текущий пользователь"),
            "HKLM" or "HKEY_LOCAL_MACHINE" => T._("все пользователи"),
            _ => hive,
        };
        var parts = new List<string> { T._("реестр"), scope };
        if (keyPath.Contains(@"\RunOnce", StringComparison.OrdinalIgnoreCase))
            parts.Add(T._("однократный запуск"));
        if (keyPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase))
            parts.Add(T._("32-разрядные программы"));
        return string.Join(", ", parts);
    }

    private static bool IsPath(string s)
        => (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] is '\\' or '/')
           || s.StartsWith(@"\\", StringComparison.Ordinal);
}

/// <summary>XAML: trusted-list entry → its readable label (ConverterParameter "ToolTip" → label + stored key).</summary>
public sealed class TrustedItemLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(parameter as string, "ToolTip", StringComparison.OrdinalIgnoreCase)
            ? TrustedItemText.ToolTip(value as string)
            : TrustedItemText.Label(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
