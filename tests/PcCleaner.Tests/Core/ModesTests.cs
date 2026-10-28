using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Settings;
using PcCleaner.Tests.Dashboard;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// Tests that choose a mode through <see cref="UiModes.Choose"/> raise the static <see cref="UiModes.Changed"/>, which every live
/// <see cref="MainViewModel"/> hears. They run alone, after the parallel collections, so two threads never refresh one shell.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModeSequentialCollection
{
    public const string Name = "Modes (sequential)";
}

/// <summary>A page that only has a key: the shell does not care what is inside.</summary>
internal sealed class FakePage : PageViewModel
{
    private readonly string _key;

    public FakePage(string key) => _key = key;

    public override string Key => _key;

    public override string Title => "Раздел " + _key;

    public override string Subtitle => "";

    public override string IconGlyph => "";

    public int ModeChanges { get; private set; }

    public override void OnModeChanged() => ModeChanges++;

    public void SetBusy(bool busy, string text = "") => (IsBusy, BusyText) = (busy, text);
}

/// <summary>What does not touch the shell or the mode event: pure rules, settings file, command line, screenshot choice.</summary>
public class ModesRulesTests
{
    private static readonly string[] AllKeys =
    {
        "Dashboard", "Cleaner", "Hardware", "Optimizer", "Processes", "Startup", "Programs", "Disk", "Security", "Network",
        "Profile", "Settings",
    };

    [Fact]
    public void SimpleMode_ShowsDashboardCleanerProgramsProfileAndSettings_InThatOrder()
    {
        var shown = AllKeys.Where(k => UiModes.IsVisible(UiMode.Simple, k)).ToList();

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }, shown);
        Assert.Equal(shown, UiModes.SimplePageKeys);
    }

    [Fact]
    public void ExpertMode_ShowsEveryPage()
        => Assert.All(AllKeys, k => Assert.True(UiModes.IsVisible(UiMode.Expert, k), k));

    [Fact]
    public void IsVisible_IgnoresTheCaseOfTheKey()
    {
        Assert.True(UiModes.IsVisible(UiMode.Simple, "cleaner"));
        Assert.False(UiModes.IsVisible(UiMode.Simple, "NETWORK"));
        Assert.False(UiModes.IsVisible(UiMode.Simple, ""));
    }

    [Fact]
    public void NoModeChosenYet_MeansEverythingIsShown()
    {
        Assert.Equal(UiMode.Expert, UiModes.Effective(null));
        Assert.Equal(UiMode.Simple, UiModes.Effective(UiMode.Simple));
        Assert.Equal(UiMode.Expert, UiModes.Of(new AppSettings()));
        Assert.Equal(UiMode.Simple, UiModes.Of(new AppSettings { UiMode = UiMode.Simple }));
    }

    [Theory]
    [InlineData("simple", UiMode.Simple)]
    [InlineData("Expert", UiMode.Expert)]
    [InlineData(" EXPERT ", UiMode.Expert)]
    [InlineData("tiny", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryParse_AcceptsOnlyTheTwoNames(string? text, UiMode? expected)
        => Assert.Equal(expected, UiModes.TryParse(text));

    // ----- settings.json -----

    [Fact]
    public void Settings_ModeIsNotChosenByDefault_AndIsSavedAsText()
    {
        Assert.Null(new AppSettings().UiMode);

        var json = JsonSerializer.Serialize(new AppSettings { UiMode = UiMode.Simple });
        Assert.Contains("\"UiMode\":\"simple\"", json);
        Assert.Equal(UiMode.Simple, JsonSerializer.Deserialize<AppSettings>(json)!.UiMode);

        json = JsonSerializer.Serialize(new AppSettings { UiMode = UiMode.Expert });
        Assert.Equal(UiMode.Expert, JsonSerializer.Deserialize<AppSettings>(json)!.UiMode);

        json = JsonSerializer.Serialize(new AppSettings());
        Assert.Contains("\"UiMode\":null", json);
        Assert.Null(JsonSerializer.Deserialize<AppSettings>(json)!.UiMode);
    }

    [Theory]
    [InlineData("\"tiny\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{ \"a\": [1, 2] }")]
    [InlineData("[1, 2]")]
    public void Settings_AnUnknownModeInTheFile_IsIgnored_AndNothingElseIsLost(string value)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>($"{{ \"UiMode\": {value}, \"CleanerMinFileAgeHours\": 12 }}")!;

        Assert.Null(settings.UiMode);
        Assert.Equal(12, settings.CleanerMinFileAgeHours);
    }

    [Fact]
    public void Sanitize_ResetsAModeThatIsNeitherOfTheTwo()
    {
        var settings = new AppSettings { UiMode = (UiMode)7 };

        Assert.Equal(1, SettingsLogic.Sanitize(settings));
        Assert.Null(settings.UiMode);
        Assert.Equal(0, SettingsLogic.Sanitize(settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UiMode.Simple)]
    [InlineData(UiMode.Expert)]
    public void Sanitize_KeepsAChosenMode(UiMode? mode)
    {
        var settings = new AppSettings { UiMode = mode };

        Assert.Equal(0, SettingsLogic.Sanitize(settings));
        Assert.Equal(mode, settings.UiMode);
    }

    [Fact]
    public void ResetSettings_KeepsTheModeAndTheLanguage()
    {
        var settings = new AppSettings { UiMode = UiMode.Simple, Language = "en", CleanerMinFileAgeHours = 72, AccountIntroShown = true };

        SettingsLogic.ResetToDefaults(settings);

        Assert.Equal(UiMode.Simple, settings.UiMode);
        Assert.True(settings.AccountIntroShown);
        Assert.Equal("en", settings.Language);
        Assert.Equal(24, settings.CleanerMinFileAgeHours);
    }

    // ----- command line (App.ParseArgs itself is tested in ParseArgsTests) -----

    [Theory]
    [InlineData(UiMode.Simple, null, true, UiMode.Simple)]    // screenshot run: the argument wins over the saved setting
    [InlineData(UiMode.Expert, UiMode.Simple, true, UiMode.Expert)]
    [InlineData(null, UiMode.Simple, true, UiMode.Simple)]
    [InlineData(UiMode.Expert, UiMode.Simple, false, UiMode.Simple)] // a real run never takes it: it would be saved with the next change
    [InlineData(UiMode.Simple, null, false, null)]
    public void StartupMode_IsTheArgumentOnlyInAScreenshotRun(UiMode? argument, UiMode? setting, bool screenshotRun, UiMode? expected)
        => Assert.Equal(expected, App.StartupMode(argument, setting, screenshotRun));

    // ----- restart: the same arguments, minus what belongs to one start only -----

    [Fact]
    public void RestartArgs_KeepTheOrdinaryArguments_AndMarkTheRestart()
        => Assert.Equal(new[] { "--page", "Cleaner", "--no-scan", "--elevation-declined", "--restarted" },
            AppRestart.Args(new[] { "--page", "Cleaner", "--no-scan", "--elevation-declined" }));

    [Fact]
    public void RestartArgs_DropTheMarkersOfOneStart()
        => Assert.Equal(new[] { "--page", "Cleaner", "--restarted" },
            AppRestart.Args(new[] { "--updated-from", "1.2.3", "--page", "Cleaner", "--elevated-restart", "--from-task", "--restarted" }));

    [Fact]
    public void RestartArgs_DropTheLanguageAndTheModeOfThatRun_SoThatTheSavedLanguageWins()
        => Assert.Equal(new[] { "--no-scan", "--restarted" },
            AppRestart.Args(new[] { "--lang", "en", "--no-scan", "--MODE", "simple" }));

    [Fact]
    public void Restart_StartsTheSameExeWithThoseArguments_AndClosesThisCopy()
    {
        var starts = new List<(string Exe, string[] Args)>();
        var shutdowns = 0;

        var started = AppRestart.Restart(@"C:\App\WaterCa7sPcCleaner.exe", new[] { "--page", "Cleaner", "--elevated-restart" },
            (exe, args) =>
            {
                starts.Add((exe, args.ToArray()));
                return true;
            },
            () => shutdowns++);

        Assert.True(started);
        var (exe, args) = Assert.Single(starts);
        Assert.Equal(@"C:\App\WaterCa7sPcCleaner.exe", exe);
        Assert.Equal(new[] { "--page", "Cleaner", "--restarted" }, args); // the new copy waits until this one has closed
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public void Restart_WhenTheNewCopyDoesNotStart_ThisOneKeepsRunning()
    {
        var shutdowns = 0;

        Assert.False(AppRestart.Restart(@"C:\App\a.exe", Array.Empty<string>(), (_, _) => false, () => shutdowns++));
        Assert.False(AppRestart.Restart(null, Array.Empty<string>(), (_, _) => true, () => shutdowns++));
        Assert.False(AppRestart.Restart("", Array.Empty<string>(), (_, _) => true, () => shutdowns++));

        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public void ARestartThatCannotStart_IsLoggedAsARestartProblem_AnUpdateThatCannotStart_AsAnUpdateProblem()
    {
        TestEnvironment.Init();
        var marker = Guid.NewGuid().ToString("N");
        var exe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-" + marker, "WaterCa7sPcCleaner.exe"); // its folder does not exist

        Assert.False(AppRestart.Start(exe, Array.Empty<string>()));
        Assert.False(UpdateEnvironment.Start(exe, Array.Empty<string>()));

        var lines = Log.Entries.ToList().Where(e => e.Level == LogLevel.Warning && e.Message.Contains(marker)).Select(e => e.Message).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Single(lines, l => l.StartsWith("Перезапуск: не удалось запустить " + exe + ": ", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith("Обновление: не удалось запустить " + exe + ": ", StringComparison.Ordinal));
    }

    // ----- screenshot mode -----

    private static List<FakePage> Pages(params string[] keys) => keys.Select(k => new FakePage(k)).ToList();

    private static readonly string[] Twelve = ShellMenuTests.AllKeys;

    [Fact]
    public void Screenshots_OfExpertMode_ShootEveryTab_NumberedByPlace()
    {
        var all = Pages(Twelve);
        var sections = ShellMenu.Build(all);

        var shots = ScreenshotRunner.PagesToShoot(all, all, sections, null);

        Assert.Equal(Enumerable.Range(1, 12), shots.Select(s => s.Number));
        Assert.Equal(Twelve, shots.Select(s => s.Page.Key));
    }

    [Fact]
    public void Screenshots_AreNamedBySectionAndTab_AndASingleTabSectionByItsKey()
    {
        var all = Pages(Twelve);
        var sections = ShellMenu.Build(all);

        Assert.Equal("01-Dashboard.png", ScreenshotRunner.FileName(1, all[0], sections));
        Assert.Equal("03-Computer-Hardware.png", ScreenshotRunner.FileName(3, all[2], sections));
        Assert.Equal("08-System-Disk.png", ScreenshotRunner.FileName(8, all[7], sections));
        Assert.Equal("12-Settings.png", ScreenshotRunner.FileName(12, all[11], sections));

        // The log names a shot like its file: «Section/Tab», or the key of a section of one page.
        Assert.Equal("Dashboard", ScreenshotRunner.ShotName(all[0], sections));
        Assert.Equal("Computer/Hardware", ScreenshotRunner.ShotName(all[2], sections));
        Assert.Equal("System/Disk", ScreenshotRunner.ShotName(all[7], sections));
        Assert.Equal("Settings", ScreenshotRunner.ShotName(all[11], sections));
    }

    [Fact]
    public void Screenshots_OfSimpleMode_SkipHiddenTabs_ButKeepTheNumbersOfTheFullList()
    {
        var all = Pages(Twelve);
        var visible = all.Where(p => UiModes.IsVisible(UiMode.Simple, p.Key)).ToList();

        var shots = ScreenshotRunner.PagesToShoot(all, visible, ShellMenu.Build(all), null);

        Assert.Equal(new[] { 1, 2, 7, 11, 12 }, shots.Select(s => s.Number));
        Assert.Equal(new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }, shots.Select(s => s.Page.Key));
    }

    [Theory]
    [InlineData("System", new[] { "Processes", "Startup", "Programs", "Disk" })]
    [InlineData("system/disk", new[] { "Disk" })]
    [InlineData("Disk", new[] { "Disk" })]
    [InlineData("Computer/Optimizer,Settings", new[] { "Optimizer", "Settings" })]
    [InlineData("System/Network", new string[0])] // Сеть is a tab of Защита, not of Система
    public void Screenshots_ARequest_NamesAPage_ASection_OrASectionTab(string request, string[] expected)
    {
        var all = Pages(Twelve);

        var shots = ScreenshotRunner.PagesToShoot(all, all, ShellMenu.Build(all), request.Split(','));

        Assert.Equal(expected, shots.Select(s => s.Page.Key));
    }

    [Fact]
    public void Screenshots_WithARequest_ShootOnlyTheVisibleTabsAmongThem_AndNameTheHiddenOnes()
    {
        var all = Pages(Twelve);
        var visible = all.Where(p => UiModes.IsVisible(UiMode.Simple, p.Key)).ToList();
        var sections = ShellMenu.Build(all);
        var requested = new[] { "settings", "Protection/Network", "Computer", "Dashboard", "Nowhere" };

        var shots = ScreenshotRunner.PagesToShoot(all, visible, sections, requested);
        var hidden = ScreenshotRunner.HiddenRequested(all, visible, sections, requested);

        Assert.Equal(new[] { (1, "Dashboard"), (12, "Settings") }, shots.Select(s => (s.Number, s.Page.Key)));
        Assert.Equal(new[] { "Protection/Network", "Computer" }, hidden); // an unknown key is not "hidden"
    }
}

/// <summary>The shell, the Settings page and the Dashboard follow the mode.</summary>
[Collection(ModeSequentialCollection.Name)]
public class ModesShellTests
{
    // Every page of the menu, in its order (ShellMenu).
    private static readonly string[] All = ShellMenuTests.AllKeys;

    private readonly AppSettings _settings = new();
    private readonly List<FakePage> _pages = All.Select(k => new FakePage(k)).ToList();
    private readonly List<(string Title, string Message)> _questions = new();
    private bool _answer;
    private int _saves;

    public ModesShellTests() => TestEnvironment.Init();

    private MainViewModel Create(UiMode? mode, bool answer = false)
    {
        _settings.UiMode = mode;
        _answer = answer;
        return new MainViewModel(_pages, () => _settings, _ => _saves++)
        {
            Ask = (title, message) =>
            {
                _questions.Add((title, message));
                return _answer;
            },
        };
    }

    private static string[] Keys(IEnumerable<PageViewModel> pages) => pages.Select(p => p.Key).ToArray();

    [Fact]
    public void ExpertMode_ShowsAllPages()
    {
        var main = Create(UiMode.Expert);

        Assert.Equal(All, Keys(main.VisiblePages));
        Assert.Equal(All, Keys(main.Pages));
    }

    [Fact]
    public void NoModeChosen_ShowsAllPages()
        => Assert.Equal(All, Keys(Create(null).VisiblePages));

    [Fact]
    public void SimpleMode_ShowsFivePages_ButKeepsAllPagesInTheFullList()
    {
        var main = Create(UiMode.Simple);

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }, Keys(main.VisiblePages));
        // Pages stays whole: screenshots, the busy check of a restart and the Network page use it, and the hidden pages
        // go on taking part in the Dashboard check (they are IHealthContributors of AppServices, not sidebar items).
        Assert.Equal(All, Keys(main.Pages));
        Assert.Equal(All, Keys(((INavigator)main).Pages));
        Assert.Same(main.Pages[0], main.SelectedPage);
    }

    [Fact]
    public void ATestShell_DoesNotBecomeTheAppNavigator()
    {
        var before = AppServices.Navigator;

        var main = Create(UiMode.Simple);

        Assert.Same(before, AppServices.Navigator);
        Assert.NotSame(main, AppServices.Navigator);
    }

    [Fact]
    public void NavigateTo_ForAVisiblePageOfSimpleMode_JustGoesThere()
    {
        var main = Create(UiMode.Simple);

        main.NavigateTo("Programs");

        Assert.Empty(_questions);
        Assert.Equal("Programs", main.SelectedPage!.Key);
        Assert.Equal(UiMode.Simple, _settings.UiMode);
    }

    [Fact]
    public void NavigateTo_ForAHiddenPage_AsksTheQuestionOfTheBrief_AndStaysWhenTheAnswerIsNo()
    {
        var main = Create(UiMode.Simple, answer: false);

        main.NavigateTo("Network");

        var (title, message) = Assert.Single(_questions);
        Assert.Equal("Раздел Network", title);
        Assert.Equal("Этот раздел есть в опытном режиме. Переключиться на опытный режим?", message);
        Assert.Equal("Dashboard", main.SelectedPage!.Key);
        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(0, _saves);
        Assert.Equal(5, main.VisiblePages.Count);
    }

    [Fact]
    public void NavigateTo_ForAHiddenPage_SwitchesToExpertModeAndGoesThere_WhenTheAnswerIsYes()
    {
        var main = Create(UiMode.Simple, answer: true);

        main.NavigateTo("network"); // the key is matched without regard to case, as before

        Assert.Single(_questions);
        Assert.Equal(UiMode.Expert, _settings.UiMode);
        Assert.Equal(1, _saves);
        Assert.Equal(All, Keys(main.VisiblePages));
        Assert.Equal("Network", main.SelectedPage!.Key);
    }

    [Fact]
    public void NavigateTo_InExpertMode_NeverAsks()
    {
        var main = Create(UiMode.Expert);

        main.NavigateTo("Security");

        Assert.Empty(_questions);
        Assert.Equal("Security", main.SelectedPage!.Key);
    }

    [Fact]
    public void NavigateTo_AnUnknownKey_DoesNothing()
    {
        var main = Create(UiMode.Simple, answer: true);

        main.NavigateTo("Nowhere");
        main.NavigateTo("");

        Assert.Empty(_questions);
        Assert.Equal("Dashboard", main.SelectedPage!.Key);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void ChangingTheMode_UpdatesTheSidebarAtOnce_AndTellsEveryPage()
    {
        var main = Create(UiMode.Simple);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)main.VisiblePages).CollectionChanged += (_, e) => changes.Add(e.Action);

        UiModes.Choose(_settings, UiMode.Expert, _ => { });

        Assert.Equal(All, Keys(main.VisiblePages));
        Assert.NotEmpty(changes);
        Assert.All(_pages, p => Assert.Equal(1, p.ModeChanges));

        UiModes.Choose(_settings, UiMode.Simple, _ => { });

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Programs", "Profile", "Settings" }, Keys(main.VisiblePages));
        Assert.All(_pages, p => Assert.Equal(2, p.ModeChanges));
    }

    [Fact]
    public void WhenTheOpenPageGetsHidden_TheDashboardIsOpened()
    {
        var main = Create(UiMode.Expert);
        main.NavigateTo("Network");
        Assert.Equal("Network", main.SelectedPage!.Key);

        UiModes.Choose(_settings, UiMode.Simple, _ => { });

        Assert.Equal("Dashboard", main.SelectedPage!.Key);
    }

    [Fact]
    public void WhenTheOpenPageStaysVisible_TheSelectionIsKept()
    {
        var main = Create(UiMode.Expert);
        main.NavigateTo("Programs");

        UiModes.Choose(_settings, UiMode.Simple, _ => { });

        Assert.Equal("Programs", main.SelectedPage!.Key);
    }

    [Fact]
    public void AnotherShellsMode_IsNotThisShellsBusiness()
    {
        var main = Create(UiMode.Simple);
        var other = new AppSettings();

        UiModes.Choose(other, UiMode.Expert, _ => { });

        Assert.Equal(5, main.VisiblePages.Count);
        Assert.All(_pages, p => Assert.Equal(0, p.ModeChanges));
    }

    [Fact]
    public async Task TheDashboardScan_StillAsksTheModulesOfTheHiddenPages_InSimpleMode()
    {
        var contributors = new[] { new FakeContributor("Очистка"), new FakeContributor("Сеть"), new FakeContributor("Безопасность") };
        var vm = new DashboardViewModel(contributors, new AppSettings { UiMode = UiMode.Simple });

        await vm.ScanCoreAsync(CancellationToken.None, persist: false);

        Assert.All(contributors, c => Assert.Equal(1, c.Calls));
        // The real list does not depend on the sidebar either: the modules of the pages the simple mode hides are in it.
        var real = AppServices.HealthContributors;
        var ofHiddenPages = new IHealthContributor[]
        {
            AppServices.Processes, AppServices.Startup, AppServices.Disk, AppServices.Optimizer, AppServices.Network, AppServices.Security,
        };
        Assert.All(ofHiddenPages, c => Assert.Contains(c, real));
    }

    // ----- a hidden page is reached only through the question -----

    [Fact]
    public void TheSelectedPageSetter_IgnoresAHiddenPage()
    {
        var main = Create(UiMode.Simple, answer: true);

        main.SelectedPage = _pages.Single(p => p.Key == "Network");

        Assert.Equal("Dashboard", main.SelectedPage!.Key);
        Assert.Empty(_questions); // a binding write is not the user asking for the page: no question either
        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void TheSelectedPageSetter_StillOpensAVisiblePage()
    {
        var main = Create(UiMode.Simple);

        main.SelectedPage = _pages.Single(p => p.Key == "Programs");

        Assert.Equal("Programs", main.SelectedPage!.Key);
        Assert.Empty(_questions);
    }

    [Fact]
    public async Task NavigateAsync_OfAHiddenPage_AsksTheSameQuestion_AndStaysWhenTheAnswerIsNo()
    {
        var main = Create(UiMode.Simple, answer: false);

        await main.NavigateAsync(_pages.Single(p => p.Key == "Security"));

        var (title, message) = Assert.Single(_questions);
        Assert.Equal("Раздел Security", title);
        Assert.Equal("Этот раздел есть в опытном режиме. Переключиться на опытный режим?", message);
        Assert.Equal("Dashboard", main.SelectedPage!.Key);
        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(5, main.VisiblePages.Count);
    }

    [Fact]
    public async Task NavigateAsync_OfAHiddenPage_SwitchesToExpertModeAndOpensIt_WhenTheAnswerIsYes()
    {
        var main = Create(UiMode.Simple, answer: true);

        await main.NavigateAsync(_pages.Single(p => p.Key == "Security"));

        Assert.Single(_questions);
        Assert.Equal(UiMode.Expert, _settings.UiMode);
        Assert.Equal(All, Keys(main.VisiblePages));
        Assert.Equal("Security", main.SelectedPage!.Key);
    }

    [Fact]
    public async Task NavigateAsync_OfAPageThatIsNotOneOfThePages_Refuses()
    {
        var main = Create(UiMode.Simple, answer: true);

        await main.NavigateAsync(new FakePage("Stranger"));

        Assert.Empty(_questions);
        Assert.Equal("Dashboard", main.SelectedPage!.Key);
        Assert.Equal(UiMode.Simple, _settings.UiMode);
    }

    [Fact]
    public void TheShellStartsOnTheFirstVisiblePage()
    {
        var pages = new[] { "Network", "Dashboard", "Programs" }.Select(k => new FakePage(k)).ToList();

        var main = new MainViewModel(pages, () => new AppSettings { UiMode = UiMode.Simple }, _ => { });

        Assert.Equal("Dashboard", main.SelectedPage!.Key); // not Pages[0]: that one is hidden in this mode
    }

    // ----- the Dashboard reaches hidden pages through the shell's question -----

    [Fact]
    public void TheDashboardsLinksAndIssueButtons_GoThroughTheShell_SoTheyAsk()
    {
        var main = Create(UiMode.Simple, answer: true);
        var dashboard = new DashboardViewModel(Array.Empty<IHealthContributor>(), _settings);
        var before = AppServices.Navigator;
        AppServices.Navigator = main;
        try
        {
            dashboard.NavigateCommand.Execute("Startup");
        }
        finally
        {
            AppServices.Navigator = before;
        }

        var (_, message) = Assert.Single(_questions);
        Assert.Equal("Этот раздел есть в опытном режиме. Переключиться на опытный режим?", message);
        Assert.Equal("Startup", main.SelectedPage!.Key);
        Assert.Equal(UiMode.Expert, _settings.UiMode);
    }

    // ----- UiModes.Choose -----

    [Fact]
    public void Choose_StoresSavesLogsAndAnnounces()
    {
        var settings = new AppSettings();
        var saved = new List<UiMode?>();
        var announced = 0;
        void OnChanged() => announced++;
        var logged = new List<string>();
        void OnLog(LogEntry e) => logged.Add(e.Message);
        UiModes.Changed += OnChanged;
        Log.EntryAdded += OnLog;
        try
        {
            Assert.True(UiModes.Choose(settings, UiMode.Simple, s => saved.Add(s.UiMode)));
        }
        finally
        {
            UiModes.Changed -= OnChanged;
            Log.EntryAdded -= OnLog;
        }

        Assert.Equal(UiMode.Simple, settings.UiMode);
        Assert.Equal(new UiMode?[] { UiMode.Simple }, saved);
        Assert.Equal(1, announced);
        Assert.Contains("Настройки: режим — простой", logged);
    }

    [Fact]
    public void Choose_TheModeAlreadyChosen_ChangesNothing()
    {
        var settings = new AppSettings { UiMode = UiMode.Expert };
        var saves = 0;
        var announced = 0;
        void OnChanged() => announced++;
        UiModes.Changed += OnChanged;
        try
        {
            Assert.False(UiModes.Choose(settings, UiMode.Expert, _ => saves++));
        }
        finally
        {
            UiModes.Changed -= OnChanged;
        }

        Assert.Equal(0, saves);
        Assert.Equal(0, announced);
    }

    [Fact]
    public void Choose_ExpertWhenNothingWasChosen_IsRecorded()
    {
        var settings = new AppSettings();
        var saves = 0;

        Assert.True(UiModes.Choose(settings, UiMode.Expert, _ => saves++)); // "not chosen" shows everything, but the choice is the user's

        Assert.Equal(UiMode.Expert, settings.UiMode);
        Assert.Equal(1, saves);
    }

    // ----- Dashboard quick links -----

    private static DashboardViewModel Dashboard(UiMode? mode)
        => new(Array.Empty<IHealthContributor>(), new AppSettings { UiMode = mode });

    [Theory]
    [InlineData(null)]
    [InlineData(UiMode.Expert)]
    public void QuickLinks_InExpertMode_AreAllSeven(UiMode? mode)
    {
        var vm = Dashboard(mode);

        Assert.Equal(7, vm.QuickLinks.Count);
        Assert.Equal("Cleaner", vm.FeaturedLink.Key);
        Assert.Equal(6, vm.MoreLinks.Count);
    }

    [Fact]
    public void QuickLinks_InSimpleMode_AreOnlyThePagesOfSimpleMode()
    {
        var vm = Dashboard(UiMode.Simple);

        Assert.Equal(new[] { "Cleaner", "Programs" }, vm.QuickLinks.Select(l => l.Key));
        Assert.Equal("Cleaner", vm.FeaturedLink.Key);
        Assert.Equal(new[] { "Programs" }, vm.MoreLinks.Select(l => l.Key));
    }

    [Fact]
    public void QuickLinks_FollowTheMode_WhenTheShellSaysItChanged()
    {
        var settings = new AppSettings { UiMode = UiMode.Expert };
        var vm = new DashboardViewModel(Array.Empty<IHealthContributor>(), settings);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.OnModeChanged(); // nothing changed: no noise
        Assert.Empty(changed);

        settings.UiMode = UiMode.Simple;
        vm.OnModeChanged();

        Assert.Equal(2, vm.QuickLinks.Count);
        Assert.Contains(nameof(DashboardViewModel.QuickLinks), changed);
        Assert.Contains(nameof(DashboardViewModel.FeaturedLink), changed);
        Assert.Contains(nameof(DashboardViewModel.MoreLinks), changed);

        settings.UiMode = UiMode.Expert;
        vm.OnModeChanged();

        Assert.Equal(7, vm.QuickLinks.Count);
    }
}

/// <summary>The «Режим» and «Язык» cards of Настройки.</summary>
[Collection(ModeSequentialCollection.Name)]
public class ModesSettingsTests
{
    private readonly AppSettings _settings = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private int _saves;

    public ModesSettingsTests() => TestEnvironment.Init();

    private SettingsViewModel Create() => new(() => _settings, _ => _saves++, _log);

    [Fact]
    public void WithoutAChoice_ExpertIsShown_AndNothingIsSaved()
    {
        var vm = Create();
        vm.LoadGeneral();

        Assert.True(vm.IsExpertMode);
        Assert.False(vm.IsSimpleMode);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void LoadGeneral_ShowsTheSavedMode()
    {
        _settings.UiMode = UiMode.Simple;
        var vm = Create();
        vm.LoadGeneral();

        Assert.True(vm.IsSimpleMode);
        Assert.False(vm.IsExpertMode);

        _settings.UiMode = UiMode.Expert;
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        vm.LoadGeneral();

        Assert.True(vm.IsExpertMode);
        Assert.Contains(nameof(SettingsViewModel.IsSimpleMode), seen);
        Assert.Contains(nameof(SettingsViewModel.IsExpertMode), seen);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void ChoosingAMode_IsSavedAtOnce_AndTheShellIsTold()
    {
        var vm = Create();
        vm.LoadGeneral();
        var announced = 0;
        void OnChanged() => announced++;
        UiModes.Changed += OnChanged;
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        try
        {
            vm.IsSimpleMode = true;
        }
        finally
        {
            UiModes.Changed -= OnChanged;
        }

        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(1, _saves);
        Assert.Equal(1, announced);
        Assert.True(vm.IsSimpleMode);
        Assert.False(vm.IsExpertMode);
        Assert.Contains(nameof(SettingsViewModel.IsSimpleMode), seen);
        Assert.Contains(nameof(SettingsViewModel.IsExpertMode), seen);

        vm.IsExpertMode = true;

        Assert.Equal(UiMode.Expert, _settings.UiMode);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void TheOptionButtons_UncheckingOne_ChangesNothing()
    {
        _settings.UiMode = UiMode.Simple;
        var vm = Create();
        vm.LoadGeneral();

        // Two radio buttons of one group: choosing one writes "false" into the other.
        vm.IsExpertMode = false;
        vm.IsSimpleMode = true;

        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void ChoosingTheModeThatIsSelected_SavesNothing()
    {
        _settings.UiMode = UiMode.Expert;
        var vm = Create();
        vm.LoadGeneral();

        vm.IsExpertMode = true;

        Assert.Equal(0, _saves);
    }

    [Fact]
    public void TheModeChangeIsWrittenToTheLog()
    {
        var vm = Create();
        var logged = new List<string>();
        void OnLog(LogEntry e) => logged.Add(e.Message);
        Log.EntryAdded += OnLog;
        try
        {
            vm.IsSimpleMode = true;
        }
        finally
        {
            Log.EntryAdded -= OnLog;
        }

        Assert.Contains("Настройки: режим — простой", logged);
    }

    [Fact]
    public void TheRadioButtonsOfTheCards_FollowTheChoiceBothWays()
    {
        // The same bindings as SettingsView.xaml: two RadioButtons of a group, each bound two-way to its own property.
        var vm = Create();
        vm.LoadGeneral();
        UiThread.Run(() =>
        {
            RadioButton Bind(Panel panel, string property)
            {
                var button = new RadioButton { GroupName = "g" + panel.GetHashCode() };
                button.SetBinding(ToggleButton.IsCheckedProperty, new Binding(property) { Source = vm, Mode = BindingMode.TwoWay });
                panel.Children.Add(button);
                return button;
            }

            var modes = new StackPanel();
            var simple = Bind(modes, nameof(SettingsViewModel.IsSimpleMode));
            var expert = Bind(modes, nameof(SettingsViewModel.IsExpertMode));
            var languages = new StackPanel();
            var russian = Bind(languages, nameof(SettingsViewModel.IsRussian));
            var english = Bind(languages, nameof(SettingsViewModel.IsEnglish));

            Assert.True(expert.IsChecked);
            Assert.False(simple.IsChecked);
            Assert.True(russian.IsChecked);

            simple.IsChecked = true; // the user clicks «Простой»

            Assert.Equal(UiMode.Simple, _settings.UiMode);
            Assert.True(simple.IsChecked);
            Assert.False(expert.IsChecked);
            Assert.Equal(1, _saves);

            english.IsChecked = true; // the user clicks «English»

            Assert.Equal("en", _settings.Language);
            Assert.True(english.IsChecked);
            Assert.False(russian.IsChecked);
            Assert.True(vm.LanguageNeedsRestart);
            Assert.Equal(2, _saves);

            expert.IsChecked = true;
            russian.IsChecked = true;

            Assert.Equal(UiMode.Expert, _settings.UiMode);
            Assert.Equal("ru", _settings.Language);
            Assert.False(vm.LanguageNeedsRestart);
            Assert.Equal(4, _saves);
            return Task.CompletedTask;
        });
    }

    // ----- language -----

    [Fact]
    public void WithoutAChoice_TheRunningLanguageIsShown_AndNoRestartIsNeeded()
    {
        var vm = Create();
        vm.LoadGeneral();

        Assert.True(vm.IsRussian);
        Assert.False(vm.IsEnglish);
        Assert.False(vm.LanguageNeedsRestart);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void ChoosingEnglish_IsSavedAtOnce_AndAsksForARestart()
    {
        var vm = Create();
        vm.LoadGeneral();
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        vm.IsEnglish = true;

        Assert.Equal("en", _settings.Language);
        Assert.Equal(1, _saves);
        Assert.True(vm.IsEnglish);
        Assert.False(vm.IsRussian);
        Assert.True(vm.LanguageNeedsRestart);
        Assert.Contains(nameof(SettingsViewModel.LanguageNeedsRestart), seen);
        Assert.Contains(nameof(SettingsViewModel.IsRussian), seen);
        Assert.Contains(nameof(SettingsViewModel.IsEnglish), seen);
        Assert.Equal(UiMode.Expert, UiModes.Of(_settings)); // the language does not touch the mode
    }

    [Fact]
    public void ChoosingTheRunningLanguageAgain_TakesTheRestartBack()
    {
        var vm = Create();
        vm.LoadGeneral();
        vm.IsEnglish = true;

        vm.IsRussian = true;

        Assert.Equal("ru", _settings.Language);
        Assert.Equal(2, _saves);
        Assert.False(vm.LanguageNeedsRestart);
    }

    [Fact]
    public void ALanguageSavedEarlier_IsShown_WithTheRestartStillPending()
    {
        _settings.Language = "en";
        var vm = Create();
        vm.LoadGeneral();

        Assert.True(vm.IsEnglish);
        Assert.True(vm.LanguageNeedsRestart);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void UncheckingALanguage_ChangesNothing()
    {
        var vm = Create();
        vm.LoadGeneral();

        vm.IsEnglish = false;
        vm.IsRussian = false;

        Assert.Null(_settings.Language);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void TheLanguageChangeIsWrittenToTheLog()
    {
        var vm = Create();
        var logged = new List<string>();
        void OnLog(LogEntry e) => logged.Add(e.Message);
        Log.EntryAdded += OnLog;
        try
        {
            vm.IsEnglish = true;
        }
        finally
        {
            Log.EntryAdded -= OnLog;
        }

        Assert.Contains("Настройки: язык — English", logged);
    }

    // ----- «Перезапустить сейчас» -----

    [Fact]
    public void RestartNow_RestartsTheProgram_WhenNothingIsRunning()
    {
        var vm = Create();
        var restarts = 0;
        vm.RestartProgram = () =>
        {
            restarts++;
            return true;
        };
        vm.OpenPages = () => new PageViewModel[] { new FakePage("Cleaner") };
        vm.Confirm = (_, _) => throw new Xunit.Sdk.XunitException("nothing is running: no question");

        vm.RestartNowCommand.Execute(null);

        Assert.Equal(1, restarts);
    }

    [Fact]
    public void RestartNow_AsksFirst_WhenAPageIsStillWorking()
    {
        var vm = Create();
        var restarts = 0;
        var questions = new List<(string Title, string Message)>();
        var answer = false;
        var busy = new FakePage("Cleaner");
        busy.SetBusy(true, "Идёт очистка…");
        vm.RestartProgram = () =>
        {
            restarts++;
            return true;
        };
        vm.OpenPages = () => new PageViewModel[] { new FakePage("Programs"), busy };
        vm.Confirm = (title, message) =>
        {
            questions.Add((title, message));
            return answer;
        };

        vm.RestartNowCommand.Execute(null);

        Assert.Equal(0, restarts);
        var (title, message) = Assert.Single(questions);
        Assert.Equal("Перезапуск программы", title);
        Assert.Contains("Раздел Cleaner", message);
        Assert.DoesNotContain("Раздел Programs", message);

        answer = true;
        vm.RestartNowCommand.Execute(null);

        Assert.Equal(1, restarts);
        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public void RestartNow_WhenTheProgramDoesNotRestart_NothingBreaks()
    {
        var vm = Create();
        vm.RestartProgram = () => false;
        vm.OpenPages = () => Array.Empty<PageViewModel>();
        var logged = new List<LogEntry>();
        void OnLog(LogEntry e) => logged.Add(e);
        Log.EntryAdded += OnLog;
        try
        {
            vm.RestartNowCommand.Execute(null);
        }
        finally
        {
            Log.EntryAdded -= OnLog;
        }

        Assert.Contains(logged, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void TheTestConstructor_NeverRestartsTheRealProgram()
    {
        var vm = Create();

        Assert.False(vm.RestartProgram());
    }
}

/// <summary>The new texts in English (the real en.json): the log lines, the question, the restart of the language card.</summary>
[Collection(LanguageSequentialCollection.Name)]
public class ModesEnglishTests
{
    public ModesEnglishTests() => TestEnvironment.Init();

    [Fact]
    public void TheModeChoice_IsLoggedInEnglish()
    {
        var logged = new List<string>();
        void OnLog(LogEntry e) => logged.Add(e.Message);
        using (TestLanguage.English())
        {
            Log.EntryAdded += OnLog;
            try
            {
                UiModes.Choose(new AppSettings(), UiMode.Simple, _ => { });
                UiModes.Choose(new AppSettings(), UiMode.Expert, _ => { });
            }
            finally
            {
                Log.EntryAdded -= OnLog;
            }
        }

        Assert.Contains("Settings: mode is now simple", logged);
        Assert.Contains("Settings: mode is now expert", logged);
    }

    [Fact]
    public void AHiddenPage_IsOfferedInEnglish()
    {
        var questions = new List<string>();
        var pages = new[] { "Dashboard", "Network" }.Select(k => new FakePage(k)).ToList();
        using (TestLanguage.English())
        {
            var main = new MainViewModel(pages, () => new AppSettings { UiMode = UiMode.Simple }, _ => { })
            {
                Ask = (_, message) =>
                {
                    questions.Add(message);
                    return false;
                },
            };

            main.NavigateTo("Network");
        }

        Assert.Equal(new[] { "This section is in expert mode. Switch to expert mode?" }, questions);
    }

    [Fact]
    public void TheLanguageCard_InEnglishRun_ShowsEnglishAndAsksForARestartForRussian()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(() => settings, _ => { }, new ObservableCollection<LogEntry>());
        var logged = new List<string>();
        void OnLog(LogEntry e) => logged.Add(e.Message);
        using (TestLanguage.English())
        {
            vm.LoadGeneral();
            Assert.True(vm.IsEnglish);
            Assert.False(vm.LanguageNeedsRestart);

            Log.EntryAdded += OnLog;
            try
            {
                vm.IsRussian = true;
            }
            finally
            {
                Log.EntryAdded -= OnLog;
            }

            Assert.Equal("ru", settings.Language);
            Assert.True(vm.LanguageNeedsRestart);
        }

        Assert.Contains("Settings: language is now Русский", logged); // the names of the languages are never translated
    }

    [Fact]
    public void TheRestartQuestion_InEnglish()
    {
        var vm = new SettingsViewModel(() => new AppSettings(), _ => { }, new ObservableCollection<LogEntry>());
        var busy = new FakePage("Cleaner");
        busy.SetBusy(true);
        var asked = new List<(string Title, string Message)>();
        vm.OpenPages = () => new PageViewModel[] { busy };
        vm.Confirm = (title, message) =>
        {
            asked.Add((title, message));
            return false;
        };

        using (TestLanguage.English())
            vm.RestartNowCommand.Execute(null);

        var (title, message) = Assert.Single(asked);
        Assert.Equal("Restart the program", title);
        Assert.Equal("Work is still in progress:\n• “Раздел Cleaner”\n\nA restart will interrupt it. Restart the program now?", message);
    }
}
