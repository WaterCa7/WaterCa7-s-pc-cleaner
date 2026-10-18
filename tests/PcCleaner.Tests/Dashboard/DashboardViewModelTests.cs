using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using Xunit;
using static PcCleaner.Tests.Dashboard.FakeContributor;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// Tests that swap the global Dialogs.ConfirmHandler or write settings.json run alone (no parallel test sees the swap).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DashboardSequentialCollection
{
    public const string Name = "Dashboard (sequential)";
}

[Collection(DashboardSequentialCollection.Name)]
public class DashboardScanTests
{
    public DashboardScanTests() => TestEnvironment.Init();

    private static DashboardViewModel CreateVm(AppSettings settings, params IHealthContributor[] contributors)
        => new(contributors, settings)
        {
            CreateRestorePointAsync = (_, _) => throw new InvalidOperationException("restore point must not be created here"),
        };

    [Fact]
    public async Task Scan_CollectsIssuesFromAllModules_AndComputesScore()
    {
        var vm = CreateVm(new AppSettings(),
            new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.Medium, reclaimable: 1000)),
            new FakeContributor("Безопасность", Issue("Безопасность", IssueSeverity.Critical)));

        var outcome = await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.False(outcome.Cancelled);
        Assert.Equal(100 - 6 - 25, outcome.Score);
        Assert.Equal(outcome.Score, vm.Score);
        Assert.Equal(DashboardScanState.Completed, vm.State);
        Assert.Equal(2, vm.CurrentIssues.Count);
        Assert.Equal(new[] { IssueSeverity.Critical, IssueSeverity.Medium }, vm.Groups.Select(g => g.Severity));
        Assert.All(vm.ModuleRows, r => Assert.Equal(ModuleScanState.Done, r.State));
        Assert.Equal(1000, vm.ReclaimableBytes);
        Assert.Equal("Нужно внимание", vm.VerdictText);
    }

    [Fact]
    public async Task Scan_WithNothingFound_ShowsNoProblems()
    {
        var vm = CreateVm(new AppSettings(), new FakeContributor("Очистка"), new FakeContributor("Диски"));
        var outcome = await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal(100, outcome.Score);
        Assert.True(vm.ShowNoProblems);
        Assert.False(vm.HasIssues);
        Assert.Equal("Отлично", vm.VerdictText);
    }

    [Fact]
    public async Task ThrowingContributor_DoesNotBreakTheScan()
    {
        var vm = CreateVm(new AppSettings(),
            new FakeContributor("Синхронный сбой", (Func<CancellationToken, Task<IReadOnlyList<HealthIssue>>>)(_ => throw new InvalidOperationException("boom"))),
            new FakeContributor("Асинхронный сбой", async ct =>
            {
                await Task.Delay(10, ct);
                throw new UnauthorizedAccessException("нет доступа");
            }),
            new FakeContributor("Возвращает null", _ => Task.FromResult<IReadOnlyList<HealthIssue>>(null!)),
            new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.High)));

        var outcome = await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.False(outcome.Cancelled);
        Assert.Equal(88, outcome.Score);
        Assert.Equal(ModuleScanState.Failed, vm.ModuleRows[0].State);
        Assert.Equal("ошибка проверки", vm.ModuleRows[0].StatusText);
        Assert.Equal(ModuleScanState.Failed, vm.ModuleRows[1].State);
        Assert.Equal(ModuleScanState.Done, vm.ModuleRows[2].State);
        Assert.Equal(ModuleScanState.Done, vm.ModuleRows[3].State);
        Assert.Equal("готово: 1 проблема", vm.ModuleRows[3].StatusText);
        Assert.Contains("Синхронный сбой", vm.ScanWarning);
        Assert.Contains("Асинхронный сбой", vm.ScanWarning);
    }

    [Fact]
    public async Task SlowContributor_HitsPerModuleTimeout_EvenIfItIgnoresTheToken()
    {
        var vm = CreateVm(new AppSettings(),
            new FakeContributor("Честный", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<HealthIssue>();
            }),
            new FakeContributor("Глухой", async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                return Array.Empty<HealthIssue>();
            }),
            new FakeContributor("Быстрый", Issue("Быстрый", IssueSeverity.Low)));
        vm.ModuleTimeout = TimeSpan.FromMilliseconds(300);

        var sw = Stopwatch.StartNew();
        var outcome = await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"scan took {sw.Elapsed}");
        Assert.False(outcome.Cancelled);
        Assert.Equal(ModuleScanState.TimedOut, vm.ModuleRows[0].State);
        Assert.Equal(ModuleScanState.TimedOut, vm.ModuleRows[1].State);
        Assert.Equal(ModuleScanState.Done, vm.ModuleRows[2].State);
        Assert.Equal(98, outcome.Score);
    }

    [Fact]
    public async Task Cancellation_StopsTheScan_AndKeepsThePreviousScore()
    {
        var settings = new AppSettings();
        var vm = CreateVm(settings, new FakeContributor("Быстрый", Issue("Быстрый", IssueSeverity.Medium)));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal(94, vm.Score);

        var hanging = new FakeContributor("Долгий", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<HealthIssue>();
        });
        var vm2 = CreateVm(settings, new FakeContributor("Быстрый", Issue("Быстрый", IssueSeverity.Medium)), hanging);
        vm2.ModuleTimeout = TimeSpan.FromMinutes(5);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var outcome = await vm2.ScanCoreAsync(cts.Token, persist: true);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"scan took {sw.Elapsed}");
        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.Score);
        Assert.Equal(DashboardScanState.Cancelled, vm2.State);
        Assert.Equal(ModuleScanState.Cancelled, vm2.ModuleRows[1].State);
        Assert.Null(vm2.Score); // no previous scan for vm2 → still "не проверялся"
        Assert.Null(ScanSummary.Load(settings)); // cancelled scans are never persisted
        Assert.NotNull(vm2.ScanWarning);
    }

    [Fact]
    public async Task AlreadyCancelledToken_DoesNotCallContributors()
    {
        var c = new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.Low));
        var vm = CreateVm(new AppSettings(), c);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = await vm.ScanCoreAsync(cts.Token, persist: false);
        Assert.True(outcome.Cancelled);
        Assert.Equal(0, c.Calls);
    }

    [Fact]
    public async Task PersistedSummary_IsShownAfterRestart()
    {
        var settings = new AppSettings();
        var now = new DateTime(2026, 10, 16, 14, 32, 0);
        var vm = CreateVm(settings,
            new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.High, reclaimable: 2048)));
        vm.Now = () => now;
        await vm.ScanCoreAsync(CancellationToken.None, persist: true);

        var restarted = CreateVm(settings, new FakeContributor("Очистка"));
        restarted.Now = () => now.AddMinutes(5);
        await restarted.OnNavigatedToAsync();
        restarted.OnNavigatedFrom();

        Assert.Equal(88, restarted.Score);
        Assert.Equal("Последняя проверка: сегодня в 14:32", restarted.LastScanText);
        Assert.Equal(2048, restarted.ReclaimableBytes);
        Assert.Equal(1, restarted.RemainingIssueCount);
        Assert.True(restarted.ShowPreviousResult);
        Assert.False(restarted.ShowIntro);
        Assert.Equal("Проверить снова", restarted.ScanButtonText);
    }

    [Fact]
    public async Task PreviewScan_IsNotPersisted()
    {
        var settings = new AppSettings();
        var vm = CreateVm(settings, new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.Low)));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal(98, vm.Score);
        Assert.Null(ScanSummary.Load(settings));
    }

    [Fact]
    public async Task FreshDashboard_ShowsIntro()
    {
        var vm = CreateVm(new AppSettings(), new FakeContributor("Очистка"));
        await vm.OnNavigatedToAsync();
        vm.OnNavigatedFrom();
        Assert.Null(vm.Score);
        Assert.True(vm.ShowIntro);
        Assert.Equal("Компьютер ещё не проверялся", vm.VerdictText);
        Assert.Equal("Проверить компьютер", vm.ScanButtonText);
        Assert.Equal("Проверка ещё не проводилась", vm.LastScanText);
    }
}

[Collection(DashboardSequentialCollection.Name)]
public class DashboardFixTests
{
    public DashboardFixTests() => TestEnvironment.Init();

    private sealed class Counter
    {
        public int Value;
    }

    private static Func<CancellationToken, Task<string>> CountingFix(Counter counter, List<string> order, string name)
        => _ =>
        {
            Interlocked.Increment(ref counter.Value);
            lock (order)
                order.Add(name);
            return Task.FromResult($"{name}: готово.");
        };

    private static async Task<T> WithConfirm<T>(Func<string, string, bool> handler, Func<Task<T>> action)
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

    private static async Task<(DashboardViewModel Vm, List<string> Order, Counter Safe, Counter Unsafe)> ScannedVm()
    {
        var order = new List<string>();
        var safe = new Counter();
        var unsafeCounter = new Counter();
        var contributor = new FakeContributor("Очистка",
            Issue("Очистка", IssueSeverity.Medium, "Временные файлы", reclaimable: 1000,
                fix: CountingFix(safe, order, "temp"), autoFixSafe: true),
            Issue("Очистка", IssueSeverity.Low, "Кэш браузеров", reclaimable: 500,
                fix: CountingFix(safe, order, "cache"), autoFixSafe: true),
            Issue("Очистка", IssueSeverity.High, "Отключить службу", fix: CountingFix(unsafeCounter, order, "service"), inFixAll: false),
            Issue("Очистка", IssueSeverity.Info, "Совет без исправления", autoFixSafe: true));
        var vm = new DashboardViewModel(new IHealthContributor[] { contributor }, new AppSettings());
        vm.CreateRestorePointAsync = (_, _) =>
        {
            lock (order)
                order.Add("restore-point");
            return Task.FromResult(false);
        };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        return (vm, order, safe, unsafeCounter);
    }

    [Fact]
    public async Task FixAll_DoesNothingWithoutConfirmation()
    {
        var (vm, order, safe, unsafeCounter) = await ScannedVm();
        Assert.Equal(2, vm.FixAllCount);
        var asked = 0;

        var result = await WithConfirm((_, _) =>
        {
            asked++;
            return false;
        }, () => vm.FixAllAsync());

        Assert.Null(result);
        Assert.Equal(1, asked);
        Assert.Equal(0, safe.Value);
        Assert.Equal(0, unsafeCounter.Value);
        Assert.Empty(order); // not even a restore point
        Assert.All(vm.CurrentIssues, i => Assert.False(i.IsFixed));
    }

    [Fact]
    public async Task FixAll_RunsOnlyInFixAllIssues_AfterOneConfirmation()
    {
        var (vm, order, safe, unsafeCounter) = await ScannedVm();
        var scoreBefore = vm.Score;
        var asked = 0;
        string? message = null;

        var result = await WithConfirm((_, m) =>
        {
            asked++;
            message = m;
            return true;
        }, () => vm.FixAllAsync());

        Assert.NotNull(result);
        Assert.Equal(1, asked);
        Assert.Contains("Временные файлы", message);
        Assert.Contains("Кэш браузеров", message);
        Assert.DoesNotContain("Отключить службу", message);
        Assert.Equal(2, safe.Value);
        Assert.Equal(0, unsafeCounter.Value);
        Assert.Equal(new[] { "restore-point", "temp", "cache" }, order);
        Assert.Equal(2, result!.Fixed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1500, result.FreedBytes);
        Assert.Equal(2, vm.FixedCount);
        Assert.True(vm.ShowRescanHint);
        Assert.Equal(0, vm.FixAllCount);
        Assert.True(vm.Score > scoreBefore);
        Assert.Equal(0, vm.ReclaimableBytes);
        Assert.NotNull(vm.ActionSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task FixAll_WithNothingInFixAll_DoesNotAsk()
    {
        var vm = new DashboardViewModel(new IHealthContributor[]
        {
            new FakeContributor("Ускорение", Issue("Ускорение", IssueSeverity.Medium, fix: _ => Task.FromResult("ok"), inFixAll: false)),
        }, new AppSettings()) { CreateRestorePointAsync = (_, _) => throw new InvalidOperationException() };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var asked = 0;
        var result = await WithConfirm((_, _) => ++asked > 0, () => vm.FixAllAsync());
        Assert.Null(result);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task FixIssue_AsksFirst_ThenFixesAndUpdatesScore()
    {
        var (vm, _, _, unsafeCounter) = await ScannedVm();
        var item = vm.Groups.SelectMany(g => g.Items).Single(i => i.Issue.Title == "Отключить службу");
        var before = vm.Score;

        await WithConfirm<bool>((_, _) => false, async () =>
        {
            await vm.FixIssueAsync(item);
            return true;
        });
        Assert.Equal(0, unsafeCounter.Value);
        Assert.False(item.Issue.IsFixed);

        string? message = null;
        await WithConfirm<bool>((_, m) =>
        {
            message = m;
            return true;
        }, async () =>
        {
            await vm.FixIssueAsync(item);
            return true;
        });

        Assert.Contains("Отключить службу", message);
        Assert.Equal(1, unsafeCounter.Value);
        Assert.True(item.Issue.IsFixed);
        Assert.Equal("service: готово.", item.Issue.FixResult);
        Assert.False(item.FixFailed);
        Assert.Equal(before + 12, vm.Score);
        Assert.Equal(1, vm.FixedCount);
    }

    [Fact]
    public async Task FixThatNeedsAdmin_IsNotMarkedFixed()
    {
        var issue = Issue("Ускорение", IssueSeverity.Low, "Игровой режим",
            fix: _ => Task.FromResult("Нужны права администратора — ничего не изменено."), autoFixSafe: true);
        var vm = new DashboardViewModel(new IHealthContributor[] { new FakeContributor("Ускорение", issue) }, new AppSettings());
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        var ok = await vm.FixOneCoreAsync(item, CancellationToken.None);

        Assert.False(ok);
        Assert.False(issue.IsFixed);
        Assert.True(issue.CanFix);
        Assert.True(item.FixFailed);
        Assert.Contains("администратора", issue.FixResult);
    }

    [Fact]
    public async Task FixThatThrows_ShowsTheErrorOnTheRow()
    {
        var issue = Issue("Очистка", IssueSeverity.Medium, "Кэш",
            fix: _ => throw new IOException("файл занят другой программой"));
        var vm = new DashboardViewModel(new IHealthContributor[] { new FakeContributor("Очистка", issue) }, new AppSettings());
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        var ok = await vm.FixOneCoreAsync(item, CancellationToken.None);

        Assert.False(ok);
        Assert.False(issue.IsFixed);
        Assert.True(item.FixFailed);
        Assert.False(item.IsFixing);
        Assert.Contains("файл занят другой программой", issue.FixResult);
    }

    [Fact]
    public async Task LongGroups_ShowFirstRows_ButFixAllCoversAllOfThem()
    {
        var fixes = new Counter();
        var issues = Enumerable.Range(0, 11)
            .Select(i => Issue("Очистка", IssueSeverity.Low, $"Кэш {i}",
                fix: _ =>
                {
                    Interlocked.Increment(ref fixes.Value);
                    return Task.FromResult("Удалено.");
                }, autoFixSafe: true))
            .ToArray();
        var vm = new DashboardViewModel(new IHealthContributor[] { new FakeContributor("Очистка", issues) }, new AppSettings())
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
        };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var group = vm.Groups.Single();
        Assert.Equal(8, group.Items.Count);
        Assert.Equal(3, group.HiddenCount);
        Assert.Equal("Показать ещё 3", group.ShowAllText);
        Assert.Equal(11, vm.FixAllCount);

        var result = await WithConfirm((_, _) => true, () => vm.FixAllAsync());
        Assert.Equal(11, result!.Fixed);
        Assert.Equal(11, fixes.Value);
        Assert.Equal("0 из 11", group.CountText);

        group.ShowAll();
        Assert.Equal(11, group.Items.Count);
        Assert.False(group.HasHidden);
    }

    [Fact]
    public void BatchSummary_IsReadable()
    {
        var text = DashboardViewModel.BatchSummaryText(new FixBatchResult(3, 2, 1, false, 1024 * 1024));
        Assert.Contains("Исправлено 2 из 3", text);
        Assert.Contains("1 МБ", text);
        Assert.Contains("Не получилось: 1", text);
        Assert.Contains("Проверить снова", text);
    }
}
