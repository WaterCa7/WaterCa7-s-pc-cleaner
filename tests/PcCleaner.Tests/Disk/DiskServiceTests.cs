using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class DiskServiceTests
{
    private static DiskService ServiceFor(DiskTestTree tree, Func<string, bool>? recycler = null)
        => new(new DiskEnvironment
        {
            SystemDriveRoot = tree.Root,
            ProtectedDirs = new[] { Path.Combine(tree.Root, "Windows"), Path.Combine(tree.Root, "Program Files") },
            ProgramDataDirs = new[] { Path.Combine(tree.Root, "AppData") },
            Recycler = recycler ?? (_ => throw new InvalidOperationException("recycler must not be called")),
        });

    [Fact]
    public void Protected_paths_cover_windows_program_files_and_drive_root_system_files()
    {
        using var tree = new DiskTestTree("protect");
        var service = ServiceFor(tree);

        Assert.True(service.IsProtectedPath(Path.Combine(tree.Root, @"Windows\System32\kernel32.dll")));
        Assert.True(service.IsProtectedPath(Path.Combine(tree.Root, @"Program Files\App\app.exe")));
        Assert.True(service.IsProtectedPath(@"D:\pagefile.sys"));
        Assert.True(service.IsProtectedPath(@"D:\hiberfil.sys"));
        Assert.True(service.IsProtectedPath(@"D:\System Volume Information\x.dat"));
        Assert.True(service.IsProtectedPath(@"D:\Windows.old\Users\a.txt"));
        Assert.True(service.IsProtectedPath(@"D:\"));
        Assert.False(service.IsProtectedPath(Path.Combine(tree.Root, @"Users\U\Videos\film.mkv")));
        Assert.False(service.IsProtectedPath(Path.Combine(tree.Root, @"WindowsBackup\film.mkv"))); // prefix of a name is not a match
        Assert.False(service.IsProtectedPath(@"D:\Games\pagefile.sys.bak"));
    }

    [Fact]
    public void Real_default_environment_protects_windows_and_program_files()
    {
        var service = new DiskService();
        Assert.True(service.IsProtectedPath(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.True(service.IsProtectedPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"X\y.exe")));
        Assert.False(service.IsProtectedPath(Path.Combine(KnownFolders.UserProfile, @"Videos\film.mkv")));
    }

    [Fact]
    public void Recycle_skips_protected_and_missing_files_and_reports_the_rest()
    {
        using var tree = new DiskTestTree("recycle");
        var ok = tree.File(@"Users\U\big.mkv", 1234);
        var busy = tree.File(@"Users\U\busy.mkv", 10);
        var system = tree.File(@"Windows\explorer.exe", 50);
        var missing = Path.Combine(tree.Root, "gone.bin");
        var called = new List<string>();
        var service = ServiceFor(tree, path =>
        {
            called.Add(path);
            return path == ok; // simulate "busy" failure for the other file
        });

        var result = service.RecycleFiles(new[] { (ok, 1234L), (busy, 10L), (system, 50L), (missing, 1L) });

        Assert.Equal(new[] { ok, busy }, called);
        Assert.Equal(ok, Assert.Single(result.Recycled).Path);
        Assert.Equal(1234, result.RecycledBytes);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Path == system && s.Reason.Contains("системный"));
        Assert.Contains(result.Skipped, s => s.Path == missing);
        Assert.True(File.Exists(system));
    }

    [Fact]
    public void Risk_note_for_files_in_appdata()
    {
        using var tree = new DiskTestTree("risk");
        var service = ServiceFor(tree);
        Assert.NotNull(service.RiskNote(Path.Combine(tree.Root, @"AppData\Local\Docker\disk.vhdx")));
        Assert.Null(service.RiskNote(Path.Combine(tree.Root, @"Users\U\film.mkv")));
    }

    [Fact]
    public void System_files_are_found_in_a_drive_root_including_hidden_ones()
    {
        using var tree = new DiskTestTree("sysroot");
        var page = tree.File("pagefile.sys", 4096);
        File.SetAttributes(page, FileAttributes.Hidden | FileAttributes.System);
        tree.File("hiberfil.sys", 2048);
        tree.File("other.sys", 1);
        tree.File(@"sub\swapfile.sys", 1);

        var found = DiskService.FindSystemFilesIn(tree.Root);
        File.SetAttributes(page, FileAttributes.Normal);

        Assert.Equal(new[] { "hiberfil.sys", "pagefile.sys" }, found.Select(f => f.Name));
        Assert.Equal(4096, found.Single(f => f.Name == "pagefile.sys").Size);
    }

    private static DriveSummary Drive(char letter, long totalGb, double freeGb, bool system = false) => new()
    {
        Letter = letter,
        RootPath = letter + @":\",
        TotalBytes = totalGb << 30,
        FreeBytes = (long)(freeGb * (1L << 30)),
        IsSystemDrive = system,
    };

    [Fact]
    public void Low_space_thresholds()
    {
        TestEnvironment.Init();
        var service = new DiskService(new DiskEnvironment { SystemDriveRoot = @"C:\" });
        var issues = service.LowSpaceIssues(new[]
        {
            Drive('C', 256, 20, system: true), // 7.8% -> High
            Drive('D', 100, 12),               // 12% -> Medium
            Drive('E', 100, 40),               // fine
            Drive('F', 6000, 300),             // 5%, but 300 GB free is plenty -> not flagged
        }).ToList();

        Assert.Equal(2, issues.Count);
        var c = issues.Single(i => i.Title.Contains("C:"));
        Assert.Equal(IssueSeverity.High, c.Severity);
        Assert.Equal("Мало места на диске C:", c.Title);
        Assert.Equal("Disk", c.TargetPageKey);
        Assert.NotNull(c.Gain);
        Assert.Equal(IssueSeverity.Medium, issues.Single(i => i.Title.Contains("D:")).Severity);
    }

    [Fact]
    public void Failing_disk_is_a_critical_issue()
    {
        var service = new DiskService(new DiskEnvironment { SystemDriveRoot = @"C:\" });
        var disks = new[]
        {
            new PhysicalDiskInfo { DeviceId = "0", Number = 0, Model = "Good SSD", Health = DiskHealth.Healthy },
            new PhysicalDiskInfo { DeviceId = "1", Number = 1, Model = "Old HDD", Health = DiskHealth.Warning, Letters = new() { 'D' } },
            new PhysicalDiskInfo { DeviceId = "2", Number = 2, Model = "Dead HDD", Health = DiskHealth.Unhealthy },
        };

        var issues = service.HealthIssues(disks).ToList();

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Critical, i.Severity));
        Assert.All(issues, i => Assert.Equal("Диск может скоро выйти из строя — сделайте резервную копию", i.Title));
        Assert.Contains("Old HDD", issues[0].Details);
        Assert.Contains("D:", issues[0].Details);
    }

    [Fact]
    public async Task Trim_fix_without_admin_changes_nothing()
    {
        if (Admin.IsElevated)
            return; // only meaningful in a normal (non-elevated) test run
        var service = new DiskService(new DiskEnvironment { SystemDriveRoot = @"C:\" });

        var message = await service.EnableTrimAsync(CancellationToken.None);
        var hibernation = await service.DisableHibernationAsync(CancellationToken.None);
        var optimize = await service.OptimizeDriveAsync('C', _ => { }, CancellationToken.None);

        Assert.Contains("администратора", message);
        Assert.Contains("администратора", hibernation);
        Assert.True(optimize.NeedsAdmin);
        Assert.False(optimize.Success);
    }

    [Fact]
    public async Task Analyze_is_read_only_and_targets_disk_page()
    {
        TestEnvironment.Init();
        var issues = await new DiskService().AnalyzeAsync(CancellationToken.None);
        Assert.All(issues, i =>
        {
            Assert.Equal("Disk", i.TargetPageKey);
            Assert.Equal("Диски", i.Module);
            Assert.False(string.IsNullOrWhiteSpace(i.Title));
            Assert.False(i.AutoFixSafe);
        });
    }
}
