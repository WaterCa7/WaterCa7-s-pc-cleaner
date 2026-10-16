using System.IO;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

public class CleanerEngineTests
{
    [Fact]
    public void Clean_RootContainingJunction_LeavesJunctionTargetUntouched()
    {
        using var t = new CleanerTestFolder("engine-junction");
        var root = t.Dir("cache");
        var junk1 = t.File(@"cache\junk1.tmp");
        var junk2 = t.File(@"cache\sub\junk2.tmp");
        var target = t.Dir("important");
        var precious = t.File(@"important\precious.docx");
        var preciousDeep = t.File(@"important\nested\photo.jpg");
        var link = Path.Combine(root, "link-to-important");
        t.Junction(link, target);

        var category = CleanerTestFolder.Category("junction", CleanRoot.Folder(root));
        var result = CleanerEngine.Clean(category, CleanerTestFolder.Rules(), t.Guard(), CancellationToken.None);

        Assert.False(File.Exists(junk1));
        Assert.False(File.Exists(junk2));
        Assert.True(File.Exists(precious));
        Assert.True(File.Exists(preciousDeep));
        Assert.True(Directory.Exists(link), "the junction itself must not be removed");
        Assert.True(Directory.Exists(root), "the category root must never be removed");
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(200, result.FreedBytes);
    }

    [Fact]
    public void Scan_RootContainingJunction_DoesNotCountTargetFiles()
    {
        using var t = new CleanerTestFolder("engine-junction-scan");
        var root = t.Dir("cache");
        t.File(@"cache\junk.tmp", 10);
        var target = t.Dir("big");
        t.File(@"big\huge.bin", 5000);
        t.Junction(Path.Combine(root, "link"), target);

        var result = CleanerEngine.Scan(CleanerTestFolder.Category("j", CleanRoot.Folder(root)), CleanerTestFolder.Rules(),
            t.Guard(), 10, CancellationToken.None);

        Assert.Equal(1, result.Files);
        Assert.Equal(10, result.Bytes);
        Assert.Equal(1, result.ReparsePointsSkipped);
    }

    [Fact]
    public void MinAge_YoungFilesAreNotCountedNorDeleted()
    {
        using var t = new CleanerTestFolder("engine-age");
        var root = t.Dir("temp");
        var old = t.File(@"temp\old.tmp", 300, TimeSpan.FromDays(2));
        var young = t.File(@"temp\young.tmp", 700, TimeSpan.FromMinutes(5));
        var category = new CleanerCategory
        {
            Id = "age",
            Group = CleanerCategory.GroupWindows,
            Name = "Возраст",
            Description = "Тест.",
            Roots = new[] { CleanRoot.Folder(root) },
            Age = AgeRule.UserSetting,
        };
        var rules = CleanerTestFolder.Rules(TimeSpan.FromHours(24));

        var scan = CleanerEngine.Scan(category, rules, t.Guard(), 10, CancellationToken.None);
        Assert.Equal(1, scan.Files);
        Assert.Equal(300, scan.Bytes);
        Assert.Equal(1, scan.TooYoungFiles);

        var clean = CleanerEngine.Clean(category, rules, t.Guard(), CancellationToken.None);
        Assert.Equal(1, clean.DeletedFiles);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(young));
    }

    [Fact]
    public void MinAge_UsesNewestOfCreationAndWriteTime()
    {
        using var t = new CleanerTestFolder("engine-age-copy");
        var root = t.Dir("temp");
        // A file unpacked by an installer: old write time, but created just now.
        var unpacked = t.File(@"temp\setup.dll", 100, TimeSpan.FromDays(400));
        File.SetCreationTimeUtc(unpacked, DateTime.UtcNow);
        var category = new CleanerCategory
        {
            Id = "age2",
            Group = CleanerCategory.GroupWindows,
            Name = "Возраст",
            Description = "Тест.",
            Roots = new[] { CleanRoot.Folder(root) },
            Age = AgeRule.UserSetting,
        };

        var clean = CleanerEngine.Clean(category, CleanerTestFolder.Rules(TimeSpan.FromHours(24)), t.Guard(),
            CancellationToken.None);

        Assert.Equal(0, clean.DeletedFiles);
        Assert.True(File.Exists(unpacked));
    }

    [Fact]
    public void Exclusions_AreRespectedWhenScanningAndCleaning()
    {
        using var t = new CleanerTestFolder("engine-excl");
        var root = t.Dir("cache");
        var keepFile = t.File(@"cache\keep.tmp");
        var keepDirFile = t.File(@"cache\keepdir\inner.tmp");
        var junk = t.File(@"cache\junk.tmp");
        var rules = CleanerTestFolder.Rules(exclusions: new[] { keepFile, Path.Combine(root, "keepdir") + "\\" });
        var category = CleanerTestFolder.Category("excl", CleanRoot.Folder(root));

        var scan = CleanerEngine.Scan(category, rules, t.Guard(), 10, CancellationToken.None);
        Assert.Equal(1, scan.Files);

        CleanerEngine.Clean(category, rules, t.Guard(), CancellationToken.None);
        Assert.True(File.Exists(keepFile));
        Assert.True(File.Exists(keepDirFile));
        Assert.False(File.Exists(junk));
    }

    [Fact]
    public void ExcludedRoot_IsSkippedEntirely()
    {
        using var t = new CleanerTestFolder("engine-excl-root");
        var root = t.Dir("cache");
        var file = t.File(@"cache\a.tmp");
        var rules = CleanerTestFolder.Rules(exclusions: new[] { root });

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("x", CleanRoot.Folder(root)), rules, t.Guard(),
            CancellationToken.None);

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void LockedFile_IsSkippedAndCounted()
    {
        using var t = new CleanerTestFolder("engine-locked");
        var root = t.Dir("cache");
        var locked = t.File(@"cache\in-use.tmp");
        var free = t.File(@"cache\free.tmp");
        var category = CleanerTestFolder.Category("locked", CleanRoot.Folder(root));

        CategoryCleanResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = CleanerEngine.Clean(category, CleanerTestFolder.Rules(), t.Guard(), CancellationToken.None);

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
        Assert.Equal(1, result.LockedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void EmptyDirectoriesAreRemovedBottomUp_ButRootIsKept()
    {
        using var t = new CleanerTestFolder("engine-dirs");
        var root = t.Dir("cache");
        t.File(@"cache\a\b\c\deep.tmp");
        t.File(@"cache\a\shallow.tmp");
        t.Dir(@"cache\empty\nested");
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            Directory.SetCreationTimeUtc(d, DateTime.UtcNow.AddDays(-3));

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("dirs", CleanRoot.Folder(root)), CleanerTestFolder.Rules(),
            t.Guard(), CancellationToken.None);

        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(5, result.DeletedDirectories); // a, a\b, a\b\c, empty, empty\nested
    }

    [Fact]
    public void EmptyDirectoriesYoungerThanMinAge_AreKept()
    {
        using var t = new CleanerTestFolder("engine-dirs-young");
        var root = t.Dir("temp");
        var fresh = t.Dir(@"temp\installer-just-started");
        var category = new CleanerCategory
        {
            Id = "dirage",
            Group = CleanerCategory.GroupWindows,
            Name = "Возраст",
            Description = "Тест.",
            Roots = new[] { CleanRoot.Folder(root) },
            Age = AgeRule.UserSetting,
        };

        CleanerEngine.Clean(category, CleanerTestFolder.Rules(TimeSpan.FromHours(24)), t.Guard(), CancellationToken.None);

        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void LockedFile_KeepsItsFolder()
    {
        using var t = new CleanerTestFolder("engine-locked-dir");
        var root = t.Dir("cache");
        var locked = t.File(@"cache\busy\file.tmp");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            CleanerEngine.Clean(CleanerTestFolder.Category("busy", CleanRoot.Folder(root)), CleanerTestFolder.Rules(), t.Guard(),
                CancellationToken.None);

        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void ReadOnlyFile_IsDeleted()
    {
        using var t = new CleanerTestFolder("engine-ro");
        var root = t.Dir("temp");
        var ro = t.File(@"temp\readonly.tmp");
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("ro", CleanRoot.Folder(root)), CleanerTestFolder.Rules(),
            t.Guard(), CancellationToken.None);

        Assert.False(File.Exists(ro));
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public void Patterns_LimitWhichFilesAreTouched()
    {
        using var t = new CleanerTestFolder("engine-patterns");
        var root = t.Dir("Explorer");
        var thumb = t.File(@"Explorer\thumbcache_256.db");
        var icon = t.File(@"Explorer\iconcache_32.db");
        var other = t.File(@"Explorer\ExplorerStartupLog.etl");
        var nested = t.File(@"Explorer\sub\thumbcache_96.db");
        var category = CleanerTestFolder.Category("thumbs",
            CleanRoot.Files(root, recursive: false, requiresAdmin: false, "thumbcache_*.db", "iconcache_*.db"));

        var scan = CleanerEngine.Scan(category, CleanerTestFolder.Rules(), t.Guard(), 10, CancellationToken.None);
        Assert.Equal(2, scan.Files);

        CleanerEngine.Clean(category, CleanerTestFolder.Rules(), t.Guard(), CancellationToken.None);
        Assert.False(File.Exists(thumb));
        Assert.False(File.Exists(icon));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void AdminRoots_AreSizedButSkippedWhenNotElevated()
    {
        using var t = new CleanerTestFolder("engine-admin");
        var userRoot = t.Dir("user");
        var systemRoot = t.Dir("system");
        var userFile = t.File(@"user\u.tmp", 100);
        var systemFile = t.File(@"system\s.tmp", 400);
        var category = CleanerTestFolder.Category("mixed", CleanRoot.Folder(userRoot),
            CleanRoot.Folder(systemRoot, requiresAdmin: true));

        var scan = CleanerEngine.Scan(category, CleanerTestFolder.Rules(), t.Guard(), 10, CancellationToken.None);
        Assert.Equal(500, scan.Bytes);
        Assert.Equal(400, scan.AdminBytes);
        Assert.Equal(100, scan.CleanableBytes(elevated: false));
        Assert.Equal(500, scan.CleanableBytes(elevated: true));

        var clean = CleanerEngine.Clean(category, CleanerTestFolder.Rules(elevated: false), t.Guard(), CancellationToken.None);
        Assert.False(File.Exists(userFile));
        Assert.True(File.Exists(systemFile));
        Assert.Equal(1, clean.SkippedAdminRoots);
    }

    [Fact]
    public void RootOutsideAllowedBases_IsRefusedAndNothingIsDeleted()
    {
        using var t = new CleanerTestFolder("engine-guard");
        var allowed = t.Dir("allowed");
        var outside = t.Dir("outside");
        var file = t.File(@"outside\must-stay.txt");
        var guard = RootGuard.ForTests(allowed);

        var scan = CleanerEngine.Scan(CleanerTestFolder.Category("g", CleanRoot.Folder(outside)), CleanerTestFolder.Rules(), guard,
            10, CancellationToken.None);
        var clean = CleanerEngine.Clean(CleanerTestFolder.Category("g", CleanRoot.Folder(outside)), CleanerTestFolder.Rules(),
            guard, CancellationToken.None);

        Assert.Equal(1, scan.RejectedRoots);
        Assert.Equal(0, scan.Files);
        Assert.Equal(1, clean.RejectedRoots);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Scan_IsReadOnly_AndReportsLargestFilesFirst()
    {
        using var t = new CleanerTestFolder("engine-scan");
        var root = t.Dir("cache");
        var paths = new List<string>();
        for (var i = 1; i <= 8; i++)
            paths.Add(t.File($@"cache\f{i}.bin", i * 10));

        var result = CleanerEngine.Scan(CleanerTestFolder.Category("s", CleanRoot.Folder(root)), CleanerTestFolder.Rules(),
            t.Guard(), 3, CancellationToken.None);

        Assert.Equal(8, result.Files);
        Assert.Equal(360, result.Bytes);
        Assert.Equal(new long[] { 80, 70, 60 }, result.Largest.Select(f => f.Size).ToArray());
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void SingleFileRoot_OnlyThatFileIsDeleted()
    {
        using var t = new CleanerTestFolder("engine-single");
        var dir = t.Dir("win");
        var dump = t.File(@"win\MEMORY.DMP", 1000);
        var neighbour = t.File(@"win\explorer.exe", 10);

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("dump", CleanRoot.File(dump, requiresAdmin: false)),
            CleanerTestFolder.Rules(), RootGuard.ForTests(dir), CancellationToken.None);

        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(neighbour));
        Assert.Equal(1000, result.FreedBytes);
    }

    [Fact]
    public void SingleFileRoot_WithUnexpectedExtension_IsRefused()
    {
        using var t = new CleanerTestFolder("engine-single-bad");
        var dir = t.Dir("win");
        var exe = t.File(@"win\explorer.exe", 10);

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("bad", CleanRoot.File(exe, requiresAdmin: false)),
            CleanerTestFolder.Rules(), RootGuard.ForTests(dir), CancellationToken.None);

        Assert.True(File.Exists(exe));
        Assert.Equal(1, result.RejectedRoots);
    }

    [Fact]
    public void Cancellation_StopsCleaningAndReportsPartialResult()
    {
        using var t = new CleanerTestFolder("engine-cancel");
        var root = t.Dir("cache");
        var file = t.File(@"cache\a.tmp");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = CleanerEngine.Clean(CleanerTestFolder.Category("c", CleanRoot.Folder(root)), CleanerTestFolder.Rules(),
            t.Guard(), cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ChainCheck_DetectsJunctionBetweenFileAndRoot()
    {
        using var t = new CleanerTestFolder("engine-chain");
        var root = t.Dir("cache");
        var target = t.Dir("elsewhere");
        t.Dir(@"elsewhere\inner");
        var link = Path.Combine(root, "link");
        t.Junction(link, target);

        Assert.True(CleanerEngine.IsChainFreeOfReparsePoints(t.Dir(@"cache\real\sub"), root));
        Assert.False(CleanerEngine.IsChainFreeOfReparsePoints(Path.Combine(link, "inner"), root));
    }
}
