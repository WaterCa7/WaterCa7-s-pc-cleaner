using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

public enum LogLevelFilter
{
    All,
    Actions,
    Warnings,
    Errors,
}

public sealed record LogFilterOption(LogLevelFilter Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Filtering / searching / copying of journal entries (pure logic, unit-tested).</summary>
internal static class LogFilter
{
    public static IReadOnlyList<LogFilterOption> Options { get; } = new[]
    {
        new LogFilterOption(LogLevelFilter.All, "Все"),
        new LogFilterOption(LogLevelFilter.Actions, "Действия"),
        new LogFilterOption(LogLevelFilter.Warnings, "Предупреждения"),
        new LogFilterOption(LogLevelFilter.Errors, "Ошибки"),
    };

    public static bool MatchesLevel(LogLevel level, LogLevelFilter filter) => filter switch
    {
        LogLevelFilter.Actions => level == LogLevel.Action,
        LogLevelFilter.Warnings => level == LogLevel.Warning,
        LogLevelFilter.Errors => level == LogLevel.Error,
        _ => true,
    };

    /// <summary>Splits the search box text into words; every word must be found (AND).</summary>
    public static string[] SplitTerms(string? search)
        => string.IsNullOrWhiteSpace(search)
            ? Array.Empty<string>()
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(LogEntry entry, LogLevelFilter filter, IReadOnlyList<string> terms)
    {
        if (!MatchesLevel(entry.Level, filter))
            return false;
        foreach (var term in terms)
        {
            if (!entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !entry.LevelText.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !entry.TimeText.Contains(term, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    public static bool Matches(LogEntry entry, LogLevelFilter filter, string? search)
        => Matches(entry, filter, SplitTerms(search));

    /// <summary>Filtered entries, newest first (the source list is chronological).</summary>
    public static List<LogEntry> Apply(IEnumerable<LogEntry> entries, LogLevelFilter filter, string? search)
    {
        var terms = SplitTerms(search);
        var result = entries.Where(e => Matches(e, filter, terms)).ToList();
        result.Reverse();
        return result;
    }

    public static string FormatLine(LogEntry e) => $"{e.Time:dd.MM.yyyy HH:mm:ss} [{e.LevelText}] {e.Message}";

    /// <summary>Text for the clipboard, in chronological order.</summary>
    public static string ToClipboardText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries.OrderBy(x => x.Time))
            sb.AppendLine(FormatLine(e));
        return sb.ToString();
    }

    public static (int Total, int Actions, int Warnings, int Errors) Count(IEnumerable<LogEntry> entries)
    {
        int total = 0, actions = 0, warnings = 0, errors = 0;
        foreach (var e in entries)
        {
            total++;
            switch (e.Level)
            {
                case LogLevel.Action:
                    actions++;
                    break;
                case LogLevel.Warning:
                    warnings++;
                    break;
                case LogLevel.Error:
                    errors++;
                    break;
            }
        }
        return (total, actions, warnings, errors);
    }
}
