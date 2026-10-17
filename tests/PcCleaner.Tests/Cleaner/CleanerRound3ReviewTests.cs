using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using Xunit;

namespace PcCleaner.Tests.Cleaner;

/// <summary>
/// Review of round 3: a Recycle Bin that cannot be read any more is never emptied (nor called "already empty"), and the
/// tooltips of the Cleaner page say the right thing in the less common states.
/// No global Dialogs handler is replaced: the questions go through the injectable Confirm / ConfirmAgain delegates.
/// </summary>
public class CleanerRound3ReviewTests
{
    private const long Mb = 1024L * 1024;

    [Fact]
    public async Task DashboardBinFix_BinCannotBeReadAtFixTime_EmptiesNothing_AndStaysOpen()
    {
        using var t = new CleanerTestFolder("r3rv-dash-unreadable");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 5 * Mb;
        bin.Items = 3;
        var issue = Assert.Single(await service.AnalyzeAsync(CancellationToken.None), i => i.Title.StartsWith("В корзине"));
        service.ConfirmAgain = (_, _) => throw new InvalidOperationException("nothing to ask about");

        bin.QueryFails = true;
        var message = await issue.FixAsync!(CancellationToken.None);

        Assert.Equal(0, bin.EmptyCalls);
        Assert.Equal("Не удалось очистить корзину: Windows не сообщила, что в ней сейчас лежит, поэтому ничего не удалено.",
            message);
        Assert.True(HealthScoring.LooksLikeFailure(message));
    }

    [Fact]
    public async Task CleanerPage_BinCannotBeReadAnyMore_IsLeftOutOfTheConfirmation_AndNotEmptied()
    {
        using var t = new CleanerTestFolder("r3rv-vm-unreadable");
        var service = CleanerServiceTests.CreateService(t, out var env, out var bin);
        Directory.CreateDirectory(env.Temp);
        var junk = Path.Combine(env.Temp, "old.tmp");
        File.WriteAllBytes(junk, new byte[3 * 1024 * 1024]);
        CleanerTestFolder.SetAge(junk, TimeSpan.FromDays(4));
        bin.Bytes = 4096;
        bin.Items = 1;
        var vm = new CleanerViewModel(service);
        await vm.PreviewScanAsync(CancellationToken.None);
        vm.UnselectAllCommand.Execute(null);
        var items = vm.Groups.SelectMany(g => g.Items).ToList();
        items.Single(i => i.IsRecycleBin).IsChecked = true;
        items.Single(i => i.Id == "win.usertemp").IsChecked = true;
        Assert.Equal(2, vm.SelectedCount);
        string? confirmation = null;
        vm.Confirm = (_, text) =>
        {
            confirmation = text;
            return false;
        };
        service.ConfirmAgain = (_, _) => throw new InvalidOperationException("the bin is not part of the cleaning");

        bin.QueryFails = true;
        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        Assert.NotNull(confirmation);
        Assert.Contains("Временные файлы пользователя", confirmation);
        Assert.DoesNotContain("Корзина —", confirmation);
        Assert.DoesNotContain("Корзина будет очищена", confirmation);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.True(File.Exists(junk)); // declined
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task CleanerPage_OnlyTheBinSelected_AndItCannotBeReadAnyMore_NothingIsConfirmedOrEmptied()
    {
        using var t = new CleanerTestFolder("r3rv-vm-unreadable-only");
        var service = CleanerServiceTests.CreateService(t, out _, out var bin);
        bin.Bytes = 4096;
        bin.Items = 1;
        var vm = new CleanerViewModel(service);
        await vm.PreviewScanAsync(CancellationToken.None);
        vm.UnselectAllCommand.Execute(null);
        var binItem = vm.Groups.SelectMany(g => g.Items).Single(i => i.IsRecycleBin);
        binItem.IsChecked = true;
        Assert.Equal(1, vm.SelectedCount);
        var asked = false;
        vm.Confirm = (_, _) => asked = true;

        bin.QueryFails = true;
        await ((AsyncRelayCommand)vm.CleanCommand).ExecuteAsync();

        Assert.False(asked);
        Assert.Equal(0, bin.EmptyCalls);
        Assert.Equal(0, vm.SelectedCount);
        Assert.NotNull(binItem.Result?.Error); // the row says why instead of claiming the bin is empty
        Assert.False(binItem.IsEmpty);
    }

    [Theory]
    [InlineData(2, false, 0L, "Удалить файлы в отмеченных категориях")] // rows sized by a stopped analysis
    [InlineData(2, true, 50 * Mb, "Удалить файлы в отмеченных категориях")]
    [InlineData(0, false, 0L, "Сначала выполните анализ")]
    [InlineData(0, true, 100L, "Удалять нечего")]
    [InlineData(0, true, 50 * Mb, "Отметьте в списке ниже")]
    public void CleanTip_MatchesWhetherTheButtonIsEnabled(int selected, bool scanned, long total, string expectedStart)
        => Assert.StartsWith(expectedStart, CleanerViewModel.IdleCleanTip(selected, scanned, total));

    [Fact]
    public void BusyTip_WhileDismRuns_DoesNotSuggestCancelling()
    {
        Assert.Equal(CleanerViewModel.DismBusyTip, CleanerViewModel.BusyTipFor(CleanerViewModel.DismRunning));
        Assert.DoesNotContain("Отмена", CleanerViewModel.DismBusyTip);
        Assert.Equal(CleanerViewModel.BusyTip, CleanerViewModel.BusyTipFor(null));
        Assert.Equal(CleanerViewModel.BusyTip, CleanerViewModel.BusyTipFor("Очистка компонентов завершена."));
    }
}
