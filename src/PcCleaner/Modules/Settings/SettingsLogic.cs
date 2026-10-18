using System.IO;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

/// <summary>Item of the "do not delete temp files younger than ..." combo box.</summary>
public sealed record AgeOption(int Hours, string Label)
{
    public override string ToString() => Label;
}

public enum ExclusionAddResult
{
    Added,
    Invalid,
    Duplicate,
    AlreadyCovered,
}

/// <summary>
/// Pure settings logic (validation, clamping, list edits, reset) so it can be unit-tested
/// without touching the real settings file.
/// </summary>
internal static class SettingsLogic
{
    public const int MinAgeHoursMin = 0;
    public const int MinAgeHoursMax = 720;
    public const int DefaultMinAgeHours = 24;

    public static readonly int[] AgePresets = { 0, 1, 6, 24, 48, 72, 168 };

    public static int ClampMinAge(int hours) => Math.Clamp(hours, MinAgeHoursMin, MinAgeHoursMax);

    /// <summary>Parses user input like " 12 " into a valid number of hours (0–720).</summary>
    public static bool TryParseMinAge(string? text, out int hours, out string? error)
    {
        hours = DefaultMinAgeHours;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            error = "Укажите число часов.";
            return false;
        }
        if (!int.TryParse(s, System.Globalization.NumberStyles.Integer, Format.Ru, out var value))
        {
            error = "Нужно целое число часов, например 24.";
            return false;
        }
        if (value < MinAgeHoursMin || value > MinAgeHoursMax)
        {
            error = $"Допустимо от {MinAgeHoursMin} до {MinAgeHoursMax} часов (30 дней).";
            return false;
        }
        hours = value;
        error = null;
        return true;
    }

    public static string AgeLabel(int hours) => hours switch
    {
        0 => "Без задержки (удалять сразу)",
        DefaultMinAgeHours => "24 часа (рекомендуется)",
        168 => "1 неделя",
        _ when hours % 24 == 0 => Format.Count(hours / 24, "день", "дня", "дней"),
        _ => Format.Count(hours, "час", "часа", "часов"),
    };

    /// <summary>Presets plus the current value if it is not one of them (e.g. edited by hand in settings.json).</summary>
    public static List<AgeOption> BuildAgeOptions(int current)
    {
        var values = new SortedSet<int>(AgePresets) { ClampMinAge(current) };
        return values.Select(h => new AgeOption(h, AgeLabel(h))).ToList();
    }

    /// <summary>
    /// Validates a typed or picked path and returns its normalized full form
    /// (environment variables expanded, no trailing backslash except for a drive root).
    /// </summary>
    public static bool TryNormalizePath(string? input, out string normalized, out string? error)
    {
        normalized = "";
        var s = input?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(s))
        {
            error = "Укажите путь к папке или файлу.";
            return false;
        }

        s = Environment.ExpandEnvironmentVariables(s);
        if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || s.IndexOfAny(new[] { '*', '?', '<', '>', '|', '"' }) >= 0)
        {
            error = "В пути есть недопустимые символы (* ? < > | \").";
            return false;
        }
        if (!Path.IsPathFullyQualified(s))
        {
            error = "Нужен полный путь, например D:\\Проекты или %USERPROFILE%\\Documents.";
            return false;
        }
        // A colon is allowed only right after the drive letter.
        if (s.Length > 2 && s.IndexOf(':', 2) >= 0)
        {
            error = "Двоеточие допустимо только после буквы диска.";
            return false;
        }

        try
        {
            s = Path.GetFullPath(s);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            error = "Некорректный путь.";
            return false;
        }

        var root = Path.GetPathRoot(s) ?? "";
        if (s.Length > root.Length)
            s = s.TrimEnd('\\', '/');
        normalized = s;
        error = null;
        return true;
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="folder"/> or lies inside it.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        var p = path.TrimEnd('\\');
        var f = folder.TrimEnd('\\');
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds a cleaner exclusion (does not save). Returns what happened and a Russian message.</summary>
    public static ExclusionAddResult TryAddExclusion(AppSettings settings, string? input, out string normalized, out string message)
    {
        if (!TryNormalizePath(input, out normalized, out var error))
        {
            message = error!;
            return ExclusionAddResult.Invalid;
        }

        var path = normalized;
        var same = settings.CleanerExclusions.FirstOrDefault(e => string.Equals(e.TrimEnd('\\'), path.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase));
        if (same is not null)
        {
            message = "Этот путь уже есть в списке исключений.";
            return ExclusionAddResult.Duplicate;
        }

        var parent = settings.CleanerExclusions.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e) && IsSameOrUnder(path, e));
        if (parent is not null)
        {
            message = $"Этот путь уже защищён: он находится внутри исключения «{parent}».";
            return ExclusionAddResult.AlreadyCovered;
        }

        // Copy-on-write: other modules may be enumerating the shared list on a background thread right now.
        settings.CleanerExclusions = new List<string>(settings.CleanerExclusions) { path };
        // Network paths are not probed: an unreachable server would freeze the UI for the SMB timeout.
        message = IsNetworkPath(path) || Directory.Exists(path) || File.Exists(path)
            ? $"Добавлено исключение: {path}"
            : $"Добавлено исключение: {path} (такой папки пока нет — правило сработает, когда она появится)";
        return ExclusionAddResult.Added;
    }

    /// <summary>"\\server\share\..." (UNC) — checking such a path may block for a long time.</summary>
    public static bool IsNetworkPath(string? path) => path is not null && path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Removes the given items (case-insensitive). Returns how many were removed.</summary>
    public static int RemoveItems(List<string> list, IEnumerable<string> items)
    {
        var removed = 0;
        foreach (var item in items.ToList())
            removed += list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        return removed;
    }

    /// <summary>
    /// Copy-on-write removal for lists shared with other modules: returns a new list without the items
    /// (the original instance is never modified, so a concurrent enumeration elsewhere cannot fail).
    /// </summary>
    public static List<string> Without(List<string> list, IEnumerable<string> items, out int removed)
    {
        var copy = new List<string>(list);
        removed = RemoveItems(copy, items);
        return copy;
    }

    /// <summary>
    /// Removes the given entries from the shared trusted list (case-insensitive) through AppSettings.RemoveTrusted, which is
    /// thread-safe, replaces the list instead of editing it in place, saves and logs. Returns how many entries were removed.
    /// </summary>
    public static int RemoveTrusted(AppSettings settings, IEnumerable<string> items)
    {
        var before = settings.TrustedItems.Count;
        foreach (var item in items.Where(i => i is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            // Blank entries (a hand-edited settings.json) are never "trusted", but they are still removed.
            if (settings.IsTrusted(item) || settings.TrustedItems.Contains(item))
                settings.RemoveTrusted(item);
        }
        return Math.Max(0, before - settings.TrustedItems.Count);
    }

    /// <summary>
    /// Resets user preferences to defaults. ModuleData is kept on purpose: it stores previous values
    /// that other modules need to undo their tweaks.
    /// </summary>
    public static void ResetToDefaults(AppSettings settings)
    {
        var defaults = new AppSettings();
        settings.CreateRestorePoint = defaults.CreateRestorePoint;
        settings.CleanerMinFileAgeHours = defaults.CleanerMinFileAgeHours;
        settings.CleanerExclusions = new List<string>(defaults.CleanerExclusions);
        settings.CleanerDisabledCategories = new List<string>(defaults.CleanerDisabledCategories);
        RemoveTrusted(settings, settings.TrustedItems.ToList());
    }

    /// <summary>
    /// Repairs values loaded from a hand-edited settings.json (out-of-range age, unknown language, null/blank/duplicate entries).
    /// Returns the number of corrections. Does not save, except that a repaired trusted list is saved by
    /// AppSettings.AddTrusted/RemoveTrusted (the only safe way to change it).
    /// </summary>
    public static int Sanitize(AppSettings settings)
    {
        var fixes = 0;
        var age = ClampMinAge(settings.CleanerMinFileAgeHours);
        if (age != settings.CleanerMinFileAgeHours)
        {
            settings.CleanerMinFileAgeHours = age;
            fixes++;
        }

        // Only the exact codes: anything else means "as the system" (the start does the same with an unknown value).
        if (settings.Language is not null && !T.IsSupported(settings.Language))
        {
            settings.Language = null;
            fixes++;
        }

        settings.CleanerExclusions ??= new List<string>();
        // A missing list (JSON null) must exist before IsTrusted/AddTrusted/RemoveTrusted can be used at all.
        settings.TrustedItems ??= new List<string>();
        settings.CleanerDisabledCategories ??= new List<string>();
        settings.ModuleData ??= new Dictionary<string, Dictionary<string, string>>();

        // Repaired lists are new instances (copy-on-write): other modules may be enumerating the old ones.
        var exclusionFixes = Dedupe(settings.CleanerExclusions, out var exclusions);
        if (exclusionFixes > 0)
            settings.CleanerExclusions = exclusions;
        var trustedFixes = Dedupe(settings.TrustedItems, out var trusted);
        if (trustedFixes > 0)
            ReplaceTrusted(settings, trusted);
        return fixes + exclusionFixes + trustedFixes;
    }

    /// <summary>
    /// Brings the trusted list to <paramref name="wanted"/> using only AppSettings.RemoveTrusted/AddTrusted
    /// (the list is shared with background scans of other pages).
    /// </summary>
    private static void ReplaceTrusted(AppSettings settings, IReadOnlyList<string> wanted)
    {
        // RemoveTrusted is case-insensitive: removing "A.EXE" also removes "a.exe", which is added back below.
        foreach (var entry in settings.TrustedItems.ToList())
        {
            if (!wanted.Contains(entry, StringComparer.Ordinal))
                settings.RemoveTrusted(entry);
        }
        foreach (var entry in wanted)
            settings.AddTrusted(entry);
    }

    private static int Dedupe(List<string> list, out List<string> cleaned)
    {
        var before = list.Count;
        cleaned = list.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cleaned.Count == before && cleaned.SequenceEqual(list))
            return 0;
        return Math.Max(1, before - cleaned.Count);
    }
}
