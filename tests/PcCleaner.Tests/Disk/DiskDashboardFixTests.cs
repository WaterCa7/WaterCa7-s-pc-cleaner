using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

/// <summary>
/// The dashboard fixes of «Старые установщики и архивы в «Загрузках»» (to the Recycle Bin) and «Осталась папка Windows.old»
/// (opens «Очистка диска»). The recycler and the tool launcher are fakes: nothing real is moved or started.
/// </summary>
public class DiskDashboardFixTests
{
    private static readonly DateTime Old = new(2025, 1, 10, 12, 0, 0);

    public DiskDashboardFixTests() => TestEnvironment.Init();

    private static DiskService Service(Func<string, RecycleOutcome>? recycler = null,
        Func<string, string, ToolLaunchResult>? launch = null, Func<string, bool>? isRunning = null)
        => new(new DiskEnvironment
        {
            SystemDriveRoot = @"C:\",
            Recycler = recycler ?? (_ => throw new InvalidOperationException("recycler must not be called")),
            LaunchTool = launch ?? ((_, _) => throw new InvalidOperationException("no tool must be started")),
            IsToolRunning = isRunning ?? (_ => false),
        });

    /// <summary>A file as the scan saw it.</summary>
    private static FoundFile Scanned(string path)
    {
        var info = new FileInfo(path);
        return new FoundFile(path, info.Length, info.CreationTime, info.LastWriteTime);
    }

    /// <summary>A file that was found by the scan and is gone by now. Its size also lifts the issue over the 1 GB threshold.</summary>
    private static FoundFile Ghost(string name = "ghost.iso")
        => new(Path.Combine(Path.GetTempPath(), "WaterCa7sTests-gone-" + Guid.NewGuid().ToString("N"), name), 2L << 30, Old, Old);

    private static HealthIssue DownloadsIssue(DiskService service, params FoundFile[] files)
        => service.OldDownloadsIssue(new List<FileGroup> { new(DownloadKind.Installers, files) })
           ?? throw new InvalidOperationException("the files must be enough for the issue");

    // ---------- старые установщики и архивы ----------

    [Fact]
    public void OldDownloadsIssue_HasAFixThatMovesToTheRecycleBin()
    {
        var issue = DownloadsIssue(Service(), Ghost());

        Assert.True(issue.CanFix);
        Assert.Equal("В Корзину", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.Equal(FixKind.MovesToRecycleBin, issue.FixKind); // «Исправить всё» says the files can be restored
        Assert.False(issue.AutoFixSafe);
        Assert.Equal("Disk", issue.TargetPageKey);
    }

    [Fact]
    public async Task Fix_MovesTheUnchangedFilesToTheRecycleBin_AndSaysWhatItLeft()
    {
        using var tree = new DiskTestTree("downloads-fix");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var archive = tree.File(@"Downloads\pack.zip", 2500, Old);
        var moved = new List<string>();
        var service = Service(path =>
        {
            moved.Add(path);
            return RecycleOutcome.Ok;
        });
        var issue = DownloadsIssue(service, Scanned(setup), Scanned(archive), Ghost());

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(new[] { setup, archive }.OrderBy(p => p), moved.OrderBy(p => p));
        Assert.StartsWith($"В Корзину перемещено 2 файла ({Format.Bytes(3500)}) — их можно восстановить.", text);
        Assert.Contains("Не тронуто: 1 файл", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task Fix_UsesTheSingularForOneFile()
    {
        using var tree = new DiskTestTree("downloads-fix-one");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var service = Service(_ => RecycleOutcome.Ok);
        var issue = DownloadsIssue(service, Scanned(setup), Ghost());

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith($"В Корзину перемещён 1 файл ({Format.Bytes(1000)}) — его можно восстановить.", text);
    }

    [Fact]
    public async Task Fix_LeavesFilesThatChangedAfterTheScan()
    {
        using var tree = new DiskTestTree("downloads-fix-changed");
        var resized = tree.File(@"Downloads\resized.exe", 1000, Old);
        var touched = tree.File(@"Downloads\touched.zip", 1000, Old);
        var issue = DownloadsIssue(Service(), Scanned(resized), Scanned(touched), Ghost());
        File.WriteAllBytes(resized, new byte[1500]);               // a different size now
        File.SetLastWriteTime(touched, Old.AddDays(30));          // same size, saved again

        var text = await issue.FixAsync!(CancellationToken.None); // a recycler call would throw

        Assert.Contains("ничего не изменено", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.True(File.Exists(resized));
        Assert.True(File.Exists(touched));
    }

    [Fact]
    public async Task Fix_WhenEveryFileIsGone_ChangesNothing()
    {
        var issue = DownloadsIssue(Service(), Ghost("a.iso"), Ghost("b.iso"));

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Файлы уже удалены — ничего не изменено.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task Fix_WhenWindowsCannotMoveAnything_ReportsAFailure()
    {
        using var tree = new DiskTestTree("downloads-fix-busy");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var issue = DownloadsIssue(Service(_ => new RecycleOutcome(RecycleFailure.InUse)), Scanned(setup), Ghost());

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith("Не удалось переместить файлы в Корзину: ", text);
        Assert.Contains("заняты или защищены", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.True(File.Exists(setup));
    }

    [Fact]
    public async Task Fix_WhenWindowsCannotMoveAnything_NeverLooksLikeAMissingAdminRight()
    {
        // The per-file reason ("нет прав на удаление этого файла") would make the Dashboard offer a restart as administrator
        // for a file the administrator could not move either. The reason goes to the log, the text is fixed.
        using var tree = new DiskTestTree("downloads-fix-denied");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var issue = DownloadsIssue(Service(_ => new RecycleOutcome(RecycleFailure.AccessDenied)), Scanned(setup), Ghost());

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Не удалось переместить файлы в Корзину: файлы заняты или защищены. Подробности — в журнале.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.False(HealthScoring.LooksLikeAdminRequired(text));
        Assert.True(File.Exists(setup));
    }

    [Fact]
    public async Task Fix_CountsAFileThatTheRecycleBinRefusedAsNotTouched()
    {
        // Both files are exactly as the scan saw them; Windows moves one and refuses the other (open in another program).
        using var tree = new DiskTestTree("downloads-fix-refused");
        var moved = tree.File(@"Downloads\setup.exe", 1000, Old);
        var busy = tree.File(@"Downloads\busy.zip", 2000, Old);
        var service = Service(path => path == busy ? new RecycleOutcome(RecycleFailure.InUse) : RecycleOutcome.Ok);

        // the issue itself needs over 1 GB of files; the fix is what is tested here
        var text = await service.RecycleOldDownloadsAsync(new[] { Scanned(moved), Scanned(busy) }, CancellationToken.None);

        Assert.StartsWith($"В Корзину перемещён 1 файл ({Format.Bytes(1000)}) — его можно восстановить.", text);
        Assert.Contains("Не тронуто: 1 файл", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
        Assert.True(File.Exists(busy));
    }

    // ---------- Windows.old ----------

    [Fact]
    public void WindowsOldIssue_HasAFixThatOpensDiskCleanup_AndIsNotPartOfFixAll()
    {
        var issue = Service().WindowsOldIssue(40L << 30, complete: true);

        Assert.StartsWith("Осталась папка Windows.old (", issue.Title);
        Assert.Equal(40L << 30, issue.ReclaimableBytes);
        Assert.True(issue.CanFix);
        Assert.Equal("Открыть очистку диска", issue.FixLabel);
        Assert.False(issue.InFixAll);
        Assert.False(issue.AutoFixSafe);
        Assert.Equal("Disk", issue.TargetPageKey);
    }

    [Fact]
    public void WindowsOldIssue_OnlyOpensATool_ButOldDownloadsFreeTheirSpace()
    {
        var service = Service();
        var windowsOld = service.WindowsOldIssue(40L << 30, complete: true);
        var downloads = DownloadsIssue(service, Ghost());

        Assert.True(windowsOld.OnlyOpensTool);
        Assert.False(downloads.OnlyOpensTool);

        // The Dashboard shows the split: the Windows.old space is freed by the user in Disk Cleanup, not by the button.
        var split = ReclaimableSplit.From(new[] { windowsOld, downloads });
        Assert.Equal(40L << 30, split.ManualBytes);
        Assert.Equal(downloads.ReclaimableBytes, split.AutoBytes);
    }

    [Fact]
    public async Task WindowsOldFix_OpensTheWizardOnce_ForTheSystemDrive()
    {
        var launches = new List<(string Exe, string Args)>();
        var service = Service(launch: (exe, args) =>
        {
            launches.Add((exe, args));
            return ToolLaunchResult.Started;
        });
        var issue = service.WindowsOldIssue(40L << 30, complete: true);

        var text = await issue.FixAsync!(CancellationToken.None);

        var (exe, args) = Assert.Single(launches);
        Assert.Equal(SystemTools.Exe("cleanmgr.exe"), exe);
        Assert.Equal("/d C", args);
        Assert.Equal("Открыта «Очистка диска»: нажмите «Очистить системные файлы» и отметьте «Предыдущие установки Windows».", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task WindowsOldFix_WhenTheWizardCannotBeStarted_ReportsAFailure()
    {
        var service = Service(launch: (_, _) => ToolLaunchResult.Failed);
        var issue = service.WindowsOldIssue(40L << 30, complete: true);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal("Не удалось открыть «Очистку диска».", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task WindowsOldFix_DoesNotOpenASecondWindow()
    {
        var service = Service(isRunning: name => name == "cleanmgr"); // starting a tool would throw
        var issue = service.WindowsOldIssue(40L << 30, complete: true);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains("уже открыта", text);
        Assert.Contains("Предыдущие установки Windows", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }
}
