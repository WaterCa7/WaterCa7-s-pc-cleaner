using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

public class AsyncRelayCommandTests
{
    public AsyncRelayCommandTests() => TestEnvironment.Init();

    [Fact]
    public async Task LastTask_CompletesWhenTheRunStartedByExecuteEnds()
    {
        var gate = new TaskCompletionSource();
        var ran = false;
        var command = new AsyncRelayCommand(async () =>
        {
            await gate.Task;
            ran = true;
        });

        command.Execute(null);

        Assert.False(command.LastTask.IsCompleted);
        gate.SetResult();
        await command.LastTask;
        Assert.True(ran);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task ACancelledRun_EndsLastTaskWithoutAnError()
    {
        var command = new AsyncRelayCommand(() => throw new OperationCanceledException());

        command.Execute(null);
        await command.LastTask;

        Assert.False(command.IsRunning);
    }
}
