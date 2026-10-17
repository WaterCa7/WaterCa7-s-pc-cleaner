using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Programs;
using Xunit;

namespace PcCleaner.Tests.Programs;

/// <summary>The dashboard's text for "Битые записи в списке программ": partial results must read as "not fixed".</summary>
public class ProgramsRound3Tests
{
    [Fact]
    public void AllEntriesDeleted_IsAFix()
    {
        var text = ProgramsService.BrokenEntriesFixText(targets: 3, deleted: 3, failures: Array.Empty<string>());
        Assert.Equal("Удалено 3 записи, резервные копии сохранены.", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public void FailedEntries_AreAFailure()
    {
        var text = ProgramsService.BrokenEntriesFixText(3, 2, new[] { "Old App: Не удалось удалить запись: файл занят другой программой." });
        Assert.Equal("Удалено 2 записи, резервные копии сохранены. Не удалось: 1.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public void CancelledPartWay_SaysSo_AndIsAFailure()
    {
        var text = ProgramsService.BrokenEntriesFixText(5, 2, Array.Empty<string>());
        Assert.Equal("Удалено 2 записи, резервные копии сохранены. Удаление прервано — не обработано: 3 записи.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public void NothingDeleted_IsAFailure_WithTheFirstReason()
    {
        var none = ProgramsService.BrokenEntriesFixText(1, 0, new[] { "Old App: Не удалось сохранить резервную копию — запись не удалена." });
        Assert.Equal("Ничего не удалено. Old App: Не удалось сохранить резервную копию — запись не удалена.", none);
        Assert.True(HealthScoring.LooksLikeFailure(none));

        var cancelled = ProgramsService.BrokenEntriesFixText(1, 0, Array.Empty<string>());
        Assert.Equal("Ничего не удалено. Удаление прервано — не обработано: 1 запись.", cancelled);
        Assert.True(HealthScoring.LooksLikeFailure(cancelled));
    }
}
