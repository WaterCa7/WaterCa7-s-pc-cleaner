using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>Recycle Bin stand-in: the real one is never queried or emptied by tests.</summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    public long Bytes { get; set; }

    public long Items { get; set; }

    public int EmptyCalls { get; private set; }

    /// <summary>Simulates SHQueryRecycleBin failing.</summary>
    public bool QueryFails { get; set; }

    public (long Bytes, long Items)? Query() => QueryFails ? null : (Bytes, Items);

    public bool Empty(out string? error)
    {
        EmptyCalls++;
        Bytes = 0;
        Items = 0;
        error = null;
        return true;
    }
}

public class CleanerServiceTests
{
    private const long Mb = 1024L * 1024;

    internal static CleanerService CreateService(CleanerTestFolder t, out CleanerEnvironment env, out FakeRecycleBin bin,
        AppSettings? settings = null)
    {
        env = CleanerEnvironment.ForTests(t.Root);
        bin = new FakeRecycleBin();
        var s = settings ?? new AppSettings();
        return new CleanerService(env, null, bin, () => false, () => s);
    }

    [Theory]
    [InlineData(50 * Mb, null)]
    [InlineData(100 * Mb, null)]
    [InlineData(101 * Mb, IssueSeverity.Low)]
    [InlineData(2 * 1024 * Mb, IssueSeverity.Medium)]
    [InlineData(6 * 1024 * Mb, IssueSeverity.High)]
    public void Severity_FollowsThresholds(long bytes, IssueSeverity? expected)
        => Assert.Equal(expected, CleanerService.SeverityFor(bytes));

    [Fact]
    public async Task Analyze_FindsJunk_AndFixCleansOnlyWhatIsAllowedWithoutAdmin()
    {
        using var t = new CleanerTestFolder("service-analyze");
        var service = CreateService(t, out var env, out _);
        Directory.CreateDirectory(env.Temp);
        var big = Path.Combine(env.Temp, "big-leftover.tmp");
        using (var fs = new FileStream(big, FileMode.CreateNew))
            fs.SetLength(120 * Mb);
        CleanerTestFolder.SetAge(big, TimeSpan.FromDays(5));
        var fresh = Path.Combine(env.Temp, "installer-in-progress.tmp");
        File.WriteAllBytes(fresh, new byte[10]);
        var systemTemp = Path.Combine(env.WindowsDir, "Temp", "system.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(systemTemp)!);
        File.WriteAllBytes(systemTemp, new byte[10]);
        CleanerTestFolder.SetAge(systemTemp, TimeSpan.FromDays(5));

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        var junk = Assert.Single(issues, i => i.FixAsync is not null && i.AutoFixSafe);
        Assert.Equal(IssueSeverity.Low, junk.Severity);
        Assert.Equal("Cleaner", junk.TargetPageKey);
        Assert.Equal("Очистка", junk.Module);
        Assert.True(junk.ReclaimableBytes >= 120 * Mb);
        Assert.StartsWith("Можно освободить", junk.Title);
        Assert.Equal(FixKind.DeletesJunk, junk.FixKind); // «Исправить всё» says these files are deleted for good
        Assert.True(File.Exists(big), "analysis must not delete anything");

        var message = await junk.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Освобождено", message);
        Assert.False(File.Exists(big));
        Assert.True(File.Exists(fresh), "files younger than the minimum age stay");
        Assert.True(File.Exists(systemTemp), "admin-only folders are not cleaned without elevation");
    }

    [Fact]
    public async Task Analyze_SmallJunk_ProducesNoIssue()
    {
        using var t = new CleanerTestFolder("service-small");
        var service = CreateService(t, out var env, out _);
        Directory.CreateDirectory(env.Temp);
        File.WriteAllBytes(Path.Combine(env.Temp, "small.tmp"), new byte[1000]);

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        Assert.DoesNotContain(issues, i => i.Title.StartsWith("Можно освободить"));
    }

    [Fact]
    public async Task Analyze_RecycleBinIssue_IsInfoAndNotAutoFixed()
    {
        using var t = new CleanerTestFolder("service-bin");
        var service = CreateService(t, out _, out var bin);
        bin.Bytes = 5 * Mb;
        bin.Items = 3;

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        var issue = Assert.Single(issues, i => i.Title.StartsWith("В корзине"));
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.False(issue.AutoFixSafe);
        Assert.Equal(5 * Mb, issue.ReclaimableBytes);
        Assert.Contains("3 объекта", issue.Details);
        Assert.Equal(0, bin.EmptyCalls);

        var message = await issue.FixAsync!(CancellationToken.None);
        Assert.Equal(1, bin.EmptyCalls);
        Assert.Contains("Корзина очищена", message);
    }

    [Fact]
    public async Task Analyze_RecycleBinIssue_IsNotInFixAll()
    {
        // Emptying the Recycle Bin cannot be undone, so it runs only from its own button on the row and is never part of
        // the «Исправить всё» batch.
        using var t = new CleanerTestFolder("service-bin-fixall");
        var service = CreateService(t, out _, out var bin);
        bin.Bytes = 5 * Mb;
        bin.Items = 3;

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        var issue = Assert.Single(issues, i => i.Title.StartsWith("В корзине"));
        Assert.True(issue.CanFix);
        Assert.False(issue.InFixAll);
    }

    [Fact]
    public async Task Analyze_RespectsCategoriesTheUserUnchecked()
    {
        using var t = new CleanerTestFolder("service-disabled");
        var settings = new AppSettings { CleanerDisabledCategories = new List<string> { "win.usertemp" } };
        var service = CreateService(t, out var env, out _, settings);
        Directory.CreateDirectory(env.Temp);
        var big = Path.Combine(env.Temp, "big.tmp");
        using (var fs = new FileStream(big, FileMode.CreateNew))
            fs.SetLength(150 * Mb);
        CleanerTestFolder.SetAge(big, TimeSpan.FromDays(5));

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        Assert.DoesNotContain(issues, i => i.Title.StartsWith("Можно освободить"));
    }

    [Fact]
    public async Task CleanCategory_FakeEnvironment_DeletesAndKeepsRoot()
    {
        using var t = new CleanerTestFolder("service-clean");
        var service = CreateService(t, out var env, out _);
        var dxCache = Path.Combine(env.LocalAppData, "D3DSCache");
        var file = Path.Combine(dxCache, @"abc\shader.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[2048]);

        var category = service.LoadCategories().Single(c => c.Id == "app.d3dscache");
        var scan = await service.ScanCategoryAsync(category, 10, CancellationToken.None);
        Assert.Equal(2048, scan.Bytes);

        var result = await service.CleanCategoryAsync(category, CancellationToken.None);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(2048, result.FreedBytes);
        Assert.False(File.Exists(file));
        Assert.True(Directory.Exists(dxCache));
    }

    [Fact]
    public async Task DeliveryOptimization_IsNotCleanedWithoutAdmin()
    {
        using var t = new CleanerTestFolder("service-do");
        var service = CreateService(t, out _, out _);
        var category = service.LoadCategories().Single(c => c.Kind == CategoryKind.DeliveryOptimization);

        var result = await service.CleanCategoryAsync(category, CancellationToken.None);

        Assert.Equal(1, result.SkippedAdminRoots);
        Assert.Equal(0, result.DeletedFiles);
    }

    [Fact]
    public async Task RecycleBinCleaning_UsesTheInjectedBin()
    {
        using var t = new CleanerTestFolder("service-bin-clean");
        var service = CreateService(t, out _, out var bin);
        bin.Bytes = 1000;
        bin.Items = 2;
        var category = service.LoadCategories().Single(c => c.Kind == CategoryKind.RecycleBin);

        var result = await service.CleanCategoryAsync(category, CancellationToken.None);

        Assert.Equal(1, bin.EmptyCalls);
        Assert.Equal(1000, result.FreedBytes);
        Assert.Equal(2, result.DeletedFiles);
    }

    [Theory]
    [InlineData(1, 0, "Освобождено 1 КБ, удалён 1 файл.")]
    [InlineData(3, 1, "Освобождено 1 КБ, удалено 3 файла, пропущен 1 занятый.")]
    [InlineData(12345, 87, "Освобождено 1 КБ, удалено 12 345 файлов, пропущено 87 занятых.")]
    [InlineData(21, 11, "Освобождено 1 КБ, удалён 21 файл, пропущено 11 занятых.")]
    public void Summary_UsesCorrectRussianAgreement(long deleted, long locked, string expected)
    {
        var r = new CategoryCleanResult { CategoryId = "x", Name = "x", FreedBytes = 1024, DeletedFiles = deleted, LockedFiles = locked };
        var actual = CleanerService.Summarize(new[] { r }).Replace(' ', ' ').Replace(' ', ' ');
        Assert.Equal(expected, actual);
    }
}
