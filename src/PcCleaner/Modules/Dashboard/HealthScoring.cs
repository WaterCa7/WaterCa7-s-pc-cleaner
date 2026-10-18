using PcCleaner.Core;
using PcCleaner.Core.Localization;

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
        null => T._("Компьютер ещё не проверялся"),
        >= 90 => T._("Отлично"),
        >= 75 => T._("Хорошо"),
        >= 50 => T._("Нужно внимание"),
        _ => T._("Плохо"),
    };

    /// <summary>Verdict of a scan in which some modules failed or timed out.</summary>
    public const string IncompleteVerdict = "Проверка неполная";

    /// <summary>
    /// Like <see cref="Verdict(int?)"/>, but a scan with failed/timed-out modules never says "Отлично"/"Хорошо": their
    /// problems are missing from the score (it can only be lower in reality), so the verdict is "Проверка неполная".
    /// "Плохо" stays: the real score can only be even lower (same rule as <see cref="ScoreBrushKey(int?, int)"/>).
    /// </summary>
    public static string Verdict(int? score, int failedModules)
        => score is >= 50 && failedModules > 0 ? T._(IncompleteVerdict) : Verdict(score);

    /// <summary>
    /// One friendly sentence under the verdict. <paramref name="problemCount"/> = remaining issues that lower the score
    /// (tips are not problems); <paramref name="fixedSome"/> = something was fixed since the scan.
    /// </summary>
    public static string VerdictDescription(int? score, int problemCount, bool fixedSome = false)
    {
        if (score is null)
            return T._("Запустите проверку — она займёт около минуты и ничего не изменит без вашего согласия.");
        if (problemCount == 0)
            return fixedSome
                ? T._("Все найденные проблемы исправлены. Нажмите «Проверить снова», чтобы обновить оценку.")
                : T._("Проблем не найдено. Компьютер в отличной форме!");
        return score switch
        {
            >= 90 => T._("Серьёзных проблем нет, остались только мелочи и советы."),
            >= 75 => T._("Всё в порядке, но кое-что можно улучшить."),
            >= 50 => T._("Есть проблемы, которые замедляют компьютер или снижают защиту."),
            _ => T._("Найдены серьёзные проблемы — рекомендуем исправить их как можно скорее."),
        };
    }

    /// <summary>
    /// <see cref="VerdictDescription(int?, int, bool)"/> for a scan in which <paramref name="failedModules"/> modules
    /// failed or timed out: never "в отличной форме" — the score only counts the modules that were checked.
    /// </summary>
    public static string VerdictDescription(int? score, int problemCount, bool fixedSome, int failedModules)
    {
        if (score is null || failedModules <= 0)
            return VerdictDescription(score, problemCount, fixedSome);
        var notChecked = Format.Count(failedModules, "раздел", "раздела", "разделов");
        if (problemCount > 0)
            return T.F("Не удалось проверить {0} — оценка учитывает только остальные и может быть завышена.", notChecked);
        return fixedSome
            ? T.F("Найденные проблемы исправлены, но {0} проверить не удалось — нажмите «Проверить снова».", notChecked)
            : T.F("В проверенных разделах проблем нет, но {0} проверить не удалось — оценка может быть завышена.", notChecked);
    }

    /// <summary>Theme brush key for the score: green / amber / red, muted when there is no score.</summary>
    public static string ScoreBrushKey(int? score) => score switch
    {
        null => "MutedBrush",
        >= 75 => "SuccessBrush",
        >= 50 => "WarningBrush",
        _ => "DangerBrush",
    };

    /// <summary>
    /// <see cref="ScoreBrushKey(int?)"/> that is never green for an incomplete scan (failed modules can only lower the
    /// real score): amber instead, red stays red.
    /// </summary>
    public static string ScoreBrushKey(int? score, int failedModules)
        => score is >= 50 && failedModules > 0 ? "WarningBrush" : ScoreBrushKey(score);

    /// <summary>Group header: "Критично", "Важно", "Средне", "Низко", "Советы".</summary>
    public static string GroupTitle(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => T._("Критично"),
        IssueSeverity.High => T._("Важно"),
        IssueSeverity.Medium => T._("Средне"),
        IssueSeverity.Low => T._("Низко"),
        _ => T._("Советы"),
    };

    /// <summary>The badge on an issue row: like <see cref="GroupTitle"/>, but a tip is "Совет", not "Советы".</summary>
    public static string SeverityLabel(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical or IssueSeverity.High or IssueSeverity.Medium or IssueSeverity.Low => GroupTitle(severity),
        _ => T._("Совет"),
    };

    /// <summary>Short explanation of a severity group for non-experts.</summary>
    public static string GroupHint(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => T._("Угрожает безопасности или стабильности — исправьте в первую очередь"),
        IssueSeverity.High => T._("Заметно мешает работе или защите компьютера"),
        IssueSeverity.Medium => T._("Стоит исправить, когда будет минутка"),
        IssueSeverity.Low => T._("Мелочи, которые чуть-чуть улучшат работу"),
        _ => T._("Полезные рекомендации — на ваше усмотрение"),
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

    /// <summary>
    /// Total bytes that can be freed by the issues that are not fixed yet. Saturates instead of throwing
    /// (LINQ Sum is checked) so a module reporting absurd sizes cannot break the scan.
    /// </summary>
    public static long TotalReclaimable(IEnumerable<HealthIssue> issues)
    {
        long total = 0;
        foreach (var issue in issues)
        {
            if (issue.IsFixed || issue.ReclaimableBytes <= 0)
                continue;
            total = issue.ReclaimableBytes > long.MaxValue - total ? long.MaxValue : total + issue.ReclaimableBytes;
        }
        return total;
    }

    /// <summary>The module's gain text, or the reclaimable size when the module did not provide one.</summary>
    public static string? GainText(HealthIssue issue)
        => !string.IsNullOrWhiteSpace(issue.Gain)
            ? issue.Gain.Trim()
            : issue.ReclaimableBytes > 0 ? T.F("Освободит {0}", Format.Bytes(issue.ReclaimableBytes)) : null;

    /// <summary>Issues that lower the score (everything except <see cref="IssueSeverity.Info"/> tips).</summary>
    public static bool IsProblem(HealthIssue issue) => issue.Severity > IssueSeverity.Info;

    private static IssueSeverity Normalize(IssueSeverity s)
        => Enum.IsDefined(s) ? s : s > IssueSeverity.Critical ? IssueSeverity.Critical : IssueSeverity.Info;

    /// <summary>
    /// "сегодня в 14:32", "вчера в 09:05", "12 октября в 14:32", "12 октября 2025 г. в 14:32";
    /// English "today at 14:32", "yesterday at 09:05", "Oct 12 at 14:32", "Oct 12, 2025 at 14:32".
    /// </summary>
    public static string RelativeTime(DateTime time, DateTime now)
    {
        var clock = time.ToString("HH:mm", T.Culture);
        var days = (now.Date - time.Date).Days;
        if (days == 0)
            return T.F("сегодня в {0}", clock);
        if (days == 1)
            return T.F("вчера в {0}", clock);
        var month = T.IsEnglish ? time.ToString("MMM", T.Culture) : MonthsGenitive[time.Month - 1];
        return time.Year == now.Year
            ? T.F("{0} {1} в {2}", time.Day, month, clock)
            : T.F("{0} {1} {2} г. в {3}", time.Day, month, time.Year, clock);
    }

    // l10n:data-begin — the Russian month names of a date (the English run uses "MMM"), not a text to translate
    private static readonly string[] MonthsGenitive =
    {
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря",
    };
    // l10n:data-end

    /// <summary>"3 проблемы" / "нет проблем".</summary>
    public static string ProblemsText(int count)
        => count == 0 ? T._("проблем нет") : Format.Count(count, "проблема", "проблемы", "проблем");

    /// <summary>"2 проблемы и 3 совета", "1 совет", "проблем нет" (tips are not called problems).</summary>
    public static string FindingsText(int problems, int tips)
    {
        if (problems <= 0 && tips <= 0)
            return T._("проблем нет");
        var problemsText = Format.Count(problems, "проблема", "проблемы", "проблем");
        var tipsText = Format.Count(tips, "совет", "совета", "советов");
        if (problems > 0 && tips > 0)
            return T.F("{0} и {1}", problemsText, tipsText);
        return problems > 0 ? problemsText : tipsText;
    }

    /// <summary>
    /// FixAsync has no success flag: modules return a message. Some messages mean "nothing was changed"
    /// (e.g. admin rights are required) or "the problem is still there" — such a result must not mark the issue as fixed.
    /// When in doubt the result counts as NOT fixed. Judged in the language of the UI (<see cref="FixResultClassifier"/>):
    /// Russian by the Russian rules, English also by the failure texts of en.json.
    /// </summary>
    public static bool LooksLikeFailure(string? message)
    {
        var language = T.Language;
        return FixResultClassifier.IsFailure(message, language, CatalogOf(language));
    }

    /// <summary>The fix result says administrator rights are required ("Нужны права администратора", "Недостаточно прав"...).</summary>
    public static bool LooksLikeAdminRequired(string? message)
    {
        var language = T.Language;
        return FixResultClassifier.NeedsAdmin(message, language, CatalogOf(language));
    }

    /// <summary>en.json in an English run (a Russian run never reads it).</summary>
    private static IReadOnlyDictionary<string, string>? CatalogOf(string language)
        => language == T.English ? T.EnglishTexts : null;
}
