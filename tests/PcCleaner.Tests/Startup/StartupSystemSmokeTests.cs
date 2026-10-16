using System.Diagnostics;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>Read-only enumeration of the real machine: must finish quickly and never throw (nothing is changed).</summary>
public class StartupSystemSmokeTests
{
    public StartupSystemSmokeTests() => TestEnvironment.Init();

    [Fact]
    public async Task RealScan_IsFastAndConsistent()
    {
        var service = new StartupService();
        var sw = Stopwatch.StartNew();

        var items = await service.ScanAsync(null, CancellationToken.None);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"Scan took {sw.Elapsed}");
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrEmpty(i.Id));
            Assert.False(i.IsService && i.CanDelete);
            Assert.False(i.IsProtected && i.CanToggle);
            if (i.IsWindowsTask)
                Assert.StartsWith(@"\Microsoft\", i.TaskPath!, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RealAnalyze_ReturnsWellFormedIssues()
    {
        var issues = await new StartupService().AnalyzeAsync(CancellationToken.None);
        Assert.All(issues, issue =>
        {
            Assert.Equal("Автозагрузка", issue.Module);
            Assert.Equal("Startup", issue.TargetPageKey);
            Assert.False(string.IsNullOrWhiteSpace(issue.Title));
            Assert.False(string.IsNullOrWhiteSpace(issue.Details));
            Assert.False(issue.AutoFixSafe);
        });
    }
}
