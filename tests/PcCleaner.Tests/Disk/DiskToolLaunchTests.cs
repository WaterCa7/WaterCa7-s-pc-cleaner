using PcCleaner.Core;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

/// <summary>"Очистка диска" / "Защита системы" buttons: one window per click, never a second one. Nothing real is started.</summary>
public class DiskToolLaunchTests
{
    public DiskToolLaunchTests() => TestEnvironment.Init();

    private static DiskService Service(Func<string, string, bool> launch, Func<string, bool> isRunning)
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
            return true;
        }, name => running && name == "cleanmgr");

        Assert.Equal(ToolLaunchResult.Started, service.OpenTool(@"C:\Windows\System32\cleanmgr.exe", "/d C", "cleanmgr"));
        Assert.Equal((@"C:\Windows\System32\cleanmgr.exe", "/d C"), Assert.Single(started));

        running = true;
        Assert.Equal(ToolLaunchResult.AlreadyOpen, service.OpenTool(@"C:\Windows\System32\cleanmgr.exe", "/d C", "cleanmgr"));
        Assert.Single(started);

        var failing = Service((_, _) => false, _ => false);
        Assert.Equal(ToolLaunchResult.Failed, failing.OpenTool("x.exe", "", "x"));
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
            return true;
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
