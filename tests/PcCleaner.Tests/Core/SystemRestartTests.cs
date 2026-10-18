using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>"Перезагрузить" on the dashboard: the question comes first and nothing restarts without a "Да". Windows is never restarted here.</summary>
[Collection("SystemRestart")]
public sealed class SystemRestartTests
{
    public SystemRestartTests() => TestEnvironment.Init();

    [Fact]
    public async Task Declined_RestartsNothing_AndSaysNothingWasChanged()
    {
        using var fake = new SystemRestartFake(answer: false);

        var text = await SystemRestart.RestartAsync(CancellationToken.None);

        Assert.Equal("Перезагрузка отменена — ничего не изменено.", text);
        Assert.Equal(0, fake.StartCalls);
        // The dashboard must not mark the row as fixed, and it is not a "run as administrator" case.
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.False(HealthScoring.LooksLikeAdminRequired(text));
    }

    [Fact]
    public async Task Question_WarnsAboutUnsavedWork()
    {
        using var fake = new SystemRestartFake(answer: false);

        await SystemRestart.RestartAsync(CancellationToken.None);

        var (title, question) = Assert.Single(fake.Questions);
        Assert.Equal("Перезагрузить компьютер", title);
        Assert.Equal("Сохраните работу во всех программах. Компьютер перезагрузится через несколько секунд. Продолжить?", question);
    }

    [Fact]
    public async Task Confirmed_RestartsExactlyOnce()
    {
        using var fake = new SystemRestartFake(answer: true);

        var text = await SystemRestart.RestartAsync(CancellationToken.None);

        Assert.Equal("Компьютер перезагружается…", text);
        Assert.Equal(1, fake.StartCalls);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task ConfirmedButRestartCannotStart_IsAFailure()
    {
        using var fake = new SystemRestartFake(answer: true, startSucceeds: false);

        var text = await SystemRestart.RestartAsync(CancellationToken.None);

        Assert.Equal("Не удалось перезагрузить компьютер.", text);
        Assert.Equal(1, fake.StartCalls);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.False(HealthScoring.LooksLikeAdminRequired(text));
    }

    [Fact]
    public async Task CancelledBeforeTheQuestion_AsksNothing()
    {
        using var fake = new SystemRestartFake(answer: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemRestart.RestartAsync(cts.Token));

        Assert.Empty(fake.Questions);
        Assert.Equal(0, fake.StartCalls);
    }

    [Fact]
    public async Task CancelledWhileTheQuestionIsOpen_DoesNotRestart()
    {
        using var cts = new CancellationTokenSource();
        using var fake = new SystemRestartFake(answer: true, onConfirm: cts.Cancel); // "Отмена" pressed, then "Да" clicked

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemRestart.RestartAsync(cts.Token));

        Assert.Equal(0, fake.StartCalls);
    }

    // ===== RestartWithoutAskingAsync: the caller (the dashboard's «Исправить проблему» question) has already asked =====

    [Fact]
    public async Task AlreadyAsked_RestartsOnceWithoutAskingAgain()
    {
        using var fake = new SystemRestartFake(answer: false); // would refuse if it were asked

        var text = await SystemRestart.RestartWithoutAskingAsync(CancellationToken.None);

        Assert.Equal("Компьютер перезагружается…", text);
        Assert.Equal(1, fake.StartCalls);
        Assert.Empty(fake.Questions);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task AlreadyAsked_RestartCannotStart_IsAFailure()
    {
        using var fake = new SystemRestartFake(answer: true, startSucceeds: false);

        var text = await SystemRestart.RestartWithoutAskingAsync(CancellationToken.None);

        Assert.Equal("Не удалось перезагрузить компьютер.", text);
        Assert.Empty(fake.Questions);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task AlreadyAsked_CancelledByTheUser_DoesNotRestart()
    {
        using var fake = new SystemRestartFake(answer: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemRestart.RestartWithoutAskingAsync(cts.Token));

        Assert.Equal(0, fake.StartCalls);
    }

    [Fact]
    public void TestEnvironment_InstallsASafeRestart_SoNoTestCanRestartWindows()
        // Not called here: if the guard were missing, calling Start would restart this computer.
        => Assert.Equal(new Func<bool>(TestEnvironment.NoRestart), SystemRestart.Start);
}
