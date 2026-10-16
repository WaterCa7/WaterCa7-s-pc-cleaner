using System.IO;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class DownloadsAdvisorTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0);

    private static FoundFile F(string name, long size, int createdDaysAgo, int modifiedDaysAgo)
        => new($@"C:\Users\U\Downloads\{name}", size, Now.AddDays(-createdDaysAgo), Now.AddDays(-modifiedDaysAgo));

    [Theory]
    [InlineData("setup.exe", DownloadKind.Installers)]
    [InlineData("tool.MSI", DownloadKind.Installers)]
    [InlineData("pack.zip", DownloadKind.Archives)]
    [InlineData("pack.rar", DownloadKind.Archives)]
    [InlineData("pack.7z", DownloadKind.Archives)]
    [InlineData("win.iso", DownloadKind.DiskImages)]
    [InlineData("photo.jpg", null)]
    [InlineData("README", null)]
    public void Kind_by_extension(string name, DownloadKind? expected)
        => Assert.Equal(expected, DownloadsAdvisor.KindOf(name));

    [Fact]
    public void Only_files_older_than_90_days_by_both_dates_are_old()
    {
        Assert.True(DownloadsAdvisor.IsOlderThan(F("a.zip", 1, 200, 200), Now, 90));
        Assert.True(DownloadsAdvisor.IsOlderThan(F("a.zip", 1, 90, 120), Now, 90));
        // downloaded (created) recently, even if the server timestamp is old -> not old
        Assert.False(DownloadsAdvisor.IsOlderThan(F("a.zip", 1, 10, 400), Now, 90));
        Assert.False(DownloadsAdvisor.IsOlderThan(F("a.zip", 1, 400, 5), Now, 90));
    }

    [Fact]
    public void Groups_old_installers_archives_and_images_sorted_by_size()
    {
        var files = new[]
        {
            F("small.exe", 10, 100, 100),
            F("big.msi", 500, 300, 300),
            F("fresh.exe", 999, 3, 3),
            F("old.zip", 70, 120, 120),
            F("ubuntu.iso", 4000, 95, 95),
            F("photo.jpg", 5000, 400, 400),
        };

        var groups = DownloadsAdvisor.GroupOld(files, Now);

        Assert.Equal(new[] { DownloadKind.Installers, DownloadKind.Archives, DownloadKind.DiskImages }, groups.Select(g => g.Kind));
        Assert.Equal(new[] { "big.msi", "small.exe" }, groups[0].Files.Select(f => f.Name));
        Assert.Equal(510, groups[0].TotalBytes);
        Assert.Equal(4000, groups[2].TotalBytes);
        Assert.DoesNotContain(groups.SelectMany(g => g.Files), f => f.Name is "fresh.exe" or "photo.jpg");
    }

    [Fact]
    public void Service_finds_old_downloads_in_a_real_folder_without_following_links()
    {
        using var tree = new DiskTestTree("downloads");
        using var elsewhere = new DiskTestTree("elsewhere");
        var downloads = tree.Dir("Downloads");
        var old = DateTime.Now.AddDays(-200);
        tree.File(@"Downloads\old-setup.exe", 300, old);
        tree.File(@"Downloads\sub\old-archive.zip", 200, old);
        tree.File(@"Downloads\new-setup.exe", 100, DateTime.Now.AddDays(-1));
        tree.File(@"Downloads\old-photo.png", 100, old);
        elsewhere.File("linked-old.zip", 5000, old);
        tree.Junction(@"Downloads\link", elsewhere.Root);

        var service = new DiskService(new DiskEnvironment { SystemDriveRoot = tree.Root, DownloadsDir = downloads });
        var groups = service.FindOldDownloads(CancellationToken.None);

        var names = groups.SelectMany(g => g.Files).Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "old-archive.zip", "old-setup.exe" }, names);
        Assert.Equal(500, groups.Sum(g => g.TotalBytes));
    }

    [Fact]
    public void Old_downloads_issue_only_above_one_gigabyte()
    {
        var service = new DiskService(new DiskEnvironment { SystemDriveRoot = @"C:\" });
        var small = new List<FileGroup> { new(DownloadKind.Archives, new[] { F("a.zip", 500L << 20, 200, 200) }) };
        var big = new List<FileGroup>
        {
            new(DownloadKind.Installers, new[] { F("a.exe", 700L << 20, 200, 200) }),
            new(DownloadKind.Archives, new[] { F("b.zip", 600L << 20, 200, 200) }),
        };

        Assert.Null(service.OldDownloadsIssue(small));
        var issue = service.OldDownloadsIssue(big);
        Assert.NotNull(issue);
        Assert.Equal(PcCleaner.Core.IssueSeverity.Low, issue!.Severity);
        Assert.Null(issue.FixAsync);
        Assert.Equal("Disk", issue.TargetPageKey);
        Assert.Equal(1300L << 20, issue.ReclaimableBytes);
        Assert.Contains("Загрузк", issue.Title);
    }

    [Fact]
    public void Big_videos_are_found_in_search_folders_only()
    {
        using var tree = new DiskTestTree("videos");
        using var elsewhere = new DiskTestTree("videos-elsewhere");
        var videos = tree.Dir("Videos");
        tree.File(@"Videos\film.mkv", 3000);
        tree.File(@"Videos\small.mp4", 10);
        tree.File(@"Videos\big.iso", 9000);
        elsewhere.File("linked.mp4", 8000);
        tree.Junction(@"Videos\link", elsewhere.Root);

        var service = new DiskService(new DiskEnvironment
        {
            SystemDriveRoot = tree.Root,
            VideoSearchDirs = new[] { videos, videos },
            BigVideoThreshold = 1000,
        });
        var found = service.FindBigVideos(CancellationToken.None);

        var file = Assert.Single(found);
        Assert.Equal("film.mkv", file.Name);
    }
}
