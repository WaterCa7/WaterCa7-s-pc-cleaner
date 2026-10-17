using System.Text.RegularExpressions;
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

    /// <summary>Verdict of a scan in which some modules failed or timed out.</summary>
    public const string IncompleteVerdict = "Проверка неполная";

    /// <summary>
    /// Like <see cref="Verdict(int?)"/>, but a scan with failed/timed-out modules never says "Отлично"/"Хорошо": their
    /// problems are missing from the score (it can only be lower in reality), so the verdict is "Проверка неполная".
    /// "Плохо" stays: the real score can only be even lower (same rule as <see cref="ScoreBrushKey(int?, int)"/>).
    /// </summary>
    public static string Verdict(int? score, int failedModules)
        => score is >= 50 && failedModules > 0 ? IncompleteVerdict : Verdict(score);

    /// <summary>
    /// One friendly sentence under the verdict. <paramref name="problemCount"/> = remaining issues that lower the score
    /// (tips are not problems); <paramref name="fixedSome"/> = something was fixed since the scan.
    /// </summary>
    public static string VerdictDescription(int? score, int problemCount, bool fixedSome = false)
    {
        if (score is null)
            return "Запустите проверку — она займёт около минуты и ничего не изменит без вашего согласия.";
        if (problemCount == 0)
            return fixedSome
                ? "Все найденные проблемы исправлены. Нажмите «Проверить снова», чтобы обновить оценку."
                : "Проблем не найдено. Компьютер в отличной форме!";
        return score switch
        {
            >= 90 => "Серьёзных проблем нет, остались только мелочи и советы.",
            >= 75 => "Всё в порядке, но кое-что можно улучшить.",
            >= 50 => "Есть проблемы, которые замедляют компьютер или снижают защиту.",
            _ => "Найдены серьёзные проблемы — рекомендуем исправить их как можно скорее.",
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
            return $"Не удалось проверить {notChecked} — оценка учитывает только остальные и может быть завышена.";
        return fixedSome
            ? $"Найденные проблемы исправлены, но {notChecked} проверить не удалось — нажмите «Проверить снова»."
            : $"В проверенных разделах проблем нет, но {notChecked} проверить не удалось — оценка может быть завышена.";
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
            : issue.ReclaimableBytes > 0 ? "Освободит " + Format.Bytes(issue.ReclaimableBytes) : null;

    /// <summary>Issues that lower the score (everything except <see cref="IssueSeverity.Info"/> tips).</summary>
    public static bool IsProblem(HealthIssue issue) => issue.Severity > IssueSeverity.Info;

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

    /// <summary>"2 проблемы и 3 совета", "1 совет", "проблем нет" (tips are not called problems).</summary>
    public static string FindingsText(int problems, int tips)
    {
        if (problems <= 0 && tips <= 0)
            return "проблем нет";
        var parts = new List<string>(2);
        if (problems > 0)
            parts.Add(Format.Count(problems, "проблема", "проблемы", "проблем"));
        if (tips > 0)
            parts.Add(Format.Count(tips, "совет", "совета", "советов"));
        return string.Join(" и ", parts);
    }

    /// <summary>
    /// FixAsync has no success flag: modules return a message. Some messages mean "nothing was changed"
    /// (e.g. admin rights are required) or "the problem is still there" — such a result must not mark the issue as fixed.
    /// When in doubt the result counts as NOT fixed: claiming that a firewall or antivirus is on while it is still off
    /// is far worse than asking the user to check again.
    /// </summary>
    public static bool LooksLikeFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        var m = message.Trim().ToLowerInvariant();
        return LooksLikeAdminRequired(m)
               || StillBroken.IsMatch(m)
               || m.StartsWith("не удалось")
               || m.StartsWith("ошибка")
               || m.StartsWith("отменено")
               || m.Contains("ничего не изменено")
               || m.Contains("ничего не было изменено")
               || m.Contains("ничего не изменилось")
               || m.Contains("ничего не сделано")
               || m.Contains("ничего не удалено")
               // Only the cancellation phrases: a bare "отменено" also appears in successful tweak messages
               // ("Автообновление драйверов отменено") which must still count as fixed.
               || m.Contains("исправление отменено")
               || m.Contains("действие отменено")
               || m.Contains("операция отменена");
    }

    /// <summary>The fix result says administrator rights are required ("Нужны права администратора", "Недостаточно прав"...).</summary>
    public static bool LooksLikeAdminRequired(string? message)
        => !string.IsNullOrWhiteSpace(message) && AdminRequired.IsMatch(message);

    // Results that are returned normally (not "Не удалось …" at the start) but mean the problem is still there, e.g.
    //   "Команда выполнена, но брандмауэр всё ещё выключен для: …", "Windows не дала включить защиту …",
    //   "Защиту не удалось включить: …", "Раздел реестра с настройками UAC не найден.",
    //   "Недоступно в тестовом окружении.", "Эту рекомендацию нельзя применить автоматически.",
    //   "… (очистка прервана)." (stopped half-way), and partial bulk results that list failures:
    //   "Удалено 2 записи … Не удалось: 3." / "… Не удалось удалить (1): …".
    // Deliberately NOT matched: "Не удалось удалить 3 занятых файла" (a normal cleaning result) and
    // "Точку восстановления создать не удалось" (a side note of a successful fix).
    private static readonly Regex StillBroken = new(
        @"\b(?:всё|все)\s+(?:ещё|еще)\s+(?:вы|от)ключ|\bпо-прежнему\s+(?:вы|от)ключ" +
        @"|\bне\s+дал[аио]?\b" +
        @"|\bне\s+удалось\s+(?:включить|выключить|отключить)\b" +
        @"|\bне\s+найден[аы]?\b" +
        @"|\bв\s+тестовом\s+окружении\b" +
        @"|\bнельзя\s+применить\b" +
        @"|\bпрерван[аоы]?\b" +
        @"|\bне\s+удалось\s*:\s*\d|\bне\s+удалось\s+удалить\s*\(\s*\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // \b keeps "нет правил" / "недостаточно правильно" from matching.
    private static readonly Regex AdminRequired = new(
        @"прав(а|ами)?\s+админ|от\s+имени\s+админ|недостаточно\s+прав\b|\bнет\s+прав\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
