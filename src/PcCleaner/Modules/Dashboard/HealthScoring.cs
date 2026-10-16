using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

/// <summary>
/// Pure scoring rules of the dashboard smart scan: penalties per severity, per-module cap, verdicts and grouping.
/// </summary>
public static class HealthScoring
{
    /// <summary>One module can never take more than this many points off the score.</summary>
    public const int MaxPenaltyPerModule = 40;

    /// <summary>Severities from the most to the least important (display order of the issue groups).</summary>
    public static readonly IssueSeverity[] SeverityOrder =
    {
        IssueSeverity.Critical, IssueSeverity.High, IssueSeverity.Medium, IssueSeverity.Low, IssueSeverity.Info,
    };

    public static int Penalty(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => 25,
        IssueSeverity.High => 12,
        IssueSeverity.Medium => 6,
        IssueSeverity.Low => 2,
        > IssueSeverity.Critical => 25,
        _ => 0,
    };

    /// <summary>
    /// Score = 100 − Σ penalties, where the penalty of every module (issues grouped by <see cref="HealthIssue.Module"/>)
    /// is capped at <see cref="MaxPenaltyPerModule"/>. Fixed issues are ignored. Result is clamped to [0, 100].
    /// </summary>
    public static int ComputeScore(IEnumerable<HealthIssue> issues)
    {
        var total = issues
            .Where(i => !i.IsFixed)
            .GroupBy(i => i.Module ?? "", StringComparer.OrdinalIgnoreCase)
            .Sum(g => Math.Min(MaxPenaltyPerModule, g.Sum(i => Penalty(i.Severity))));
        return Math.Clamp(100 - total, 0, 100);
    }

    /// <summary>"Отлично" ≥ 90, "Хорошо" ≥ 75, "Нужно внимание" ≥ 50, "Плохо" &lt; 50; null = never scanned.</summary>
    public static string Verdict(int? score) => score switch
    {
        null => "Компьютер ещё не проверялся",
        >= 90 => "Отлично",
        >= 75 => "Хорошо",
        >= 50 => "Нужно внимание",
        _ => "Плохо",
    };

    /// <summary>One friendly sentence under the verdict.</summary>
    public static string VerdictDescription(int? score, int issueCount)
    {
        if (score is null)
            return "Запустите проверку — она займёт около минуты и ничего не изменит без вашего согласия.";
        if (issueCount == 0)
            return "Проблем не найдено. Компьютер в отличной форме!";
        return score switch
        {
            >= 90 => "Серьёзных проблем нет, остались только мелочи и советы.",
            >= 75 => "Всё в порядке, но кое-что можно улучшить.",
            >= 50 => "Есть проблемы, которые замедляют компьютер или снижают защиту.",
            _ => "Найдены серьёзные проблемы — рекомендуем исправить их как можно скорее.",
        };
    }

    /// <summary>Theme brush key for the score: green / amber / red, muted when there is no score.</summary>
    public static string ScoreBrushKey(int? score) => score switch
    {
        null => "MutedBrush",
        >= 75 => "SuccessBrush",
        >= 50 => "WarningBrush",
        _ => "DangerBrush",
    };

    /// <summary>Group header: "Критично", "Важно", "Средне", "Низко", "Советы".</summary>
    public static string GroupTitle(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => "Критично",
        IssueSeverity.High => "Важно",
        IssueSeverity.Medium => "Средне",
        IssueSeverity.Low => "Низко",
        _ => "Советы",
    };

    /// <summary>Short explanation of a severity group for non-experts.</summary>
    public static string GroupHint(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => "Угрожает безопасности или стабильности — исправьте в первую очередь",
        IssueSeverity.High => "Заметно мешает работе или защите компьютера",
        IssueSeverity.Medium => "Стоит исправить, когда будет минутка",
        IssueSeverity.Low => "Мелочи, которые чуть-чуть улучшат работу",
        _ => "Полезные рекомендации — на ваше усмотрение",
    };

    /// <summary>
    /// Groups issues by severity (most severe first, empty groups omitted). Inside a group issues that free more space
    /// come first; otherwise the original (module) order is kept.
    /// </summary>
    public static IReadOnlyList<(IssueSeverity Severity, IReadOnlyList<HealthIssue> Issues)> Group(IEnumerable<HealthIssue> issues)
    {
        var list = issues.ToList();
        var result = new List<(IssueSeverity, IReadOnlyList<HealthIssue>)>();
        foreach (var severity in SeverityOrder)
        {
            var items = list
                .Where(i => Normalize(i.Severity) == severity)
                .OrderByDescending(i => i.ReclaimableBytes) // OrderBy is stable: ties keep module order
                .ToList();
            if (items.Count > 0)
                result.Add((severity, items));
        }
        return result;
    }

    /// <summary>Total bytes that can be freed by the issues that are not fixed yet.</summary>
    public static long TotalReclaimable(IEnumerable<HealthIssue> issues)
        => issues.Where(i => !i.IsFixed).Sum(i => Math.Max(0, i.ReclaimableBytes));

    private static IssueSeverity Normalize(IssueSeverity s)
        => Enum.IsDefined(s) ? s : s > IssueSeverity.Critical ? IssueSeverity.Critical : IssueSeverity.Info;

    /// <summary>
    /// "сегодня в 14:32", "вчера в 09:05", "12 октября в 14:32", "12 октября 2025 г. в 14:32".
    /// </summary>
    public static string RelativeTime(DateTime time, DateTime now)
    {
        var clock = time.ToString("HH:mm", Format.Ru);
        var days = (now.Date - time.Date).Days;
        if (days == 0)
            return $"сегодня в {clock}";
        if (days == 1)
            return $"вчера в {clock}";
        var month = MonthsGenitive[time.Month - 1];
        return time.Year == now.Year
            ? $"{time.Day} {month} в {clock}"
            : $"{time.Day} {month} {time.Year} г. в {clock}";
    }

    private static readonly string[] MonthsGenitive =
    {
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря",
    };

    /// <summary>"3 проблемы" / "нет проблем".</summary>
    public static string ProblemsText(int count)
        => count == 0 ? "проблем нет" : Format.Count(count, "проблема", "проблемы", "проблем");

    /// <summary>
    /// FixAsync has no success flag: modules return a message. Some messages mean "nothing was changed"
    /// (e.g. admin rights are required) — such a result must not mark the issue as fixed.
    /// </summary>
    public static bool LooksLikeFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        var m = message.ToLowerInvariant();
        return m.Contains("нужны права администратора")
               || m.Contains("требуются права администратора")
               || m.Contains("необходимы права администратора")
               || m.Contains("нужны права админ")
               || m.StartsWith("не удалось")
               || m.StartsWith("ошибка")
               || m.Contains("ничего не изменено")
               || m.Contains("ничего не было изменено")
               || m.Contains("отменено");
    }
}
