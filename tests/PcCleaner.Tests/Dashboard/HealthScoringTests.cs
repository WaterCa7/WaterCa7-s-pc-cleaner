using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using Xunit;
using static PcCleaner.Tests.Dashboard.FakeContributor;

namespace PcCleaner.Tests.Dashboard;

public class HealthScoringTests
{
    [Fact]
    public void NoIssues_ScoreIs100()
        => Assert.Equal(100, HealthScoring.ComputeScore(Array.Empty<HealthIssue>()));

    [Fact]
    public void Penalties_AreSubtractedPerSeverity()
    {
        var issues = new[]
        {
            Issue("A", IssueSeverity.Critical), // 25
            Issue("B", IssueSeverity.High), // 12
            Issue("C", IssueSeverity.Medium), // 6
            Issue("D", IssueSeverity.Low), // 2
            Issue("E", IssueSeverity.Info), // 0
        };
        Assert.Equal(100 - 25 - 12 - 6 - 2, HealthScoring.ComputeScore(issues));
    }

    [Fact]
    public void InfoIssues_DoNotLowerTheScore()
        => Assert.Equal(100, HealthScoring.ComputeScore(Enumerable.Range(0, 20).Select(_ => Issue("A", IssueSeverity.Info))));

    [Fact]
    public void OneModule_CannotTakeMoreThan40Points()
    {
        var issues = new[]
        {
            Issue("Очистка", IssueSeverity.Critical),
            Issue("Очистка", IssueSeverity.Critical),
            Issue("Очистка", IssueSeverity.Critical), // 75 → capped at 40
            Issue("Диски", IssueSeverity.Medium), // 6
        };
        Assert.Equal(100 - 40 - 6, HealthScoring.ComputeScore(issues));
    }

    [Fact]
    public void ModuleCap_IsCaseInsensitivePerModuleName()
    {
        var issues = new[] { Issue("Очистка", IssueSeverity.Critical), Issue("очистка", IssueSeverity.Critical) };
        Assert.Equal(60, HealthScoring.ComputeScore(issues));
    }

    [Fact]
    public void Score_IsClampedToZero()
    {
        var issues = Enumerable.Range(0, 5)
            .SelectMany(m => Enumerable.Range(0, 3).Select(_ => Issue("M" + m, IssueSeverity.Critical)));
        Assert.Equal(0, HealthScoring.ComputeScore(issues));
    }

    [Fact]
    public void FixedIssues_AreIgnored()
    {
        var fixedIssue = Issue("A", IssueSeverity.Critical);
        fixedIssue.IsFixed = true;
        Assert.Equal(88, HealthScoring.ComputeScore(new[] { fixedIssue, Issue("B", IssueSeverity.High) }));
    }

    [Theory]
    [InlineData(null, "Компьютер ещё не проверялся")]
    [InlineData(100, "Отлично")]
    [InlineData(90, "Отлично")]
    [InlineData(89, "Хорошо")]
    [InlineData(75, "Хорошо")]
    [InlineData(74, "Нужно внимание")]
    [InlineData(50, "Нужно внимание")]
    [InlineData(49, "Плохо")]
    [InlineData(0, "Плохо")]
    public void Verdict_UsesThresholds(int? score, string expected)
        => Assert.Equal(expected, HealthScoring.Verdict(score));

    [Theory]
    [InlineData(null, "MutedBrush")]
    [InlineData(95, "SuccessBrush")]
    [InlineData(75, "SuccessBrush")]
    [InlineData(60, "WarningBrush")]
    [InlineData(10, "DangerBrush")]
    public void ScoreBrush_FollowsVerdictColors(int? score, string expected)
        => Assert.Equal(expected, HealthScoring.ScoreBrushKey(score));

    [Fact]
    public void Group_OrdersBySeverity_AndOmitsEmptyGroups()
    {
        var issues = new[]
        {
            Issue("A", IssueSeverity.Low, "low"),
            Issue("A", IssueSeverity.Info, "info"),
            Issue("B", IssueSeverity.Critical, "crit"),
            Issue("C", IssueSeverity.Low, "low2"),
        };
        var groups = HealthScoring.Group(issues);
        Assert.Equal(new[] { IssueSeverity.Critical, IssueSeverity.Low, IssueSeverity.Info }, groups.Select(g => g.Severity));
        Assert.Equal(new[] { "low", "low2" }, groups[1].Issues.Select(i => i.Title));
        Assert.Equal(new[] { "Критично", "Низко", "Советы" }, groups.Select(g => HealthScoring.GroupTitle(g.Severity)));
    }

    [Fact]
    public void Group_PutsBiggerReclaimableFirst_AndKeepsOrderOtherwise()
    {
        var issues = new[]
        {
            Issue("A", IssueSeverity.Medium, "first"),
            Issue("B", IssueSeverity.Medium, "big", reclaimable: 5_000),
            Issue("C", IssueSeverity.Medium, "second"),
            Issue("D", IssueSeverity.Medium, "small", reclaimable: 10),
        };
        var group = Assert.Single(HealthScoring.Group(issues));
        Assert.Equal(new[] { "big", "small", "first", "second" }, group.Issues.Select(i => i.Title));
    }

    [Fact]
    public void TotalReclaimable_SkipsFixedAndNegative()
    {
        var a = Issue("A", IssueSeverity.Low, reclaimable: 100);
        var b = Issue("B", IssueSeverity.Low, reclaimable: 50);
        var c = Issue("C", IssueSeverity.Low, reclaimable: -10);
        b.IsFixed = true;
        Assert.Equal(100, HealthScoring.TotalReclaimable(new[] { a, b, c }));
    }

    [Fact]
    public void RelativeTime_IsFriendlyRussian()
    {
        var now = new DateTime(2026, 10, 16, 18, 0, 0);
        Assert.Equal("сегодня в 14:32", HealthScoring.RelativeTime(new DateTime(2026, 10, 16, 14, 32, 0), now));
        Assert.Equal("вчера в 09:05", HealthScoring.RelativeTime(new DateTime(2026, 10, 15, 9, 5, 0), now));
        Assert.Equal("3 марта в 07:00", HealthScoring.RelativeTime(new DateTime(2026, 3, 3, 7, 0, 0), now));
        Assert.Equal("31 декабря 2025 г. в 23:59", HealthScoring.RelativeTime(new DateTime(2025, 12, 31, 23, 59, 0), now));
    }

    [Theory]
    [InlineData(0, "проблем нет")]
    [InlineData(1, "1 проблема")]
    [InlineData(3, "3 проблемы")]
    [InlineData(5, "5 проблем")]
    [InlineData(21, "21 проблема")]
    public void ProblemsText_UsesPlurals(int n, string expected)
        => Assert.Equal(expected, HealthScoring.ProblemsText(n));

    [Theory]
    [InlineData("Удалено 1,2 ГБ временных файлов.", false)]
    [InlineData("Игровой режим включён.", false)]
    [InlineData("Удалено 300 МБ. Не удалось удалить 3 занятых файла.", false)]
    [InlineData("Для этого действия нужны права администратора. Ничего не изменено.", true)]
    [InlineData("Не удалось обновить сигнатуры: нет сети", true)]
    [InlineData("Ошибка: служба не найдена", true)]
    [InlineData("", false)]
    public void LooksLikeFailure_DetectsNothingChangedMessages(string message, bool expected)
        => Assert.Equal(expected, HealthScoring.LooksLikeFailure(message));
}

public class DashboardSystemInfoTests
{
    [Theory]
    [InlineData("Windows 10 Home", 22631, "Windows 11 Домашняя")]
    [InlineData("Windows 10 Pro", 26100, "Windows 11 Pro")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData("Windows 10 Home Single Language", 22000, "Windows 11 Домашняя для одного языка")]
    [InlineData("Windows 10 Enterprise", 19044, "Windows 10 Корпоративная")]
    [InlineData(null, null, "Windows")]
    public void WindowsName_FixesWindows11AndTranslatesEdition(string? product, int? build, string expected)
        => Assert.Equal(expected, SystemInfo.WindowsName(product, build));

    [Fact]
    public void WindowsVersionText_CombinesDisplayVersionAndBuild()
    {
        Assert.Equal("24H2, сборка 26100.2033", SystemInfo.WindowsVersionText("24H2", 26100, 2033));
        Assert.Equal("сборка 19045", SystemInfo.WindowsVersionText(null, 19045, null));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", "Intel Core i7-10700K @ 3.80GHz")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor            ", "AMD Ryzen 7 5800X 8-Core")]
    [InlineData("  ", "Неизвестный процессор")]
    public void CleanCpuName_RemovesNoise(string raw, string expected)
        => Assert.Equal(expected, SystemInfo.CleanCpuName(raw));

    [Fact]
    public void ReadStatic_AndReadLive_DoNotThrow()
    {
        TestEnvironment.Init();
        var info = SystemInfo.ReadStatic();
        Assert.False(string.IsNullOrWhiteSpace(info.WindowsName));
        Assert.True(info.LogicalProcessors > 0);
        var live = SystemInfo.ReadLive();
        Assert.True(live.Uptime > TimeSpan.Zero);
        Assert.NotNull(live.Memory);
        Assert.InRange(live.CpuPercent ?? 0, 0, 100);
    }

    [Theory]
    [InlineData(1000.0, "900:3:2", 3)]
    [InlineData(700.0, "900:3:2", 2)]
    [InlineData(0.0, "760:4:2", 4)]
    public void WidthToColumns_PicksColumns(double width, string parameter, int expected)
        => Assert.Equal(expected, new WidthToColumnsConverter().Convert(width, typeof(int), parameter, Format.Ru));
}

[Collection(DashboardSequentialCollection.Name)]
public class ScanSummaryTests
{
    public ScanSummaryTests() => TestEnvironment.Init();

    [Fact]
    public void Summary_RoundTripsThroughSettings()
    {
        var settings = new AppSettings();
        var summary = new ScanSummary
        {
            Score = 73,
            Time = new DateTime(2026, 10, 16, 14, 32, 0),
            IssueCount = 5,
            ImportantCount = 2,
            FixableCount = 3,
            ReclaimableBytes = 4_500_000_000,
            FailedModules = 1,
        };
        summary.Save(settings);

        Assert.Equal(summary, ScanSummary.Load(settings));

        // and survives a real save/load of settings.json
        var reloaded = AppSettings.Load();
        Assert.Equal(summary, ScanSummary.Load(reloaded));
    }

    [Fact]
    public void CorruptSummary_IsIgnored()
    {
        var settings = new AppSettings();
        settings.ModuleData["Dashboard"] = new Dictionary<string, string> { ["LastScan"] = "{ not json" };
        Assert.Null(ScanSummary.Load(settings));
    }

    [Fact]
    public void MissingSummary_IsNull()
        => Assert.Null(ScanSummary.Load(new AppSettings()));
}
