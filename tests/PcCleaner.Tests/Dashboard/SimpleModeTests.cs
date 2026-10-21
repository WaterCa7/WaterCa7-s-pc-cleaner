using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Dashboard;

/// <summary>
/// The Dashboard in the simple mode: one button «Проверить и исправить» (scan, then the usual «Исправить всё» question, or
/// «Всё в порядке»), rows with a title, the fix button and «Подробнее», no per-module rows and no report buttons. The expert
/// mode keeps everything as it was. The mode reaches the view model the way the shell sends it: settings.UiMode and OnModeChanged.
/// </summary>
[Collection(DashboardSequentialCollection.Name)]
public class SimpleModeDashboardTests
{
    public SimpleModeDashboardTests() => TestEnvironment.Init();

    private sealed class Counter
    {
        public int Value;
    }

    private sealed class RecordingNavigator : INavigator
    {
        public List<string> Opened { get; } = new();

        public void NavigateTo(string pageKey) => Opened.Add(pageKey);
    }

    private static Func<CancellationToken, Task<string>> Counting(Counter counter)
        => _ =>
        {
            Interlocked.Increment(ref counter.Value);
            return Task.FromResult("Готово.");
        };

    private static HealthIssue NewIssue(string title, Func<CancellationToken, Task<string>>? fix = null, string? target = "Cleaner",
        bool inFixAll = true, string details = "Описание проблемы", string? gain = "Освободит 1 МБ",
        IssueSeverity severity = IssueSeverity.Medium, bool onlyOpensTool = false)
        => new()
        {
            Module = "Очистка",
            Title = title,
            Details = details,
            Gain = gain,
            Severity = severity,
            FixAsync = fix,
            InFixAll = inFixAll,
            OnlyOpensTool = onlyOpensTool,
            TargetPageKey = target,
        };

    private static DashboardViewModel Create(AppSettings settings, params IHealthContributor[] contributors)
        => new(contributors, settings)
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
            OpenUri = _ => throw new InvalidOperationException("a test must not open anything"),
            RequestElevation = _ => throw new InvalidOperationException("a test must not restart the app"),
        };

    private static DashboardViewModel Create(UiMode? mode, params HealthIssue[] issues)
        => Create(new AppSettings { UiMode = mode }, new FakeContributor("Очистка", issues));

    private static async Task<DashboardViewModel> ScannedAsync(UiMode? mode, params HealthIssue[] issues)
    {
        var vm = Create(mode, issues);
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        return vm;
    }

    private static async Task<List<(string Title, string Message)>> RunWithAnswerAsync(bool answer, Func<Task> action)
    {
        var questions = new List<(string Title, string Message)>();
        var previous = Dialogs.ConfirmHandler;
        Dialogs.ConfirmHandler = (title, message) =>
        {
            questions.Add((title, message));
            return answer;
        };
        try
        {
            await action();
        }
        finally
        {
            Dialogs.ConfirmHandler = previous;
        }
        return questions;
    }

    private static IssueItem RowOf(DashboardViewModel vm, string title)
        => vm.Groups.SelectMany(g => g.AllItems).Single(i => i.Issue.Title == title);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not come true");
            await Task.Delay(10);
        }
    }

    // ----- the main button -----

    [Theory]
    [InlineData(null)]
    [InlineData(UiMode.Expert)]
    public async Task Expert_TheMainButtonIsTheScan_WithTheOldTexts(UiMode? mode)
    {
        var vm = Create(mode, NewIssue("Мусор"));

        Assert.False(vm.IsSimpleMode);
        Assert.True(vm.IsExpertMode);
        Assert.Same(vm.ScanCommand, vm.MainScanCommand);
        Assert.Equal("Проверить компьютер", vm.ScanButtonText);
        Assert.Equal("Около минуты · только чтение, ничего не меняет", vm.ScanHintText);
        Assert.StartsWith("Проверит все разделы", vm.ScanButtonToolTip);
        Assert.StartsWith("Кнопка «Проверить компьютер» заглянет во все разделы", vm.IntroText);

        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal("Проверить снова", vm.ScanButtonText);
    }

    [Fact]
    public async Task Simple_TheMainButtonChecksAndFixes_BeforeAndAfterAScan()
    {
        var vm = Create(UiMode.Simple, NewIssue("Мусор"));

        Assert.True(vm.IsSimpleMode);
        Assert.False(vm.IsExpertMode);
        Assert.Same(vm.CheckAndFixCommand, vm.MainScanCommand);
        Assert.NotSame(vm.ScanCommand, vm.CheckAndFixCommand);
        Assert.Equal("Проверить и исправить", vm.ScanButtonText);
        Assert.Contains("ничего не изменим без вашего согласия", vm.ScanHintText);
        Assert.Contains("спросит подтверждение", vm.ScanButtonToolTip);
        Assert.StartsWith("Кнопка «Проверить и исправить»", vm.IntroText);

        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        Assert.Equal("Проверить и исправить", vm.ScanButtonText);
    }

    [Fact]
    public void TheMode_ReachesTheMainButton_WhenTheShellSaysItChanged()
    {
        var settings = new AppSettings { UiMode = UiMode.Expert };
        var vm = Create(settings, new FakeContributor("Очистка"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.UiMode = UiMode.Simple;
        vm.OnModeChanged();

        Assert.True(vm.IsSimpleMode);
        Assert.Same(vm.CheckAndFixCommand, vm.MainScanCommand);
        Assert.Equal("Проверить и исправить", vm.ScanButtonText);
        foreach (var name in new[]
                 {
                     nameof(DashboardViewModel.IsSimpleMode), nameof(DashboardViewModel.IsExpertMode), nameof(DashboardViewModel.MainScanCommand),
                     nameof(DashboardViewModel.ScanButtonText), nameof(DashboardViewModel.ScanButtonToolTip), nameof(DashboardViewModel.ScanHintText),
                     nameof(DashboardViewModel.IntroText),
                 })
            Assert.Contains(name, changed);

        settings.UiMode = UiMode.Expert;
        vm.OnModeChanged();

        Assert.False(vm.IsSimpleMode);
        Assert.Same(vm.ScanCommand, vm.MainScanCommand);
        Assert.Equal("Проверить компьютер", vm.ScanButtonText);
    }

    // ----- check and fix -----

    [Fact]
    public async Task CheckAndFix_Scans_ThenAsksTheUsualQuestionOnce_AndFixes()
    {
        var calls = new Counter();
        var vm = Create(UiMode.Simple, NewIssue("Мусор", Counting(calls)), NewIssue("Кэш", Counting(calls)));

        var questions = await RunWithAnswerAsync(true, () => vm.CheckAndFixCommand.ExecuteAsync());

        Assert.Equal(DashboardScanState.Completed, vm.State);
        var (title, message) = Assert.Single(questions);
        Assert.Equal("Исправить всё", title);
        Assert.Contains("Будут исправлены проблемы (2)", message);
        Assert.Contains("Мусор", message);
        Assert.Contains("Кэш", message);
        Assert.Equal(2, calls.Value);
        Assert.Equal(2, vm.FixedCount);
        Assert.StartsWith("Исправлено", vm.ActionSummary);
    }

    [Fact]
    public async Task CheckAndFix_WhenTheAnswerIsNo_FixesNothing()
    {
        var calls = new Counter();
        var vm = Create(UiMode.Simple, NewIssue("Мусор", Counting(calls)));

        var questions = await RunWithAnswerAsync(false, () => vm.CheckAndFixCommand.ExecuteAsync());

        Assert.Single(questions);
        Assert.Equal(0, calls.Value);
        Assert.Equal(0, vm.FixedCount);
        Assert.Equal(DashboardScanState.Completed, vm.State); // the result stays: the rows still have their buttons
        Assert.Equal(1, vm.FixAllCount);
        Assert.False(RowOf(vm, "Мусор").Issue.IsFixed);
    }

    [Fact]
    public async Task CheckAndFix_WithNothingFound_SaysAllIsFine_WithoutAQuestion()
    {
        var vm = Create(UiMode.Simple);

        var questions = await RunWithAnswerAsync(true, () => vm.CheckAndFixCommand.ExecuteAsync());

        Assert.Empty(questions);
        Assert.Equal(DashboardScanState.Completed, vm.State);
        Assert.Equal("Всё в порядке", vm.ActionSummary);
    }

    [Fact]
    public async Task CheckAndFix_WhenTheScanIsIncomplete_DoesNotClaimThatAllIsFine()
    {
        var broken = new FakeContributor("Сеть", (Func<CancellationToken, Task<IReadOnlyList<HealthIssue>>>)(_ => throw new InvalidOperationException("boom")));
        var vm = Create(new AppSettings { UiMode = UiMode.Simple }, broken);

        var questions = await RunWithAnswerAsync(true, () => vm.CheckAndFixCommand.ExecuteAsync());

        Assert.Empty(questions);
        Assert.True(vm.IsIncomplete);
        Assert.Null(vm.ActionSummary);
        Assert.NotNull(vm.ScanWarning);
    }

    [Fact]
    public async Task CheckAndFix_WhenNothingCanBeFixedAtOnce_AsksNothing_AndDoesNotSayAllIsFine()
    {
        var calls = new Counter();
        var vm = Create(UiMode.Simple,
            NewIssue("Нет исправления", fix: null),
            NewIssue("Удалить программу", Counting(calls), inFixAll: false),
            NewIssue("Откроется окно", Counting(calls), onlyOpensTool: true));

        var questions = await RunWithAnswerAsync(true, () => vm.CheckAndFixCommand.ExecuteAsync());

        Assert.Empty(questions);
        Assert.Equal(0, calls.Value);
        Assert.Equal(0, vm.FixAllCount);
        Assert.Equal("Исправлять отсюда нечего — остальное в списке ниже", vm.ActionSummary);
    }

    [Fact]
    public async Task CheckAndFix_StoppedByCancel_NeverAsksAndNeverFixes()
    {
        var calls = new Counter();
        var hanging = new FakeContributor("Долгий", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<HealthIssue>();
        });
        var vm = Create(new AppSettings { UiMode = UiMode.Simple },
            new FakeContributor("Очистка", NewIssue("Мусор", Counting(calls))), hanging);
        vm.ModuleTimeout = TimeSpan.FromMinutes(5);

        var questions = await RunWithAnswerAsync(true, async () =>
        {
            var run = vm.CheckAndFixCommand.ExecuteAsync();
            await WaitUntilAsync(() => vm.IsScanning && vm.ModuleRows.Any(r => r.State == ModuleScanState.Done));
            Assert.False(vm.CheckAndFixCommand.CanExecute(null)); // the usual busy state: the button waits
            Assert.False(vm.ScanCommand.CanExecute(null));
            vm.CancelCommand.Execute(null);
            await run;
        });

        Assert.Equal(DashboardScanState.Cancelled, vm.State);
        Assert.Equal(1, vm.FixAllCount); // the finished module's issue is shown, but nothing is offered to fix
        Assert.Empty(questions);
        Assert.Equal(0, calls.Value);
        Assert.Null(vm.ActionSummary);
        Assert.True(vm.CheckAndFixCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAndFix_WhileTheDashboardIsBusy_DoesNothing()
    {
        // IsScanning is already true when ScanCoreAsync only has queued the module on the thread pool (SmartScanner runs it with
        // Task.Run), so the test waits for the module itself to be running — not for IsScanning, and not for a time.
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new FakeContributor("Долгий", async ct =>
        {
            running.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<HealthIssue>();
        });
        var vm = Create(new AppSettings { UiMode = UiMode.Simple }, hanging);
        vm.ModuleTimeout = TimeSpan.FromMinutes(5);
        var scan = vm.ScanCommand.ExecuteAsync();
        try
        {
            await running.Task.WaitAsync(TimeSpan.FromSeconds(10)); // only a guard against a hang, nothing depends on the time
            Assert.True(vm.IsScanning);

            // Busy: the command returns at once. A second scan would never end here (the module hangs), hence the guard.
            var questions = await RunWithAnswerAsync(true, () => vm.CheckAndFixCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Empty(questions);
            Assert.Equal(1, hanging.Calls); // no second scan was started
        }
        finally
        {
            vm.CancelCommand.Execute(null);
            await scan;
        }
    }

    // ----- rows -----

    [Fact]
    public async Task ExpertRow_ShowsEverything_AsItAlwaysDid()
    {
        var vm = await ScannedAsync(UiMode.Expert, NewIssue("Мусор", target: "Optimizer"));
        var row = RowOf(vm, "Мусор");

        Assert.False(row.IsSimple);
        Assert.True(row.ShowBadges);
        Assert.True(row.ShowDetails);
        Assert.True(row.ShowGain);
        Assert.True(row.OpensPageForDetails);
        Assert.True(row.ShowDetailsButton);
        Assert.Equal("Подробнее", row.DetailsText);
        Assert.Equal("Открыть раздел, который нашёл проблему", row.DetailsToolTip);
    }

    [Fact]
    public async Task ExpertRow_WithoutAPage_HasNoDetailsButton()
    {
        var vm = await ScannedAsync(UiMode.Expert, NewIssue("Мусор", target: null));

        Assert.False(RowOf(vm, "Мусор").ShowDetailsButton);
    }

    [Fact]
    public async Task SimpleRow_ShowsTheTitleAndTheButtons_AndKeepsTheRestBehindDetails()
    {
        var vm = await ScannedAsync(UiMode.Simple, NewIssue("Мусор", Counting(new Counter()), target: "Cleaner"));
        var row = RowOf(vm, "Мусор");

        Assert.True(row.IsSimple);
        Assert.False(row.ShowBadges); // module, severity and «Безопасно исправить»
        Assert.False(row.ShowDetails);
        Assert.False(row.ShowGain);
        Assert.True(row.Issue.CanFix); // the fix button (FixLabel) stays
        Assert.True(row.ShowDetailsButton);
        Assert.Equal("Подробнее", row.DetailsText);
    }

    [Fact]
    public async Task SimpleRow_OfAVisiblePage_OpensThatPage_NotTheDetails()
    {
        var vm = await ScannedAsync(UiMode.Simple, NewIssue("Мусор", target: "Cleaner"), NewIssue("Программа", target: "Programs"));
        var navigator = new RecordingNavigator();
        var before = AppServices.Navigator;
        AppServices.Navigator = navigator;
        try
        {
            foreach (var title in new[] { "Мусор", "Программа" })
            {
                var row = RowOf(vm, title);
                Assert.True(row.OpensPageForDetails);
                Assert.NotNull(row.DetailsToolTip);
                row.DetailsCommand.Execute(null);
                Assert.False(row.IsExpanded);
                Assert.False(row.ShowDetails);
            }
        }
        finally
        {
            AppServices.Navigator = before;
        }

        Assert.Equal(new[] { "Cleaner", "Programs" }, navigator.Opened);
    }

    [Fact]
    public async Task SimpleRow_OfAHiddenPage_ExpandsTheDetailsInPlace_InsteadOfAskingForTheExpertMode()
    {
        var vm = await ScannedAsync(UiMode.Simple, NewIssue("Медленная сеть", target: "Network"));
        var row = RowOf(vm, "Медленная сеть");
        var navigator = new RecordingNavigator();
        var before = AppServices.Navigator;
        AppServices.Navigator = navigator;
        var shown = new List<string?>();
        row.PropertyChanged += (_, e) => shown.Add(e.PropertyName);
        try
        {
            Assert.False(row.OpensPageForDetails);
            Assert.True(row.ShowDetailsButton);
            Assert.Null(row.DetailsToolTip);

            row.DetailsCommand.Execute(null);

            Assert.True(row.IsExpanded);
            Assert.True(row.ShowDetails);
            Assert.True(row.ShowGain);
            Assert.Equal("Свернуть", row.DetailsText);
            Assert.Contains(nameof(IssueItem.ShowDetails), shown);
            Assert.Contains(nameof(IssueItem.ShowGain), shown);
            Assert.Contains(nameof(IssueItem.DetailsText), shown);

            row.DetailsCommand.Execute(null);

            Assert.False(row.IsExpanded);
            Assert.False(row.ShowDetails);
            Assert.Equal("Подробнее", row.DetailsText);
        }
        finally
        {
            AppServices.Navigator = before;
        }

        Assert.Empty(navigator.Opened);
    }

    [Fact]
    public async Task SimpleRow_WithoutAPage_ButWithDetails_AlsoExpandsInPlace()
    {
        var vm = await ScannedAsync(UiMode.Simple, NewIssue("Совет", target: null), NewIssue("Пусто", target: null, details: "", gain: null));

        var advice = RowOf(vm, "Совет");
        Assert.True(advice.ShowDetailsButton);
        advice.DetailsCommand.Execute(null);
        Assert.True(advice.ShowDetails);

        Assert.False(RowOf(vm, "Пусто").ShowDetailsButton); // nothing more to show
    }

    [Fact]
    public async Task SimpleRow_ThatOpensAPage_AndHasNoDetails_StillHasTheButton()
    {
        var vm = await ScannedAsync(UiMode.Simple, NewIssue("Мусор", target: "Cleaner", details: "", gain: null));

        Assert.True(RowOf(vm, "Мусор").ShowDetailsButton);
    }

    [Fact]
    public async Task ARowOpenedInTheSimpleMode_IsClosedAgain_WhenTheModeChanges()
    {
        var settings = new AppSettings { UiMode = UiMode.Simple };
        var vm = Create(settings, new FakeContributor("Очистка", new[] { NewIssue("Медленная сеть", target: "Network") }));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var row = RowOf(vm, "Медленная сеть");
        row.DetailsCommand.Execute(null);
        Assert.True(row.IsExpanded);
        Assert.Equal("Свернуть", row.DetailsText);
        var shown = new List<string?>();
        row.PropertyChanged += (_, e) => shown.Add(e.PropertyName);

        settings.UiMode = UiMode.Expert;
        vm.OnModeChanged();

        Assert.False(row.IsExpanded); // not left open for the next time the simple mode is chosen
        Assert.Contains(nameof(IssueItem.IsExpanded), shown);

        settings.UiMode = UiMode.Simple;
        vm.OnModeChanged();

        Assert.False(row.IsExpanded);
        Assert.False(row.ShowDetails);
        Assert.Equal("Подробнее", row.DetailsText);
    }

    [Fact]
    public async Task TheRows_FollowTheMode_AlsoTheOnesBehindShowMore()
    {
        var issues = Enumerable.Range(1, IssueGroup.InitialVisible + 3).Select(i => NewIssue("Проблема " + i, target: "Network")).ToArray();
        var settings = new AppSettings { UiMode = UiMode.Expert };
        var vm = Create(settings, new FakeContributor("Очистка", issues));
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var all = vm.Groups.SelectMany(g => g.AllItems).ToList();
        Assert.Equal(issues.Length, all.Count);
        Assert.All(all, i => Assert.False(i.IsSimple));

        settings.UiMode = UiMode.Simple;
        vm.OnModeChanged();

        Assert.All(all, i =>
        {
            Assert.True(i.IsSimple);
            Assert.False(i.ShowBadges);
            Assert.False(i.ShowDetails);
            Assert.False(i.OpensPageForDetails); // Network is not a page of the simple mode
        });

        settings.UiMode = UiMode.Expert;
        vm.OnModeChanged();

        Assert.All(all, i =>
        {
            Assert.False(i.IsSimple);
            Assert.True(i.ShowDetails);
            Assert.True(i.OpensPageForDetails);
        });
    }

    [Fact]
    public async Task ARowOfANewScan_StartsInTheModeOfTheDashboard()
    {
        var settings = new AppSettings { UiMode = UiMode.Simple };
        var vm = Create(settings, new FakeContributor("Очистка", NewIssue("Мусор")));

        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.True(RowOf(vm, "Мусор").IsSimple);
    }

    [Fact]
    public async Task TheContextMenuEntry_OpensOnlyPagesTheModeHas()
    {
        var vm = await ScannedAsync(UiMode.Simple, NewIssue("Мусор", target: "Cleaner"), NewIssue("Сеть", target: "Network"));

        Assert.True(RowOf(vm, "Мусор").OpenPageCommand!.CanExecute(null));
        Assert.False(RowOf(vm, "Сеть").OpenPageCommand!.CanExecute(null));
    }
}

/// <summary>The page header of the shell: one line with the whole text in the tooltip, in the simple mode.</summary>
[Collection(ModeSequentialCollection.Name)]
public class SimpleModeShellTests
{
    public SimpleModeShellTests() => TestEnvironment.Init();

    [Fact]
    public void TheShell_KnowsTheSimpleMode_AndFollowsTheSwitch()
    {
        var settings = new AppSettings { UiMode = UiMode.Expert };
        var pages = new[] { "Dashboard", "Cleaner", "Network" }.Select(k => new FakePage(k)).ToList();
        var main = new MainViewModel(pages, () => settings, _ => { });
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(main.IsSimpleMode);

        UiModes.Choose(settings, UiMode.Simple, _ => { });

        Assert.True(main.IsSimpleMode);
        Assert.Contains(nameof(MainViewModel.IsSimpleMode), changed);

        UiModes.Choose(settings, UiMode.Expert, _ => { });

        Assert.False(main.IsSimpleMode);
    }

    [Fact]
    public void TheShell_StartsInTheSimpleMode_WhenItWasChosen()
        => Assert.True(new MainViewModel(new[] { new FakePage("Dashboard") }, () => new AppSettings { UiMode = UiMode.Simple }, _ => { }).IsSimpleMode);
}

/// <summary>A binding that names no property fails silently in WPF: the row would just stay empty. This catches a typo without a window.</summary>
public class SimpleModeViewTests
{
    private static readonly Regex Binding = new(@"\{Binding\s+(?:Path=)?(?<path>[A-Za-z_]\w*)(?<rest>[^}]*)\}", RegexOptions.Compiled);

    private static string XamlOf(params string[] path)
    {
        var text = File.ReadAllText(Path.Combine(new[] { LocalizationCatalogTests.SourceRoot() }.Concat(path).ToArray()));
        return Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);
    }

    private static List<string> UnknownBindings(string xaml, IEnumerable<Type> types)
    {
        var known = types.SelectMany(t => t.GetProperties()).Select(p => p.Name).ToHashSet();
        return Binding.Matches(xaml)
            .Where(m => !m.Groups["rest"].Value.Contains("ElementName") && !m.Groups["rest"].Value.Contains("RelativeSource"))
            .Select(m => m.Groups["path"].Value)
            .Where(name => !known.Contains(name))
            .Distinct()
            .ToList();
    }

    [Fact]
    public void EveryBindingOfTheDashboardView_NamesAPropertyOfItsViewModels()
    {
        var types = new[]
        {
            typeof(DashboardViewModel), typeof(IssueItem), typeof(IssueGroup), typeof(ModuleScanRow), typeof(SeverityChip), typeof(QuickLink),
        };

        Assert.Empty(UnknownBindings(XamlOf("Modules", "Dashboard", "DashboardView.xaml"), types));
    }

    [Fact]
    public void TheDashboardView_UsesTheModeProperties()
    {
        var xaml = XamlOf("Modules", "Dashboard", "DashboardView.xaml");

        foreach (var name in new[]
                 {
                     "MainScanCommand", "ScanButtonText", "ScanButtonToolTip", "ScanHintText", "IntroText", "IsExpertMode", "ShowBadges", "ShowDetails",
                     "ShowGain", "ShowDetailsButton", "DetailsText", "DetailsCommand", "DetailsToolTip", "OpensPageForDetails",
                 })
            Assert.Contains("{Binding " + name, xaml);
    }

    [Fact]
    public void TheShellHeader_TrimsTheSubtitleToOneLine_InTheSimpleMode()
    {
        var xaml = XamlOf("MainWindow.xaml");

        Assert.Contains("{Binding IsSimpleMode}", xaml);
        Assert.Contains("CharacterEllipsis", xaml);
        Assert.Contains("<Setter Property=\"ToolTip\" Value=\"{Binding SelectedPage.Subtitle}\"", xaml);
        Assert.NotNull(typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsSimpleMode)));
    }
}
