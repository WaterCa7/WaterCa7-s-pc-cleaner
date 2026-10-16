using System.IO;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

public class SafeFileWalkerTests
{
    [Fact]
    public void DoesNotFollowOrDescendIntoJunctions()
    {
        using var t = new CleanerTestFolder("walker-junction");
        var root = t.Dir("root");
        t.File(@"root\a.tmp");
        t.File(@"root\sub\b.tmp");
        var target = t.Dir("outside");
        t.File(@"outside\secret.txt");
        t.File(@"outside\deep\secret2.txt");
        t.Junction(Path.Combine(root, "link"), target);

        var stats = new WalkStats();
        var files = SafeFileWalker.EnumerateFiles(root, new WalkOptions(), stats, CancellationToken.None)
            .Select(f => f.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(new[] { "a.tmp", "b.tmp" }, files);
        Assert.Equal(1, stats.ReparsePointsSkipped);
    }

    [Fact]
    public void RootThatIsAJunction_IsNotWalked()
    {
        using var t = new CleanerTestFolder("walker-rootlink");
        var target = t.Dir("target");
        t.File(@"target\file.tmp");
        var link = t.Path("link");
        t.Junction(link, target);

        var stats = new WalkStats();
        var files = SafeFileWalker.EnumerateFiles(link, new WalkOptions(), stats, CancellationToken.None).ToList();

        Assert.Empty(files);
        Assert.True(stats.RootIsReparsePoint);
    }

    [Fact]
    public void ExcludedPaths_AreNeitherReturnedNorEntered()
    {
        using var t = new CleanerTestFolder("walker-excl");
        var root = t.Dir("root");
        t.File(@"root\keep\inner.tmp");
        t.File(@"root\other.tmp");
        t.File(@"root\skip-me.tmp");
        var rules = CleanerTestFolder.Rules(exclusions: new[] { Path.Combine(root, "keep"), Path.Combine(root, "skip-me.tmp") });

        var files = SafeFileWalker.EnumerateFiles(root, new WalkOptions { IsExcluded = rules.IsExcluded }, null,
            CancellationToken.None).Select(f => f.Name).ToList();

        Assert.Equal(new[] { "other.tmp" }, files);
    }

    [Fact]
    public void NonRecursive_ReturnsOnlyTopLevelFiles()
    {
        using var t = new CleanerTestFolder("walker-flat");
        var root = t.Dir("root");
        t.File(@"root\top.db");
        t.File(@"root\sub\nested.db");

        var files = SafeFileWalker.EnumerateFiles(root, new WalkOptions { Recursive = false }, null, CancellationToken.None)
            .Select(f => f.Name).ToList();

        Assert.Equal(new[] { "top.db" }, files);
    }

    [Fact]
    public void MissingRoot_YieldsNothing()
    {
        using var t = new CleanerTestFolder("walker-missing");
        var files = SafeFileWalker.EnumerateFiles(t.Path("does-not-exist"), new WalkOptions(), null, CancellationToken.None);
        Assert.Empty(files);
    }

    [Fact]
    public void Cancellation_Throws()
    {
        using var t = new CleanerTestFolder("walker-cancel");
        var root = t.Dir("root");
        t.File(@"root\a.tmp");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SafeFileWalker.EnumerateFiles(root, new WalkOptions(), null, cts.Token).ToList());
    }
}
