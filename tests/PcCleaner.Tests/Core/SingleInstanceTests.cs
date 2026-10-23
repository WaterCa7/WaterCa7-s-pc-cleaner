using PcCleaner.Core;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// One copy of the program per Windows session. A Windows mutex belongs to a thread and can be taken again by the thread that
/// holds it, so every second copy here takes it from another thread (Task.Run), and each copy is released on its own thread.
/// Every test uses its own name: nothing here meets a real running copy of the program.
/// </summary>
public class SingleInstanceTests
{
    public SingleInstanceTests() => TestEnvironment.Init();

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static string NewName() => "WaterCa7sPcCleaner.Test." + Guid.NewGuid().ToString("N");

    /// <summary>A copy on another thread: acquires, reports whether it got the place, and closes again on that thread.</summary>
    private static Task<bool> OtherCopy(string name, TimeSpan waitForPrevious)
        => Task.Run(() =>
        {
            using var copy = SingleInstance.Acquire(name, waitForPrevious);
            return copy is not null;
        });

    [Fact]
    public void TheFirstCopy_GetsThePlace()
    {
        using var first = SingleInstance.Acquire(NewName(), TimeSpan.Zero);

        Assert.NotNull(first);
    }

    [Fact]
    public void ASecondCopy_DoesNotStart_WhileTheFirstRuns()
    {
        var name = NewName();
        using var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(first);

        var second = OtherCopy(name, TimeSpan.Zero);

        Assert.True(second.Wait(Patience));
        Assert.False(second.Result);
    }

    [Fact]
    public void ASecondCopy_AsksTheFirstToShowItsWindow()
    {
        var name = NewName();
        using var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(first);
        using var shown = new ManualResetEventSlim();
        first!.ListenForActivation(() => shown.Set());

        Assert.True(OtherCopy(name, TimeSpan.Zero).Wait(Patience));

        Assert.True(shown.Wait(Patience));
    }

    [Fact]
    public void EverySecondCopy_AsksAgain()
    {
        var name = NewName();
        using var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        var count = 0;
        using var twice = new CountdownEvent(2);
        first!.ListenForActivation(() =>
        {
            Interlocked.Increment(ref count);
            twice.Signal();
        });

        Assert.True(OtherCopy(name, TimeSpan.Zero).Wait(Patience));
        Assert.True(OtherCopy(name, TimeSpan.Zero).Wait(Patience));

        Assert.True(twice.Wait(Patience));
        Assert.Equal(2, count);
    }

    [Fact]
    public void AfterTheFirstCopyCloses_ANewOneStarts()
    {
        var name = NewName();
        var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(first);
        first!.Dispose();

        var next = OtherCopy(name, TimeSpan.Zero);

        Assert.True(next.Wait(Patience));
        Assert.True(next.Result);
    }

    [Fact]
    public void ARestartedCopy_WaitsUntilThePreviousOneHasClosed()
    {
        // The restart (rights, update, language) starts the next copy before this one has finished closing.
        var name = NewName();
        var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(first);

        var restarted = OtherCopy(name, Patience);
        Thread.Sleep(300);
        Assert.False(restarted.IsCompleted);
        first!.Dispose();

        Assert.True(restarted.Wait(Patience));
        Assert.True(restarted.Result);
    }

    [Fact]
    public void ARestartedCopy_GivesUp_WhenThePreviousOneDoesNotClose()
    {
        var name = NewName();
        using var first = SingleInstance.Acquire(name, TimeSpan.Zero);
        Assert.NotNull(first);

        var restarted = OtherCopy(name, TimeSpan.FromMilliseconds(300));

        Assert.True(restarted.Wait(Patience));
        Assert.False(restarted.Result);
    }

    [Fact]
    public void ACopyThatCrashed_DoesNotBlockTheNextStart()
    {
        // A copy that ended without closing (a crash, a killed process) leaves an abandoned mutex: the next copy takes it.
        var name = NewName();
        var crashed = new Thread(() => SingleInstance.Acquire(name, TimeSpan.Zero));
        crashed.Start();
        Assert.True(crashed.Join(Patience));

        // Join can return before the OS thread is gone, and only then is the mutex abandoned: the next copy waits for that
        // (WaitOne ends with AbandonedMutexException, the catch in SingleInstance.Acquire) instead of looking once.
        var next = OtherCopy(name, Patience);

        Assert.True(next.Wait(Patience));
        Assert.True(next.Result);
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        var first = SingleInstance.Acquire(NewName(), TimeSpan.Zero);
        first!.ListenForActivation(() => { });

        first.Dispose();
        first.Dispose();
    }

    // ----- which starts wait for the previous copy -----

    [Theory]
    [InlineData("", false)]                              // a click on the shortcut
    [InlineData("--page Cleaner", false)]
    [InlineData("--elevation-declined", false)]
    [InlineData("--elevated-restart", true)]             // the restart with rights at startup
    [InlineData("--updated-from 1.2.0", true)]           // the updater started the new version
    [InlineData("--restarted", true)]                    // «Перезапустить сейчас», «Запустить как админ»
    [InlineData("--from-task", true)]                    // the task's copy: the shortcut's copy is still closing
    [InlineData("--page Cleaner --RESTARTED", true)]     // arguments are case-insensitive
    public void WaitsForPrevious(string args, bool expected)
        => Assert.Equal(expected, SingleInstance.WaitsForPrevious(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void TheRestartMarker_BelongsToOneStart_AndIsNotPassedOn()
        => Assert.Equal(new[] { "--page", "Cleaner" },
            UpdateService.WithoutStartMarkers(new[] { "--restarted", "--page", "Cleaner", "--elevated-restart", "--from-task" }));
}
