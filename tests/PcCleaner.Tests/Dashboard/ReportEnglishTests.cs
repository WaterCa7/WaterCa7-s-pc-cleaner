using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// The report (HTML page, plain text, file name) in English (the real en.json): the language of the page, every label, the dates,
/// the severity badges. The user's own data in the sample is English too, so a single Cyrillic letter in the output is a text
/// that was left untranslated. Runs alone (the language is process-wide) and always switches back to Russian.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class ReportEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    public ReportEnglishTests() => TestEnvironment.Init();

    private static HealthIssue NewIssue(string module, IssueSeverity severity, string title, string details = "Details", string? gain = null,
        bool fixable = false)
        => new()
        {
            Module = module,
            Title = title,
            Details = details,
            Severity = severity,
            Gain = gain,
            FixAsync = fixable ? _ => Task.FromResult("Done.") : null,
        };

    /// <summary>The same report as the Russian tests use, with English data (a score of 67, a previous check on Oct 14).</summary>
    private static DashboardReport SampleReport(IEnumerable<HealthIssue>? issues = null)
    {
        var list = (issues ?? new[]
        {
            NewIssue("Cleanup", IssueSeverity.Info, "The Recycle Bin is not empty", fixable: true),
            NewIssue("Security", IssueSeverity.Critical, "Antivirus is off", "Real-time protection is turned off."),
            NewIssue("Disks", IssueSeverity.Medium, "Low space on drive D:", gain: "Free up about 10 GB"),
        }).ToList();
        return new DashboardReport
        {
            CreatedAt = new DateTime(2026, 10, 16, 14, 40, 0),
            ScanTime = new DateTime(2026, 10, 16, 14, 32, 0),
            Score = 67,
            Delta = ScoreDelta.Create(52, 67, new DateTime(2026, 10, 14, 9, 0, 0)),
            Reclaimable = ReclaimableSplit.From(list),
            Pc = new ReportPcInfo
            {
                ComputerName = "WATERCAT-PC",
                WindowsName = "Windows 11 Home",
                CpuName = "Intel Core i7-10700K",
                LogicalProcessors = 16,
                RamBytes = 32UL * 1024 * 1024 * 1024,
                SystemDrive = "C:",
                SystemDriveFree = 400L * 1024 * 1024 * 1024,
                SystemDriveTotal = 1000L * 1024 * 1024 * 1024,
                Uptime = TimeSpan.FromHours(30),
                IsAdmin = false,
            },
            Modules = new[]
            {
                new ReportModule("Cleanup", ModuleScanState.Done, "done: 1 tip", null),
                new ReportModule("Programs", ModuleScanState.Failed, "check failed", "No access to the registry"),
            },
            Groups = DashboardReport.GroupIssues(list),
            Warning = "Could not check: Programs.",
            AppVersion = "1.2.3",
        };
    }

    [Fact]
    public void TheHtmlPage_IsEnglish_WithLangEn_AndEveryHeading()
    {
        using var language = TestLanguage.English();

        var html = ReportBuilder.BuildHtml(SampleReport());

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<title>Computer check report WATERCAT-PC — ", html);
        Assert.Contains("<h1>Computer check report</h1>", html);
        Assert.Contains("Checked: October 16, 2026, 14:32", html);
        Assert.Contains("Computer: WATERCAT-PC", html);
        Assert.Contains("Report created: October 16, 2026, 14:40", html);
        Assert.Contains("<div class=\"eyebrow\">Computer status</div>", html);
        Assert.Contains("aria-label=\"Score 67 out of 100\"", html);
        Assert.Contains("<span>out of 100</span>", html);
        Assert.Contains("<h2>About this computer</h2>", html);
        Assert.Contains("<h2>Checked sections</h2>", html);
        Assert.Contains("<h2>Problems found</h2>", html);
        Assert.Contains("Problems are grouped by importance — the most serious are at the top.", html);
        Assert.Contains("Report created by <b>WaterCa7s PC Cleaner</b> 1.2.3", html);
        Assert.Contains("Passwords and file contents are not included in the report.", html);
        Assert.DoesNotMatch(Cyrillic, html);
    }

    [Fact]
    public void TheSeverityBadges_AreEnglish_AndATipIsATip_NotTheGroupTitle()
    {
        using var language = TestLanguage.English();

        var html = ReportBuilder.BuildHtml(SampleReport());

        Assert.Contains("<span class=\"badge badge-sev\">Critical</span>", html);
        Assert.Contains("<span class=\"badge badge-sev\">Medium</span>", html);
        Assert.Contains("<span class=\"badge badge-sev\">Tip</span>", html); // not "Tips": the group title is plural, the badge is not
        Assert.DoesNotContain("badge-sev\">Tips<", html);
        Assert.Contains("<h3><span class=\"sev-dot\" aria-hidden=\"true\"></span>Tips ", html);
        Assert.Equal("Tip", HealthScoring.SeverityLabel(IssueSeverity.Info));
    }

    [Fact]
    public void TheResultOfAFix_IsLabelledInEnglish_InHtmlAndText()
    {
        var fixedIssue = NewIssue("Disks", IssueSeverity.Medium, "Low space on drive D:", gain: "Free up about 10 GB");
        fixedIssue.IsFixed = true;
        fixedIssue.FixResult = "Done.";
        var failedIssue = NewIssue("Security", IssueSeverity.Critical, "Antivirus is off");
        failedIssue.FixResult = "Could not turn it on.";
        using var language = TestLanguage.English();

        var report = SampleReport(new[] { fixedIssue, failedIssue });
        var html = ReportBuilder.BuildHtml(report);
        var text = ReportBuilder.BuildText(report);

        Assert.Contains("<span class=\"badge badge-ok\">✓ Fixed</span>", html);
        Assert.Contains("<span class=\"badge badge-bad\">Not fixed</span>", html);
        Assert.Contains("<b>Fix result:</b> Done.", html);
        Assert.Contains("<b>Fix attempt:</b> Could not turn it on.", html);
        Assert.Contains("Fixed in this session: 1", html);
        Assert.Contains("1. [Disks] Low space on drive D: — fixed", text);
        Assert.Contains("     What you gain: Free up about 10 GB", text);
        Assert.Contains("     Fix result: Done.", text);
        Assert.Contains("     Not fixed: Could not turn it on.", text);
        Assert.DoesNotMatch(Cyrillic, html);
        Assert.DoesNotMatch(Cyrillic, text);
    }

    [Fact]
    public void TheTextReport_IsEnglish_WithItsHeadingsInCapitals()
    {
        using var language = TestLanguage.English();

        var text = ReportBuilder.BuildText(SampleReport());

        Assert.Contains("Computer check report — WaterCa7s PC Cleaner", text);
        Assert.Contains("Checked: October 16, 2026, 14:32", text);
        Assert.Contains("Score: 67 out of 100 — ", text);
        Assert.Contains("Was 52 → now 67 (+15) — compared with the check of October 14, 2026, 09:00", text);
        Assert.Contains("Warning: Could not check: Programs.", text);
        Assert.Contains("ABOUT THIS COMPUTER", text);
        Assert.Contains("CHECKED SECTIONS", text);
        Assert.Contains("PROBLEMS FOUND", text);
        Assert.Contains("✗ Programs — check failed (No access to the registry)", text);
        Assert.DoesNotMatch(Cyrillic, text);
    }

    [Fact]
    public void ACleanReport_SaysSoInEnglish()
    {
        using var language = TestLanguage.English();
        var report = SampleReport(Array.Empty<HealthIssue>());

        var html = ReportBuilder.BuildHtml(report);
        var text = ReportBuilder.BuildText(report);

        Assert.Contains("<p><b>No problems found</b></p>", html);
        Assert.Contains("Your computer is in great shape.", html);
        Assert.Contains("  No problems found.", text);
        Assert.DoesNotMatch(Cyrillic, html);
    }

    [Fact]
    public void TheDefaultFileName_AndTheDates_FollowTheLanguage()
    {
        var time = new DateTime(2026, 10, 6, 9, 5, 0);

        Assert.Equal("Отчёт WaterCa7s PC Cleaner 2026-10-06 09-05.html", ReportBuilder.DefaultFileName(time));
        Assert.Equal("6 октября 2026 г., 09:05", ReportBuilder.LongDateTime(time));
        using (TestLanguage.English())
        {
            var name = ReportBuilder.DefaultFileName(time);

            Assert.Equal("WaterCa7s PC Cleaner report 2026-10-06 09-05.html", name);
            Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.Equal("October 6, 2026, 09:05", ReportBuilder.LongDateTime(time));
        }
    }

    [Fact]
    public void TheRussianReport_KeepsLangRu_AndItsTexts()
    {
        var html = ReportBuilder.BuildHtml(SampleReport());

        Assert.Contains("<html lang=\"ru\">", html);
        Assert.Contains("<h1>Отчёт о проверке компьютера</h1>", html);
        Assert.Contains("Проверка: 16 октября 2026 г., 14:32", html);
        Assert.Contains("aria-label=\"Оценка 67 из 100\"", html);
        Assert.Contains("<span class=\"badge badge-sev\">Совет</span>", html);
        Assert.Contains("title=\"Windows.old и другое, что удаляете вы сами в «Очистке диска»\"", ReportBuilder.BuildHtml(SampleReport(ManualSpace())));
    }

    /// <summary>A finding without a fix button that holds space: the report notes what the user frees by hand.</summary>
    private static HealthIssue[] ManualSpace() => new[]
    {
        new HealthIssue { Module = "Disks", Title = "Windows.old", Severity = IssueSeverity.Low, ReclaimableBytes = 5L * 1024 * 1024 * 1024 },
    };

    [Fact]
    public void TheHintOfTheSpaceYouFreeByHand_IsEnglish()
    {
        using var language = TestLanguage.English();

        var html = ReportBuilder.BuildHtml(SampleReport(ManualSpace()));

        Assert.Contains("title=\"Windows.old and other things you delete yourself in Disk Cleanup\"", html);
        Assert.DoesNotMatch(Cyrillic, html);
    }
}
