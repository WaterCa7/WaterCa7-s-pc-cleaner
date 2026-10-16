using System.IO;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class SpaceAnalyzerTests
{
    private static PathRules NoRules => new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public async Task Aggregates_sizes_and_never_follows_junctions()
    {
        using var tree = new DiskTestTree("analyzer");
        using var outside = new DiskTestTree("outside");
        tree.File(@"a\one.bin", 1000);
        tree.File(@"a\two.bin", 2000);
        tree.File(@"b\c\three.bin", 3000);
        tree.File(@"b\movie.mp4", 500);
        outside.File(@"huge.bin", 100_000);
        tree.Junction("link-to-a", Path.Combine(tree.Root, "a"));
        tree.Junction(@"b\link-outside", outside.Root);

        var result = await new SpaceAnalyzer(NoRules, topCount: 10, workers: 3).RunAsync(tree.Root, CancellationToken.None);

        Assert.False(result.IsPartial);
        Assert.Equal(6500, result.TotalBytes);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(2, result.SkippedLinks);
        Assert.Equal(3, result.FolderCount); // a, b, b\c — links are not counted as folders

        var root = result.Root;
        var link = Assert.Single(root.Children, c => c.Name == "link-to-a");
        Assert.True(link.IsLink);
        Assert.Equal(0, link.TotalBytes);
        var b = Assert.Single(root.Children, c => c.Name == "b");
        Assert.Equal(3500, b.TotalBytes);
        Assert.Equal(500, b.OwnBytes);
        Assert.True(Assert.Single(b.Children, c => c.Name == "link-outside").IsLink);
        Assert.DoesNotContain(result.LargestFiles, f => f.Size == 100_000);
    }

    [Fact]
    public async Task Children_are_sorted_by_size_and_paths_are_full()
    {
        using var tree = new DiskTestTree("sorted");
        tree.File(@"small\x.bin", 10);
        tree.File(@"big\y.bin", 5000);
        tree.File(@"mid\z.bin", 700);

        var result = await new SpaceAnalyzer(NoRules, 10, 2).RunAsync(tree.Root, CancellationToken.None);

        Assert.Equal(new[] { "big", "mid", "small" }, result.Root.Children.Select(c => c.Name));
        Assert.Equal(Path.Combine(tree.Root, "big"), result.Root.Children[0].FullPath);
        Assert.Equal(3, result.FolderCount);
    }

    [Fact]
    public async Task Largest_files_are_top_n_biggest_first()
    {
        using var tree = new DiskTestTree("topn");
        for (var i = 1; i <= 10; i++)
            tree.File($@"d{i % 3}\f{i}.dat", i * 100);

        var result = await new SpaceAnalyzer(NoRules, topCount: 3, workers: 4).RunAsync(tree.Root, CancellationToken.None);

        Assert.Equal(new long[] { 1000, 900, 800 }, result.LargestFiles.Select(f => f.Size));
        Assert.EndsWith("f10.dat", result.LargestFiles[0].Path);
        Assert.True(File.Exists(result.LargestFiles[0].Path));
    }

    [Fact]
    public async Task Breakdown_by_type_uses_extension_and_folder_context()
    {
        using var tree = new DiskTestTree("types");
        var downloads = tree.Dir("Downloads");
        var programs = tree.Dir("Apps");
        tree.File(@"Downloads\setup.exe", 400);
        tree.File(@"Apps\Game\game.exe", 300);
        tree.File(@"Apps\Game\intro.mp4", 50);
        tree.File(@"Videos\film.mkv", 1000);
        tree.File(@"Docs\report.pdf", 20);
        tree.File(@"Docs\notes.xyz", 5);
        var rules = new PathRules(Array.Empty<string>(), new[] { programs }, new[] { downloads });

        var result = await new SpaceAnalyzer(rules, 10, 2).RunAsync(tree.Root, CancellationToken.None);
        long Bytes(FileCategory c) => result.Categories.Single(s => s.Category == c).Bytes;

        Assert.Equal(400, Bytes(FileCategory.Installers));
        Assert.Equal(350, Bytes(FileCategory.Programs)); // everything inside a program folder
        Assert.Equal(1000, Bytes(FileCategory.Video));
        Assert.Equal(20, Bytes(FileCategory.Documents));
        Assert.Equal(5, Bytes(FileCategory.Other));
        Assert.Equal(result.TotalBytes, result.Categories.Sum(c => c.Bytes));
    }

    [Fact]
    public async Task Cancelled_scan_returns_partial_result_without_throwing()
    {
        using var tree = new DiskTestTree("cancel");
        tree.File(@"a\b\c.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new SpaceAnalyzer(NoRules, 10, 2).RunAsync(tree.Root, cts.Token);

        Assert.True(result.IsPartial);
        Assert.NotNull(result.Root);
    }

    [Fact]
    public async Task Missing_folder_is_reported_as_inaccessible_not_thrown()
    {
        TestEnvironment.Init();
        var path = Path.Combine(TestEnvironment.DataDir, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var result = await new SpaceAnalyzer(NoRules, 10, 2).RunAsync(path, CancellationToken.None);

        Assert.Equal(0, result.TotalBytes);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public void TopN_orders_by_size_then_path()
    {
        var hits = new[]
        {
            new FileHit(@"C:\b.bin", 5, DateTime.UtcNow, FileCategory.Other),
            new FileHit(@"C:\a.bin", 5, DateTime.UtcNow, FileCategory.Other),
            new FileHit(@"C:\c.bin", 9, DateTime.UtcNow, FileCategory.Other),
            new FileHit(@"C:\d.bin", 1, DateTime.UtcNow, FileCategory.Other),
        };

        var top = SpaceAnalyzer.TopN(hits, 3);

        Assert.Equal(new[] { @"C:\c.bin", @"C:\a.bin", @"C:\b.bin" }, top.Select(h => h.Path));
    }

    [Fact]
    public void Files_entry_row_does_not_reopen_the_current_folder()
    {
        var root = new FolderNode(@"C:\data", null);
        var sub = new FolderNode("sub", root);
        var leaf = new FolderNode("leaf", sub);
        sub.Children = new[] { leaf };
        root.Children = new[] { sub };

        Assert.True(new FolderRow(sub, "sub", 10, 1, 50, false).CanOpen);
        Assert.False(new FolderRow(root, "Файлы в этой папке", 10, 1, 50, true).CanOpen);
        Assert.False(new FolderRow(leaf, "leaf", 10, 1, 50, false).CanOpen); // no subfolders to show
    }

    [Fact]
    public void Junction_is_detected_as_link_by_reparse_tag()
    {
        using var tree = new DiskTestTree("tag");
        var target = tree.Dir("target");
        var link = tree.Junction("link", target);

        Assert.True(ReparsePoints.IsLinkOrUnknown(link));
        Assert.Null(ReparsePoints.GetTag(target));
    }
}
