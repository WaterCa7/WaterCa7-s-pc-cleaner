using System.Reflection;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// The Dashboard in English (the real en.json): group titles, times, counts, buttons, the texts of a scan and of the fixes.
/// Runs alone (the language is process-wide) and always switches back to Russian.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class DashboardEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    public DashboardEnglishTests() => TestEnvironment.Init();

    private static HealthIssue NewIssue(string title, Func<CancellationToken, Task<string>>? fix = null,
        IssueSeverity severity = IssueSeverity.Medium, long reclaimable = 1024 * 1024)
        => new()
        {
            Module = "Cleanup",
            Title = title,
            Details = "Details of the problem",
            Severity = severity,
            ReclaimableBytes = reclaimable,
            FixAsync = fix,
            TargetPageKey = "Cleaner",
        };

    private static DashboardViewModel Create(UiMode mode, params HealthIssue[] issues)
        => new(new IHealthContributor[] { new FakeContributor("Cleanup", issues) }, new AppSettings { UiMode = mode })
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            Now = () => new DateTime(2026, 10, 18, 14, 32, 0),
        };

    private static Task<string> Done(CancellationToken _) => Task.FromResult("Done.");

    [Fact]
    public void TheGroupTitlesHintsAndBadges_AreEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal(new[] { "Critical", "Important", "Medium", "Low", "Tips" },
                HealthScoring.SeverityOrder.Select(HealthScoring.GroupTitle));
            Assert.Equal(new[] { "Critical", "Important", "Medium", "Low", "Tip" },
                HealthScoring.SeverityOrder.Select(HealthScoring.SeverityLabel));
            Assert.Equal("Worth fixing when you have a minute", HealthScoring.GroupHint(IssueSeverity.Medium));
            Assert.All(HealthScoring.SeverityOrder, s => Assert.DoesNotMatch(Cyrillic, HealthScoring.GroupHint(s)));
        }

        Assert.Equal("Критично", HealthScoring.GroupTitle(IssueSeverity.Critical));
        Assert.Equal("Совет", HealthScoring.SeverityLabel(IssueSeverity.Info));
    }

    [Fact]
    public void RelativeTime_UsesTheDateFormatsOfTheLanguage()
    {
        var now = new DateTime(2026, 10, 18, 20, 0, 0);

        using (TestLanguage.English())
        {
            Assert.Equal("today at 14:32", HealthScoring.RelativeTime(new DateTime(2026, 10, 18, 14, 32, 0), now));
            Assert.Equal("yesterday at 09:05", HealthScoring.RelativeTime(new DateTime(2026, 10, 17, 9, 5, 0), now));
            Assert.Equal("Oct 12 at 14:32", HealthScoring.RelativeTime(new DateTime(2026, 10, 12, 14, 32, 0), now));
            Assert.Equal("Oct 12, 2025 at 14:32", HealthScoring.RelativeTime(new DateTime(2025, 10, 12, 14, 32, 0), now));
        }

        Assert.Equal("сегодня в 14:32", HealthScoring.RelativeTime(new DateTime(2026, 10, 18, 14, 32, 0), now));
        Assert.Equal("12 октября 2025 г. в 14:32", HealthScoring.RelativeTime(new DateTime(2025, 10, 12, 14, 32, 0), now));
    }

    [Fact]
    public void ProblemsAndFindings_AreCountedInEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("no problems", HealthScoring.ProblemsText(0));
            Assert.Equal("1 problem", HealthScoring.ProblemsText(1));
            Assert.Equal("3 problems", HealthScoring.ProblemsText(3));
            Assert.Equal("2 problems and 3 tips", HealthScoring.FindingsText(2, 3));
            Assert.Equal("1 tip", HealthScoring.FindingsText(0, 1));
            Assert.Equal("1 problem", HealthScoring.FindingsText(1, 0));
            Assert.Equal("no problems", HealthScoring.FindingsText(0, 0));
        }

        Assert.Equal("2 проблемы и 3 совета", HealthScoring.FindingsText(2, 3));
    }

    [Fact]
    public void TheVerdicts_AndTheGain_AreEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("The computer has not been checked yet", HealthScoring.Verdict(null));
            Assert.Equal("Excellent", HealthScoring.Verdict(95));
            Assert.Equal("Good", HealthScoring.Verdict(80));
            Assert.Equal("Needs attention", HealthScoring.Verdict(60));
            Assert.Equal("Poor", HealthScoring.Verdict(10));
            Assert.Equal("Check incomplete", HealthScoring.Verdict(90, failedModules: 2));
            Assert.Equal("Could not check 2 sections — the score counts only the others and may be too high.",
                HealthScoring.VerdictDescription(80, problemCount: 2, fixedSome: false, failedModules: 2));
            Assert.Equal("Frees up 1 MB", HealthScoring.GainText(NewIssue("x")));
        }
    }

    [Fact]
    public void TheScoreDelta_AndTheReclaimableSplit_AreEnglish()
    {
        var now = new DateTime(2026, 10, 18, 20, 0, 0);
        var delta = ScoreDelta.Create(52, 78, new DateTime(2026, 10, 17, 9, 5, 0))!;
        var split = new ReclaimableSplit(2L * 1024 * 1024 * 1024, 41L * 1024 * 1024 * 1024);

        using (TestLanguage.English())
        {
            Assert.Equal("Was 52 → now 78 (+26)", delta.Text);
            Assert.Equal("Compared with the previous check (yesterday at 09:05)", delta.Explanation(now));
            Assert.Equal("no change", ScoreDelta.Create(70, 70)!.ChangeText);
            Assert.Equal("You can free up 2 GB", split.AutoText);
            Assert.Equal("Up to 41 GB more — if you decide to delete it yourself", split.ManualText);
            Assert.Equal("You can free up 2 GB (up to 41 GB more — if you decide to delete it yourself)", split.SummaryText);
        }
    }

    [Fact]
    public void TheSystemInfo_ReadsInEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("Windows 11 Home", SystemInfo.WindowsName("Windows 10 Home", 26100));
            Assert.Equal("24H2, build 26100.2033", SystemInfo.WindowsVersionText("24H2", 26100, 2033));
            Assert.Equal("Unknown processor", SystemInfo.CleanCpuName(""));
        }

        Assert.Equal("Windows 11 Домашняя", SystemInfo.WindowsName("Windows 10 Home", 26100));
    }

    [Fact]
    public void TheReportRows_AreEnglish()
    {
        var pc = new ReportPcInfo { ComputerName = "PC", CpuName = "Ryzen", LogicalProcessors = 1, RamBytes = 16UL * 1024 * 1024 * 1024, IsAdmin = true };

        using (TestLanguage.English())
        {
            var rows = pc.Rows().ToDictionary(r => r.Label, r => r.Value);
            Assert.Equal("PC", rows["Computer name"]);
            Assert.Equal("Ryzen (1 logical processor)", rows["Processor"]);
            Assert.Equal("16 GB", rows["Memory (RAM)"]);
            Assert.Equal("no data", rows["Graphics card"]);
            Assert.Equal("administrator — all checks are available", rows["Program rights"]);
        }
    }

    [Fact]
    public void ABatchSummary_IsEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("Fixed 3 problems. Click “Check again” to update the score.",
                DashboardViewModel.BatchSummaryText(new FixBatchResult(3, 3, 0, false, 0)));
            Assert.Equal("Fixed 1 of 3. About 1 MB freed. Failed: 2 — the reason is given in the problem row. " +
                         "Administrator rights are needed for 1. Click “Check again” to update the score.",
                DashboardViewModel.BatchSummaryText(new FixBatchResult(3, 1, 2, false, 1024 * 1024, NeedAdmin: 1)));
            Assert.Equal("Fixing was canceled — nothing was changed.",
                DashboardViewModel.BatchSummaryText(new FixBatchResult(2, 0, 0, true, 0)));
        }
    }

    [Fact]
    public void AModuleRow_ShowsItsStatusInEnglish()
    {
        using (TestLanguage.English())
        {
            var row = new ModuleScanRow("Cleanup");
            Assert.Equal("Waiting", row.DisplayStatus);
            row.SetRunning();
            Assert.Equal("Checking…", row.DisplayStatus);
            row.SetResult(new ModuleScanResult("Cleanup", ModuleScanState.Done, new[] { NewIssue("x") }, null, TimeSpan.Zero));
            Assert.Equal("Done: 1 problem", row.DisplayStatus);
            row.SetResult(new ModuleScanResult("Cleanup", ModuleScanState.Failed, Array.Empty<HealthIssue>(), "boom", TimeSpan.Zero));
            Assert.Equal("Check failed", row.DisplayStatus);
            Assert.Contains("The other sections were checked as usual. Open the section “Cleanup” to see the details.", row.ToolTipText);
        }
    }

    [Fact]
    public async Task TheExpertScan_SaysItInEnglish()
    {
        using (TestLanguage.English())
        {
            var vm = Create(UiMode.Expert, NewIssue("Temporary files", Done), NewIssue("Tip", severity: IssueSeverity.Info, reclaimable: 0));

            Assert.Equal("Home", vm.Title);
            Assert.Equal("Check computer", vm.ScanButtonText);
            Assert.Equal("The computer has not been checked yet", vm.VerdictText);
            Assert.Equal("No check has been run yet", vm.LastScanText);
            Assert.Equal(new[] { "Cleanup", "Startup", "Processes", "Programs", "Speed up", "Network", "Security" },
                vm.QuickLinks.Select(l => l.Title));
            Assert.Equal("Open the “Cleanup” page", vm.FeaturedLink.ToolTip);

            await vm.ScanCoreAsync(CancellationToken.None, persist: false);

            Assert.Equal("Check again", vm.ScanButtonText);
            Assert.Equal("Excellent", vm.VerdictText);
            Assert.Equal("Last check: today at 14:32", vm.LastScanText);
            Assert.Equal("Found: 1 problem and 1 tip", vm.IssueCountText);
            Assert.Equal("Found 1 problem and 1 tip · can be fixed from here: 1", vm.ResultsSummaryText);
            Assert.Equal("Fix all (1)", vm.FixAllText);
            Assert.Equal(new[] { "Medium", "Tips" }, vm.Groups.Select(g => g.Title));

            var row = vm.Groups.SelectMany(g => g.AllItems).Single(i => i.Issue.Title == "Temporary files");
            Assert.Equal("Medium", row.SeverityText);
            Assert.Equal("Fix", row.FixLabelText);
            Assert.Equal("Frees up 1 MB", row.GainText);
            Assert.Equal("Included in “Fix all”", row.AutoFixHint);
            Assert.Equal("Open the page that found the problem", row.DetailsToolTip);
            Assert.Equal("Show in full", row.ExpandText);
            Assert.Equal("Open settings", row.SettingsLabel);
            Assert.Equal("Problem: Temporary files\n\nDetails of the problem\n\nWhat will change: Frees up 1 MB\n\n" +
                         "The fix is done by the section “Cleanup” — only what is described above. " +
                         "Everything done is recorded in the log (“Settings” → action log).\n\nFix it now?",
                DashboardViewModel.FixConfirmText(row).Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public async Task TheFixAllQuestion_AndTheFixResult_AreEnglish()
    {
        using (TestLanguage.English())
        {
            var vm = Create(UiMode.Simple, NewIssue("Temporary files", Done), NewIssue("Cache", Done));
            var questions = new List<(string Title, string Message)>();
            var previous = Dialogs.ConfirmHandler;
            Dialogs.ConfirmHandler = (title, message) =>
            {
                questions.Add((title, message));
                return true;
            };
            try
            {
                await vm.CheckAndFixCommand.ExecuteAsync();
            }
            finally
            {
                Dialogs.ConfirmHandler = previous;
            }

            var (title, message) = Assert.Single(questions);
            Assert.Equal("Fix all", title);
            Assert.StartsWith("These problems will be fixed (2):", message);
            Assert.EndsWith("Continue?", message);
            Assert.DoesNotMatch(Cyrillic, message);
            Assert.Equal("Fixed 2 problems. About 2 MB freed. Click “Check again” to update the score.", vm.ActionSummary);
        }
    }

    [Fact]
    public async Task EveryTextOfAnEnglishDashboard_IsEnglish()
    {
        using (TestLanguage.English())
        {
            foreach (var mode in new[] { UiMode.Simple, UiMode.Expert })
            {
                var vm = Create(mode, NewIssue("Temporary files", Done, IssueSeverity.High), NewIssue("Tip", severity: IssueSeverity.Info, reclaimable: 0));
                AssertNoRussian(vm, "before the scan");
                await vm.ScanCoreAsync(CancellationToken.None, persist: false);
                AssertNoRussian(vm, "after the scan");
                foreach (var group in vm.Groups)
                {
                    AssertNoRussian(group, "a group");
                    foreach (var item in group.AllItems)
                        AssertNoRussian(item, "a row");
                }
                foreach (var row in vm.ModuleRows)
                    AssertNoRussian(row, "a module row");
                foreach (var link in vm.QuickLinks)
                    AssertNoRussian(link, "a quick link");
            }
        }
    }

    /// <summary>Every public string property of the object reads without a Cyrillic letter.</summary>
    private static void AssertNoRussian(object target, string when)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (property.PropertyType != typeof(string))
                continue;
            var value = (string?)property.GetValue(target);
            Assert.True(value is null || !Cyrillic.IsMatch(value),
                $"{target.GetType().Name}.{property.Name} ({when}) is not English: {value}");
        }
    }
}
