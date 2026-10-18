using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The first-run screen: shown after the startup splash while no mode is chosen; a card chooses the mode (through
/// <see cref="UiModes.Choose"/>, which raises the static <see cref="UiModes.Changed"/>, hence the sequential collection),
/// RU / EN saves the language and restarts. Settings, saving, restart and the running language are replaced, so nothing here
/// writes settings.json or starts the program.
/// </summary>
[Collection(ModeSequentialCollection.Name)]
public class WelcomeViewModelTests
{
    private readonly AppSettings _settings = new();
    private string _language = "ru";
    private int _saves;
    private int _restarts;
    private bool _restartSucceeds = true;

    public WelcomeViewModelTests() => TestEnvironment.Init();

    private WelcomeViewModel Create(UiMode? mode = null)
    {
        _settings.UiMode = mode;
        return new WelcomeViewModel(() => _settings, _ => _saves++, () =>
        {
            _restarts++;
            return _restartSucceeds;
        }, () => _language);
    }

    // ----- when it appears -----

    [Fact]
    public void ANewWelcome_IsHidden_AndTouchesNothing()
    {
        var welcome = Create();

        Assert.False(welcome.IsVisible);
        Assert.Equal(0, _saves);
        Assert.Equal(0, _restarts);
        Assert.Null(_settings.UiMode);
        Assert.Null(_settings.Language);
    }

    [Fact]
    public void AtStart_NoModeChosenYet_ShowsTheWelcome()
    {
        var welcome = Create(mode: null);

        Assert.True(welcome.ShowAtStart(screenshotRun: false));

        Assert.True(welcome.IsVisible);
    }

    [Theory]
    [InlineData(UiMode.Simple)]
    [InlineData(UiMode.Expert)]
    public void AtStart_AChosenMode_KeepsTheWelcomeHidden(UiMode chosen)
    {
        var welcome = Create(chosen);

        Assert.False(welcome.ShowAtStart(screenshotRun: false));

        Assert.False(welcome.IsVisible);
    }

    [Fact]
    public void AtStart_ARealRun_IgnoresTheWelcomeSwitch_WhenAModeIsChosen()
    {
        // --welcome is for a picture of the screen; a real run shows it only until the first choice
        var welcome = Create(UiMode.Simple);

        Assert.False(welcome.ShowAtStart(screenshotRun: false, forced: true));

        Assert.False(welcome.IsVisible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UiMode.Simple)]
    [InlineData(UiMode.Expert)]
    public void AtStart_AScreenshotRun_NeverShowsTheWelcome_WithoutTheSwitch(UiMode? chosen)
    {
        var welcome = Create(chosen);

        Assert.False(welcome.ShowAtStart(screenshotRun: true));

        Assert.False(welcome.IsVisible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UiMode.Simple)]
    [InlineData(UiMode.Expert)]
    public void AtStart_AScreenshotRun_ShowsTheWelcome_WithTheSwitch_WhateverTheMode(UiMode? chosen)
    {
        var welcome = Create(chosen);

        Assert.True(welcome.ShowAtStart(screenshotRun: true, forced: true));

        Assert.True(welcome.IsVisible);
    }

    [Fact]
    public void Hide_ClosesTheWelcomeWithoutChoosingAnything()
    {
        var welcome = Create();
        welcome.ShowAtStart(screenshotRun: false);

        welcome.Hide();

        Assert.False(welcome.IsVisible);
        Assert.Null(_settings.UiMode);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void IsVisible_RaisesPropertyChanged_SoTheWindowCanFadeTheOverlay()
    {
        var welcome = Create();
        var changed = new List<string?>();
        welcome.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        welcome.ShowAtStart(screenshotRun: false);
        welcome.Hide();

        Assert.Equal(new[] { nameof(WelcomeViewModel.IsVisible), nameof(WelcomeViewModel.IsVisible) }, changed);
    }

    // ----- the two cards -----

    [Fact]
    public void ChoosingSimple_SavesTheMode_AndClosesTheWelcome()
    {
        var welcome = Create();
        welcome.ShowAtStart(screenshotRun: false);

        welcome.ChooseSimpleCommand.Execute(null);

        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(1, _saves);
        Assert.False(welcome.IsVisible);
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public void ChoosingExpert_SavesTheMode_AndClosesTheWelcome()
    {
        var welcome = Create();
        welcome.ShowAtStart(screenshotRun: false);

        welcome.ChooseExpertCommand.Execute(null);

        // «Опытный» is also what a shell without a choice shows, but here it is a choice and is kept
        Assert.Equal(UiMode.Expert, _settings.UiMode);
        Assert.Equal(1, _saves);
        Assert.False(welcome.IsVisible);
    }

    [Fact]
    public void ChoosingAMode_TellsWhichOneWasChosen_ForTheShell()
    {
        var welcome = Create();
        var chosen = new List<UiMode>();
        welcome.Chosen += chosen.Add;

        welcome.ChooseSimpleCommand.Execute(null);

        Assert.Equal(new[] { UiMode.Simple }, chosen);
    }

    [Fact]
    public void ChoosingAMode_DoesNotChangeTheLanguage()
    {
        var welcome = Create();

        welcome.ChooseSimpleCommand.Execute(null);

        Assert.Null(_settings.Language);
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public void ChoosingTheModeThatIsAlreadySaved_StillClosesTheWelcome()
    {
        // a screenshot of the welcome screen of a program that already has a mode: choosing is closing
        var welcome = Create(UiMode.Simple);
        welcome.ShowAtStart(screenshotRun: true, forced: true);
        var chosen = new List<UiMode>();
        welcome.Chosen += chosen.Add;

        welcome.ChooseSimpleCommand.Execute(null);

        Assert.False(welcome.IsVisible);
        Assert.Equal(0, _saves); // nothing changed, so nothing is saved
        Assert.Equal(new[] { UiMode.Simple }, chosen);
    }

    // ----- RU / EN -----

    [Theory]
    [InlineData("ru", true, false)]
    [InlineData("en", false, true)]
    public void TheSwitch_MarksTheLanguageOfThisRun(string running, bool russian, bool english)
    {
        _language = running;
        var welcome = Create();

        Assert.Equal(russian, welcome.IsRussian);
        Assert.Equal(english, welcome.IsEnglish);
    }

    [Fact]
    public void ChoosingTheOtherLanguage_SavesIt_AndRestartsOnce()
    {
        var welcome = Create();
        welcome.ShowAtStart(screenshotRun: false);

        welcome.ChooseEnglishCommand.Execute(null);

        Assert.Equal("en", _settings.Language);
        Assert.Equal(1, _saves);
        Assert.Equal(1, _restarts);
        // no mode was chosen, so the restarted program shows the welcome screen again (and this one stays up until it is gone)
        Assert.Null(_settings.UiMode);
        Assert.True(welcome.IsVisible);
    }

    [Fact]
    public void ChoosingRussianWhileEnglishIsShown_SavesIt_AndRestarts()
    {
        _language = "en";
        var welcome = Create();

        welcome.ChooseRussianCommand.Execute(null);

        Assert.Equal("ru", _settings.Language);
        Assert.Equal(1, _saves);
        Assert.Equal(1, _restarts);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void ChoosingTheLanguageThatIsShown_DoesNothing(string running)
    {
        _language = running;
        var welcome = Create();

        (running == "ru" ? welcome.ChooseRussianCommand : welcome.ChooseEnglishCommand).Execute(null);

        Assert.Null(_settings.Language);
        Assert.Equal(0, _saves);
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public void ASecondClickOnTheLanguage_WhileTheProgramRestarts_DoesNotStartAnotherCopy()
    {
        var welcome = Create();

        welcome.ChooseEnglishCommand.Execute(null);
        welcome.ChooseEnglishCommand.Execute(null);
        welcome.ChooseRussianCommand.Execute(null); // back to the shown one: nothing either

        Assert.Equal(1, _restarts);
        Assert.Equal(1, _saves);
        Assert.Equal("en", _settings.Language);
    }

    [Fact]
    public void WhenTheRestartFails_TheLanguageStaysSaved_AndTheScreenKeepsWorking()
    {
        _restartSucceeds = false;
        var welcome = Create();
        welcome.ShowAtStart(screenshotRun: false);

        welcome.ChooseEnglishCommand.Execute(null);

        Assert.Equal("en", _settings.Language);
        Assert.True(welcome.IsVisible);

        // the user can try again, or simply choose a mode
        welcome.ChooseEnglishCommand.Execute(null);
        Assert.Equal(2, _restarts);

        welcome.ChooseSimpleCommand.Execute(null);
        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.False(welcome.IsVisible);
    }

    [Fact]
    public void TheRealWelcome_UsesTheAppSettings_AndIsHiddenAtFirst()
    {
        var welcome = new WelcomeViewModel();

        Assert.False(welcome.IsVisible);
    }
}

/// <summary>The shell owns the welcome screen: a choice there changes the sidebar at once and lands on the Dashboard.</summary>
[Collection(ModeSequentialCollection.Name)]
public class WelcomeShellTests
{
    private static readonly string[] Ten =
        { "Dashboard", "Cleaner", "Processes", "Startup", "Programs", "Disk", "Optimizer", "Network", "Security", "Settings" };

    private readonly AppSettings _settings = new();
    private readonly List<FakePage> _pages = Ten.Select(k => new FakePage(k)).ToList();
    private int _saves;

    public WelcomeShellTests() => TestEnvironment.Init();

    private MainViewModel Create(UiMode? mode)
    {
        _settings.UiMode = mode;
        return new MainViewModel(_pages, () => _settings, _ => _saves++);
    }

    [Fact]
    public void TheShell_HasAWelcome_ThatIsHiddenUntilAskedToShow()
    {
        var main = Create(mode: null);

        Assert.NotNull(main.Welcome);
        Assert.False(main.Welcome.IsVisible);
    }

    [Fact]
    public void ChoosingSimple_OnTheWelcome_ShrinksTheSidebar_SavesTheMode_AndOpensTheDashboard()
    {
        var main = Create(mode: null);
        main.Welcome.ShowAtStart(screenshotRun: false);

        main.Welcome.ChooseSimpleCommand.Execute(null);

        Assert.Equal(UiMode.Simple, _settings.UiMode);
        Assert.Equal(1, _saves);
        Assert.Equal(new[] { "Dashboard", "Cleaner", "Programs", "Settings" }, main.VisiblePages.Select(p => p.Key));
        Assert.Equal("Dashboard", main.SelectedPage!.Key);
        Assert.False(main.Welcome.IsVisible);
    }

    [Fact]
    public void ChoosingExpert_OnTheWelcome_KeepsEveryPage_AndOpensTheDashboard()
    {
        var main = Create(mode: null);
        main.NavigateTo("Network");
        Assert.Equal("Network", main.SelectedPage!.Key);

        main.Welcome.ChooseExpertCommand.Execute(null);

        Assert.Equal(UiMode.Expert, _settings.UiMode);
        Assert.Equal(Ten, main.VisiblePages.Select(p => p.Key));
        Assert.Equal("Dashboard", main.SelectedPage!.Key);
    }

    [Fact]
    public void TheShellOfATest_SavesTheLanguage_ButHasNothingToRestartWith()
    {
        var main = Create(mode: null);

        // the language switch of a shell made for tests has no real restart behind it: it must neither throw nor close the test host
        main.Welcome.ChooseEnglishCommand.Execute(null);

        Assert.Equal("en", _settings.Language);
        Assert.Equal(1, _saves);
        Assert.Null(_settings.UiMode);
    }
}
