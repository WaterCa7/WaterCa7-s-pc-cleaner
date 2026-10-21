using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Disk;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Disk;

/// <summary>
/// The Disks page and its dashboard findings in English (the real en.json): the low-space, old-downloads, Windows.old and failing-disk
/// issues with their fix results (and that a failed fix is still a failure), the labels of drives and categories, the suggestion cards.
/// Runs alone (the language is process-wide) and always switches back to Russian; the Russian twin of the main texts is checked
/// outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class DiskEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");
    private static readonly DateTime Old = new(2025, 1, 10, 12, 0, 0);

    public DiskEnglishTests() => TestEnvironment.Init();

    private static void AssertEnglish(params string?[] texts)
    {
        foreach (var text in texts)
            Assert.DoesNotMatch(Cyrillic, text ?? "");
    }

    private static DiskService Service(Func<string, RecycleOutcome>? recycler = null, Func<string, string, ToolLaunchResult>? launch = null,
        Func<string, bool>? isRunning = null)
        => new(new DiskEnvironment
        {
            SystemDriveRoot = @"C:\",
            Recycler = recycler ?? (_ => throw new InvalidOperationException("recycler must not be called")),
            LaunchTool = launch ?? ((_, _) => throw new InvalidOperationException("no tool must be started")),
            IsToolRunning = isRunning ?? (_ => false),
        });

    private static DriveSummary Drive(char letter, long totalGb, double freeGb, bool system = false) => new()
    {
        Letter = letter,
        RootPath = letter + @":\",
        TotalBytes = totalGb << 30,
        FreeBytes = (long)(freeGb * (1L << 30)),
        IsSystemDrive = system,
    };

    private static FoundFile Scanned(string path)
    {
        var info = new FileInfo(path);
        return new FoundFile(path, info.Length, info.CreationTime, info.LastWriteTime);
    }

    /// <summary>A file found by the scan that is gone by now; its size lifts the issue over the 1 GB threshold.</summary>
    private static FoundFile Ghost()
        => new(Path.Combine(Path.GetTempPath(), "WaterCa7sTests-gone-" + Guid.NewGuid().ToString("N"), "ghost.iso"), 2L << 30, Old, Old);

    // ================================================================ low space

    [Fact]
    public void ALowSpaceIssue_HasEnglishTitleDetailsAndGain_AndRussianIsUnchanged()
    {
        var drive = Drive('C', 256, 20, system: true);
        var service = Service();

        var russian = Assert.Single(service.LowSpaceIssues(new[] { drive }));
        Assert.Equal("Диски", russian.Module);
        Assert.Equal("Мало места на диске C:", russian.Title);

        using (TestLanguage.English())
        {
            var issue = Assert.Single(service.LowSpaceIssues(new[] { drive }));
            var need = (long)(drive.TotalBytes * 0.15) - drive.FreeBytes;

            Assert.Equal("Disks", issue.Module);
            Assert.Equal("Low space on drive C:", issue.Title);
            Assert.Equal($"{Format.Bytes(drive.FreeBytes)} free of {Format.Bytes(drive.TotalBytes)} ({Format.Percent(drive.FreePercent)}). " +
                         "This is the system drive: when it is low on space, Windows runs slower and cannot install updates or create a page file. " +
                         "Open the Disks page → “What takes up space” to find what you can delete.", issue.Details);
            Assert.Equal($"Free up ≈ {Format.Bytes(need)} to get 15% free", issue.Gain);
            AssertEnglish(issue.Module, issue.Title, issue.Details, issue.Gain);
        }
    }

    [Fact]
    public void AFailingDisk_HasAnEnglishTitleAndDetails()
    {
        var service = Service();
        var disks = new[]
        {
            new PhysicalDiskInfo { DeviceId = "1", Number = 1, Model = "Old HDD", Health = DiskHealth.Warning, Letters = new() { 'D' } },
        };

        using (TestLanguage.English())
        {
            var issue = Assert.Single(service.HealthIssues(disks));

            Assert.Equal("The drive may fail soon — back up your files", issue.Title);
            Assert.StartsWith("The drive “Old HDD” (volumes: D:) reports the status “Warning”. Copy your important files", issue.Details);
            AssertEnglish(issue.Title, issue.Details);
        }
    }

    // ================================================================ old downloads

    [Fact]
    public async Task OldDownloads_HaveAnEnglishTitleLabelAndFixResult()
    {
        using var tree = new DiskTestTree("en-downloads");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var busy = tree.File(@"Downloads\busy.zip", 2000, Old);
        var service = Service(path => path == busy ? new RecycleOutcome(RecycleFailure.InUse) : RecycleOutcome.Ok);

        using (TestLanguage.English())
        {
            var issue = service.OldDownloadsIssue(new List<FileGroup> { new(DownloadKind.Installers, new[] { Ghost() }) });

            Assert.NotNull(issue);
            Assert.Equal("Old installers and archives in “Downloads”: 2 GB", issue!.Title);
            Assert.Equal("To Recycle Bin", issue.FixLabel);
            Assert.Equal("Frees up to 2 GB", issue.Gain);
            Assert.StartsWith("1 file older than 90 days (installers — 2 GB). The button moves them all to the Recycle Bin", issue.Details);

            // One file is moved, the other is refused by the Recycle Bin (open in another program): a success with a side note.
            var text = await service.RecycleOldDownloadsAsync(new[] { Scanned(setup), Scanned(busy) }, CancellationToken.None);

            Assert.StartsWith($"1 file was moved to the Recycle Bin ({Format.Bytes(1000)}) — you can restore it.", text);
            Assert.Contains("Left untouched: 1 file", text);
            Assert.False(HealthScoring.LooksLikeFailure(text), text);
            AssertEnglish(issue.Title, issue.Details, issue.FixLabel, text);
        }
    }

    [Fact]
    public async Task OldDownloads_WhenNothingCouldBeMoved_AreStillAFailure_ButNotAnAdminProblem()
    {
        using var tree = new DiskTestTree("en-downloads-denied");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var service = Service(_ => new RecycleOutcome(RecycleFailure.AccessDenied));

        using (TestLanguage.English())
        {
            var text = await service.RecycleOldDownloadsAsync(new[] { Scanned(setup) }, CancellationToken.None);

            Assert.Equal("Could not move the files to the Recycle Bin: the files are in use or protected. See the log for details.", text);
            Assert.True(HealthScoring.LooksLikeFailure(text));
            Assert.False(HealthScoring.LooksLikeAdminRequired(text));
        }
    }

    [Fact]
    public async Task OldDownloads_ThatChangedSinceTheScan_AreAFailure_AndThatAreGone_AreNot()
    {
        using var tree = new DiskTestTree("en-downloads-changed");
        var setup = tree.File(@"Downloads\setup.exe", 1000, Old);
        var scanned = Scanned(setup);
        tree.File(@"Downloads\setup.exe", 5000, Old); // another size than the scan saw
        var service = Service();

        using (TestLanguage.English())
        {
            var changed = await service.RecycleOldDownloadsAsync(new[] { scanned }, CancellationToken.None);
            var gone = await service.RecycleOldDownloadsAsync(new[] { Ghost() }, CancellationToken.None);

            Assert.Equal("The files changed after the check — nothing was changed. Open Disks → “Space tips” and look at the list again.", changed);
            Assert.True(HealthScoring.LooksLikeFailure(changed));
            Assert.Equal("The files are already deleted — there is nothing to do.", gone);
            Assert.False(HealthScoring.LooksLikeFailure(gone));
        }
    }

    // ================================================================ Windows.old

    [Fact]
    public void WindowsOld_HasAnEnglishTitleGainAndLabel_AndAPartialSizeSaysAtLeast()
    {
        var service = Service();

        using (TestLanguage.English())
        {
            var whole = service.WindowsOldIssue(5L << 30, complete: true);
            var partial = service.WindowsOldIssue(5L << 30, complete: false);

            Assert.Equal("The Windows.old folder is left over (5 GB)", whole.Title);
            Assert.Equal("The Windows.old folder is left over (at least 5 GB)", partial.Title);
            Assert.Equal("Frees up at least 5 GB", partial.Gain);
            Assert.Equal("Open Disk Cleanup", whole.FixLabel);
            AssertEnglish(whole.Title, partial.Title, partial.Gain, whole.Details, whole.FixLabel);
        }
    }

    [Fact]
    public async Task WindowsOldFix_ResultsAreEnglish_AndAFailedLaunchIsAFailure()
    {
        using (TestLanguage.English())
        {
            var started = await Service(launch: (_, _) => ToolLaunchResult.Started).WindowsOldIssue(5L << 30, true).FixAsync!(CancellationToken.None);
            var open = await Service(isRunning: name => name == "cleanmgr").WindowsOldIssue(5L << 30, true).FixAsync!(CancellationToken.None);
            var failed = await Service(launch: (_, _) => ToolLaunchResult.Failed).WindowsOldIssue(5L << 30, true).FixAsync!(CancellationToken.None);

            Assert.Equal("“Disk Cleanup” is open: click “Clean up system files” and check “Previous Windows installation(s)”.", started);
            Assert.StartsWith("“Disk Cleanup” is already open — switch to it from the taskbar", open);
            Assert.Equal("Could not open “Disk Cleanup”.", failed);
            Assert.False(HealthScoring.LooksLikeFailure(started));
            Assert.False(HealthScoring.LooksLikeFailure(open));
            Assert.True(HealthScoring.LooksLikeFailure(failed));
        }
    }

    // ================================================================ admin-only fixes

    [Fact]
    public async Task TheTrimAndHibernationFixes_WithoutAdmin_AreEnglish_AndNeedAdministratorRights()
    {
        if (Admin.IsElevated)
            return; // only meaningful in a normal (non-elevated) test run
        var service = Service();

        using (TestLanguage.English())
        {
            var trim = await service.EnableTrimAsync(CancellationToken.None);
            var hibernation = await service.DisableHibernationAsync(CancellationToken.None);
            var optimize = await service.OptimizeDriveAsync('C', _ => { }, CancellationToken.None);

            Assert.Equal("Administrator rights are needed: restart the program as administrator and try again. Nothing was changed.", trim);
            Assert.Equal(trim, hibernation);
            Assert.True(HealthScoring.LooksLikeAdminRequired(trim));
            Assert.True(HealthScoring.LooksLikeFailure(trim));
            Assert.Equal("Administrator rights are needed. Nothing was changed.", optimize.Summary);
            Assert.True(optimize.NeedsAdmin);
        }
    }

    // ================================================================ labels

    [Fact]
    public void TheLabelsOfDrivesAndDisks_AreEnglish()
    {
        var drive = new DriveItem(new DriveSummary
        {
            Letter = 'C',
            RootPath = @"C:\",
            TotalBytes = 500L << 30,
            FreeBytes = 20L << 30,
            IsSystemDrive = true,
            Disk = new PhysicalDiskInfo { DeviceId = "0", Number = 0, Model = "Test SSD", Kind = DriveKind.Ssd, Health = DiskHealth.Healthy },
        }, TrimState.Disabled);
        var russianTitle = drive.Title;

        using (TestLanguage.English())
        {
            Assert.Equal("Local disk (C:)", drive.Title);
            Assert.Equal("free of 500 GB", drive.TotalText);
            Assert.Equal("TRIM is off", drive.TrimText);
            Assert.Equal("Healthy", drive.HealthText);
            Assert.Equal("Very little free space — Windows may run slower.", drive.LowSpaceText);
            Assert.Equal("Optimize", drive.OptimizeText);
            Assert.Contains("ReTrim", drive.OptimizeToolTip);
            Assert.Contains(drive.Badges, b => b.Text == "SSD");
            Assert.Contains(drive.Badges, b => b.Text == "System");
            AssertEnglish(drive.Title, drive.TotalText, drive.TrimText, drive.HealthText, drive.LowSpaceText, drive.OptimizeToolTip,
                string.Join(" ", drive.Badges.Select(b => b.Text + " " + b.ToolTip)));

            Assert.Equal("Type unknown", StorageLabels.KindLabel(DriveKind.Unknown));
            Assert.Equal("Virtual", StorageLabels.BusLabel(14));
            Assert.Equal("SATA", StorageLabels.BusLabel(11));
            Assert.Equal("Operating normally", StorageLabels.OperationalStatusLabel(2L));
            Assert.Equal("Status code 99", StorageLabels.OperationalStatusLabel(99L));
            Assert.Equal("No data", StorageLabels.OperationalStatusLabel(null));
            Assert.Equal("7200 rpm", StorageLabels.SpindleLabel(7200));
        }

        Assert.Equal("Локальный диск (C:)", russianTitle);
        Assert.Equal("Локальный диск (C:)", drive.Title);
    }

    [Fact]
    public void ThePhysicalDiskRow_HasEnglishStats()
    {
        using (TestLanguage.English())
        {
            // the model keeps the status text it was built with, so it is built in English too
            var disk = new PhysicalDiskInfo
            {
                DeviceId = "0",
                Number = 0,
                Model = "Test SSD",
                Kind = DriveKind.Ssd,
                Health = DiskHealth.Warning,
                SizeBytes = 512L << 30,
                Letters = new() { 'C', 'D' },
                Reliability = new ReliabilityInfo("0", 55, null, 72, 480, 0, 0),
            };

            var row = new PhysicalDiskItem(disk, TrimState.Enabled, reliabilityRequested: true);

            Assert.Equal($"Disk 0 · {Format.Bytes(512L << 30)} · volumes C:, D:", row.Details);
            Assert.Equal(new[] { "State", "TRIM", "Temperature", "Wear", "Power-on time", "Read errors" }, row.Stats.Select(s => s.Label).ToArray());
            Assert.Contains(row.Stats, s => s.Label == "Power-on time" && s.Value == "480 h");
            Assert.Contains(row.Stats, s => s.Label == "TRIM" && s.Value == "on");
            AssertEnglish(row.Details, string.Join(" ", row.Stats.Select(s => s.Label + " " + s.Value + " " + s.ToolTip)));
        }
    }

    [Fact]
    public void TheCategories_FollowTheLanguage_InBothDirections()
    {
        Assert.Equal("Видео", FileClassifier.Info(FileCategory.Video).Name);

        using (TestLanguage.English())
        {
            var video = FileClassifier.Info(FileCategory.Video);
            Assert.Equal("Videos", video.Name);
            Assert.Equal("Games and programs", FileClassifier.Info(FileCategory.Programs).Name);
            AssertEnglish(video.Name, video.Description);
            foreach (var category in Enum.GetValues<FileCategory>())
            {
                var info = FileClassifier.Info(category);
                AssertEnglish(info.Name, info.Description);
            }
        }

        Assert.Equal("Видео", FileClassifier.Info(FileCategory.Video).Name); // the cache is rebuilt for Russian
    }

    [Fact]
    public void TheDownloadGroups_HaveEnglishTitlesAndDescriptions()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("Old program installers", DownloadsAdvisor.GroupTitle(DownloadKind.Installers));
            Assert.Equal("Old archives in “Downloads”", DownloadsAdvisor.GroupTitleInDownloads(DownloadKind.Archives));
            Assert.Equal("Archives (.zip, .rar, .7z) in “Downloads” older than 90 days. If you have already unpacked them, you no longer need the archives.",
                DownloadsAdvisor.GroupDescription(DownloadKind.Archives, 90));
        }
    }

    [Fact]
    public void ASuggestionCard_SaysAtLeast_AndCountsTheCheckedFilesInEnglish()
    {
        var item = new SuggestionItem(SuggestionKind.OldDownloads, "x", Tone.Warning, "Title", "Description", 0);
        item.AddFiles(new[]
        {
            new SuggestionFile(new FoundFile(@"C:\Downloads\a.iso", 1000, Old, Old), null),
            new SuggestionFile(new FoundFile(@"C:\Downloads\b.iso", 2000, Old, Old), null),
        });

        using (TestLanguage.English())
        {
            item.SetSize(5L << 30, atLeast: true);
            Assert.Equal("at least 5 GB", item.SizeText);
            item.SetSize(5L << 30, atLeast: false);
            Assert.Equal("5 GB", item.SizeText);

            Assert.Equal("Show 2 files", item.ExpandText);
            Assert.Equal("Nothing is checked", item.CheckedText);
            item.Files[0].IsChecked = true;
            Assert.Equal($"Checked: 1 file — {Format.Bytes(1000)}", item.CheckedText);
            item.IsExpanded = true;
            Assert.Equal("Hide files", item.ExpandText);
        }
    }
}
