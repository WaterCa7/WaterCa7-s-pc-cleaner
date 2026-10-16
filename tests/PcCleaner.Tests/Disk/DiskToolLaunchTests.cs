using PcCleaner.Core;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

/// <summary>"Очистка диска" / "Защита системы" buttons: one window per click, never a second one. Nothing real is started.</summary>
public class DiskToolLaunchTests
{
    public DiskToolLaunchTests() => TestEnvironment.Init();

    private static DiskService Service(Func<string, string, ToolLaunchResult> launch, Func<string, bool> isRunning)
        => new(new DiskEnvironment
        {
            SystemDriveRoot = @"C:\",
            LaunchTool = launch,
            IsToolRunning = isRunning,
            Recycler = _ => throw new InvalidOperationException("recycler must not be called"),
        });

    [Fact]
    public void OpenTool_StartsTheToolOnlyWhenItsWindowIsNotOpen()
    {
        var started = new List<(string Exe, string Args)>();
        var running = false;
        var service = Service((exe, args) =>
        {
            started.Add((exe, args));
            return ToolLaunchResult.Started;
        }, name => running && name == "cleanmgr");

        Assert.Equal(ToolLaunchResult.Started, service.OpenTool(@"C:\Windows\System32\cleanmgr.exe", "/d C", "cleanmgr"));
        Assert.Equal((@"C:\Windows\System32\cleanmgr.exe", "/d C"), Assert.Single(started));

        running = true;
        Assert.Equal(ToolLaunchResult.AlreadyOpen, service.OpenTool(@"C:\Windows\System32\cleanmgr.exe", "/d C", "cleanmgr"));
        Assert.Single(started);

        var failing = Service((_, _) => ToolLaunchResult.Failed, _ => false);
        Assert.Equal(ToolLaunchResult.Failed, failing.OpenTool("x.exe", "", "x"));
    }

    [Fact]
    public void OpenTool_ReportsACancelledUacPromptAsCancelled_NotAsAFailure()
    {
        // «Защита системы» needs admin rights: answering "No" in the UAC prompt is the user's choice, not an error.
        var service = Service((_, _) => ToolLaunchResult.Cancelled, _ => false);
        Assert.Equal(ToolLaunchResult.Cancelled,
            service.OpenTool(@"C:\Windows\System32\SystemPropertiesProtection.exe", "", "SystemPropertiesProtection"));
    }

    [Fact]
    public async Task SystemProtectionButton_CancelledUac_ShowsNoErrorAndIsUsableAgain()
    {
        var launches = 0;
        var vm = new DiskViewModel(Service((_, _) =>
        {
            Interlocked.Increment(ref launches);
            return ToolLaunchResult.Cancelled;
        }, _ => false))
        {
            ToolLaunchCooldown = TimeSpan.FromSeconds(30), // must not be waited for after a cancelled prompt
        };

        // In tests Dialogs.Error writes "[диалог] <title>: <text>" to the log.
        var errorDialogs = new List<string>();
        void OnEntry(LogEntry e)
        {
            if (e.Message.Contains("[диалог]", StringComparison.Ordinal) && e.Message.Contains("Защита системы", StringComparison.Ordinal))
                lock (errorDialogs)
                    errorDialogs.Add(e.Message);
        }

        Log.EntryAdded += OnEntry;
        try
        {
            var run = vm.SystemProtectionCommand.ExecuteAsync();
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(run, finished);
            Assert.Equal(1, launches);
            Assert.True(vm.SystemProtectionCommand.CanExecute(null));
            Assert.Empty(errorDialogs);
        }
        finally
        {
            Log.EntryAdded -= OnEntry;
        }
    }

    [Fact]
    public void IsRunning_IgnoresAProcessWithoutAWindow()
    {
        // Windows' SilentCleanup task runs "cleanmgr /autoclean" without a window: that must not block the button.
        // The test host is such a windowless process (read-only check, nothing is started).
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(SystemTools.IsRunning(self.ProcessName));
        Assert.False(SystemTools.IsRunning("no-such-process-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task DiskCleanupButton_OpensOneWindowOnADoubleClick()
    {
        var launches = 0;
        string? arguments = null;
        var vm = new DiskViewModel(Service((_, args) =>
        {
            arguments = args;
            Interlocked.Increment(ref launches);
            return ToolLaunchResult.Started;
        }, _ => false))
        {
            ToolLaunchCooldown = TimeSpan.FromMilliseconds(200),
        };

        var first = vm.DiskCleanupCommand.ExecuteAsync();
        Assert.False(vm.DiskCleanupCommand.CanExecute(null));
        var second = vm.DiskCleanupCommand.ExecuteAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(1, launches);
        Assert.Equal("/d C", arguments);
        Assert.True(vm.DiskCleanupCommand.CanExecute(null));
    }
}
