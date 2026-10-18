using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>The Settings page in English (the real en.json): the log, the backups, the quarantine, the restore point texts.</summary>
[Collection(LanguageSequentialCollection.Name)]
public class SettingsEnglishTests
{
    private static readonly Regex Cyrillic = new("[\u0400-\u04FF]");

    public SettingsEnglishTests() => TestEnvironment.Init();

    private static SettingsViewModel Create(AppSettings? settings = null)
        => new(() => settings ?? new AppSettings(), _ => { }, new ObservableCollection<LogEntry>());

    [Fact]
    public void TheLogTab_FilterAndSummary_AreEnglish()
    {
        var log = new ObservableCollection<LogEntry>
        {
            new(new DateTime(2026, 10, 18, 14, 5, 7), LogLevel.Action, "Deleted"),
            new(new DateTime(2026, 10, 18, 14, 5, 8), LogLevel.Warning, "Careful"),
        };

        using (TestLanguage.English())
        {
            Assert.Equal(new[] { "All", "Actions", "Warnings", "Errors" }, LogFilter.Options.Select(o => o.Label));
            Assert.Equal("Oct 18, 2026 14:05:07 [Action] Deleted", LogFilter.FormatLine(log[0]));

            var vm = new SettingsViewModel(() => new AppSettings(), _ => { }, log);
            vm.RebuildLog();
            Assert.Equal("Actions: 1 · warnings: 1 · errors: 0", vm.LogCounters);
            Assert.Equal("2 entries in total during this run", vm.LogSummary);
            Assert.Equal("log entries", vm.LogTileLabel);
        }
    }

    [Fact]
    public void TheGeneralTab_TextsAndValidation_AreEnglish()
    {
        using (TestLanguage.English())
        {
            var vm = Create();
            vm.LoadGeneral();

            Assert.StartsWith("Version ", vm.VersionLine);
            Assert.EndsWith(" is installed", vm.VersionLine);
            Assert.Equal("Updates have not been checked yet", vm.LastCheckText);
            if (!Admin.IsElevated)
                Assert.Equal("Restore points cannot be created without administrator rights.", vm.RestorePointAdminWarning);
            Assert.Equal(new[] { "No delay (delete at once)", "1 hour", "6 hours", "24 hours (recommended)", "2 days", "3 days", "1 week" },
                vm.AgeOptions.Select(o => o.Label));

            Assert.False(SettingsLogic.TryParseMinAge("", out _, out var empty));
            Assert.Equal("Enter the number of hours.", empty);
            Assert.False(SettingsLogic.TryParseMinAge("1000", out _, out var range));
            Assert.Equal("From 0 to 720 hours (30 days).", range);
            Assert.Equal(ExclusionAddResult.Invalid, SettingsLogic.TryAddExclusion(new AppSettings(), "relative", out _, out var message));
            Assert.Equal(@"A full path is required, for example D:\Projects or %USERPROFILE%\Documents.", message);
        }
    }

    [Fact]
    public void TheRestorePointTexts_AreEnglish()
    {
        var now = new DateTime(2026, 10, 18, 14, 5, 0);

        using (TestLanguage.English())
        {
            var created = RestorePointText.Describe(RestorePointResult.Created, now, disabledByPolicy: false);
            Assert.Equal("Restore point created Oct 18, 2026 14:05.", created.Status);
            Assert.Contains("“Open System Restore”", created.Details);

            var failed = RestorePointText.Describe(RestorePointResult.Failed, now, disabledByPolicy: true);
            Assert.Equal("Windows did not create a restore point — see the Log tab for details.", failed.Status);
            Assert.Equal(
                "Windows did not create a restore point.\n\n" +
                "System Restore is turned off by group policy — restore points cannot be created on this computer. See the Log tab for details.",
                failed.Details);

            foreach (var result in Enum.GetValues<RestorePointResult>())
            {
                var text = RestorePointText.Describe(result, now, false);
                Assert.False(Cyrillic.IsMatch(text.Status + text.Details), text.Status);
            }
        }
    }

    [Fact]
    public void TheBackupsAndTheTrustedList_AreEnglish()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("hosts file", new BackupEntry { FullPath = "a.hosts", Time = DateTime.Now, Kind = BackupKind.Hosts, Title = "x" }.KindText);
            Assert.Equal("Registry (system)", new BackupEntry { FullPath = "a.reg", Time = DateTime.Now, Kind = BackupKind.Registry, Title = "x", NeedsAdmin = true }.KindText);
            Assert.Equal("Program startup (Run) — current user", BackupCatalog.FriendlyKeyName(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"));
            Assert.Equal("Home", BackupCatalog.ModuleDisplayName("Dashboard"));

            Assert.Equal("Startup: Updater (registry, current user)", TrustedItemText.Label(@"reg|HKCU|Software\Microsoft\Windows\CurrentVersion\Run|Updater"));
            Assert.Equal("Scheduled task: Vendor\\Task", TrustedItemText.Label(@"task|\Vendor\Task"));
            Assert.Equal("Service: Spooler", TrustedItemText.Label("svc|Spooler"));
            Assert.Equal("Program: tool.exe", TrustedItemText.Label("tool.exe"));
        }
    }

    [Fact]
    public void TheQuarantineAlternativeName_AndTheAgeLabels_FollowTheLanguage()
    {
        using (TestLanguage.English())
        {
            Assert.Equal(@"C:\Apps\tool (restored).exe", QuarantineActions.AlternativePath(@"C:\Apps\tool.exe", _ => false));
            Assert.Equal(@"C:\Apps\tool (restored 2).exe", QuarantineActions.AlternativePath(@"C:\Apps\tool.exe", p => p.EndsWith("(restored).exe", StringComparison.Ordinal)));
            Assert.Equal("at most once every 24 hours", SystemRestoreInfo.FrequencyText(1440));
            Assert.Equal("at most once every 3 hours", SystemRestoreInfo.FrequencyText(180));
            Assert.Equal("with no time limit", SystemRestoreInfo.FrequencyText(0));
        }
    }
}
