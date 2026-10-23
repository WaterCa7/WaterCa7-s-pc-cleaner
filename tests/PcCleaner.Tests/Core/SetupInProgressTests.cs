using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// A start while the setup runs (a shortcut click during an update, the setup's own relaunch, the postinstall launch) waits until
/// the setup has exited. The waits here are fakes; the real mutex test uses its own name, never the setup's.
/// </summary>
public class SetupInProgressTests
{
    public SetupInProgressTests() => TestEnvironment.Init();

    private const string WaitText = "Идёт установка программы — ждём, пока она закончится";

    private static int WaitLines() => Log.Entries.ToList().Count(e => e.Message == WaitText);

    [Fact]
    public void NoSetup_NoWait_NoLogLine()
    {
        var delays = 0;
        var before = WaitLines();

        Assert.True(SetupInProgress.WaitUntilDone(() => false, TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(500), _ => delays++));

        Assert.Equal(0, delays);
        Assert.Equal(before, WaitLines());
    }

    [Fact]
    public void ARunningSetup_IsWaitedFor_AndLoggedOnce()
    {
        var left = 3;
        var delays = new List<TimeSpan>();
        var before = WaitLines();

        Assert.True(SetupInProgress.WaitUntilDone(() => left-- > 0, TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(500), delays.Add));

        Assert.Equal(3, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMilliseconds(500), d));
        Assert.Equal(before + 1, WaitLines());
    }

    [Fact]
    public void ASetupThatNeverEnds_IsWaitedForAtMostTheLimit()
    {
        var delays = 0;

        Assert.False(SetupInProgress.WaitUntilDone(() => true, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500), _ => delays++));

        Assert.Equal(6, delays);
    }

    [Fact]
    public void TheProbe_SeesAMutexOfThisSession_OnlyWhileItExists()
    {
        var name = "WaterCa7sPcCleaner.Test." + Guid.NewGuid().ToString("N");
        Assert.False(SetupInProgress.MutexExists(name));

        using (new Mutex(false, @"Local\" + name))
            Assert.True(SetupInProgress.MutexExists(name));

        Assert.False(SetupInProgress.MutexExists(name));
    }

    [Fact]
    public void TheProgramWaitsForTheMutexThatTheSetupCreates()
        => Assert.Equal("WaterCa7sPcCleaner.Setup", SetupInProgress.MutexName);
}
