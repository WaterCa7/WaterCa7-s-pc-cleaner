using System.ComponentModel;
using PcCleaner.Core;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Security;
using PcCleaner.Modules.Startup;
using Xunit;
using static PcCleaner.Tests.Dashboard.FakeContributor;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// Regression tests for the final review of the dashboard: fixes that failed or were stopped must never be shown as
/// "Исправлено", and a scan with failed modules must never be called "Отлично" or become the next baseline.
/// </summary>
public class DashboardFixOutcomeTextTests
{
    // Real failure texts of the fixes offered on the dashboard that do not start with "Не удалось".
    [Theory]
    [InlineData("Команда выполнена, но брандмауэр всё ещё выключен для: частной сети. Вероятно, его отключает другая программа.")]
    [InlineData("Команда выполнена, но брандмауэр всё ещё выключен для: общественной сети. Его отключает групповая политика — её меняет администратор компьютера (или удалите её, если компьютер не рабочий).")]
    [InlineData("Защиту не удалось включить: она отключена групповой политикой. Если компьютер не рабочий, политику оставил «активатор» или вирус.")]
    [InlineData("Windows не дала включить защиту (возможно, мешает «Защита от подделки» или другой антивирус). Включите её вручную: «Безопасность Windows» → «Защита от вирусов и угроз» → «Управление настройками».")]
    [InlineData("Раздел реестра с настройками UAC не найден.")]
    [InlineData("Microsoft Defender не найден на этом компьютере.")]
    [InlineData("Недоступно в тестовом окружении.")]
    [InlineData("Обновление недоступно в тестовом окружении.")]
    [InlineData("Эту рекомендацию нельзя применить автоматически.")]
    [InlineData("Освобождено 10 МБ, удалено 5 файлов (очистка прервана).")]
    [InlineData("Удалено 2 записи, резервные копии сохранены. Не удалось: 3.")]
    public void FailureTexts_AreNotTreatedAsFixed(string message)
        => Assert.True(HealthScoring.LooksLikeFailure(message), message);

    // Real success texts must still count as fixed.
    [Theory]
    [InlineData("Брандмауэр включён для всех типов сетей. Выключить его снова можно в «Безопасность Windows» → «Брандмауэр и защита сети».")]
    [InlineData("Защита в реальном времени включена.")]
    [InlineData("UAC включён. Перезагрузите компьютер, чтобы изменение вступило в силу.\n\nРезервная копия реестра: C:\\backup.reg")]
    [InlineData("Антивирусные базы Microsoft Defender обновлены.")]
    [InlineData("Удалено 2 записи, резервные копии сохранены.")]
    [InlineData("Битых записей больше нет.")]
    [InlineData("Нечего очищать: подходящие файлы уже удалены.")]
    [InlineData("Удалено 300 МБ. Не удалось удалить 3 занятых файла.")]
    [InlineData("«Xbox Game Bar»: готово. Скриншоты и запись клипов через Xbox Game Bar будут недоступны, пока вы не вернёте настройку.")]
    [InlineData("TRIM включён. Windows снова будет сообщать SSD об освободившихся блоках.")]
    [InlineData("Включён план электропитания «Высокая производительность».")]
    public void SuccessTexts_StillCountAsFixed(string message)
        => Assert.False(HealthScoring.LooksLikeFailure(message), message);

    [Fact]
    public void SecurityTexts_FromTheModuleItself_AreClassifiedCorrectly()
    {
        Assert.True(HealthScoring.LooksLikeFailure(SecurityService.NeedAdminMessage));
        // A successful SMBv1 fix mentions a restore point that could not be created: still fixed.
        var smb1 = "SMBv1 отключён. Перезагрузите компьютер, чтобы изменение вступило в силу.\n\n" +
                   SecurityRules.RestorePointText(RestorePointResult.Failed);
        Assert.False(HealthScoring.LooksLikeFailure(smb1));
    }

    [Fact]
    public void CleanerTexts_FromTheModuleItself_AreClassifiedCorrectly()
    {
        var stopped = CleanerService.Summarize(new[]
        {
            new CategoryCleanResult { CategoryId = "temp", Name = "Временные файлы", FreedBytes = 10 << 20, DeletedFiles = 5, Cancelled = true },
        });
        Assert.True(HealthScoring.LooksLikeFailure(stopped), stopped);

        var done = CleanerService.Summarize(new[]
        {
            new CategoryCleanResult { CategoryId = "temp", Name = "Временные файлы", FreedBytes = 10 << 20, DeletedFiles = 5, LockedFiles = 3 },
        });
        Assert.False(HealthScoring.LooksLikeFailure(done), done);
    }

    [Fact]
    public void StartupBulkTexts_FromTheModuleItself_AreClassifiedCorrectly()
    {
        TestEnvironment.Init();
        static StartupItem Entry(string name) => new()
        {
            Id = "RegistryRun|" + name,
            Kind = StartupKind.RegistryRun,
            Scope = StartupScope.CurrentUser,
            Name = name,
        };

        var all = new StartupBulkResult(new[] { Entry("A"), Entry("B") }, Array.Empty<(StartupItem, string)>());
        Assert.False(HealthScoring.LooksLikeFailure(all.Summary), all.Summary);

        var partly = new StartupBulkResult(new[] { Entry("A") }, new[] { (Entry("B"), "файл занят") });
        Assert.True(HealthScoring.LooksLikeFailure(partly.Summary), partly.Summary);
    }
}

public class DashboardIncompleteScoringTests
{
    [Theory]
    [InlineData(100, 0, "Отлично")]
    [InlineData(100, 1, "Проверка неполная")]
    [InlineData(80, 3, "Проверка неполная")]
    [InlineData(50, 1, "Проверка неполная")]
    [InlineData(49, 1, "Плохо")] // the real score can only be lower: "Плохо" is still true
    [InlineData(30, 0, "Плохо")]
    [InlineData(null, 2, "Компьютер ещё не проверялся")]
    public void Verdict_OfAnIncompleteScan_IsNeverExcellent(int? score, int failed, string expected)
        => Assert.Equal(expected, HealthScoring.Verdict(score, failed));

    [Theory]
    [InlineData(100, 0, "SuccessBrush")]
    [InlineData(100, 1, "WarningBrush")]
    [InlineData(60, 1, "WarningBrush")]
    [InlineData(35, 1, "DangerBrush")] // the real score can only be lower: red stays red
    [InlineData(null, 1, "MutedBrush")]
    public void ScoreBrush_OfAnIncompleteScan_IsNeverGreen(int? score, int failed, string expected)
        => Assert.Equal(expected, HealthScoring.ScoreBrushKey(score, failed));

    [Fact]
    public void VerdictDescription_OfAnIncompleteScan_NamesTheGap()
    {
        var none = HealthScoring.VerdictDescription(100, 0, fixedSome: false, failedModules: 2);
        Assert.DoesNotContain("отличной форме", none);
        Assert.Contains("2 раздела", none);
        Assert.Contains("завышена", none);

        Assert.Contains("1 раздел", HealthScoring.VerdictDescription(88, 1, fixedSome: false, failedModules: 1));
        Assert.Contains("Проверить снова", HealthScoring.VerdictDescription(100, 0, fixedSome: true, failedModules: 1));
        // complete scans keep the old texts
        Assert.Equal("Проблем не найдено. Компьютер в отличной форме!", HealthScoring.VerdictDescription(100, 0, false, 0));
    }

    [Fact]
    public void IncompleteSummary_HasNoDelta_AndPassesOnItsInheritedBaseline()
    {
        var t0 = new DateTime(2026, 10, 10, 9, 0, 0);
        var incomplete = new ScanSummary
        {
            Score = 100, MeasuredScore = 100, Time = t0.AddDays(2), FailedModules = 1, PreviousScore = 46, PreviousTime = t0,
        };
        Assert.True(incomplete.IsIncomplete);
        Assert.Null(incomplete.Delta);
        Assert.Equal((46, (DateTime?)t0), incomplete.NextBaseline);

        Assert.Null((incomplete with { PreviousScore = null, PreviousTime = null }).NextBaseline);

        var complete = incomplete with { FailedModules = 0, MeasuredScore = 94, Score = 100 };
        Assert.NotNull(complete.Delta);
        Assert.Equal((94, (DateTime?)t0.AddDays(2)), complete.NextBaseline);
    }

    [Fact]
    public void Report_OfAnIncompleteScan_IsNeverExcellent()
    {
        var report = new DashboardReport
        {
            CreatedAt = new DateTime(2026, 10, 16, 14, 40, 0),
            ScanTime = new DateTime(2026, 10, 16, 14, 32, 0),
            Score = 100,
            Modules = new[]
            {
                new ReportModule("Очистка", ModuleScanState.Done, "готово: проблем нет", null),
                new ReportModule("Безопасность", ModuleScanState.TimedOut, "превышено время ожидания", "Проверка не уложилась в 60 с"),
            },
            Warning = "Не удалось проверить: Безопасность.",
            FailedModules = 1,
        };

        var html = ReportBuilder.BuildHtml(report);
        Assert.Contains("Проверка неполная", html);
        Assert.Contains("verdict score-warn", html);
        Assert.DoesNotContain("Отлично", html);
        Assert.DoesNotContain("отличной форме", html);
        Assert.Contains("В проверенных разделах проблем не найдено", html);

        var text = ReportBuilder.BuildText(report);
        Assert.Contains("100 из 100 — Проверка неполная", text);
        Assert.DoesNotContain("отличной форме", text);
        Assert.DoesNotContain("  Проблем не найдено.", text);
    }
}

[Collection(DashboardSequentialCollection.Name)]
public class DashboardFinalReviewTests
{
    public DashboardFinalReviewTests() => TestEnvironment.Init();

    private const string FirewallStillOff =
        "Команда выполнена, но брандмауэр всё ещё выключен для: частной сети. Вероятно, его отключает другая программа.";

    private static DashboardViewModel Vm(AppSettings settings, params IHealthContributor[] contributors)
        => new(contributors, settings)
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            ReadStaticInfo = () => new StaticSystemInfo("Windows 11 Pro", "24H2", "TEST-PC", "Test CPU", 8, 16UL << 30),
        };

    private static async Task<T> WithConfirm<T>(Func<Task<T>> action)
    {
        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = (_, _) => true;
        try
        {
            return await action();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
    }

    private static FakeContributor Failing(string name)
        => new(name, (Func<CancellationToken, Task<IReadOnlyList<HealthIssue>>>)(_ => throw new Win32Exception(5)));

    // ---------------------------------------------------------------- failed fixes

    [Fact]
    public async Task SecurityFixThatLeavesTheFirewallOff_IsNotMarkedFixed()
    {
        var issue = Issue("Безопасность", IssueSeverity.High, "Брандмауэр Windows выключен",
            fix: _ => Task.FromResult(FirewallStillOff));
        var vm = Vm(new AppSettings(), new FakeContributor("Безопасность", issue));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();
        Assert.Equal(88, vm.Score);

        await WithConfirm(async () =>
        {
            await vm.FixIssueAsync(item);
            return true;
        });

        Assert.False(issue.IsFixed);
        Assert.True(issue.CanFix);
        Assert.True(item.FixFailed);
        Assert.Equal(FirewallStillOff, issue.FixResult);
        Assert.Equal(88, vm.Score);
        Assert.Equal(0, vm.FixedCount);
        Assert.StartsWith("Не исправлено", vm.ActionSummary);
    }

    [Fact]
    public async Task RealSecurityFixes_ThatReportFailure_AreNeverMarkedFixed()
    {
        // Sandbox: every Security action returns SecurityActionResult.Fail before touching the system. The dashboard
        // only sees the message (Wrap drops Success), so this checks the module's real failure texts end to end.
        using var sb = new PcCleaner.Tests.Security.SecuritySandbox();
        var checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();
        checks[checks.FindIndex(c => c.Id == SecurityCheckIds.Firewall)] =
            new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Danger, "Выключен для частной сети", "d");
        var report = new SecurityReport
        {
            Defender = new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = false, SignatureAgeDays = 0 },
            FirewallProfiles = new[]
            {
                new FirewallProfileState("Private", false), new FirewallProfileState("Public", true), new FirewallProfileState("Domain", true),
            },
            Checks = checks,
        };
        var issues = sb.Service.BuildIssues(report);
        Assert.Equal(2, issues.Count(i => i.FixAsync is not null));

        var vm = Vm(new AppSettings(), new FakeContributor("Безопасность", issues.ToArray()));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var before = vm.Score;
        var items = vm.Groups.SelectMany(g => g.Items).Where(i => i.Issue.CanFix).ToList();
        Assert.Equal(2, items.Count);

        foreach (var item in items)
        {
            await WithConfirm(async () =>
            {
                await vm.FixIssueAsync(item);
                return true;
            });
            Assert.False(item.Issue.IsFixed, item.Issue.Title);
            Assert.True(item.FixFailed, item.Issue.Title);
            Assert.True(item.Issue.CanFix, item.Issue.Title);
        }
        Assert.Equal(before, vm.Score);
        Assert.Equal(0, vm.FixedCount);
    }

    // ---------------------------------------------------------------- stopped fixes

    [Fact]
    public async Task FixStoppedByCancel_ThatReturnsAPartialResult_IsNotMarkedFixed()
    {
        DashboardViewModel? vm = null;
        var issue = Issue("Программы", IssueSeverity.Low, "Битые записи в списке программ",
            fix: _ =>
            {
                vm!.CancelCommand.Execute(null); // "Отмена" while the fix runs; the module stops and reports what it did
                return Task.FromResult("Удалено 2 записи, резервные копии сохранены.");
            });
        vm = Vm(new AppSettings(), new FakeContributor("Программы", issue));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.Single().Items.Single();

        await WithConfirm(async () =>
        {
            await vm.FixIssueAsync(item);
            return true;
        });

        Assert.False(issue.IsFixed);
        Assert.True(issue.CanFix);
        Assert.True(item.FixFailed);
        Assert.False(item.IsFixing);
        Assert.Contains("Удалено 2 записи", issue.FixResult);
        Assert.Contains(DashboardViewModel.InterruptedNote, issue.FixResult);
        Assert.Equal(98, vm.Score);
        Assert.Equal(0, vm.FixedCount);
        Assert.StartsWith("Не исправлено", vm.ActionSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task FixSafe_StoppedInsideTheCleaning_DoesNotCountTheEstimateAsFreed()
    {
        DashboardViewModel? vm = null;
        var nextRan = 0;
        var junk = Issue("Очистка", IssueSeverity.Medium, "Можно освободить 2 ГБ мусора", reclaimable: 2L << 30,
            fix: _ =>
            {
                vm!.CancelCommand.Execute(null);
                return Task.FromResult("Освобождено 10 МБ, удалено 5 файлов."); // no marker: only the token tells
            }, autoFixSafe: true);
        var next = Issue("Ускорение", IssueSeverity.Low, "Игровой режим", reclaimable: 500,
            fix: _ =>
            {
                Interlocked.Increment(ref nextRan);
                return Task.FromResult("Готово.");
            }, autoFixSafe: true);
        vm = Vm(new AppSettings(), new FakeContributor("Смешанный", junk, next));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var before = vm.Score;

        var result = await WithConfirm(() => vm.FixSafeAsync());

        Assert.NotNull(result);
        Assert.True(result!.Cancelled);
        Assert.True(result.Interrupted);
        Assert.Equal(0, result.Fixed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(0, nextRan);
        Assert.False(junk.IsFixed);
        Assert.False(next.IsFixed);
        Assert.Equal(before, vm.Score);
        Assert.Equal((2L << 30) + 500, vm.ReclaimableBytes);
        Assert.DoesNotContain("ничего не изменено", vm.ActionSummary);
        Assert.Contains("остановлено на середине", vm.ActionSummary);
        Assert.Contains("Остальные исправления отменены", vm.ActionSummary);
        Assert.DoesNotContain("Освобождено", vm.ActionSummary);
    }

    [Fact]
    public async Task FixSafe_CountsOnlyCompletedFixesAsFreed()
    {
        DashboardViewModel? vm = null;
        var first = Issue("Очистка", IssueSeverity.Medium, "Временные файлы", reclaimable: 1000,
            fix: _ => Task.FromResult("Удалено."), autoFixSafe: true);
        var second = Issue("Очистка", IssueSeverity.Low, "Кэш браузеров", reclaimable: 5000,
            fix: _ =>
            {
                vm!.CancelCommand.Execute(null);
                return Task.FromResult("Освобождено 1 КБ, удалено 1 файл (очистка прервана).");
            }, autoFixSafe: true);
        vm = Vm(new AppSettings(), new FakeContributor("Очистка", first, second));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        var result = await WithConfirm(() => vm.FixSafeAsync());

        Assert.Equal(1, result!.Fixed);
        Assert.True(result.Interrupted);
        Assert.Equal(1000, result.FreedBytes);
        Assert.True(first.IsFixed);
        Assert.False(second.IsFixed);
        Assert.Equal(5000, vm.ReclaimableBytes);
        Assert.Contains("Исправлено 1 из 2", vm.ActionSummary);
        Assert.DoesNotContain("Остальные", vm.ActionSummary); // nothing was left to cancel
    }

    [Fact]
    public void BatchSummary_OfAnInterruptedFix_NeverSaysNothingChanged()
    {
        var text = DashboardViewModel.BatchSummaryText(new FixBatchResult(1, 0, 0, true, 0, Interrupted: true));
        Assert.DoesNotContain("ничего не изменено", text);
        Assert.Contains("остановлено на середине", text);
        Assert.DoesNotContain("Остальные", text);

        Assert.Equal("Исправление отменено — ничего не изменено.",
            DashboardViewModel.BatchSummaryText(new FixBatchResult(2, 0, 0, true, 0)));
    }

    // ---------------------------------------------------------------- incomplete scans

    [Fact]
    public async Task AllModulesFailing_IsNotExcellent_AndShowsNoProblemsPlaceholderNowhere()
    {
        var vm = Vm(new AppSettings(), Enumerable.Range(0, 8).Select(i => (IHealthContributor)Failing($"M{i}")).ToArray());

        var outcome = await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.Equal(100, outcome.Score);
        Assert.True(vm.IsIncomplete);
        Assert.Equal("Проверка неполная", vm.VerdictText);
        Assert.DoesNotContain("отличной форме", vm.VerdictDescription);
        Assert.Contains("8 разделов", vm.VerdictDescription);
        Assert.False(vm.ShowNoProblems);
        Assert.Equal("WarningBrush", vm.ScoreBrushKey);
        Assert.Equal("В проверенных разделах проблем нет", vm.IssueCountText);
        Assert.NotNull(vm.ScanWarning);
    }

    [Fact]
    public async Task TimedOutModule_MakesTheScanIncomplete()
    {
        var vm = Vm(new AppSettings(),
            new FakeContributor("Безопасность", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<HealthIssue>();
            }),
            new FakeContributor("Очистка", Issue("Очистка", IssueSeverity.Low)));
        vm.ModuleTimeout = TimeSpan.FromMilliseconds(300);

        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.Equal(98, vm.Score);
        Assert.Equal("Проверка неполная", vm.VerdictText);
        Assert.Contains("1 раздел", vm.VerdictDescription);
        Assert.Equal("WarningBrush", vm.ScoreBrushKey);
    }

    [Fact]
    public async Task IncompleteScan_ShowsNoDelta_AndNeverBecomesTheBaseline()
    {
        var settings = new AppSettings();
        var t0 = new DateTime(2026, 10, 10, 9, 0, 0);
        new ScanSummary { Score = 46, MeasuredScore = 46, Time = t0, IssueCount = 4 }.Save(settings);

        // 1. Security fails: the remaining modules find nothing → 100, but it must not say "Было 46 → стало 100".
        var vm = Vm(settings, Failing("Безопасность"), new FakeContributor("Очистка"));
        vm.Now = () => t0.AddDays(1);
        await vm.OnNavigatedToAsync();
        vm.OnNavigatedFrom();
        await vm.ScanCoreAsync(CancellationToken.None, persist: true);

        Assert.Equal(100, vm.Score);
        Assert.False(vm.HasScoreDelta);
        Assert.Equal("Проверка неполная", vm.VerdictText);
        var saved = ScanSummary.Load(settings)!;
        Assert.Equal(1, saved.FailedModules);
        Assert.Null(saved.Delta);
        Assert.Equal((46, (DateTime?)t0), saved.NextBaseline);

        // 2. After a restart the saved incomplete result is still not called "Отлично".
        var restarted = Vm(settings, new FakeContributor("Очистка"));
        restarted.Now = () => t0.AddDays(1).AddMinutes(5);
        await restarted.OnNavigatedToAsync();
        restarted.OnNavigatedFrom();
        Assert.Equal("Проверка неполная", restarted.VerdictText);
        Assert.False(restarted.HasScoreDelta);

        // 3. The next complete scan compares with the last COMPLETE one (46), not with the partial 100.
        var vm2 = Vm(settings, new FakeContributor("Безопасность", Issue("Безопасность", IssueSeverity.Medium)),
            new FakeContributor("Очистка"));
        vm2.Now = () => t0.AddDays(2);
        await vm2.OnNavigatedToAsync();
        vm2.OnNavigatedFrom();
        await vm2.ScanCoreAsync(CancellationToken.None, persist: true);

        Assert.Equal(94, vm2.Score);
        Assert.Equal("Отлично", vm2.VerdictText);
        Assert.True(vm2.HasScoreDelta);
        Assert.Equal("Было 46 → стало 94 (+48)", vm2.ScoreDeltaText);
        Assert.Equal(t0, vm2.ScoreDelta!.PreviousTime);
    }

    [Fact]
    public async Task FirstScan_ThatIsIncomplete_GivesTheNextScanNothingToCompareWith()
    {
        var settings = new AppSettings();
        var vm = Vm(settings, Failing("Безопасность"), new FakeContributor("Очистка"));
        await vm.ScanCoreAsync(CancellationToken.None, persist: true);
        Assert.Null(ScanSummary.Load(settings)!.NextBaseline);

        var vm2 = Vm(settings, new FakeContributor("Безопасность"), new FakeContributor("Очистка"));
        await vm2.OnNavigatedToAsync();
        vm2.OnNavigatedFrom();
        await vm2.ScanCoreAsync(CancellationToken.None, persist: true);
        Assert.Equal(100, vm2.Score);
        Assert.False(vm2.HasScoreDelta);
        Assert.Equal("Отлично", vm2.VerdictText);
        Assert.True(vm2.ShowNoProblems);
    }

    [Fact]
    public async Task Report_AfterAnIncompleteScan_SaysSo()
    {
        var vm = Vm(new AppSettings(), Failing("Безопасность"), new FakeContributor("Очистка"));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        var report = (await vm.BuildReportAsync())!;

        Assert.Equal(1, report.FailedModules);
        Assert.Equal("Проверка неполная", report.Verdict);
        Assert.Null(report.Delta);
        Assert.DoesNotContain("отличной форме", ReportBuilder.BuildHtml(report));
    }
}
