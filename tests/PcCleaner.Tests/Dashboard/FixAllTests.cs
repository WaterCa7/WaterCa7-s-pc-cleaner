using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// "Исправить всё (N)" on the Dashboard: which issues it takes (CanFix &amp;&amp; InFixAll, not only AutoFixSafe), the fix
/// label and the settings link on every row.
/// </summary>
[Collection(DashboardSequentialCollection.Name)]
public class FixAllTests
{
    public FixAllTests() => TestEnvironment.Init();

    private sealed class Counter
    {
        public int Value;
    }

    private static Func<CancellationToken, Task<string>> Counting(Counter counter, string result = "Готово.")
        => _ =>
        {
            Interlocked.Increment(ref counter.Value);
            return Task.FromResult(result);
        };

    private static HealthIssue NewIssue(string title, Func<CancellationToken, Task<string>>? fix = null,
        bool autoFixSafe = false, bool inFixAll = true, string? settingsUri = null, string fixLabel = "Исправить",
        bool onlyOpensTool = false, long reclaimable = 0, string? consequence = null, FixKind fixKind = FixKind.Settings,
        string module = "Ускорение")
        => new()
        {
            Module = module,
            Title = title,
            Details = "Описание проблемы",
            Severity = IssueSeverity.Medium,
            ReclaimableBytes = reclaimable,
            FixAsync = fix,
            AutoFixSafe = autoFixSafe,
            InFixAll = inFixAll,
            OnlyOpensTool = onlyOpensTool,
            SettingsUri = settingsUri,
            FixLabel = fixLabel,
            Consequence = consequence,
            FixKind = fixKind,
            TargetPageKey = "Optimizer",
        };

    private static async Task<DashboardViewModel> ScannedAsync(params HealthIssue[] issues)
    {
        var vm = new DashboardViewModel(new IHealthContributor[] { new FakeContributor("Ускорение", issues) }, new AppSettings())
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            OpenUri = _ => throw new InvalidOperationException("a test must not open anything"),
            RequestElevation = _ => throw new InvalidOperationException("a test must not restart the app"),
        };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        return vm;
    }

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

    private static IssueItem RowOf(DashboardViewModel vm, string title)
        => vm.Groups.SelectMany(g => g.AllItems).Single(i => i.Issue.Title == title);

    private static List<IssueItem> RowsOf(DashboardViewModel vm, params string[] titles)
        => titles.Select(t => RowOf(vm, t)).ToList();

    [Fact]
    public void HealthIssue_Defaults()
    {
        var issue = new HealthIssue { Module = "M", Title = "T" };

        Assert.Equal("Исправить", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.False(issue.OnlyOpensTool);
        Assert.True(issue.RunsInFixAll);
        Assert.Null(issue.SettingsUri);
    }

    [Fact]
    public void HealthIssue_RunsInFixAll_OnlyWhenItIsInFixAll_AndDoesNotOnlyOpenATool()
    {
        Assert.False(new HealthIssue { Module = "M", Title = "T", InFixAll = false }.RunsInFixAll);
        Assert.False(new HealthIssue { Module = "M", Title = "T", OnlyOpensTool = true }.RunsInFixAll);
        Assert.False(new HealthIssue { Module = "M", Title = "T", InFixAll = true, OnlyOpensTool = true }.RunsInFixAll);
    }

    [Fact]
    public async Task FixOne_ThatOnlyOpensATool_IsNotMarkedFixed_AndShowsItsText()
    {
        const string opened = "Открыта «Очистка диска»: нажмите «Очистить системные файлы».";
        var calls = new Counter();
        var vm = await ScannedAsync(
            NewIssue("Осталась папка Windows.old", Counting(calls, opened), inFixAll: false, onlyOpensTool: true));
        var score = vm.Score;
        var row = RowOf(vm, "Осталась папка Windows.old");

        await WithConfirm((_, _) => true, async () =>
        {
            await vm.FixIssueAsync(row);
            return true;
        });

        Assert.Equal(1, calls.Value);
        Assert.False(row.Issue.IsFixed);
        Assert.True(row.Issue.CanFix);                   // the button stays: the user may open the wizard again
        Assert.Equal(opened, row.Issue.FixResult);       // the row still shows what happened...
        Assert.False(row.FixFailed);                     // ...and it is not drawn as a failure
        Assert.Equal(0, vm.FixedCount);
        Assert.Equal(score, vm.Score);
        Assert.DoesNotContain("Исправлено", vm.ActionSummary);
        Assert.DoesNotContain("Не исправлено", vm.ActionSummary);
        Assert.Contains(opened, vm.ActionSummary);
    }

    [Fact]
    public async Task FixOne_ThatOnlyOpensATool_StillFailsWhenTheToolCannotBeOpened()
    {
        var vm = await ScannedAsync(
            NewIssue("Осталась папка Windows.old", _ => Task.FromResult("Не удалось открыть «Очистку диска»."), inFixAll: false,
                onlyOpensTool: true));
        var row = RowOf(vm, "Осталась папка Windows.old");

        await WithConfirm((_, _) => true, async () =>
        {
            await vm.FixIssueAsync(row);
            return true;
        });

        Assert.False(row.Issue.IsFixed);
        Assert.True(row.FixFailed);
        Assert.StartsWith("Не исправлено", vm.ActionSummary);
    }

    [Fact]
    public async Task FixOne_Ordinary_IsMarkedFixed()
    {
        var vm = await ScannedAsync(NewIssue("Обычное", Counting(new Counter(), "Готово: всё исправлено.")));
        var row = RowOf(vm, "Обычное");

        await WithConfirm((_, _) => true, async () =>
        {
            await vm.FixIssueAsync(row);
            return true;
        });

        Assert.True(row.Issue.IsFixed);
        Assert.False(row.Issue.CanFix);
        Assert.False(row.FixFailed);
        Assert.Equal(1, vm.FixedCount);
        Assert.StartsWith("Исправлено: Обычное", vm.ActionSummary);
    }

    [Fact]
    public async Task WindowsOldIssue_RunThroughTheDashboard_OnlyOpensTheWizard()
    {
        // The real issue of the Disk module; the tool launcher is a fake, nothing is started.
        var launches = new List<(string Exe, string Args)>();
        var disk = new DiskService(new DiskEnvironment
        {
            SystemDriveRoot = @"C:\",
            LaunchTool = (exe, args) =>
            {
                launches.Add((exe, args));
                return ToolLaunchResult.Started;
            },
            IsToolRunning = _ => false,
            Recycler = _ => throw new InvalidOperationException("recycler must not be called"),
        });
        const long size = 40L << 30;
        var issue = disk.WindowsOldIssue(size, complete: true);
        Assert.True(issue.OnlyOpensTool);
        var vm = await ScannedAsync(issue);
        var row = RowOf(vm, issue.Title);

        await WithConfirm((_, _) => true, async () =>
        {
            await vm.FixIssueAsync(row);
            return true;
        });

        Assert.Single(launches);
        Assert.False(issue.IsFixed);
        Assert.False(row.FixFailed);
        Assert.StartsWith("Открыта «Очистка диска»", issue.FixResult);
        Assert.Equal(0, vm.FixedCount);
        // its size still counts, and it stays in the "free it yourself" part
        Assert.Equal(new ReclaimableSplit(0, size), vm.Reclaimable);
        Assert.True(vm.HasManualReclaimable);
        Assert.False(vm.HasAutoReclaimable);
    }

    [Fact]
    public async Task FixOneCore_ThatOnlyOpensATool_ReturnsNotFixed()
    {
        var vm = await ScannedAsync(
            NewIssue("Только открывает", Counting(new Counter(), "Открыта «Очистка диска»."), inFixAll: false, onlyOpensTool: true),
            NewIssue("Обычное", Counting(new Counter())));

        Assert.False(await vm.FixOneCoreAsync(RowOf(vm, "Только открывает"), CancellationToken.None));
        Assert.True(await vm.FixOneCoreAsync(RowOf(vm, "Обычное"), CancellationToken.None));
    }

    [Fact]
    public async Task FixAllCount_CountsFixableInFixAllIssues()
    {
        var calls = new Counter();
        var vm = await ScannedAsync(
            NewIssue("Первое", Counting(calls)),
            NewIssue("Второе", Counting(calls), autoFixSafe: true),
            NewIssue("Перезагрузка", Counting(calls), inFixAll: false),
            NewIssue("Совет без исправления"));

        Assert.Equal(2, vm.FixAllCount);
        Assert.Equal("Исправить всё (2)", vm.FixAllText);
        Assert.True(vm.HasFixAll);
        Assert.True(vm.FixAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task HasFixAll_IsFalse_WhenOnlyStandaloneFixesRemain()
    {
        var vm = await ScannedAsync(NewIssue("Перезагрузка", Counting(new Counter()), inFixAll: false));

        Assert.Equal(0, vm.FixAllCount);
        Assert.False(vm.HasFixAll);
        Assert.False(vm.FixAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task FixAll_RunsOnlyInFixAllIssues()
    {
        var included = new Counter();
        var standalone = new Counter();
        var vm = await ScannedAsync(
            NewIssue("Первое", Counting(included)),
            NewIssue("Второе", Counting(included)),
            NewIssue("Удалить программу", Counting(standalone), inFixAll: false));

        var batch = await WithConfirm((_, _) => true, () => vm.FixAllAsync());

        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Total);
        Assert.Equal(2, batch.Fixed);
        Assert.Equal(2, included.Value);
        Assert.Equal(0, standalone.Value);
        Assert.False(RowOf(vm, "Удалить программу").Issue.IsFixed);
        Assert.Equal(0, vm.FixAllCount);
    }

    [Fact]
    public async Task FixAll_NotAutoFixSafeIssuesAreIncluded()
    {
        var calls = new Counter();
        var vm = await ScannedAsync(NewIssue("Не помечено безопасным", Counting(calls), autoFixSafe: false, inFixAll: true));

        var batch = await WithConfirm((_, _) => true, () => vm.FixAllAsync());

        Assert.Equal(1, batch!.Fixed);
        Assert.Equal(1, calls.Value);
        Assert.True(RowOf(vm, "Не помечено безопасным").Issue.IsFixed);
    }

    [Fact]
    public async Task FixAll_DoesNothingWithoutConfirmation()
    {
        var calls = new Counter();
        var vm = await ScannedAsync(NewIssue("Первое", Counting(calls)));
        string? title = null;

        var batch = await WithConfirm((t, _) =>
        {
            title = t;
            return false;
        }, () => vm.FixAllAsync());

        Assert.Null(batch);
        Assert.Equal("Исправить всё", title);
        Assert.Equal(0, calls.Value);
    }

    [Fact]
    public async Task FixAllConfirmText_NamesTheItemsAndExplainsWhatIsNotIncluded()
    {
        var vm = await ScannedAsync(
            NewIssue("Первое", Counting(new Counter())),
            NewIssue("Второе", Counting(new Counter())));
        var items = vm.Groups.SelectMany(g => g.AllItems).ToList();

        var many = DashboardViewModel.FixAllConfirmText(items);
        var one = DashboardViewModel.FixAllConfirmText(items.Take(1).ToList());

        Assert.StartsWith("Будут исправлены проблемы (2):", many);
        Assert.Contains("Первое", many);
        Assert.Contains("Второе", many);
        Assert.Contains("очистка Корзины и перезагрузка сюда не входят", many);
        Assert.DoesNotContain("низкорисковые", many);
        Assert.StartsWith("Будет исправлено:", one);
    }

    [Fact]
    public async Task FixAllConfirmText_NamesWhatEachFixDoes_AndWhatToKnowBeforeIt()
    {
        const string restart = "Изменение вступит в силу после перезагрузки компьютера.";
        var vm = await ScannedAsync(
            NewIssue("Прокси", Counting(new Counter()), fixLabel: "Выключить прокси"),
            NewIssue("Обычное", Counting(new Counter())),
            NewIssue("Планирование GPU", Counting(new Counter()), consequence: restart));

        var text = DashboardViewModel.FixAllConfirmText(RowsOf(vm, "Прокси", "Обычное", "Планирование GPU"));
        var lines = text.Split(Environment.NewLine);

        Assert.Contains("• Прокси → Выключить прокси (Ускорение)", lines);
        Assert.Contains("• Обычное (Ускорение)", lines); // the plain «Исправить» adds nothing
        var gpu = Array.IndexOf(lines, "• Планирование GPU (Ускорение)");
        Assert.True(gpu >= 0, text);
        Assert.Equal("  Важно: " + restart, lines[gpu + 1]); // the single fix's «Важно:» line is in the batch too
        Assert.Single(lines, l => l.Contains("Важно:"));
    }

    [Fact]
    public async Task FixAllConfirmText_ClosingParagraph_SpeaksOnlyOfWhatIsIncluded()
    {
        const string junk = "Временные файлы и кэш удаляются насовсем — документы, пароли и история браузеров не затрагиваются.";
        const string downloads = "Файлы из «Загрузок» уходят в Корзину — их можно восстановить.";
        const string settings = "Изменённые настройки Windows можно вернуть обратно.";
        const string notIncluded = "Удаление программ, очистка Корзины и перезагрузка сюда не входят — для них есть отдельные кнопки.";
        var vm = await ScannedAsync(
            NewIssue("Настройка", Counting(new Counter())),
            NewIssue("Мусор", Counting(new Counter()), fixKind: FixKind.DeletesJunk, module: "Очистка"),
            NewIssue("Загрузки", Counting(new Counter()), fixKind: FixKind.MovesToRecycleBin, module: "Диски"));

        var onlySettings = DashboardViewModel.FixAllConfirmText(RowsOf(vm, "Настройка"));
        var onlyJunk = DashboardViewModel.FixAllConfirmText(RowsOf(vm, "Мусор"));
        var onlyDownloads = DashboardViewModel.FixAllConfirmText(RowsOf(vm, "Загрузки"));
        var all = DashboardViewModel.FixAllConfirmText(RowsOf(vm, "Мусор", "Загрузки", "Настройка"));

        Assert.Contains(settings, onlySettings);
        Assert.DoesNotContain("насовсем", onlySettings);
        Assert.DoesNotContain("«Загрузок»", onlySettings);

        Assert.Contains(junk, onlyJunk);
        Assert.DoesNotContain(settings, onlyJunk);
        Assert.DoesNotContain("«Загрузок»", onlyJunk);

        Assert.Contains(downloads, onlyDownloads);
        Assert.DoesNotContain("насовсем", onlyDownloads);
        Assert.DoesNotContain(settings, onlyDownloads);

        Assert.Contains($"{junk} {downloads} {settings} {notIncluded}", all);
        foreach (var text in new[] { onlySettings, onlyJunk, onlyDownloads })
            Assert.Contains(notIncluded, text);
    }

    [Fact]
    public async Task FixAllConfirmText_OnlyNeutralFixes_ClaimNothingCanBeChangedBack()
    {
        const string notIncluded = "Удаление программ, очистка Корзины и перезагрузка сюда не входят — для них есть отдельные кнопки.";
        var vm = await ScannedAsync(
            NewIssue("Кэш DNS", Counting(new Counter()), fixKind: FixKind.Neutral, module: "Сеть"),
            NewIssue("Базы антивируса", Counting(new Counter()), fixKind: FixKind.Neutral, module: "Безопасность"));

        var text = DashboardViewModel.FixAllConfirmText(RowsOf(vm, "Кэш DNS", "Базы антивируса"));
        var lines = text.Split(Environment.NewLine);

        Assert.DoesNotContain("вернуть обратно", text);
        Assert.DoesNotContain("насовсем", text);
        Assert.DoesNotContain("«Загрузок»", text);
        Assert.Contains(notIncluded, lines); // the paragraph is that one sentence alone
    }

    [Fact]
    public async Task FixAllConfirmText_ImportantLinesCountTowardTheShortList()
    {
        // 18 problems and 3 «Важно:» lines do not fit one per line: the per-module form is used, the consequences stay.
        var vm = await ScannedAsync(Enumerable.Range(1, 18)
            .Select(i => NewIssue($"Проблема {i:00}", Counting(new Counter()), consequence: i <= 3 ? $"Последствие {i}." : null))
            .ToArray());

        var text = DashboardViewModel.FixAllConfirmText(vm.Groups.SelectMany(g => g.AllItems).ToList());
        var lines = text.Split(Environment.NewLine);

        Assert.Contains(lines, l => l.StartsWith("• Ускорение (18): Проблема 01; Проблема 02;", StringComparison.Ordinal));
        Assert.Contains("  Важно: Проблема 01 — Последствие 1.", lines);
        Assert.Contains("  Важно: Проблема 03 — Последствие 3.", lines);
        Assert.True(lines.Count(l => l.StartsWith("•", StringComparison.Ordinal) || l.StartsWith("  Важно", StringComparison.Ordinal)) <= 20,
            text);
    }

    [Fact]
    public async Task FixAll_LeavesAFixThatOnlyOpensATool_EvenIfItSaysItIsInFixAll()
    {
        var calls = new Counter();
        var vm = await ScannedAsync(
            NewIssue("Только открывает", Counting(calls, "Открыта «Очистка диска»."), inFixAll: true, onlyOpensTool: true),
            NewIssue("Обычное", Counting(new Counter())));

        Assert.Equal(1, vm.FixAllCount);
        Assert.Equal("Не входит в «Исправить всё»: это действие запускается только отдельно",
            RowOf(vm, "Только открывает").AutoFixHint); // the row's hint says the same
        var batch = await WithConfirm((_, _) => true, () => vm.FixAllAsync());

        Assert.Equal(1, batch!.Total);
        Assert.Equal(0, calls.Value);
    }

    [Fact]
    public async Task FixConfirmText_OfAFixThatOnlyOpensATool_SaysWhereTheSpaceIsFreed()
    {
        var vm = await ScannedAsync(
            NewIssue("Осталась папка Windows.old", Counting(new Counter()), inFixAll: false, onlyOpensTool: true,
                reclaimable: 40L << 30));

        var text = DashboardViewModel.FixConfirmText(RowOf(vm, "Осталась папка Windows.old"));

        Assert.Contains($"Что изменится: Откроется «Очистка диска» — освободить до {Format.Bytes(40L << 30)} можно там", text);
        Assert.DoesNotContain("Освободит", text);
    }

    [Fact]
    public async Task FixAll_AdminResults_OfferElevation()
    {
        var calls = new Counter();
        var vm = await ScannedAsync(
            NewIssue("Нужны права", _ => Task.FromResult("Нужны права администратора: служба не изменена.")),
            NewIssue("Обычное", Counting(calls)));
        var reasons = new List<string>();
        vm.RequestElevation = reason =>
        {
            reasons.Add(reason);
            return true;
        };

        var batch = await WithConfirm((_, _) => true, () => vm.FixAllAsync());

        Assert.NotNull(batch);
        Assert.Equal(1, batch!.NeedAdmin);
        Assert.Equal(1, batch.Failed);
        Assert.Equal(1, batch.Fixed);
        Assert.Contains("права администратора", vm.ActionSummary);
        // An administrator session never asks to restart itself.
        if (Admin.IsElevated)
        {
            Assert.Empty(reasons);
        }
        else
        {
            var reason = Assert.Single(reasons);
            Assert.Equal("исправления, которым нужны права администратора", reason);
        }
    }

    [Fact]
    public async Task FixAll_FixThatThrowsUnauthorizedAccess_CountsInNeedAdmin()
    {
        var vm = await ScannedAsync(
            NewIssue("Нет доступа", _ => throw new UnauthorizedAccessException("Access to the path is denied.")),
            NewIssue("Обычное", Counting(new Counter())));
        var reasons = new List<string>();
        vm.RequestElevation = reason =>
        {
            reasons.Add(reason);
            return true;
        };

        var batch = await WithConfirm((_, _) => true, () => vm.FixAllAsync());

        Assert.NotNull(batch);
        Assert.Equal(1, batch!.NeedAdmin);
        Assert.Equal(1, batch.Failed);
        Assert.Equal(1, batch.Fixed);
        Assert.True(RowOf(vm, "Нет доступа").AccessDenied);
        Assert.Contains("Для 1 нужны права администратора.", vm.ActionSummary);
        if (Admin.IsElevated)
            Assert.Empty(reasons);
        else
            Assert.Equal("исправления, которым нужны права администратора", Assert.Single(reasons));
    }

    [Fact]
    public async Task FixAll_WithoutAdminResults_DoesNotOfferElevation()
    {
        var vm = await ScannedAsync(
            NewIssue("Не получилось", _ => Task.FromResult("Не удалось изменить параметр: файл занят.")),
            NewIssue("Обычное", Counting(new Counter())));
        var asked = 0;
        vm.RequestElevation = _ =>
        {
            asked++;
            return true;
        };

        var batch = await WithConfirm((_, _) => true, () => vm.FixAllAsync());

        Assert.Equal(0, batch!.NeedAdmin);
        Assert.Equal(1, batch.Failed);
        Assert.Equal(0, asked);
        Assert.DoesNotContain("администратора", vm.ActionSummary);
    }

    [Fact]
    public void BatchSummary_MentionsAdminRights_OnlyWhenSomeFixesNeedThem()
    {
        var with = DashboardViewModel.BatchSummaryText(new FixBatchResult(3, 1, 2, false, 0, NeedAdmin: 2));
        var without = DashboardViewModel.BatchSummaryText(new FixBatchResult(3, 1, 2, false, 0));

        Assert.Contains("Для 2 нужны права администратора.", with);
        Assert.DoesNotContain("администратора", without);
    }

    [Fact]
    public async Task IssueItem_FixLabelAndHint_FollowTheIssue()
    {
        var vm = await ScannedAsync(
            NewIssue("Первое", Counting(new Counter()), fixLabel: "Перезагрузить"),
            NewIssue("Второе", Counting(new Counter()), inFixAll: false));

        var first = RowOf(vm, "Первое");
        var second = RowOf(vm, "Второе");

        Assert.Equal("Перезагрузить", first.Issue.FixLabel);
        Assert.Equal("Входит в «Исправить всё»", first.AutoFixHint);
        Assert.Equal("Не входит в «Исправить всё»: это действие запускается только отдельно", second.AutoFixHint);
    }

    [Fact]
    public async Task IssueItem_SettingsLabel()
    {
        var vm = await ScannedAsync(
            NewIssue("Настройки", settingsUri: "ms-settings:windowsupdate"),
            NewIssue("Страница", settingsUri: "https://example.com/help"),
            NewIssue("Без ссылки"));

        var settings = RowOf(vm, "Настройки");
        var page = RowOf(vm, "Страница");
        var none = RowOf(vm, "Без ссылки");

        Assert.True(settings.HasSettings);
        Assert.Equal("Открыть настройки", settings.SettingsLabel);
        Assert.True(page.HasSettings);
        Assert.Equal("Открыть страницу", page.SettingsLabel);
        Assert.False(none.HasSettings);
        Assert.False(none.OpenSettingsCommand!.CanExecute(null));
    }

    [Fact]
    public async Task OpenSettings_OpensTheUri()
    {
        var vm = await ScannedAsync(NewIssue("Обновления", settingsUri: "ms-settings:windowsupdate"));
        var opened = new List<string>();
        vm.OpenUri = uri => opened.Add(uri);
        var row = RowOf(vm, "Обновления");

        Assert.True(row.OpenSettingsCommand!.CanExecute(null));
        row.OpenSettingsCommand.Execute(null);

        Assert.Equal(new[] { "ms-settings:windowsupdate" }, opened);
    }

    [Fact]
    public async Task OpenSettings_WhenOpeningFails_ShowsAnError()
    {
        var vm = await ScannedAsync(NewIssue("Обновления", settingsUri: "ms-settings:windowsupdate"));
        vm.OpenUri = _ => throw new InvalidOperationException("нет обработчика");
        var row = RowOf(vm, "Обновления");

        var previous = Dialogs.ErrorHandler;
        string? shown = null;
        Dialogs.ErrorHandler = (_, message) => shown = message;
        try
        {
            row.OpenSettingsCommand!.Execute(null);
        }
        finally
        {
            Dialogs.ErrorHandler = previous;
        }

        Assert.NotNull(shown);
        Assert.Contains("ms-settings:windowsupdate", shown);
    }
}
