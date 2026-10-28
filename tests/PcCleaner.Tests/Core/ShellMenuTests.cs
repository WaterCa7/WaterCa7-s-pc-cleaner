using PcCleaner.Core;
using PcCleaner.Core.Localization;
using Xunit;

namespace PcCleaner.Tests.Core;

public class ShellMenuTests
{
    internal static readonly string[] AllKeys =
    {
        "Dashboard", "Cleaner", "Hardware", "Optimizer", "Processes", "Startup", "Programs", "Disk", "Security", "Network",
        "Profile", "Settings",
    };

    private static List<FakePage> Pages(params string[] keys) => keys.Select(k => new FakePage(k)).ToList();

    [Fact]
    public void TheMenu_HasSevenSections_WithTheTabsOfTheSpec()
    {
        var sections = ShellMenu.Build(Pages(AllKeys));

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Computer", "System", "Protection", "Profile", "Settings" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Hardware", "Optimizer" }, sections[2].Tabs.Select(p => p.Key));
        Assert.Equal(new[] { "Processes", "Startup", "Programs", "Disk" }, sections[3].Tabs.Select(p => p.Key));
        Assert.Equal(new[] { "Security", "Network" }, sections[4].Tabs.Select(p => p.Key));
        Assert.Equal(new[] { SectionPlace.Top, SectionPlace.Top, SectionPlace.Top, SectionPlace.Top, SectionPlace.Top, SectionPlace.Bottom, SectionPlace.Bottom },
            sections.Select(s => s.Place));
    }

    [Fact]
    public void ASectionOfOnePage_TakesTheTitleAndIconOfThatPage()
    {
        var sections = ShellMenu.Build(Pages(AllKeys));

        Assert.Equal("Раздел Dashboard", sections[0].Title);
        Assert.Equal("Раздел Settings", sections[6].Title);
        Assert.Equal("Компьютер", sections[2].Title);
        Assert.Equal("Система", sections[3].Title);
        Assert.Equal("Защита", sections[4].Title);
    }

    [Fact]
    public void MissingPages_AreSkipped_AndASectionWithoutPagesIsLeftOut()
    {
        var sections = ShellMenu.Build(Pages("Dashboard", "Optimizer", "Settings"));

        Assert.Equal(new[] { "Dashboard", "Computer", "Settings" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Optimizer" }, sections[1].Tabs.Select(p => p.Key));
    }

    [Fact]
    public void APageNoSectionNames_GetsASectionOfItsOwn_AtTheEndOfTheMainList()
    {
        var sections = ShellMenu.Build(Pages("Dashboard", "Phone", "Settings"));

        Assert.Equal(new[] { "Dashboard", "Phone", "Settings" }, sections.Select(s => s.Key));
        Assert.Equal(SectionPlace.Top, sections[1].Place);
    }

    [Fact]
    public void VisibleTabs_FollowTheFilter_InTheOrderOfTheTabs()
    {
        var system = ShellMenu.Build(Pages(AllKeys)).Single(s => s.Key == "System");

        system.SyncVisibleTabs(_ => true);
        Assert.Equal(new[] { "Processes", "Startup", "Programs", "Disk" }, system.VisibleTabs.Select(p => p.Key));
        Assert.True(system.ShowTabs);

        system.SyncVisibleTabs(p => p.Key == "Programs");
        Assert.Equal(new[] { "Programs" }, system.VisibleTabs.Select(p => p.Key));
        Assert.False(system.ShowTabs); // one tab: no strip
        Assert.True(system.HasVisibleTabs);

        system.SyncVisibleTabs(_ => false);
        Assert.False(system.HasVisibleTabs);
        Assert.Null(system.EntryTab);
    }

    [Fact]
    public void EntryTab_IsTheLastOpenedTab_WhileItIsVisible_ElseTheFirstVisible()
    {
        var system = ShellMenu.Build(Pages(AllKeys)).Single(s => s.Key == "System");
        system.SyncVisibleTabs(_ => true);
        Assert.Equal("Processes", system.EntryTab!.Key);

        system.Remember(system.Tabs.Single(p => p.Key == "Disk"));
        Assert.Equal("Disk", system.EntryTab!.Key);

        system.SyncVisibleTabs(p => p.Key != "Disk");
        Assert.Equal("Processes", system.EntryTab!.Key);
    }

    [Fact]
    public void Remember_IgnoresAPageOfAnotherSection()
    {
        var sections = ShellMenu.Build(Pages(AllKeys));
        var system = sections.Single(s => s.Key == "System");
        system.SyncVisibleTabs(_ => true);

        system.Remember(sections[0].Tabs[0]);

        Assert.Equal("Processes", system.EntryTab!.Key);
        Assert.False(system.Contains(sections[0].Tabs[0]));
    }
}

/// <summary>The shell over the menu: two sidebar lists, the tab strip, navigation by page key.</summary>
[Collection(ModeSequentialCollection.Name)]
public class ShellSectionsTests
{
    private readonly AppSettings _settings = new() { UiMode = UiMode.Expert };
    private readonly List<FakePage> _pages = ShellMenuTests.AllKeys.Select(k => new FakePage(k)).ToList();
    private readonly List<string> _questions = new();
    private bool _answer;

    public ShellSectionsTests() => TestEnvironment.Init();

    private MainViewModel Shell() => new(_pages, () => _settings, _ => { })
    {
        Ask = (title, _) =>
        {
            _questions.Add(title);
            return _answer;
        },
    };

    [Fact]
    public void TheSidebar_HasFiveSectionsOnTop_AndProfileAndSettingsAtTheBottom()
    {
        var shell = Shell();

        Assert.Equal(new[] { "Dashboard", "Cleaner", "Computer", "System", "Protection" }, shell.TopSections.Select(s => s.Key));
        Assert.Equal(new[] { "Profile", "Settings" }, shell.BottomSections.Select(s => s.Key));
        Assert.Equal(12, shell.Pages.Count); // every page stays in Pages
        Assert.Equal("Dashboard", shell.SelectedSection!.Key);
    }

    [Theory]
    [InlineData("Dashboard", "Dashboard")]
    [InlineData("Cleaner", "Cleaner")]
    [InlineData("Hardware", "Computer")]
    [InlineData("Optimizer", "Computer")]
    [InlineData("Processes", "System")]
    [InlineData("Startup", "System")]
    [InlineData("Programs", "System")]
    [InlineData("Disk", "System")]
    [InlineData("Security", "Protection")]
    [InlineData("Network", "Protection")]
    [InlineData("Profile", "Profile")]
    [InlineData("Settings", "Settings")]
    public void NavigateTo_APageKey_OpensItsSectionAndTab(string pageKey, string sectionKey)
    {
        var shell = Shell();

        shell.NavigateTo(pageKey);

        Assert.Equal(pageKey, shell.SelectedPage!.Key);
        Assert.Equal(sectionKey, shell.SelectedSection!.Key);
    }

    [Fact]
    public void ASection_OpensOnItsFirstTab_ThenOnTheLastOpenedOne()
    {
        var shell = Shell();
        var system = shell.TopSections.Single(s => s.Key == "System");

        shell.SelectedTopSection = system;
        Assert.Equal("Processes", shell.SelectedPage!.Key);

        shell.NavigateTo("Disk");
        shell.NavigateTo("Dashboard");
        shell.SelectedTopSection = system;
        Assert.Equal("Disk", shell.SelectedPage!.Key);
    }

    [Fact]
    public void OnlyTheListOfTheOpenSection_HasASelection_AndANullWriteIsIgnored()
    {
        var shell = Shell();

        shell.NavigateTo("Settings");
        Assert.Null(shell.SelectedTopSection);
        Assert.Equal("Settings", shell.SelectedBottomSection!.Key);

        shell.SelectedTopSection = null; // what a list writes when its selection is cleared
        Assert.Equal("Settings", shell.SelectedPage!.Key);

        shell.NavigateTo("Network");
        Assert.Equal("Protection", shell.SelectedTopSection!.Key);
        Assert.Null(shell.SelectedBottomSection);
    }

    [Fact]
    public void TheHeader_ShowsTheSection_WhenThereAreTabs_ElseThePage()
    {
        var shell = Shell();

        shell.NavigateTo("Disk");
        Assert.Equal("Система", shell.HeaderTitle);
        Assert.True(shell.SelectedSection!.ShowTabs);

        shell.NavigateTo("Cleaner");
        Assert.Equal("Раздел Cleaner", shell.HeaderTitle);
        Assert.False(shell.SelectedSection!.ShowTabs);
    }

    [Fact]
    public void SimpleMode_HidesTabs_NotTheSectionsThatStillHaveOne()
    {
        _settings.UiMode = UiMode.Simple;
        var shell = Shell();

        Assert.Equal(new[] { "Dashboard", "Cleaner", "System" }, shell.TopSections.Select(s => s.Key));
        Assert.Equal(new[] { "Profile", "Settings" }, shell.BottomSections.Select(s => s.Key));
        var system = shell.TopSections.Single(s => s.Key == "System");
        Assert.Equal(new[] { "Programs" }, system.VisibleTabs.Select(p => p.Key));

        shell.SelectedTopSection = system;
        Assert.Equal("Programs", shell.SelectedPage!.Key);
        Assert.Equal("Раздел Programs", shell.HeaderTitle); // one tab: no strip, the page's own title
    }

    [Fact]
    public void AHiddenTab_IsStillOfferedWithTheExpertMode()
    {
        _settings.UiMode = UiMode.Simple;
        var shell = Shell();

        _answer = false;
        shell.NavigateTo("Disk");
        Assert.Equal(new[] { "Раздел Disk" }, _questions);
        Assert.Equal("Dashboard", shell.SelectedPage!.Key);
    }
}
