using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Security;
using PcCleaner.Tests.Core;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>
/// The Security page and its dashboard issues in English (the real en.json): hosts hijack, firewall off, UAC off, proxy, Winlogon
/// and IFEO hijacks with their fix results (a fix that needs administrator rights is still admin-only, a failed one still a failure,
/// nothing is prefixed twice), the cards of the checks, the decisions that used to read the words (the exclusions state, the title of
/// the update issue, the failure opening) and a sweep for Russian leftovers. Runs alone (the language is process-wide) and always
/// switches back to Russian; the Russian twin of the main texts is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class SecurityEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");
    private const string Evil = @"C:\Users\Public\evil.exe";

    public SecurityEnglishTests() => TestEnvironment.Init();

    private static void AssertEnglish(params string?[] texts)
    {
        foreach (var text in texts)
            Assert.DoesNotMatch(Cyrillic, text ?? "");
    }

    private static async Task<(HealthIssue Issue, SecurityReport Report)> ScanIssueAsync(SecuritySandbox sb, Func<HealthIssue, bool> pick)
    {
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        return (sb.Service.BuildIssues(report).Single(pick), report);
    }

    private static void WriteHosts(SecuritySandbox sb, params string[] lines)
        => File.WriteAllText(sb.Env.HostsPath, string.Join("\r\n", lines) + "\r\n");

    // ================================================================ hosts

    [Fact]
    public async Task HostsHijack_HasAnEnglishIssue_AndTheFixResultsAreRecognised()
    {
        using var sb = new SecuritySandbox();
        WriteHosts(sb, "127.0.0.1 localhost", "203.0.113.9 github.com", "203.0.113.9 www.vk.com", "10.0.0.5 old-nas.test");
        var (russian, _) = await ScanIssueAsync(sb, i => i.Title == "Файл hosts подменяет адреса сайтов");
        Assert.Equal("Убрать подмену", russian.FixLabel);

        using var language = TestLanguage.English();
        var (issue, report) = await ScanIssueAsync(sb, i => i.Title == "The hosts file replaces website addresses");

        Assert.Equal("Remove the replacement", issue.FixLabel);
        Assert.Equal("Security", issue.Module);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.StartsWith("Website addresses are replaced: 2, for example github.com. This is how viruses lead you to fake pages.", issue.Details);
        Assert.Equal("Hosts file", SecurityCheckIds.Title(SecurityCheckIds.Hosts));
        AssertEnglish(issue.Module, issue.Title, issue.Details, issue.FixLabel, report.Check(SecurityCheckIds.Hosts)!.State, report.Check(SecurityCheckIds.Hosts)!.Details);
        Assert.All(report.Hosts.Entries, e => AssertEnglish(e.Reason));

        // The file changes after the scan: nothing is removed, and the text is a failure that says so once.
        WriteHosts(sb, "# added later", "127.0.0.1 localhost", "203.0.113.9 github.com");
        var failed = await issue.FixAsync!(CancellationToken.None);
        Assert.Equal("Could not fix. The hosts file changed after the check. Press “Check again” and select the lines again.", failed);
        Assert.True(HealthScoring.LooksLikeFailure(failed));

        // A fresh scan removes the hijack lines and keeps the others.
        var (fresh, _) = await ScanIssueAsync(sb, i => i.Title == "The hosts file replaces website addresses");
        var done = await fresh.FixAsync!(CancellationToken.None);
        Assert.StartsWith("Removed 1 line from the hosts file. Websites open at their real addresses again.", done);
        Assert.False(HealthScoring.LooksLikeFailure(done), done);
        Assert.Equal("# added later\r\n127.0.0.1 localhost\r\n", File.ReadAllText(sb.Env.HostsPath));
    }

    [Fact]
    public void TheHostsFile_ChangedAfterTheScan_ThrowsAnEnglishMessage_ThatIsShownAsItIs()
    {
        using var language = TestLanguage.English();
        var dir = TestEnvironment.NewTempDir("hosts-en");
        var path = Path.Combine(dir, "hosts");
        File.WriteAllText(path, "127.0.0.1 localhost\r\n203.0.113.9 github.com\r\n");
        var entry = HostsFile.Parse(File.ReadAllText(path)).Single(e => e.Hostnames.Contains("github.com"));
        File.WriteAllText(path, "# changed\r\n127.0.0.1 localhost\r\n203.0.113.9 github.com\r\n");

        var ex = Assert.Throws<HostsChangedException>(() => HostsFile.RemoveLines(path, new[] { entry }, dir));

        Assert.Equal("The hosts file changed after the check. Press “Check again” and select the lines again.", ErrorText.Sentence(ex));
    }

    [Fact]
    public void TheHostsReasons_AreEnglish()
    {
        using var language = TestLanguage.English();
        var bank = HostsFile.Parse("203.0.113.9 www.sberbank.ru").Single();
        HostsRules.Classify(bank);
        Assert.Equal(SecurityStatus.Danger, bank.Risk);
        Assert.Equal("Redirect: the site Sberbank (www.sberbank.ru) will open from the address 203.0.113.9. This is how viruses plant fake pages " +
                     "to steal passwords. For banks and government services this is especially dangerous. If you did not add this line yourself, remove it.",
            bank.Reason);

        var blocked = HostsFile.Parse("0.0.0.0 update.microsoft.com\r\n0.0.0.0 kaspersky.com\r\n0.0.0.0 ads.example.test").ToList();
        blocked.ForEach(HostsRules.Classify);
        Assert.Equal("update.microsoft.com is blocked — Windows will not be able to get security updates.", blocked[0].Reason);
        Assert.StartsWith("The Kaspersky site (kaspersky.com) is blocked.", blocked[1].Reason);
        Assert.StartsWith("Blocking a website", blocked[2].Reason);
        Assert.All(blocked, e => AssertEnglish(e.Reason));
    }

    // ================================================================ firewall, UAC, proxy

    [Fact]
    public async Task FirewallOff_HasAnEnglishIssue_AndItsFixIsAFailureInTheSandbox()
    {
        using var sb = new SecuritySandbox();
        var profiles = new[] { new FirewallProfileState("Private", true), new FirewallProfileState("Public", false) };
        SecurityReport Build() => new()
        {
            FirewallProfiles = profiles,
            Checks = new[] { SecurityRules.EvaluateFirewall(profiles, Array.Empty<SecurityProduct>()) }, // the card text is made with the language of the moment
        };
        Assert.Equal("Брандмауэр Windows выключен", sb.Service.BuildIssues(Build()).Single().Title);

        using var language = TestLanguage.English();
        var report = Build();
        var card = report.Checks.Single();
        var issue = sb.Service.BuildIssues(report).Single();

        Assert.Equal("Off for the public network", card.State);
        Assert.Equal("The Windows Firewall is off", issue.Title);
        Assert.StartsWith("Off for the public network. Without a firewall the computer is open to attacks from the network.", issue.Details);
        Assert.Equal("Fix", issue.FixLabel);
        Assert.Equal("Protection from attacks over the network", issue.Gain);
        Assert.StartsWith("Private: on · Public: off", card.Details);
        AssertEnglish(card.State, card.Details, issue.Title, issue.Details, issue.FixLabel, issue.Gain);

        var text = await issue.FixAsync!(CancellationToken.None); // the sandbox is not the real system
        Assert.Equal("Could not fix. Not available in the test environment.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public void TheFirewallCard_NamesSeveralProfiles_InEnglish()
    {
        using var language = TestLanguage.English();
        var two = SecurityRules.EvaluateFirewall(new[]
        {
            new FirewallProfileState("Private", false), new FirewallProfileState("Public", false), new FirewallProfileState("Domain", true),
        }, Array.Empty<SecurityProduct>());
        Assert.Equal("Off for the private and public networks", two.State);
        Assert.Equal(SecurityStatus.Danger, two.Status);

        var domain = SecurityRules.EvaluateFirewall(new[] { new FirewallProfileState("Domain", false, ByPolicy: true) }, Array.Empty<SecurityProduct>());
        Assert.Equal("Off for the domain network", domain.State);
        Assert.Contains("(policy)", domain.Details);
        Assert.Contains("The firewall is turned off by a Group Policy", domain.Details);
        AssertEnglish(two.Details, domain.Details);
    }

    [Fact]
    public async Task UacOff_HasAnEnglishIssue_AndTheFixTellsToRestart()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.UacPolicies))
        {
            key.SetValue("EnableLUA", 0, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord);
        }

        using var language = TestLanguage.English();
        var (issue, report) = await ScanIssueAsync(sb, i => i.Title == "User Account Control (UAC) is off");

        Assert.Equal("UAC is off", report.Check(SecurityCheckIds.Uac)!.State);
        Assert.Equal("Any program gets administrator rights without your knowledge. A restart is needed after turning it on.", issue.Details);
        Assert.Equal("After turning UAC on you need to restart the computer.", issue.Consequence);
        Assert.Equal("User Account Control", SecurityCheckIds.Title(SecurityCheckIds.Uac));

        var text = await issue.FixAsync!(CancellationToken.None);
        Assert.StartsWith("UAC is on. Restart the computer for the change to take effect.\n\nRegistry backup: ", text);
        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        AssertEnglish(text);
    }

    [Fact]
    public async Task Proxy_HasAnEnglishIssue_AndTheFixResultsAreRecognised()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.InternetSettings))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "203.0.113.5:8080");
        }

        using var language = TestLanguage.English();
        var (issue, report) = await ScanIssueAsync(sb, i => i.Title == "Proxy 203.0.113.5:8080 is on");

        Assert.Equal("Turn off proxy", issue.FixLabel);
        Assert.StartsWith("Adware replaces the proxy.", issue.Details);
        Assert.Contains("The button turns the proxy off", issue.Details);
        Assert.True(issue.InFixAll);

        // The settings changed after the scan: nothing is touched, and the text is a failure.
        using (var key = sb.Env.InternetSettings.Open(writable: true))
            key!.SetValue("ProxyServer", "203.0.113.6:8080");
        var stale = await issue.FixAsync!(CancellationToken.None);
        Assert.Equal("Could not fix. The proxy settings changed after the check, so nothing was changed. Press “Check again”.", stale);
        Assert.True(HealthScoring.LooksLikeFailure(stale));

        var (fresh, _) = await ScanIssueAsync(sb, i => i.Title == "Proxy 203.0.113.6:8080 is on");
        var done = await fresh.FixAsync!(CancellationToken.None);
        Assert.StartsWith("The proxy is turned off — browsers connect to websites directly.", done);
        Assert.False(HealthScoring.LooksLikeFailure(done), done);
        AssertEnglish(done, report.Check(SecurityCheckIds.Proxy)!.Details);
    }

    [Fact]
    public async Task Winlogon_AndIfeoHijacks_HaveEnglishIssues_AndRestoreInEnglish()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "explorer.exe, " + Evil);
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        using (var key = sb.Create(sb.Env.Ifeo))
        using (var sub = key.CreateSubKey("taskmgr.exe"))
            sub.SetValue("Debugger", Evil);
        var restorePoints = new List<string>();
        sb.Service.CreateRestorePointAsync = (description, _) =>
        {
            restorePoints.Add(description);
            return Task.FromResult(true);
        };

        using var language = TestLanguage.English();
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        var issues = sb.Service.BuildIssues(report);

        var winlogon = issues.Single(i => i.Title == "The startup at Windows sign-in is replaced");
        Assert.Equal("Restore", winlogon.FixLabel);
        Assert.StartsWith("Shell = “explorer.exe, " + Evil + "” (should be explorer.exe)", winlogon.Details);
        var ifeo = issues.Single(i => i.Title == "The launch of system programs is hijacked");
        Assert.Equal("Remove the hijack", ifeo.FixLabel);
        Assert.Contains("The launch of taskmgr.exe is replaced by “" + Evil + "”.", ifeo.Details);

        var restored = await winlogon.FixAsync!(CancellationToken.None);
        Assert.StartsWith("The standard values were restored:\n• Shell: “explorer.exe, " + Evil + "” → “explorer.exe”", restored);
        Assert.False(HealthScoring.LooksLikeFailure(restored), restored);
        var removed = await ifeo.FixAsync!(CancellationToken.None);
        Assert.StartsWith("The hijack for taskmgr.exe was removed — the program starts as usual again.", removed);
        Assert.False(HealthScoring.LooksLikeFailure(removed), removed);
        Assert.Equal(new[] { "Restoring the sign-in settings (Winlogon)", "Removing the program launch hijack (IFEO)" }, restorePoints);
        AssertEnglish(restored, removed, winlogon.Details, ifeo.Details, winlogon.Consequence, ifeo.Consequence);
    }

    // ================================================================ results: admin rights, failures, nothing twice

    [Fact]
    public void AnAdminOnlyFix_IsStillAdminOnly_InEnglish()
    {
        using var language = TestLanguage.English();

        var text = SecurityService.DashboardText(SecurityActionResult.Fail(SecurityService.NeedAdminMessage));

        Assert.Equal("Could not fix. Administrator rights are needed for this. Restart the program as administrator (the button at the bottom left) " +
                     "and try again — nothing has been changed yet.", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.True(HealthScoring.LooksLikeAdminRequired(text));
        Assert.Single(Regex.Matches(text, "Could not fix")); // said once
    }

    [Fact]
    public void AFailedFix_WhoseTextAlreadyOpensWithTheFailure_IsNotPrefixedTwice_InBothLanguages()
    {
        var russian = SecurityRules.ThreatRemovalOutcome(3, 1, null);
        Assert.StartsWith("Не удалось обезвредить все угрозы", russian.Message);
        Assert.Equal(russian.Message, SecurityService.DashboardText(russian));

        using var language = TestLanguage.English();
        var english = SecurityRules.ThreatRemovalOutcome(3, 1, null);
        Assert.StartsWith("Could not neutralize all the threats: 2 of 3 neutralized.", english.Message);
        Assert.True(english.OpensWithFailure);
        Assert.Equal(english.Message, SecurityService.DashboardText(english));
        Assert.True(HealthScoring.LooksLikeFailure(english.Message));

        // Every producer of a failure that says "Could not ..." sets the flag, so the dashboard never writes "Could not fix. Could not ...".
        var withReason = SecurityRules.ThreatRemovalOutcome(2, 2, "Access is denied.");
        Assert.Equal("Could not neutralize the threats: Access is denied. Open Windows Security → Protection history and choose an action by hand — " +
                     "or run a full scan.", withReason.Message);
        Assert.Equal(withReason.Message, SecurityService.DashboardText(withReason));

        // A failure without the flag gets the prefix, in the language of the run.
        Assert.Equal("Could not fix. Nothing to do.", SecurityService.DashboardText(SecurityActionResult.Fail("Nothing to do.")));
        Assert.Equal("Could not fix.", SecurityService.DashboardText(SecurityActionResult.Fail("  ")));
        Assert.Equal("Could not fix. The scan was canceled.",
            SecurityService.DashboardText(new SecurityActionResult(false, "The scan was canceled.", Cancelled: true)));
    }

    [Fact]
    public void TheSuccessTexts_AreEnglish_AndNotFailures()
    {
        using var language = TestLanguage.English();
        var texts = new[]
        {
            SecurityService.FirewallEnabledMessage, SecurityService.RealtimeEnabledMessage, SecurityService.SignaturesUpdatedMessage,
            SecurityRules.ThreatRemovalOutcome(2, 0, null).Message,
            SecurityRules.Smb1DisabledText(true, RestorePointResult.Created),
            SecurityRules.Smb1DisabledText(false, RestorePointResult.RecentPointExists),
            SecurityRules.Smb1DisabledText(true, RestorePointResult.DisabledInSettings),
            SecurityRules.Smb1DisabledText(false, RestorePointResult.Failed),
        };

        foreach (var text in texts)
        {
            AssertEnglish(text);
            Assert.False(HealthScoring.LooksLikeFailure(text), text);
        }
        Assert.StartsWith("SMBv1 is turned off. Restart the computer for the change to take effect.\n\nA restore point was created before the change.",
            texts[4]);
        Assert.Contains("No restore point was created (system protection may be off).", texts[7]);
    }

    // ================================================================ decisions that used to read the words

    [Fact]
    public void TheUpdateIssue_IsChosenByTheServiceState_NotByTheWords_InBothLanguages()
    {
        using var sb = new SecuritySandbox();
        static SecurityReport Report(UpdatesState updates) => new() { Updates = updates, Checks = new[] { SecurityRules.EvaluateUpdates(updates, DateTime.Now) } };
        var serviceOff = new UpdatesState(null, 0, null, true);
        var oldUpdates = new UpdatesState(DateTime.Now.AddDays(-120), 5, "KB500", false);

        Assert.Equal("Служба обновлений Windows отключена", sb.Service.BuildIssues(Report(serviceOff)).Single().Title);
        Assert.Equal("Windows давно не обновлялась", sb.Service.BuildIssues(Report(oldUpdates)).Single().Title);

        using var language = TestLanguage.English();
        var disabled = Report(serviceOff);
        var old = Report(oldUpdates);
        Assert.Equal("The Windows Update service is off", sb.Service.BuildIssues(disabled).Single().Title);
        var oldIssue = sb.Service.BuildIssues(old).Single();
        Assert.Equal("Windows has not been updated for a long time", oldIssue.Title);
        Assert.StartsWith("The last update was installed on ", oldIssue.Details);
        Assert.Equal("Windows updates", SecurityCheckIds.Title(SecurityCheckIds.Updates));
        AssertEnglish(oldIssue.Details, sb.Service.BuildIssues(disabled).Single().Details);
    }

    [Fact]
    public void TooBroadExclusions_TurnAHealthyDefenderIntoAWarning_ByTheStateNotByTheWords()
    {
        var healthy = new DefenderStatus
        {
            ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0,
            Exclusions = new[] { @"C:\" },
        };
        var plain = new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0 };
        var stale = new DefenderStatus
        {
            ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 10, Exclusions = new[] { @"C:\" },
        };

        Assert.Equal("Слишком широкие исключения антивируса", SecurityRules.EvaluateAntivirus(healthy, Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>()).State);

        using var language = TestLanguage.English();
        var broad = SecurityRules.EvaluateAntivirus(healthy, Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>());
        Assert.Equal(SecurityStatus.Warning, broad.Status);
        Assert.Equal("Antivirus exclusions that are too broad", broad.State);
        Assert.Contains("Microsoft Defender does not scan: C:\\.", broad.Details);

        var ok = SecurityRules.EvaluateAntivirus(plain, Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>());
        Assert.Equal("Microsoft Defender is on, the databases are up to date", ok.State);

        // Not healthy to begin with: the state of the databases stays.
        var old = SecurityRules.EvaluateAntivirus(stale, Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>());
        Assert.Equal("The antivirus databases are out of date (10 days)", old.State);
        AssertEnglish(broad.Details, ok.Details, old.Details);
    }

    // ================================================================ cards and words

    [Fact]
    public void TheChecks_HaveEnglishTitlesExplanationsAndStatusWords()
    {
        Assert.Equal("Файл hosts", SecurityCheckIds.Title(SecurityCheckIds.Hosts));
        Assert.Equal("Опасно", SecurityStatus.Danger.ToRussian());

        using var language = TestLanguage.English();
        foreach (var id in SecurityCheckIds.All)
        {
            Assert.NotEmpty(SecurityCheckIds.Explanation(id));
            AssertEnglish(SecurityCheckIds.Title(id), SecurityCheckIds.Explanation(id));
        }
        Assert.Equal("Dangerous", SecurityStatus.Danger.ToRussian());
        Assert.Equal("Fine", SecurityStatus.Ok.ToRussian());
        Assert.Equal("5 days ago", SecurityRules.AgoText(5));
        Assert.Equal("yesterday", SecurityRules.AgoText(1));
        Assert.Equal("never", SecurityRules.AgoText(null));
        Assert.Equal("2 problems and 1 tip", SecurityRules.FindingsText(2, 1));
        Assert.Equal("no problems", SecurityRules.FindingsText(0, 0));
        Assert.Equal("Removed 1 line", SecurityRules.LinesRemovedText(1));
        Assert.Equal("Removed 3 lines", SecurityRules.LinesRemovedText(3));
        Assert.Equal("1 line will be removed", SecurityRules.LinesToRemoveText(1));
        Assert.Equal("5 lines will be removed", SecurityRules.LinesToRemoveText(5));
        Assert.Equal("public network", new FirewallProfileState("Public", false).DisplayName);
        Assert.Equal("Microsoft Defender", SecurityRules.ActiveAntivirusNames(
            new[] { SecurityRules.MakeProduct("Microsoft Defender", 0x61100), SecurityRules.MakeProduct("Kaspersky", 0x61100) },
            new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, RunningMode = "Normal" })[0]);
    }

    [Fact]
    public void TheDeviceChecks_AreEnglish()
    {
        using var language = TestLanguage.English();
        var results = new[]
        {
            SecurityRules.EvaluateSmb1(Smb1State.Unknown, isAdmin: false),
            SecurityRules.EvaluateSecureBoot(new SecureBootState(true, 0, true)),
            SecurityRules.EvaluateTpm(new TpmState(false, null, null, null), isAdmin: true),
            SecurityRules.EvaluateTpm(new TpmState(true, true, true, "1.2"), isAdmin: true),
            SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.Off, IsLaptop: true, IsHomeEdition: false)),
            SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.On, IsLaptop: false, IsHomeEdition: false)),
            SecurityRules.EvaluateMemoryIntegrity(DeviceGuardState.Unknown),
        };

        Assert.Equal("Secure Boot is off", results[1].State);
        Assert.Equal("TPM not found", results[2].State);
        Assert.Equal("TPM 1.2 — an old version", results[3].State);
        Assert.Equal("The laptop drive is not encrypted", results[4].State);
        Assert.Equal("Drive C: is protected by encryption: without the password or recovery key the data cannot be read, even if the drive is taken " +
                     "out of the computer.", results[5].Details);
        Assert.StartsWith("Secure Boot is turned on in the BIOS/UEFI settings:", SecurityRules.SecureBootHowTo);
        Assert.StartsWith("The TPM module is turned on in the BIOS/UEFI settings", SecurityRules.TpmHowTo);
        foreach (var result in results)
            AssertEnglish(result.State, result.Details);
        AssertEnglish(SecurityRules.SecureBootHowTo, SecurityRules.TpmHowTo);
    }

    [Fact]
    public void TheWordsOfTpmtoolAndYesNo_AreStillReadInTheWindowsLanguage()
    {
        using var language = TestLanguage.English();

        var state = SecurityRules.ParseTpmTool("-Наличие TPM: Да\r\n-Версия TPM: 2.0\r\n");

        Assert.True(state.Present);
        Assert.Equal("2.0", state.Version);
        Assert.False(SecurityRules.ParseYesNo("Нет"));
        Assert.True(SecurityRules.ParseYesNo("истина"));
    }

    [Fact]
    public void TheExtensions_AreEnglish_AndANamedBundledExtensionIsStillFound()
    {
        using var language = TestLanguage.English();
        var ext = new BrowserExtension
        {
            Id = "abc", Name = "Visual Bookmarks by Yandex", Browser = "Chrome", Profile = "", Version = "1.0", Enabled = false,
            Source = ExtensionSource.OtherProgram, Kind = BrowserKind.Chromium,
        };
        SecurityRules.ClassifyExtension(ext);

        Assert.Equal(SecurityStatus.Info, ext.Risk);
        Assert.Equal("Replaces the new tab and is often installed along with other programs. It is off now, but it is better to remove it. " +
                     "It was installed by another program, not by you.", ext.Reason);
        Assert.Equal("Another program", ext.SourceText);
        Assert.Equal("Disabled", ext.StateText);
        Assert.Equal("Version: 1.0", ext.VersionLabel);
        Assert.Equal("Source: Another program", ext.SourceLabel);

        var adware = new BrowserExtension { Id = "d", Name = "Super Coupon Finder", Browser = "Edge", Enabled = true, Source = ExtensionSource.Store, Kind = BrowserKind.Chromium };
        SecurityRules.ClassifyExtension(adware);
        Assert.Equal("The name looks like an adware extension (“coupon”): such extensions often replace the search and the start page.", adware.Reason);
        var card = SecurityRules.EvaluateExtensions(new[] { adware }, 1);
        Assert.Equal("Suspicious: 1 of 1", card.State);
        Assert.StartsWith("For example, “Super Coupon Finder” (Edge). Extensions are removed in the browser itself", card.Details);
    }

    // ================================================================ sweep

    [Fact]
    public async Task AScanWithManyProblems_HasNoRussianLeft_InEnglish()
    {
        using var sb = new SecuritySandbox();
        WriteHosts(sb, "127.0.0.1 localhost", "203.0.113.9 github.com", "0.0.0.0 avast.test", "garbage line", "10.0.0.5 old-nas.test");
        using (var key = sb.Create(sb.Env.UacPolicies))
        {
            key.SetValue("EnableLUA", 0, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord);
        }
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "explorer.exe, " + Evil);
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        using (var key = sb.Create(sb.Env.Ifeo))
        {
            using var taskmgr = key.CreateSubKey("taskmgr.exe");
            taskmgr.SetValue("Debugger", Evil);
            using var other = key.CreateSubKey("notepad.exe");
            other.SetValue("Debugger", @"C:\Tools\mynotepad.exe");
        }
        using (var key = sb.Create(sb.Env.InternetSettings))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "127.0.0.1:8080");
            key.SetValue("AutoConfigURL", "http://203.0.113.1/proxy.pac");
        }
        using (var key = sb.Create(sb.Env.SmartScreenPolicy))
            key.SetValue("EnableSmartScreen", 0, RegistryValueKind.DWord);
        using (var key = sb.Create(sb.Env.TerminalServer))
            key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);

        using var language = TestLanguage.English();
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        var issues = sb.Service.BuildIssues(report);

        Assert.True(issues.Count >= 7, "the scan should find many problems, got " + issues.Count);
        foreach (var check in report.Checks)
            AssertEnglish(SecurityCheckIds.Title(check.Id), check.State, check.Details);
        foreach (var issue in issues)
            AssertEnglish(issue.Module, issue.Title, issue.Details, issue.FixLabel, issue.Gain, issue.Consequence);
        foreach (var entry in report.Hosts.Entries)
            AssertEnglish(entry.Reason);
        foreach (var hijack in report.Ifeo)
            AssertEnglish(hijack.Reason, hijack.ViewText);
        // The details of every dashboard issue say what the fix will do in English as well.
        foreach (var issue in issues.Where(i => i.FixAsync is not null))
            AssertEnglish(issue.Details);

        var page = new SecurityViewModel(sb.Service);
        Assert.Equal("Security", page.Title);
        AssertEnglish(page.Subtitle, page.OverallText, page.OkCountText, page.InfoCountText, page.WarningCountText, page.DangerCountText,
            page.UnknownCountText, page.AttentionHeader, page.OkHeader, page.HostsTabHeader, page.ThreatsTabHeader, page.RemoveThreatsText,
            page.ExtensionsTabHeader, page.IfeoTabHeader, page.RemoveHostsText, page.ThreatsEmptyText, page.ExtensionsEmptyText);
        foreach (var card in page.BuildCards(report))
        {
            AssertEnglish(card.Title, card.State, card.Details, card.Explanation);
            foreach (var action in card.Actions)
                AssertEnglish(action.Label, action.ToolTip);
        }
    }
}
