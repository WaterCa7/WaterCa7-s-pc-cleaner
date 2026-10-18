using System.IO;
using System.Runtime.InteropServices;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>Regression tests added during the review (junction races, redirected roots, exclusions, settings).</summary>
public class CleanerReviewTests
{
    private const long Mb = 1024L * 1024;

    [Fact]
    public void TryDeleteFile_PathThroughJunctionCreatedAfterTheWalk_IsRefused()
    {
        // Simulates the race: a sub-folder is swapped for a junction after the walk. The path still looks like it is
        // inside the root, but the handle shows where the file really is, so nothing is deleted.
        using var t = new CleanerTestFolder("review-race");
        var root = t.Dir("cache");
        var outside = t.Dir("documents");
        var precious = t.File(@"documents\thesis.docx", 500);
        var link = Path.Combine(root, "sub");
        t.Junction(link, outside);
        var rootPath = SafePathTools.Normalize(root)!;
        var rootReal = SafeDelete.GetRealFolderPath(rootPath)!;

        var outcome = CleanerEngine.TryDeleteFile(Path.Combine(link, "thesis.docx"), rootPath, rootReal,
            CleanRoot.Folder(root), CleanerTestFolder.Rules(), null, out var size, out var error);

        Assert.Equal(DeleteOutcome.Skipped, outcome);
        Assert.Equal(0, size);
        Assert.Null(error);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void SafeDelete_JunctionItself_IsNeverRemovedAsAFolder()
    {
        using var t = new CleanerTestFolder("review-junction-dir");
        var root = t.Dir("cache");
        var outside = t.Dir("empty-target");
        var link = Path.Combine(root, "link");
        t.Junction(link, outside);
        var rootReal = SafeDelete.GetRealFolderPath(SafePathTools.Normalize(root)!)!;

        var outcome = SafeDelete.Delete(link, Path.Combine(rootReal, "link"), isDirectory: true, _ => true, out _, out _);

        Assert.Equal(DeleteOutcome.Skipped, outcome);
        Assert.True(Directory.Exists(link));
        Assert.True(Directory.Exists(outside));
        Assert.Null(SafeDelete.GetRealFolderPath(link)); // a junction is never treated as a real folder
    }

    [Fact]
    public void RootBehindJunctionedParent_IsNeitherSizedNorCleaned()
    {
        // e.g. %LOCALAPPDATA%\Google moved to another drive (or into Documents) with a junction.
        using var t = new CleanerTestFolder("review-redirected");
        var realParent = t.Dir("real-parent");
        var junk = t.File(@"real-parent\cache\data.bin", 300);
        var linkParent = t.Path("link-parent");
        t.Junction(linkParent, realParent);
        var category = CleanerTestFolder.Category("redirected", CleanRoot.Folder(Path.Combine(linkParent, "cache")));

        var scan = CleanerEngine.Scan(category, CleanerTestFolder.Rules(), t.Guard(), 10, CancellationToken.None);
        var clean = CleanerEngine.Clean(category, CleanerTestFolder.Rules(), t.Guard(), CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(1, scan.ReparsePointsSkipped);
        Assert.Equal(0, clean.DeletedFiles);
        Assert.True(File.Exists(junk));
    }

    [Fact]
    public void ExcludedFolder_WithTrailingSeparator_IsKeptEvenWhenEmpty()
    {
        using var t = new CleanerTestFolder("review-excl-dir");
        var root = t.Dir("temp");
        var keep = t.Dir(@"temp\keep");
        var other = t.Dir(@"temp\other");
        Directory.SetCreationTimeUtc(keep, DateTime.UtcNow.AddDays(-3));
        Directory.SetCreationTimeUtc(other, DateTime.UtcNow.AddDays(-3));
        var rules = CleanerTestFolder.Rules(exclusions: new[] { keep + "\\" });

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("keep", CleanRoot.Folder(root)), rules, t.Guard(),
            CancellationToken.None);

        Assert.True(Directory.Exists(keep));
        Assert.False(Directory.Exists(other));
        Assert.Equal(1, result.DeletedDirectories);
    }

    [Fact]
    public void Exclusion_WrittenWithShortName_StillProtectsTheFile()
    {
        using var t = new CleanerTestFolder("review-excl-short");
        var root = t.Dir("temp");
        var file = t.File(@"temp\VeryLongFolderNameForShortNames\important-data.bin", 50);
        var shortDir = ShortPath(Path.GetDirectoryName(file)!);
        if (shortDir is null || !shortDir.Contains('~'))
            return; // 8.3 names are disabled on this volume: nothing to test

        var rules = CleanerTestFolder.Rules(exclusions: new[] { shortDir + "\\" });
        var category = CleanerTestFolder.Category("short", CleanRoot.Folder(root));

        var scan = CleanerEngine.Scan(category, rules, t.Guard(), 10, CancellationToken.None);
        CleanerEngine.Clean(category, rules, t.Guard(), CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(20_000_000)]
    [InlineData(-5)]
    public async Task OutOfRangeMinAgeSetting_DoesNotBreakTheAnalysis(int hours)
    {
        using var t = new CleanerTestFolder("review-age-range");
        var settings = new AppSettings { CleanerMinFileAgeHours = hours };
        var service = CleanerServiceTests.CreateService(t, out var env, out _, settings);
        Directory.CreateDirectory(env.Temp);
        var file = Path.Combine(env.Temp, "old.tmp");
        File.WriteAllBytes(file, new byte[100]);
        CleanerTestFolder.SetAge(file, TimeSpan.FromDays(5));

        var rules = service.CurrentRules();
        var category = service.LoadCategories().Single(c => c.Id == "win.usertemp");
        var scan = await service.ScanCategoryAsync(category, 10, CancellationToken.None);

        Assert.InRange(rules.MinFileAge, TimeSpan.Zero, TimeSpan.FromHours(CleanerRules.MaxMinFileAgeHours));
        Assert.Null(scan.Error);
        Assert.Equal(hours < 0 ? 1 : 0, scan.Files); // a huge minimum age keeps the 5-day-old file
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DashboardFix_SkipsSystemFolders_EvenWhenElevated()
    {
        using var t = new CleanerTestFolder("review-fix-elevated");
        var env = CleanerEnvironment.ForTests(t.Root);
        var settings = new AppSettings();
        var service = new CleanerService(env, null, new FakeRecycleBin(), () => true, () => settings);
        Directory.CreateDirectory(env.Temp);
        var big = Path.Combine(env.Temp, "leftover.tmp");
        using (var fs = new FileStream(big, FileMode.CreateNew))
            fs.SetLength(120 * Mb);
        CleanerTestFolder.SetAge(big, TimeSpan.FromDays(5));
        var systemTemp = Path.Combine(env.WindowsDir, "Temp", "system.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(systemTemp)!);
        File.WriteAllBytes(systemTemp, new byte[10]);
        CleanerTestFolder.SetAge(systemTemp, TimeSpan.FromDays(5));

        var issues = await service.AnalyzeAsync(CancellationToken.None);
        var junk = Assert.Single(issues, i => i.FixAsync is not null && i.AutoFixSafe);
        await junk.FixAsync!(CancellationToken.None);

        Assert.False(File.Exists(big));
        Assert.True(File.Exists(systemTemp), "the one-click fix only cleans user folders");
    }

    [Fact]
    public void EmptyLeftoverBrowserFolders_AreNotListedAsInstalledBrowsers()
    {
        using var t = new CleanerTestFolder("review-browser-leftover");
        var env = CleanerEnvironment.ForTests(t.Root);
        CatalogTests.InstallFakeBrowser(env, @"Yandex\YandexBrowser\Application\browser.exe");
        CatalogTests.InstallFakeBrowser(env, @"Mozilla Firefox\firefox.exe");
        CatalogTests.InstallFakeBrowser(env, @"Microsoft\Edge\Application\msedge.exe");
        Directory.CreateDirectory(Path.Combine(env.LocalAppData, @"Yandex\YandexBrowser\User Data"));
        Directory.CreateDirectory(Path.Combine(env.LocalAppData, @"Mozilla\Firefox\Profiles\abc.default\storage"));
        Directory.CreateDirectory(Path.Combine(env.LocalAppData, @"Microsoft\Edge\User Data\Default\Code Cache"));

        var ids = CleanerCatalog.Build(env, 24).Select(c => c.Id).ToList();

        Assert.DoesNotContain("browser.yandex", ids);
        Assert.DoesNotContain("browser.firefox", ids);
        Assert.Contains("browser.edge", ids);
    }

    [Fact]
    public void RecycleBinResultLine_CountsObjectsNotFiles()
    {
        var results = new[]
        {
            new CategoryCleanResult { CategoryId = CleanerCatalog.RecycleBinId, Name = "Корзина", FreedBytes = 10, DeletedFiles = 8 },
            new CategoryCleanResult { CategoryId = "win.usertemp", Name = "Темп", FreedBytes = 5, DeletedFiles = 21 },
        };

        var card = new CleanResultViewModel(results, cancelled: false);

        Assert.Equal("удалено 8 объектов", Plain(card.Lines.Single(l => l.Name == "Корзина").Note));
        Assert.Equal("удалён 21 файл", Plain(card.Lines.Single(l => l.Name == "Темп").Note));
        Assert.Equal("удалён 1 объект", Plain(CleanerText.DeletedObjects(1)));
    }

    [Fact]
    public async Task FirstVisit_AfterACancelledPreview_StartsTheAnalysis()
    {
        using var t = new CleanerTestFolder("review-vm-cancelled");
        var service = CleanerServiceTests.CreateService(t, out _, out _);
        var vm = new CleanerViewModel(service);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await vm.PreviewScanAsync(cts.Token);
        Assert.False(vm.IsCatalogLoaded);

        await vm.OnNavigatedToAsync();
        Assert.True(vm.IsBusy || vm.HasScanned, "the page must not stay on the loading placeholder");

        await vm.PreviewScanAsync(CancellationToken.None); // waits for the running analysis
        Assert.True(vm.HasScanned);
    }

    [Fact]
    public void RunningBadge_TellsTheUserWhatToDo()
    {
        using var t = new CleanerTestFolder("review-badge");
        var env = CleanerEnvironment.ForTests(t.Root, new HashSet<string>(new[] { "chrome", "Discord" },
            StringComparer.OrdinalIgnoreCase));
        CatalogTests.CreateFakeInstallation(t, env);
        var categories = CleanerCatalog.Build(env, 24).ToDictionary(c => c.Id);

        var chrome = new CategoryItemViewModel(categories["browser.chrome"], Array.Empty<string>(), true, false, _ => { });
        var discord = new CategoryItemViewModel(categories["app.discord"], Array.Empty<string>(), true, false, _ => { });

        Assert.True(chrome.IsRunning);
        Assert.Equal("Закройте браузер для полной очистки", chrome.RunningText);
        Assert.Equal("Закройте программу для полной очистки", discord.RunningText);
    }

    private static string Plain(string s) => s.Replace(' ', ' ').Replace(' ', ' ');

    private static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        var len = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);
}
