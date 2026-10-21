using System.Globalization;
using System.Net;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Dashboard;

/// <summary>
/// Renders a <see cref="DashboardReport"/> as a self-contained HTML page (UTF-8, inline CSS, no scripts and no external
/// resources; readable in light and dark mode and on paper) or as plain text for the clipboard. Pure: no I/O.
/// Every dynamic string is HTML-encoded.
/// </summary>
public static class ReportBuilder
{
    /// <summary>"Отчёт WaterCa7s PC Cleaner 2026-10-16 20-35.html" ("WaterCa7s PC Cleaner report 2026-10-16 20-35.html" in English).</summary>
    public static string DefaultFileName(DateTime time)
        => T.F("Отчёт WaterCa7s PC Cleaner {0}.html", time.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture));

    /// <summary>"16 октября 2026 г., 20:35" ("October 16, 2026, 20:35" in English): the format itself is the catalog text.</summary>
    public static string LongDateTime(DateTime time) => time.ToString(T._("d MMMM yyyy 'г.,' HH:mm"), T.Culture);

    // ============================================================ HTML

    public static string BuildHtml(DashboardReport r)
    {
        var sb = new StringBuilder(48 * 1024);
        var scoreClass = ScoreClass(r.Score, r.FailedModules);

        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(T.Language).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"color-scheme\" content=\"light dark\">\n");
        sb.Append("<meta name=\"generator\" content=\"").Append(E($"{r.AppName} {r.AppVersion}")).Append("\">\n");
        sb.Append("<title>").Append(E(T.F("Отчёт о проверке компьютера {0} — {1}", r.Pc.ComputerName, Format.DateTime(r.ScanTime))))
            .Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main class=\"page\">\n");

        // ----- masthead
        sb.Append("<header class=\"masthead\">\n");
        sb.Append("<div class=\"brand\"><span class=\"logo\" aria-hidden=\"true\"><span class=\"drop\"></span></span>")
            .Append("<span>").Append(E(r.AppName)).Append("</span></div>\n");
        sb.Append("<h1>").Append(T._("Отчёт о проверке компьютера")).Append("</h1>\n");
        sb.Append("<p class=\"meta\">").Append(T.F("Проверка: {0}", E(LongDateTime(r.ScanTime))))
            .Append(" · ").Append(T.F("Компьютер: {0}", E(r.Pc.ComputerName)));
        if (Math.Abs((r.CreatedAt - r.ScanTime).TotalMinutes) >= 1)
            sb.Append(" · ").Append(T.F("Отчёт создан: {0}", E(LongDateTime(r.CreatedAt))));
        sb.Append("</p>\n</header>\n");

        // ----- score
        var score = r.Score is { } s ? Math.Clamp(s, 0, 100) : 0;
        sb.Append("<section class=\"card hero\">\n");
        sb.Append("<div class=\"gauge ").Append(scoreClass).Append("\" style=\"--p:")
            .Append(score.ToString(CultureInfo.InvariantCulture)).Append("\" role=\"img\" aria-label=\"")
            .Append(E(r.Score is null ? T._("Оценки нет") : T.F("Оценка {0} из 100", score))).Append("\">")
            .Append("<div class=\"gauge-value\"><b>").Append(r.Score is null ? "—" : score.ToString(CultureInfo.InvariantCulture))
            .Append("</b><span>").Append(T._("из 100")).Append("</span></div></div>\n");
        sb.Append("<div class=\"hero-text\">\n<div class=\"eyebrow\">").Append(T._("Состояние компьютера")).Append("</div>\n");
        sb.Append("<div class=\"verdict ").Append(scoreClass).Append("\">").Append(E(r.Verdict)).Append("</div>\n");
        sb.Append("<p class=\"lead\">").Append(E(r.VerdictDescription)).Append("</p>\n<div class=\"chips\">");
        if (r.Delta is { } delta)
        {
            var trend = delta.Trend switch
            {
                ScoreTrend.Better => "chip-good",
                ScoreTrend.Worse => "chip-bad",
                _ => "",
            };
            sb.Append("<span class=\"chip ").Append(trend).Append("\" title=\"").Append(E(DeltaExplanation(delta))).Append("\">")
                .Append(E(delta.Text)).Append("</span>");
        }
        sb.Append("<span class=\"chip\">").Append(E(r.FindingsText)).Append("</span>");
        if (r.FixedCount > 0)
            sb.Append("<span class=\"chip chip-good\">").Append(E(T.F("Исправлено в этом сеансе: {0}", r.FixedCount))).Append("</span>");
        if (r.Reclaimable.AutoText is { } auto)
            sb.Append("<span class=\"chip chip-accent\">").Append(E(auto)).Append("</span>");
        if (r.Reclaimable.ManualText is { } manual)
            sb.Append("<span class=\"chip\" title=\"").Append(T._("Windows.old и другое, что удаляете вы сами в «Очистке диска»")).Append("\">")
                .Append(E(manual)).Append("</span>");
        sb.Append("</div>\n</div>\n</section>\n");

        if (!string.IsNullOrWhiteSpace(r.Warning))
            sb.Append("<p class=\"note note-warn\">").Append(E(r.Warning)).Append("</p>\n");

        // ----- computer + modules
        sb.Append("<div class=\"columns\">\n<section class=\"card\">\n<h2>").Append(T._("Об этом компьютере")).Append("</h2>\n<dl class=\"facts\">\n");
        foreach (var (label, value) in r.Pc.Rows())
            sb.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd></div>\n");
        sb.Append("</dl>\n</section>\n");

        sb.Append("<section class=\"card\">\n<h2>").Append(T._("Проверенные разделы")).Append("</h2>\n");
        if (r.Modules.Count == 0)
        {
            sb.Append("<p class=\"muted\">").Append(T._("Нет данных о разделах.")).Append("</p>\n");
        }
        else
        {
            sb.Append("<ul class=\"modules\">\n");
            foreach (var m in r.Modules)
            {
                sb.Append("<li class=\"").Append(ModuleClass(m.State)).Append("\"><span class=\"dot\" aria-hidden=\"true\"></span>")
                    .Append("<span class=\"mod-name\">").Append(E(m.Name)).Append("</span>")
                    .Append("<span class=\"mod-status\">").Append(E(Capitalize(m.StatusText)));
                if (!string.IsNullOrWhiteSpace(m.Error))
                    sb.Append("<br><small>").Append(E(m.Error)).Append("</small>");
                sb.Append("</span></li>\n");
            }
            sb.Append("</ul>\n");
        }
        sb.Append("</section>\n</div>\n");

        // ----- findings
        sb.Append("<section class=\"card\">\n<h2>").Append(T._("Найденные проблемы")).Append("</h2>\n");
        var all = r.AllIssues.ToList();
        if (all.Count == 0 && r.IsIncomplete)
        {
            sb.Append("<p class=\"muted\">")
                .Append(T._("В проверенных разделах проблем не найдено. Часть разделов проверить не удалось (см. «Проверенные разделы») — их проблемы в отчёт не попали."))
                .Append("</p>\n");
        }
        else if (all.Count == 0)
        {
            sb.Append("<div class=\"empty\"><div class=\"empty-mark\" aria-hidden=\"true\">✓</div>")
                .Append("<p><b>").Append(T._("Проблем не найдено")).Append("</b></p><p class=\"muted\">")
                .Append(T._("Компьютер в отличной форме. Чтобы так и оставалось, запускайте проверку примерно раз в неделю.")).Append("</p></div>\n");
        }
        else
        {
            var summary = r.FixedCount > 0 ? T.F("{0}, исправлено {1}", r.FindingsText, r.FixedCount) : r.FindingsText;
            sb.Append("<p class=\"muted\">").Append(E(summary)).Append(". ")
                .Append(T._("Проблемы сгруппированы по важности — сверху самые серьёзные.")).Append("</p>\n");
            foreach (var group in r.Groups.Where(g => g.Issues.Count > 0))
            {
                var sev = SeverityClass(group.Severity);
                sb.Append("<div class=\"group ").Append(sev).Append("\">\n<h3><span class=\"sev-dot\" aria-hidden=\"true\"></span>")
                    .Append(E(group.Title)).Append(" <span class=\"count\">").Append(group.Issues.Count.ToString(CultureInfo.InvariantCulture))
                    .Append("</span><span class=\"hint\">").Append(E(group.Hint)).Append("</span></h3>\n");
                foreach (var issue in group.Issues)
                    AppendIssue(sb, issue, group, sev);
                sb.Append("</div>\n");
            }
        }
        sb.Append("</section>\n");

        // ----- footer
        sb.Append("<footer class=\"footer\">\n<p>").Append(T.F("Отчёт создан программой {0}", $"<b>{E(r.AppName)}</b> {E(r.AppVersion)}"))
            .Append(" · <a href=\"").Append(E(r.RepoUrl)).Append("\">").Append(E(DisplayUrl(r.RepoUrl)))
            .Append("</a></p>\n<p>")
            .Append(T._("Перед тем как отправить отчёт другим людям, просмотрите его: в описаниях могут встречаться имена ваших файлов и папок. Пароли и содержимое файлов в отчёт не попадают."))
            .Append("</p>\n</footer>\n");

        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendIssue(StringBuilder sb, ReportIssue issue, ReportGroup group, string sev)
    {
        sb.Append("<article class=\"issue ").Append(sev).Append(issue.IsFixed ? " is-fixed" : "").Append("\">\n<div class=\"badges\">")
            .Append("<span class=\"badge badge-sev\">").Append(E(HealthScoring.SeverityLabel(group.Severity))).Append("</span>")
            .Append("<span class=\"badge\">").Append(E(issue.Module)).Append("</span>");
        if (issue.IsFixed)
            sb.Append("<span class=\"badge badge-ok\">✓ ").Append(T._("Исправлено")).Append("</span>");
        else if (issue.FixFailed)
            sb.Append("<span class=\"badge badge-bad\">").Append(T._("Не исправлено")).Append("</span>");
        sb.Append("</div>\n<h4>").Append(E(issue.Title)).Append("</h4>\n");
        if (!string.IsNullOrWhiteSpace(issue.Details))
            sb.Append("<p class=\"details\">").Append(E(issue.Details.Trim())).Append("</p>\n");
        if (!string.IsNullOrWhiteSpace(issue.Gain))
            sb.Append("<p class=\"gain\">").Append(E(issue.Gain)).Append("</p>\n");
        if (!string.IsNullOrWhiteSpace(issue.FixResult))
        {
            sb.Append("<p class=\"result ").Append(issue.IsFixed ? "result-ok" : "result-bad").Append("\"><b>")
                .Append(issue.IsFixed ? T._("Результат исправления:") : T._("Попытка исправления:")).Append("</b> ")
                .Append(E(issue.FixResult)).Append("</p>\n");
        }
        sb.Append("</article>\n");
    }

    // ============================================================ Plain text

    public static string BuildText(DashboardReport r)
    {
        var sb = new StringBuilder(16 * 1024);
        var nl = Environment.NewLine;

        sb.Append(T.F("Отчёт о проверке компьютера — {0}", r.AppName)).Append(nl);
        sb.Append(T.F("Проверка: {0}", LongDateTime(r.ScanTime))).Append(nl).Append(nl);

        sb.Append(T.F("Оценка: {0} — {1}", r.Score is { } s ? T.F("{0} из 100", Math.Clamp(s, 0, 100)) : T._("нет"), r.Verdict)).Append(nl);
        sb.Append(r.VerdictDescription).Append(nl);
        if (r.Delta is { } delta)
        {
            var explanation = DeltaExplanation(delta);
            sb.Append(delta.Text).Append(" — ").Append(char.ToLower(explanation[0], T.Culture)).Append(explanation[1..]).Append(nl);
        }
        sb.Append(r.FixedCount > 0 ? T.F("{0}, исправлено в этом сеансе: {1}", r.FindingsText, r.FixedCount) : r.FindingsText).Append(nl);
        if (r.Reclaimable.SummaryText is { } space)
            sb.Append(space).Append(nl);
        if (!string.IsNullOrWhiteSpace(r.Warning))
            sb.Append(T.F("Внимание: {0}", r.Warning)).Append(nl);

        sb.Append(nl).Append(T._("Об этом компьютере").ToUpper(T.Culture)).Append(nl);
        foreach (var (label, value) in r.Pc.Rows())
            sb.Append("  ").Append(label).Append(": ").Append(value).Append(nl);

        sb.Append(nl).Append(T._("Проверенные разделы").ToUpper(T.Culture)).Append(nl);
        foreach (var m in r.Modules)
        {
            sb.Append("  ").Append(ModuleMark(m.State)).Append(' ').Append(m.Name).Append(" — ").Append(m.StatusText);
            if (!string.IsNullOrWhiteSpace(m.Error))
                sb.Append(" (").Append(m.Error.Trim()).Append(')');
            sb.Append(nl);
        }

        sb.Append(nl).Append(T._("Найденные проблемы").ToUpper(T.Culture)).Append(nl);
        if (!r.AllIssues.Any())
        {
            sb.Append("  ").Append(r.IsIncomplete
                ? T._("В проверенных разделах проблем не найдено (часть разделов проверить не удалось).")
                : T._("Проблем не найдено.")).Append(nl);
        }
        else
        {
            foreach (var group in r.Groups.Where(g => g.Issues.Count > 0))
            {
                sb.Append(nl).Append(group.Title).Append(" (").Append(group.Issues.Count).Append(") · ").Append(group.Hint).Append(nl);
                var n = 0;
                foreach (var issue in group.Issues)
                {
                    n++;
                    sb.Append("  ").Append(n).Append(". [").Append(issue.Module).Append("] ").Append(issue.Title);
                    if (issue.IsFixed)
                        sb.Append(" — ").Append(T._("исправлено"));
                    sb.Append(nl);
                    if (!string.IsNullOrWhiteSpace(issue.Details))
                        AppendIndented(sb, issue.Details, "     ", nl);
                    if (!string.IsNullOrWhiteSpace(issue.Gain))
                        sb.Append("     ").Append(T.F("Что даст: {0}", issue.Gain)).Append(nl);
                    if (!string.IsNullOrWhiteSpace(issue.FixResult))
                        sb.Append("     ").Append(issue.IsFixed ? T.F("Результат исправления: {0}", issue.FixResult) : T.F("Не исправлено: {0}", issue.FixResult)).Append(nl);
                }
            }
        }

        sb.Append(nl).Append("—").Append(nl)
            .Append(r.AppName).Append(' ').Append(r.AppVersion).Append(" · ").Append(r.RepoUrl).Append(nl);
        return sb.ToString();
    }

    private static void AppendIndented(StringBuilder sb, string text, string indent, string nl)
    {
        foreach (var line in text.Trim().Replace("\r\n", "\n").Split('\n'))
            sb.Append(indent).Append(line.TrimEnd()).Append(nl);
    }

    // ============================================================ Helpers

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>
    /// "Сравнение с проверкой 14 октября 2026 г., 09:00". Absolute, not "вчера": a saved report is read days later.
    /// </summary>
    internal static string DeltaExplanation(ScoreDelta delta) => delta.PreviousTime is { } t
        ? T.F("Сравнение с проверкой {0}", LongDateTime(t))
        : T._("Сравнение с прошлой проверкой");

    private static string Capitalize(string? s)
        => string.IsNullOrEmpty(s) ? "" : char.ToUpper(s[0], T.Culture) + s[1..];

    private static string DisplayUrl(string url)
    {
        var u = url.Trim();
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return u[prefix.Length..].TrimEnd('/');
        }
        return u;
    }

    internal static string ScoreClass(int? score) => score switch
    {
        null => "score-none",
        >= 75 => "score-good",
        >= 50 => "score-warn",
        _ => "score-bad",
    };

    /// <summary>Like the dashboard gauge: an incomplete scan is never green (amber instead, red stays red).</summary>
    internal static string ScoreClass(int? score, int failedModules)
        => score is >= 50 && failedModules > 0 ? "score-warn" : ScoreClass(score);

    internal static string SeverityClass(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Critical => "sev-critical",
        IssueSeverity.High => "sev-high",
        IssueSeverity.Medium => "sev-medium",
        IssueSeverity.Low => "sev-low",
        > IssueSeverity.Critical => "sev-critical",
        _ => "sev-info",
    };

    private static string ModuleClass(ModuleScanState state) => state switch
    {
        ModuleScanState.Done => "mod mod-done",
        ModuleScanState.Failed => "mod mod-failed",
        ModuleScanState.TimedOut => "mod mod-timeout",
        _ => "mod mod-other",
    };

    private static string ModuleMark(ModuleScanState state) => state switch
    {
        ModuleScanState.Done => "✓",
        ModuleScanState.Failed => "✗",
        ModuleScanState.TimedOut => "⏱",
        _ => "–",
    };

    // The source file may be checked out with CRLF: keep the generated page consistently LF.
    private static readonly string Css = CssSource.ReplaceLineEndings("\n");

    // Light by default, dark via prefers-color-scheme, always light on paper. No url(), no @import, no fonts to download.
    private const string CssSource = """
:root {
  color-scheme: light dark;
  --bg: #f4f6f9; --surface: #ffffff; --surface2: #f0f3f7; --line: #dde3eb;
  --text: #1a2029; --muted: #5d6878;
  --accent: #0e9f6e; --accent-soft: #e3f6ee;
  --danger: #d23f3f; --danger-soft: #fbe9e9;
  --warning: #b7791f; --warning-soft: #fdf3dc;
  --info: #2f6fd6; --info-soft: #e6effc;
  --track: #e3e8ef;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #0f1218; --surface: #171b23; --surface2: #1f242e; --line: #2a3140;
    --text: #e7eaf0; --muted: #8e98aa;
    --accent: #34d399; --accent-soft: rgba(52, 211, 153, .12);
    --danger: #f87171; --danger-soft: rgba(248, 113, 113, .14);
    --warning: #fbbf24; --warning-soft: rgba(251, 191, 36, .14);
    --info: #60a5fa; --info-soft: rgba(96, 165, 250, .14);
    --track: #293040;
  }
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body {
  margin: 0; background: var(--bg); color: var(--text);
  font: 15px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif;
}
.page { max-width: 980px; margin: 0 auto; padding: 32px 24px 40px; }
h1, h2, h3, h4 { margin: 0; line-height: 1.25; }
h1 { font-size: 28px; font-weight: 650; margin-top: 14px; }
h2 { font-size: 19px; font-weight: 600; margin-bottom: 14px; }
.muted { color: var(--muted); }
.masthead { margin-bottom: 20px; }
.brand { display: flex; align-items: center; gap: 10px; font-weight: 700; font-size: 15px; }
.logo { width: 30px; height: 30px; border-radius: 9px; background: var(--accent); display: inline-flex; align-items: center; justify-content: center; }
.drop { width: 11px; height: 11px; background: #06261a; border-radius: 0 50% 50% 50%; transform: rotate(45deg); margin-top: 4px; }
.meta { color: var(--muted); margin: 6px 0 0; }
.card { background: var(--surface); border: 1px solid var(--line); border-radius: 12px; padding: 22px 24px; margin-bottom: 16px; }
.hero { display: flex; gap: 28px; align-items: center; }
.gauge {
  --p: 0; --c: var(--muted);
  flex: 0 0 auto; width: 148px; height: 148px; border-radius: 50%;
  background: conic-gradient(var(--c) calc(var(--p) * 1%), var(--track) 0);
  display: grid; place-items: center;
  -webkit-print-color-adjust: exact; print-color-adjust: exact;
}
.gauge::before { content: ""; grid-area: 1 / 1; width: 120px; height: 120px; border-radius: 50%; background: var(--surface); }
.gauge-value { grid-area: 1 / 1; position: relative; text-align: center; line-height: 1.05; }
.gauge-value b { display: block; font-size: 44px; font-weight: 700; }
.gauge-value span { color: var(--muted); font-size: 13px; }
.score-good { --c: var(--accent); }
.score-warn { --c: var(--warning); }
.score-bad { --c: var(--danger); }
.score-none { --c: var(--muted); }
.eyebrow { font-size: 12px; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; color: var(--muted); }
.verdict { font-size: 30px; font-weight: 650; color: var(--c); margin: 2px 0 4px; }
.lead { margin: 0 0 12px; color: var(--muted); }
.chips { display: flex; flex-wrap: wrap; gap: 8px; }
.chip { background: var(--surface2); border-radius: 7px; padding: 4px 10px; font-size: 13.5px; }
.chip-accent, .chip-good { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
.chip-bad { background: var(--danger-soft); color: var(--danger); font-weight: 600; }
.note { border-radius: 10px; padding: 11px 14px; margin: 0 0 16px; }
.note-warn { background: var(--warning-soft); }
.columns { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 16px; align-items: start; }
.columns > .card { margin-bottom: 16px; }
.facts { margin: 0; }
.facts > div { display: grid; grid-template-columns: 150px minmax(0, 1fr); gap: 12px; padding: 7px 0; border-top: 1px solid var(--line); }
.facts > div:first-child { border-top: 0; padding-top: 0; }
.facts dt { color: var(--muted); }
.facts dd { margin: 0; overflow-wrap: anywhere; }
.modules { list-style: none; margin: 0; padding: 0; }
.mod { display: grid; grid-template-columns: 12px 120px minmax(0, 1fr); gap: 10px; align-items: baseline; padding: 7px 0; border-top: 1px solid var(--line); }
.mod:first-child { border-top: 0; padding-top: 0; }
.mod .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--muted); -webkit-print-color-adjust: exact; print-color-adjust: exact; }
.mod-name { font-weight: 600; }
.mod-status { color: var(--muted); }
.mod-status small { font-size: 12.5px; }
.mod-done .dot { background: var(--accent); }
.mod-failed .dot { background: var(--danger); }
.mod-failed .mod-status { color: var(--danger); }
.mod-timeout .dot { background: var(--warning); }
.mod-timeout .mod-status { color: var(--warning); }
.group { margin-top: 22px; }
.group h3 { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; font-size: 16px; margin-bottom: 10px; }
.group .count { background: var(--sev-soft); color: var(--sev); font-size: 12.5px; font-weight: 700; border-radius: 6px; padding: 1px 8px; }
.group .hint { color: var(--muted); font-size: 13px; font-weight: 400; }
.sev-dot { width: 10px; height: 10px; border-radius: 50%; background: var(--sev); -webkit-print-color-adjust: exact; print-color-adjust: exact; }
.sev-critical, .sev-high { --sev: var(--danger); --sev-soft: var(--danger-soft); }
.sev-medium { --sev: var(--warning); --sev-soft: var(--warning-soft); }
.sev-low { --sev: var(--info); --sev-soft: var(--info-soft); }
.sev-info { --sev: var(--accent); --sev-soft: var(--accent-soft); }
.issue {
  background: var(--surface2); border-radius: 10px; border-left: 4px solid var(--sev);
  padding: 12px 16px; margin-bottom: 10px; break-inside: avoid; page-break-inside: avoid;
}
.issue.is-fixed { opacity: .8; }
.badges { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 6px; }
.badge { background: var(--surface); border: 1px solid var(--line); color: var(--muted); border-radius: 5px; padding: 0 7px; font-size: 12.5px; }
.badge-sev { background: var(--sev-soft); border-color: transparent; color: var(--sev); font-weight: 600; }
.badge-ok { background: var(--accent-soft); border-color: transparent; color: var(--accent); font-weight: 600; }
.badge-bad { background: var(--danger-soft); border-color: transparent; color: var(--danger); font-weight: 600; }
.issue h4 { font-size: 15.5px; font-weight: 600; }
.details { margin: 4px 0 0; color: var(--muted); white-space: pre-line; overflow-wrap: anywhere; }
.gain { margin: 8px 0 0; color: var(--accent); font-weight: 600; }
.result { margin: 10px 0 0; border-radius: 8px; padding: 8px 12px; overflow-wrap: anywhere; }
.result-ok { background: var(--accent-soft); }
.result-bad { background: var(--danger-soft); }
.empty { text-align: center; padding: 18px 0 8px; }
.empty p { margin: 4px 0; }
.empty-mark { width: 56px; height: 56px; margin: 0 auto 10px; border-radius: 50%; background: var(--accent-soft); color: var(--accent); font-size: 28px; line-height: 56px; }
.footer { color: var(--muted); font-size: 13px; text-align: center; margin-top: 26px; }
.footer p { margin: 4px 0; }
.footer a { color: var(--accent); }
@media (max-width: 720px) {
  .page { padding: 20px 14px 32px; }
  .hero { flex-direction: column; align-items: flex-start; }
  .columns { grid-template-columns: minmax(0, 1fr); gap: 0; }
  .facts > div { grid-template-columns: minmax(0, 1fr); gap: 0; }
  .mod { grid-template-columns: 12px minmax(0, 1fr); row-gap: 0; }
  .mod-status { grid-column: 2; }
  h1 { font-size: 24px; }
}
@media print {
  :root {
    color-scheme: light;
    --bg: #ffffff; --surface: #ffffff; --surface2: #f6f7f9; --line: #d5dbe3;
    --text: #000000; --muted: #4a5361;
    --accent: #0b7d57; --accent-soft: #e3f6ee;
    --danger: #b42323; --danger-soft: #fbe9e9;
    --warning: #8a5a0b; --warning-soft: #fdf3dc;
    --info: #1f57b0; --info-soft: #e6effc;
    --track: #e3e8ef;
  }
  @page { margin: 14mm; }
  body { font-size: 12.5px; }
  .page { max-width: none; padding: 0; }
  .card { border-color: var(--line); break-inside: auto; }
  .hero, .columns > .card, h2, h3 { break-inside: avoid; page-break-inside: avoid; }
  h2, h3 { break-after: avoid; page-break-after: avoid; }
  .chip, .badge, .count, .result, .issue, .note, .logo { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  .footer a { color: var(--text); text-decoration: none; }
}

""";
}
