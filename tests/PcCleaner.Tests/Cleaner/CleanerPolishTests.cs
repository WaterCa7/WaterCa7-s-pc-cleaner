using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>The Disk Cleanup launcher, busy-vs-protected wording and copy-on-write settings lists.</summary>
public class CleanerPolishTests
{
    public CleanerPolishTests() => TestEnvironment.Init();

    private static (CleanerViewModel Vm, AsyncRelayCommand Command) Launcher(CleanerTestFolder t, Func<string, bool> start,
        Func<bool>? isRunning = null)
    {
        var env = CleanerEnvironment.ForTests(t.Root, startDiskCleanup: start, isDiskCleanupRunning: isRunning);
        var service = new CleanerService(env, null, new FakeRecycleBin(), () => false, () => new AppSettings());
        var vm = new CleanerViewModel(service) { DiskCleanupCooldown = TimeSpan.FromMilliseconds(200) };
        return (vm, (AsyncRelayCommand)vm.LaunchDiskCleanupCommand);
    }

    [Fact]
    public async Task DiskCleanupWizard_OpensOnce_OnADoubleClick()
    {
        using var t = new CleanerTestFolder("launcher-double");
        var launches = 0;
        string? drive = null;
        var (_, command) = Launcher(t, d =>
        {
            drive = d;
            Interlocked.Increment(ref launches);
            return true;
        });

        // Two clicks in a row: the second one arrives while the first is still running.
        var first = command.ExecuteAsync();
        Assert.False(command.CanExecute(null));
        var second = command.ExecuteAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(1, launches);
        Assert.Equal(Path.GetPathRoot(t.Root)!.TrimEnd('\\').TrimEnd(':'), drive);
        Assert.True(command.CanExecute(null)); // usable again once the window had time to appear
    }

    [Fact]
    public async Task DiskCleanupWizard_IsNotStartedAgainWhileItsWindowIsOpen()
    {
        using var t = new CleanerTestFolder("launcher-open");
        var launches = 0;
        var (_, command) = Launcher(t, _ =>
        {
            launches++;
            return true;
        }, isRunning: () => true);

        await command.ExecuteAsync();

        Assert.Equal(0, launches);
    }

    [Fact]
    public void DiskCleanupLaunch_ReportsWhatHappened()
    {
        using var t = new CleanerTestFolder("launcher-service");
        var running = false;
        var env = CleanerEnvironment.ForTests(t.Root, startDiskCleanup: _ => true, isDiskCleanupRunning: () => running);
        var service = new CleanerService(env, null, new FakeRecycleBin(), () => false, () => new AppSettings());

        Assert.Equal(DiskCleanupLaunch.Started, service.LaunchDiskCleanup());
        running = true;
        Assert.Equal(DiskCleanupLaunch.AlreadyOpen, service.LaunchDiskCleanup());

        var failing = CleanerEnvironment.ForTests(t.Root); // the test default never starts anything and reports a failure
        var failingService = new CleanerService(failing, null, new FakeRecycleBin(), () => false, () => new AppSettings());
        Assert.Equal(DiskCleanupLaunch.Failed, failingService.LaunchDiskCleanup());
    }

    [Fact]
    public void DiskCleanupRunningCheck_IgnoresAWindowlessProcess()
    {
        // Windows' SilentCleanup task runs "cleanmgr /autoclean" hidden: that is not an open wizard the user could switch
        // to. The test host has no window either (read-only check, nothing is started).
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(CleanerEnvironment.IsProcessRunning(self.ProcessName));
    }

    // ---------- skipped files: busy vs no access ----------

    private static string Plain(string s) => s.Replace(' ', ' ').Replace(' ', ' ');

    [Theory]
    [InlineData(1, 0, "пропущен 1 занятый")]
    [InlineData(87, 0, "пропущено 87 занятых")]
    [InlineData(0, 1, "пропущен 1 файл без доступа")]
    [InlineData(0, 3, "пропущено 3 файла без доступа")]
    [InlineData(0, 12, "пропущено 12 файлов без доступа")]
    [InlineData(87, 5, "пропущено: 87 занятых, 5 без доступа")]
    [InlineData(1, 21, "пропущено: 1 занятый, 21 без доступа")]
    [InlineData(0, 0, "")]
    public void SkippedWording_TellsBusyFilesFromProtectedOnes(long busy, long denied, string expected)
        => Assert.Equal(expected, Plain(CleanerText.Skipped(busy, denied)));

    [Fact]
    public void ResultSummary_AndHint_DependOnWhyFilesWereSkipped()
    {
        CategoryCleanResult Result(long locked, long denied) => new()
        {
            CategoryId = "win.usertemp", Name = "Временные файлы", FreedBytes = 2048, DeletedFiles = 10,
            LockedFiles = locked, DeniedFiles = denied,
        };

        var busyOnly = new CleanResultViewModel(new[] { Result(3, 0) }, cancelled: false);
        Assert.Equal("Удалено 10 файлов, пропущено 3 занятых.", Plain(busyOnly.Summary));
        Assert.Contains("открыты в запущенных программах", busyOnly.Hint);
        Assert.DoesNotContain("без доступа", busyOnly.Hint);

        var deniedOnly = new CleanResultViewModel(new[] { Result(0, 2) }, cancelled: false, elevated: false);
        Assert.Equal("Удалено 10 файлов, пропущено 2 файла без доступа.", Plain(deniedOnly.Summary));
        Assert.Contains("от имени администратора", deniedOnly.Hint);
        Assert.DoesNotContain("запущенных программах", deniedOnly.Hint);
        Assert.Equal("удалено 10 файлов, пропущено 2 файла без доступа", Plain(deniedOnly.Lines.Single().Note));

        var deniedAsAdmin = new CleanResultViewModel(new[] { Result(0, 2) }, cancelled: false, elevated: true);
        Assert.Contains("защищены Windows", deniedAsAdmin.Hint);

        var both = new CleanResultViewModel(new[] { Result(1, 4) }, cancelled: false);
        Assert.Equal("Удалено 10 файлов, пропущено: 1 занятый, 4 без доступа.", Plain(both.Summary));
        Assert.Contains("запущенных программах", both.Hint);
        Assert.Contains("без доступа", both.Hint);

        Assert.Null(new CleanResultViewModel(new[] { Result(0, 0) }, cancelled: false).Hint);
        Assert.Equal("Освобождено 2 КБ, удалено 10 файлов, пропущено: 1 занятый, 4 без доступа.",
            Plain(CleanerService.Summarize(new[] { Result(1, 4) })));
    }

    // ---------- settings lists are replaced, never edited in place ----------

    [Fact]
    public async Task CheckingCategories_ReplacesTheDisabledList()
    {
        using var t = new CleanerTestFolder("cow-disabled");
        var settings = new AppSettings();
        var service = CleanerServiceTests.CreateService(t, out _, out _, settings);
        var vm = new CleanerViewModel(service);
        await vm.PreviewScanAsync(CancellationToken.None);
        var thumbs = vm.Groups.SelectMany(g => g.Items).Single(i => i.Id == "win.thumbcache");
        var before = settings.CleanerDisabledCategories;

        thumbs.IsChecked = false;

        Assert.Contains("win.thumbcache", settings.CleanerDisabledCategories);
        Assert.Empty(before); // another page may be reading the old list right now
    }
}
