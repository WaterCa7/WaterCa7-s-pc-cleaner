using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class DiskServiceTests
{
    private static DiskService ServiceFor(DiskTestTree tree, Func<string, RecycleOutcome>? recycler = null)
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
    public void Protected_file_reached_through_a_junction_is_still_protected_and_never_recycled()
    {
        using var tree = new DiskTestTree("protect-link");
        var windows = tree.Dir("Windows");
        var system = tree.File(@"Windows\explorer.exe", 50);
        var link = tree.Junction(@"Users\U\Shortcut", windows);
        var viaLink = Path.Combine(link, "explorer.exe");
        var called = new List<string>();
        var service = new DiskService(new DiskEnvironment
        {
            SystemDriveRoot = tree.Root,
            // the real location, as Windows reports it (the temp path may contain 8.3 short names)
            ProtectedDirs = new[] { FinalPaths.Resolve(windows)! },
            Recycler = path =>
            {
                called.Add(path);
                return RecycleOutcome.Ok;
            },
        });

        Assert.True(File.Exists(viaLink));
        Assert.True(service.IsProtectedPath(viaLink));
        Assert.True(service.IsProtectedPath(system));

        var result = service.RecycleFiles(new[] { (viaLink, 50L) });

        Assert.Empty(called);
        Assert.Empty(result.Recycled);
        Assert.Contains(result.Skipped, s => s.Path == viaLink && s.Reason.Contains("системный"));
        Assert.True(File.Exists(system));
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows\explorer.exe", @"C:\Windows\explorer.exe")]
    [InlineData(@"\\?\UNC\server\share\a.txt", @"\\server\share\a.txt")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void Final_path_prefixes_are_removed(string raw, string expected)
        => Assert.Equal(expected, FinalPaths.Normalize(raw));

    [Fact]
    public void Final_path_of_missing_file_is_null()
    {
        TestEnvironment.Init();
        Assert.Null(FinalPaths.Resolve(Path.Combine(TestEnvironment.DataDir, "missing-" + Guid.NewGuid().ToString("N"), "x.bin")));
    }

    [Theory]
    [InlineData(@"C:\Users\U", @"C:\", true)] // a drive root keeps its separator
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"D:\Data", @"C:\", false)]
    [InlineData(@"C:\Users\U\Videos", @"C:\Users\U", true)]
    [InlineData(@"C:\Users\U", @"C:\Users\U\", true)]
    [InlineData(@"C:\Users\UX", @"C:\Users\U", false)]
    [InlineData(@"C:\Windows\x.dll", @"", false)]
    public void Is_under_handles_drive_roots_and_name_prefixes(string path, string dir, bool expected)
        => Assert.Equal(expected, DiskService.IsUnder(path, dir));

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
            return path == ok ? RecycleOutcome.Ok : new RecycleOutcome(RecycleFailure.InUse); // the other file is busy
        });

        var result = service.RecycleFiles(new[] { (ok, 1234L), (busy, 10L), (system, 50L), (missing, 1L) });

        Assert.Equal(new[] { ok, busy }, called);
        Assert.Equal(ok, Assert.Single(result.Recycled).Path);
        Assert.Equal(1234, result.RecycledBytes);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Path == system && s.Reason.Contains("системный"));
        Assert.Contains(result.Skipped, s => s.Path == missing);
        Assert.Contains(result.Skipped, s => s.Path == busy && s.Reason.Contains("открыт в другой программе"));
        Assert.True(File.Exists(system));
    }

    [Theory]
    [InlineData((int)RecycleFailure.NoRecycleBin, "нет корзины")]
    [InlineData((int)RecycleFailure.DeclinedPermanentDelete, "удалить его навсегда вы отказались")]
    [InlineData((int)RecycleFailure.InUse, "открыт в другой программе")]
    [InlineData((int)RecycleFailure.AccessDenied, "нет прав")]
    [InlineData((int)RecycleFailure.Missing, "уже удалён")]
    public void Each_recycle_failure_states_its_real_reason(int failure, string expected)
    {
        using var tree = new DiskTestTree("recycle-reasons");
        var file = tree.File(@"Users\U\film.mkv", 100);
        var service = ServiceFor(tree, _ => new RecycleOutcome((RecycleFailure)failure));

        var result = service.RecycleFiles(new[] { (file, 100L) });

        Assert.Empty(result.Recycled);
        Assert.Contains(expected, Assert.Single(result.Skipped).Reason);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Shell_results_are_classified_by_their_real_reason()
    {
        int NotAsked() => throw new InvalidOperationException("the probe is not needed here");

        Assert.True(SafeRecycler.Classify(0, aborted: false, stillExists: false, NotAsked).Success);
        // The user answered "No" to Windows' "delete permanently?" (the file is too big for the Recycle Bin): the file
        // itself could be deleted (probe = 0).
        Assert.Equal(RecycleFailure.DeclinedPermanentDelete, SafeRecycler.Classify(0, aborted: true, stillExists: true, () => 0).Failure);
        Assert.Equal(RecycleFailure.DeclinedPermanentDelete, SafeRecycler.Classify(1223, false, true, () => 0).Failure);
        Assert.Equal(RecycleFailure.DeclinedPermanentDelete, SafeRecycler.Classify(0x75, false, true, () => 0).Failure);
        // With FOF_NOERRORUI Windows may abort silently on an error too: a busy or protected file is not a refused prompt.
        Assert.Equal(RecycleFailure.InUse, SafeRecycler.Classify(0x75, aborted: true, stillExists: true, () => 32).Failure);
        Assert.Equal(RecycleFailure.AccessDenied, SafeRecycler.Classify(0, aborted: true, stillExists: true, () => 5).Failure);
        // Aborted, but the file is gone anyway: "already deleted", not "you refused".
        Assert.Equal(RecycleFailure.Missing, SafeRecycler.Classify(0x75, aborted: true, stillExists: false, NotAsked).Failure);
        Assert.Equal(RecycleFailure.InUse, SafeRecycler.Classify(32, false, true, NotAsked).Failure);
        Assert.Equal(RecycleFailure.AccessDenied, SafeRecycler.Classify(0x78, false, true, NotAsked).Failure);
        Assert.Equal(RecycleFailure.AccessDenied, SafeRecycler.Classify(5, false, true, NotAsked).Failure);
        Assert.Equal(RecycleFailure.Missing, SafeRecycler.Classify(0x7C, false, true, NotAsked).Failure);
        Assert.Equal(RecycleFailure.Missing, SafeRecycler.Classify(0x402, false, stillExists: false, NotAsked).Failure);

        // Unspecific shell codes: the probe tells a busy file from a protected one.
        Assert.Equal(RecycleFailure.InUse, SafeRecycler.Classify(0x402, false, true, () => 32).Failure);
        Assert.Equal(RecycleFailure.AccessDenied, SafeRecycler.Classify(0x402, false, true, () => 5).Failure);
        var unknown = SafeRecycler.Classify(0x402, false, true, () => 0);
        Assert.Equal(RecycleFailure.Failed, unknown.Failure);
        Assert.Contains("код 1026", unknown.Reason);
    }

    [Fact]
    public void Delete_probe_tells_a_busy_file_from_a_free_one_without_deleting_anything()
    {
        using var tree = new DiskTestTree("recycle-probe");
        var file = tree.File(@"Users\U\locked.bin", 10);

        Assert.Equal(0, SafeRecycler.ProbeDelete(file));
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) // no FileShare.Delete: "in use"
            Assert.Equal(32, SafeRecycler.ProbeDelete(file));
        Assert.Equal(2, SafeRecycler.ProbeDelete(Path.Combine(tree.Root, "missing.bin")));
        Assert.True(File.Exists(file));
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
