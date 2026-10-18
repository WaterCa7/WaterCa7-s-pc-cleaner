using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Disk;
using PcCleaner.Modules.Network;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Modules.Processes;
using PcCleaner.Modules.Programs;
using PcCleaner.Modules.Security;
using PcCleaner.Modules.Settings;
using PcCleaner.Modules.Startup;
using PcCleaner.Tests.Updates;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The shell, the startup screens and the update strip in English (the real en.json): the sidebar titles and the page
/// subtitles, the status bar, the admin line, the update strip, the questions before a restart. Russian is restored after each.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public class ShellEnglishTests
{
    private static readonly Regex Cyrillic = new("[\u0400-\u04FF]");

    public ShellEnglishTests() => TestEnvironment.Init();

    private static PageViewModel[] RealPages() => new PageViewModel[]
    {
        new DashboardViewModel(),
        new CleanerViewModel(AppServices.Cleaner),
        new ProcessesViewModel(AppServices.Processes),
        new StartupViewModel(AppServices.Startup),
        new ProgramsViewModel(AppServices.Programs),
        new DiskViewModel(AppServices.Disk),
        new OptimizerViewModel(AppServices.Optimizer),
        new NetworkViewModel(AppServices.Network),
        new SecurityViewModel(AppServices.Security),
        new SettingsViewModel(() => new AppSettings(), _ => { }, new ObservableCollection<LogEntry>()),
    };

    [Fact]
    public void TheSidebarTitles_AreEnglish_AndEverySubtitleHasNoRussianLeft()
    {
        var pages = RealPages();

        using (TestLanguage.English())
        {
            Assert.Equal(
                new[] { "Home", "Cleanup", "Processes", "Startup", "Programs", "Disks", "Speed up", "Network", "Security", "Settings" },
                pages.Select(p => p.Title));
            Assert.All(pages, p => Assert.False(Cyrillic.IsMatch(p.Subtitle), $"{p.Key}: {p.Subtitle}"));
            Assert.Equal("The overall state of your computer and a one-click check", pages[0].Subtitle);
        }

        Assert.Equal("Главная", pages[0].Title); // and Russian is back
        Assert.Equal("Настройки", pages[9].Title);
    }

    [Fact]
    public void TheShell_ShowsItsStatusAndAdminLine_InEnglish()
    {
        using (TestLanguage.English())
        {
            var main = new MainViewModel(new[] { new FakePage("Dashboard") }, () => new AppSettings { UiMode = UiMode.Expert }, _ => { });

            Assert.Equal("Ready", main.StatusText);
            Assert.Equal(Admin.IsElevated ? "Administrator rights" : "Limited mode", main.AdminText);
        }

        Assert.Equal("Готово", new MainViewModel(new[] { new FakePage("Dashboard") }, () => new AppSettings(), _ => { }).StatusText);
    }

    [Fact]
    public void TheUpdateStrip_IsEnglish()
    {
        var manifest = UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", 2_000_000, new string('a', 64)), out _)!;

        using (TestLanguage.English())
        {
            var ready = UpdateBanner.For(UpdateState.Ready, manifest, new Version(1, 1, 0))!;
            Assert.Equal("Version 1.2.0 is downloaded — it will be installed the next time the program starts.", ready.Text);
            Assert.Equal("Restart now", ready.Action);

            var updated = UpdateBanner.For(UpdateState.Updated, manifest, new Version(1, 2, 0))!;
            Assert.Equal("The program was updated to version 1.2.0.", updated.Text);
            Assert.Equal("What's new", updated.Action);

            var manual = UpdateBanner.For(UpdateState.ManualRequired, manifest, new Version(1, 1, 0))!;
            Assert.Equal("Version 1.2.0 could not be installed automatically.", manual.Text);
            Assert.Equal("Download manually", manual.Action);
        }
    }

    [Fact]
    public void TheQuestionsBeforeARestart_AreEnglish()
    {
        using (TestLanguage.English())
        {
            var banner = UpdateBanner.BusyWarning(new[] { "Cleanup", "Disks" });
            Assert.Equal(
                "Work is still in progress in the sections “Cleanup”, “Disks”.\n\n" +
                "On restart this work will be interrupted: what is already done is kept, the rest has to be started again.\n\n" +
                "Restart the program and install the update now?",
                banner);

            var admin = Admin.BusyWarning(new[] { ("Cleanup", "Scanning…") });
            Assert.StartsWith("Work is still in progress in the section:\n• “Cleanup”: Scanning\n\n", admin);
            Assert.EndsWith("press the button again.", admin);
            Assert.Contains("Other programs' windows (for example, an uninstaller) keep working.", admin);

            Assert.Equal("Restart as administrator", Admin.RestartTitle);
            Assert.Equal(
                "The action “Create a restore point” needs administrator rights.\n\nRestart the program as administrator?",
                Admin.ElevationQuestion("Create a restore point", standardUser: false));
            Assert.Contains("Your Windows account is a standard one", Admin.ElevationQuestion("x", standardUser: true));
            Assert.Equal(
                "The program is running as “Admin”, while “Anna” is signed in to Windows. " +
                "Cleanup, startup, browsers and other profile checks apply to “Admin”, not to “Anna”.",
                Admin.OtherAccountText(@"PC\Admin", @"PC\Anna"));
        }
    }

    [Fact]
    public void TheSplashTexts_AndTheTitleOfALogLevel_AreEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("Checking for updates…", T._(SplashViewModel.CheckingText));
            Assert.Equal("Installing the update…", T._(SplashViewModel.InstallingText));
            Assert.Equal("Warning", new LogEntry(DateTime.Now, LogLevel.Warning, "x").LevelText);
            Assert.Equal("Error", new LogEntry(DateTime.Now, LogLevel.Error, "x").LevelText);
            Assert.Equal("Action", new LogEntry(DateTime.Now, LogLevel.Action, "x").LevelText);
            Assert.Equal("Info", new LogEntry(DateTime.Now, LogLevel.Info, "x").LevelText);
            Assert.Equal("Critical", new SeverityToTextConverter().Convert(IssueSeverity.Critical, typeof(string), null, T.Culture));
            Assert.Equal("Tip", new SeverityToTextConverter().Convert(IssueSeverity.Info, typeof(string), null, T.Culture));
        }

        Assert.Equal("Внимание", new LogEntry(DateTime.Now, LogLevel.Warning, "x").LevelText);
        Assert.Equal(SplashViewModel.CheckingText, T._(SplashViewModel.CheckingText));
    }

    [Fact]
    public void TheBulkTextLists_AreEnglish()
    {
        var nl = Environment.NewLine;

        using (TestLanguage.English())
        {
            Assert.Equal("• a\n• b\n• c\n…and 2 more", BulkText.BulletList(new[] { "a", "b", "c", "d", "e" }, 3));
            Assert.Equal(
                "Delete 2." + nl + nl + "Not visible in the list right now:" + nl + "• h" + nl + nl + "In the list:" + nl + "• s",
                BulkText.TargetList("Delete 2", new[] { "h" }, new[] { "s" }));
        }

        Assert.Equal("• a\n• b\n…и ещё 1", BulkText.BulletList(new[] { "a", "b", "c" }, 2));
    }

    [Fact]
    public void AQuotedName_FollowsTheLanguage()
    {
        Assert.Equal("«Очистка»", T.Quote("Очистка"));
        using (TestLanguage.English())
            Assert.Equal("“Cleanup”", T.Quote("Cleanup"));
    }
}
