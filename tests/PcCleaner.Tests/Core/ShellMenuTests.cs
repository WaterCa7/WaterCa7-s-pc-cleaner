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
