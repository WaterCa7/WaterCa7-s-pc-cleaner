using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using Xunit;
using static PcCleaner.Tests.Dashboard.FakeContributor;

namespace PcCleaner.Tests.Dashboard;

/// <summary>Regression tests for defects found in the dashboard review.</summary>
public class DashboardScoringRegressionTests
{
    [Theory]
    [InlineData("Недостаточно прав для изменения службы.", true)]
    [InlineData("Запустите программу от имени администратора — ничего не сделано.", true)]
    [InlineData("Требуются права администратора.", true)]
    [InlineData("Нет прав на запись в HKLM.", true)]
    [InlineData("Ничего не удалено: файлы заняты.", true)]
    [InlineData("Исправление отменено.", true)]
    [InlineData("Отменено пользователем.", true)]
    // Successful results that merely contain "отменено"/"нет прав…" words must still count as fixed.
    [InlineData("Автообновление драйверов отменено.", false)]
    [InlineData("Нет правил, блокирующих обновления, — всё в порядке.", false)]
    [InlineData("Удалено 1,2 ГБ временных файлов.", false)]
    public void LooksLikeFailure_HandlesCommonPhrasings(string message, bool expected)
        => Assert.Equal(expected, HealthScoring.LooksLikeFailure(message));

    [Theory]
    [InlineData("Нужны права администратора. Ничего не изменено.", true)]
    [InlineData("Недостаточно прав.", true)]
    [InlineData("Запустите от имени администратора", true)]
    [InlineData("Нет правил для удаления", false)]
    [InlineData("Файл занят другой программой", false)]
    [InlineData(null, false)]
    public void LooksLikeAdminRequired_Works(string? message, bool expected)
        => Assert.Equal(expected, HealthScoring.LooksLikeAdminRequired(message));

    [Theory]
    [InlineData(0, 0, "проблем нет")]
    [InlineData(2, 0, "2 проблемы")]
    [InlineData(0, 1, "1 совет")]
    [InlineData(5, 3, "5 проблем и 3 совета")]
    [InlineData(1, 5, "1 проблема и 5 советов")]
    public void FindingsText_SeparatesTipsFromProblems(int problems, int tips, string expected)
        => Assert.Equal(expected, HealthScoring.FindingsText(problems, tips));

    [Fact]
    public void TotalReclaimable_SaturatesInsteadOfThrowing()
    {
        var issues = new[]
        {
            Issue("A", IssueSeverity.Low, reclaimable: long.MaxValue),
            Issue("B", IssueSeverity.Low, reclaimable: long.MaxValue),
        };
        Assert.Equal(long.MaxValue, HealthScoring.TotalReclaimable(issues));
    }

    [Fact]
    public void VerdictDescription_AfterFixingEverything_DoesNotClaimNothingWasFound()
    {
        Assert.Contains("исправлены", HealthScoring.VerdictDescription(100, 0, fixedSome: true));
        Assert.Contains("Проблем не найдено", HealthScoring.VerdictDescription(100, 0, fixedSome: false));
    }

    [Fact]
    public void ShortenForDialog_CutsVeryLongTexts()
    {
        var longText = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Строка {i}"));
        var result = DashboardViewModel.ShortenForDialog(longText);
        Assert.Contains("Строка 1", result);
        Assert.DoesNotContain("Строка 60", result);
        Assert.Contains("полное описание", result);
        Assert.Equal("Короткий текст", DashboardViewModel.ShortenForDialog("  Короткий текст  "));
    }
}

[Collection(DashboardSequentialCollection.Name)]
public class DashboardViewModelRegressionTests
{
    public DashboardViewModelRegressionTests() => TestEnvironment.Init();

    private static T WithConfirm<T>(Func<string, string, bool> handler, Func<T> action)
    {
        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = handler;
        try
        {
            return action();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
    }

    private static async Task<T> WithConfirmAsync<T>(Func<string, string, bool> handler, Func<Task<T>> action)
    {
        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = handler;
        try
        {
            return await action();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
    }

    private static DashboardViewModel Vm(AppSettings settings, params IHealthContributor[] contributors)
        => new(contributors, settings) { CreateRestorePointAsync = (_, _) => Task.FromResult(false) };

    [Fact]
    public async Task ScanWithAbsurdReclaimableSizes_CompletesInsteadOfGettingStuck()
    {
        var vm = Vm(new AppSettings(),
            new FakeContributor("A", Issue("A", IssueSeverity.Low, reclaimable: long.MaxValue)),
            new FakeContributor("B", Issue("B", IssueSeverity.Low, reclaimable: long.MaxValue)));

        var outcome = await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.False(outcome.Cancelled);
        Assert.Equal(DashboardScanState.Completed, vm.State);
        Assert.False(vm.IsScanning);
        Assert.Equal(long.MaxValue, vm.ReclaimableBytes);
    }

    [Fact]
    public async Task Tips_AreNotCalledProblems_AndSurviveRestart()
    {
        var settings = new AppSettings();
        var vm = Vm(settings, new FakeContributor("Ускорение",
            Issue("Ускорение", IssueSeverity.Medium, "Проблема"),
            Issue("Ускорение", IssueSeverity.Info, "Совет 1"),
            Issue("Ускорение", IssueSeverity.Info, "Совет 2")));

        await vm.ScanCoreAsync(CancellationToken.None, persist: true);

        Assert.Equal("Найдено: 1 проблема и 2 совета", vm.IssueCountText);
        Assert.Equal("готово: 1 проблема и 2 совета", vm.ModuleRows.Single().StatusText);
        Assert.StartsWith("Найдено 1 проблема и 2 совета", vm.ResultsSummaryText);

        var restarted = Vm(settings, new FakeContributor("Ускорение"));
        await restarted.OnNavigatedToAsync();
        restarted.OnNavigatedFrom();
        Assert.Equal("Найдено: 1 проблема и 2 совета", restarted.IssueCountText);
        Assert.Contains("1 проблема и 2 совета", restarted.PreviousResultText);
    }

    [Fact]
    public async Task OnlyTips_ScoreIsPerfect_AndTextsSayNoProblems()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Безопасность",
            Issue("Безопасность", IssueSeverity.Info, "Совет 1"),
            Issue("Безопасность", IssueSeverity.Info, "Совет 2")));

        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.Equal(100, vm.Score);
        Assert.Equal("Проблем нет · 2 совета", vm.IssueCountText);
        Assert.Equal("Проблем не найдено. Компьютер в отличной форме!", vm.VerdictDescription);
    }

    [Fact]
    public async Task FixingEverything_UpdatesChipsAndTexts()
    {
        var vm = Vm(new AppSettings(), new FakeContributor("Очистка",
            Issue("Очистка", IssueSeverity.High, "Важная", fix: _ => Task.FromResult("Готово.")),
            Issue("Очистка", IssueSeverity.Low, "Мелкая", fix: _ => Task.FromResult("Готово."))));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal(new[] { IssueSeverity.High, IssueSeverity.Low }, vm.SeverityChips.Select(c => c.Severity));

        var important = vm.Groups.SelectMany(g => g.Items).Single(i => i.Issue.Title == "Важная");
        await WithConfirmAsync((_, _) => true, async () =>
        {
            await vm.FixIssueAsync(important);
            return true;
        });
        Assert.Equal(new[] { IssueSeverity.Low }, vm.SeverityChips.Select(c => c.Severity));
        Assert.Equal(1, vm.SeverityChips.Single().Count);

        var minor = vm.Groups.SelectMany(g => g.Items).Single(i => i.Issue.Title == "Мелкая");
        await WithConfirmAsync((_, _) => true, async () =>
        {
            await vm.FixIssueAsync(minor);
            return true;
        });

        Assert.Empty(vm.SeverityChips);
        Assert.Equal("Все проблемы исправлены", vm.IssueCountText);
        Assert.Contains("исправлены", vm.VerdictDescription);
        Assert.Equal(100, vm.Score);
    }

    [Fact]
    public async Task FixSafeConfirmation_NamesEveryItem_EvenForLongLists()
    {
        var issues = Enumerable.Range(1, 25)
            .Select(i => Issue(i <= 15 ? "Очистка" : "Ускорение", IssueSeverity.Low, $"Пункт-{i:00}",
                fix: _ => Task.FromResult("Готово."), autoFixSafe: true))
            .ToArray();
        var vm = Vm(new AppSettings(), new FakeContributor("Смешанный", issues));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        string? message = null;
        var result = await WithConfirmAsync((_, m) =>
        {
            message = m;
            return false;
        }, () => vm.FixSafeAsync());

        Assert.Null(result);
        Assert.NotNull(message);
        for (var i = 1; i <= 25; i++)
            Assert.Contains($"Пункт-{i:00}", message);
        Assert.Contains("Очистка (15)", message);
        Assert.Contains("Ускорение (10)", message);
        Assert.All(vm.CurrentIssues, i => Assert.False(i.IsFixed));
    }

    [Fact]
    public async Task FixSafe_CancelledWhileCreatingRestorePoint_FixesNothing_AndSaysSo()
    {
        var fixes = 0;
        var vm = new DashboardViewModel(new IHealthContributor[]
        {
            new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.Low, "Кэш",
                fix: _ =>
                {
                    Interlocked.Increment(ref fixes);
                    return Task.FromResult("Удалено.");
                }, autoFixSafe: true)),
        }, new AppSettings());
        vm.CreateRestorePointAsync = (_, ct) =>
        {
            vm.CancelCommand.Execute(null); // the user presses "Отмена" in the busy strip
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        var result = await WithConfirmAsync((_, _) => true, () => vm.FixSafeAsync());

        Assert.NotNull(result);
        Assert.True(result!.Cancelled);
        Assert.Equal(0, result.Fixed);
        Assert.Equal(0, fixes);
        Assert.Equal("Исправление отменено — ничего не изменено.", vm.ActionSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SingleFixConfirmation_DescribesTheProblemAndTheResult()
    {
        var issue = Issue("Очистка", IssueSeverity.Medium, "Временные файлы занимают 2 ГБ", reclaimable: 2L << 30,
            fix: _ => Task.FromResult("Удалено."));
        var vm = Vm(new AppSettings(), new FakeContributor("Очистка", issue));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        var text = DashboardViewModel.FixConfirmText(item);

        Assert.Contains("Проблема: Временные файлы занимают 2 ГБ", text);
        Assert.Contains("Описание проблемы", text);
        Assert.Contains("Что изменится: Освободит 2 ГБ", text);
        Assert.Contains("«Очистка»", text);
    }

    [Fact]
    public async Task FixThrowingAccessDenied_OffersAdminRestart_ForThatFix()
    {
        var issue = Issue("Ускорение", IssueSeverity.Low, "Игровой режим",
            fix: _ => throw new UnauthorizedAccessException("Отказано в доступе"));
        var vm = Vm(new AppSettings(), new FakeContributor("Ускорение", issue));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        var ok = await vm.FixOneCoreAsync(item, CancellationToken.None);

        Assert.False(ok);
        Assert.True(item.FixFailed);
        Assert.Equal(!Admin.IsElevated, item.NeedsAdmin);

        if (Admin.IsElevated)
        {
            Assert.False(item.RequestAdminCommand!.CanExecute(null));
            return;
        }
        string? message = null;
        WithConfirm((_, m) =>
        {
            message = m;
            return false; // declined: nothing restarts
        }, () =>
        {
            item.RequestAdminCommand!.Execute(null);
            return true;
        });
        Assert.Contains("Игровой режим", message);
        Assert.DoesNotContain("полная проверка", message);
    }

    [Fact]
    public async Task AdminRestart_IsNotOfferedWhileFixesAreRunning()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Vm(new AppSettings(), new FakeContributor("Очистка",
            Issue("Очистка", IssueSeverity.Low, "Кэш", fix: _ => release.Task, autoFixSafe: true)));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = (_, _) => true;
        Task<FixBatchResult?> batch;
        try
        {
            batch = vm.FixSafeAsync();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }

        var waited = 0;
        while (!item.IsFixing && waited++ < 200)
            await Task.Delay(10);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RequestAdminCommand.CanExecute(null));
        Assert.False(item.RequestAdminCommand!.CanExecute(null));

        release.SetResult("Удалено.");
        var result = await batch;
        Assert.Equal(1, result!.Fixed);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OldScanNudge_IsHiddenWhileANewScanRuns()
    {
        var settings = new AppSettings();
        var now = new DateTime(2026, 10, 16, 12, 0, 0);
        new ScanSummary { Score = 90, Time = now.AddDays(-10), IssueCount = 1 }.Save(settings);

        var gate = new TaskCompletionSource<IReadOnlyList<HealthIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Vm(settings, new FakeContributor("Очистка", _ => gate.Task));
        vm.Now = () => now;
        await vm.OnNavigatedToAsync();
        vm.OnNavigatedFrom();
        Assert.True(vm.IsScanOld);

        var scan = vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.True(vm.IsScanning);
        Assert.False(vm.IsScanOld);

        gate.SetResult(Array.Empty<HealthIssue>());
        await scan;
        Assert.False(vm.IsScanOld);
        Assert.Equal(100, vm.Score);
    }
}
