using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>
/// Sandbox: registry under HKCU\Software\WaterCa7sPcCleanerTests\Security\&lt;guid&gt; and files in a temp folder.
/// Deleted again in Dispose. Nothing on the real system is touched.
/// </summary>
public sealed class SecuritySandbox : IDisposable
{
    /// <summary>
    /// Test classes that create sandboxes run one after another: Dispose removes the shared parent key when it is empty,
    /// and a sandbox created in parallel under that key failed with "marked for deletion".
    /// </summary>
    public const string Collection = "SecurityRegistrySandbox";

    public const string RootKey = @"Software\WaterCa7sPcCleanerTests\Security";

    public SecuritySandbox()
    {
        TestEnvironment.Init();
        KeyPath = RootKey + "\\" + Guid.NewGuid().ToString("N");
        TempDir = TestEnvironment.NewTempDir("security");
        Env = SecurityEnvironment.CreateSandbox(KeyPath, TempDir);
        Service = new SecurityService(Env);
    }

    public string KeyPath { get; }
    public string TempDir { get; }
    public SecurityEnvironment Env { get; }
    public SecurityService Service { get; }

    public RegistryKey Create(RegLocation loc) => loc.Create();

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            DeleteIfEmpty(RootKey);
            DeleteIfEmpty(@"Software\WaterCa7sPcCleanerTests");
        }
        catch (Exception)
        {
            // best effort; other test classes may be using the parent keys concurrently
        }
        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                return;
        }
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}

[CollectionDefinition(SecuritySandbox.Collection, DisableParallelization = true)]
public sealed class SecuritySandboxCollection
{
}

[Collection(SecuritySandbox.Collection)]
public class SecurityServiceSandboxTests
{
    private static int BackupCount() => Directory.Exists(AppPaths.BackupsDir) ? Directory.GetFiles(AppPaths.BackupsDir, "*.reg").Length : 0;

    [Fact]
    public void Sandbox_PointsOnlyToHkcuTestKeyAndTempFolder()
    {
        using var sb = new SecuritySandbox();
        var locations = new[]
        {
            sb.Env.UacPolicies, sb.Env.Winlogon, sb.Env.UserWinlogon, sb.Env.Ifeo, sb.Env.IfeoWow, sb.Env.InternetSettings,
            sb.Env.ExplorerKey, sb.Env.SmartScreenPolicy, sb.Env.TerminalServer, sb.Env.DefenderPolicy, sb.Env.FirewallPolicy,
            sb.Env.LanmanServerParameters, sb.Env.Smb1ServerDriver, sb.Env.Smb1ClientDriver, sb.Env.SecureBootState,
            sb.Env.HvciScenario, sb.Env.WindowsVersion,
        };
        Assert.All(locations, l =>
        {
            Assert.Equal(RegistryHive.CurrentUser, l.Hive);
            Assert.StartsWith(SecuritySandbox.RootKey, l.SubKey);
            Assert.False(l.NeedsAdmin);
        });
        Assert.StartsWith(sb.TempDir, sb.Env.HostsPath);
        Assert.False(sb.Env.IsRealSystem);
    }

    [Fact]
    public async Task DisableProxy_TurnsOffProxyRemovesPacKeepsServer()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.InternetSettings))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "1.2.3.4:8080");
            key.SetValue("AutoConfigURL", "http://evil.example/proxy.pac");
            key.SetValue("Other", "keep");
        }
        var before = BackupCount();

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Warning, report.Check(SecurityCheckIds.Proxy)!.Status);

        var result = await sb.Service.DisableProxyAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        using (var key = sb.Env.InternetSettings.Open())
        {
            Assert.Equal(0, key!.GetValue("ProxyEnable"));
            Assert.Null(key.GetValue("AutoConfigURL"));
            Assert.Equal("1.2.3.4:8080", key.GetValue("ProxyServer"));
            Assert.Equal("keep", key.GetValue("Other"));
        }
        Assert.True(BackupCount() > before);
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Ok, after.Check(SecurityCheckIds.Proxy)!.Status);
    }

    [Fact]
    public async Task RemoveIfeoDebugger_DeletesOnlyDebuggerValue()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Ifeo.Child("taskmgr.exe")))
        {
            key.SetValue("Debugger", @"C:\Users\Public\evil.exe");
            key.SetValue("GlobalFlag", 512, RegistryValueKind.DWord);
        }
        using (sb.Create(sb.Env.Ifeo.Child("clean.exe")))
        {
        }

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        var entry = Assert.Single(report.Ifeo);
        Assert.Equal("taskmgr.exe", entry.ImageName);
        Assert.Equal(SecurityStatus.Danger, entry.Risk);
        Assert.Equal(SecurityStatus.Danger, report.Check(SecurityCheckIds.Ifeo)!.Status);

        var result = await sb.Service.RemoveIfeoDebuggerAsync(entry, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        using (var key = sb.Env.Ifeo.Child("taskmgr.exe").Open())
        {
            Assert.NotNull(key);
            Assert.Null(key!.GetValue("Debugger"));
            Assert.Equal(512, key.GetValue("GlobalFlag"));
        }
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Ok, after.Check(SecurityCheckIds.Ifeo)!.Status);
    }

    [Fact]
    public async Task RemoveIfeoDebugger_ChangedValue_ChangesNothing()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.IfeoWow.Child("avp.exe")))
            key.SetValue("Debugger", "svchost.exe");
        var entry = Assert.Single((await sb.Service.ScanAsync(null, CancellationToken.None)).Ifeo);
        Assert.True(entry.IsWow64);
        using (var key = sb.Env.IfeoWow.Child("avp.exe").Open(writable: true))
            key!.SetValue("Debugger", "other.exe");

        var result = await sb.Service.RemoveIfeoDebuggerAsync(entry, CancellationToken.None);

        Assert.False(result.Success);
        using var check = sb.Env.IfeoWow.Child("avp.exe").Open();
        Assert.Equal("other.exe", check!.GetValue("Debugger"));
    }

    [Fact]
    public async Task RestoreWinlogon_RestoresDefaultsAndRemovesUserShell()
    {
        using var sb = new SecuritySandbox();
        var win = sb.Env.WindowsDir;
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", @"explorer.exe, C:\Users\Public\evil.exe");
            key.SetValue("Userinit", Path.Combine(win, "system32", "userinit.exe") + @",C:\Users\Public\evil2.exe,");
            key.SetValue("AutoRestartShell", 1, RegistryValueKind.DWord);
        }
        using (var key = sb.Create(sb.Env.UserWinlogon))
            key.SetValue("Shell", @"C:\Users\Public\evil3.exe");

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Danger, report.Check(SecurityCheckIds.Winlogon)!.Status);

        var result = await sb.Service.RestoreWinlogonAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        using (var key = sb.Env.Winlogon.Open())
        {
            Assert.Equal("explorer.exe", key!.GetValue("Shell"));
            Assert.Equal(SecurityRules.DefaultUserinit(win), key.GetValue("Userinit"));
            Assert.Equal(1, key.GetValue("AutoRestartShell"));
        }
        using (var key = sb.Env.UserWinlogon.Open())
            Assert.Null(key!.GetValue("Shell"));
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Ok, after.Check(SecurityCheckIds.Winlogon)!.Status);
    }

    [Fact]
    public async Task RestoreWinlogon_ValuesChangedAfterConfirmation_ChangesNothing()
    {
        using var sb = new SecuritySandbox();
        var win = sb.Env.WindowsDir;
        var defaultUserinit = SecurityRules.DefaultUserinit(win);
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", @"C:\Users\Public\evil.exe");
            key.SetValue("Userinit", defaultUserinit);
        }
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        // The user confirmed "Shell → explorer.exe" only; then something rewrites Userinit.
        using (var key = sb.Env.Winlogon.Open(writable: true))
            key!.SetValue("Userinit", defaultUserinit + @"C:\Users\Public\other.exe,");

        var result = await sb.Service.RestoreWinlogonAsync(report.Winlogon, CancellationToken.None);

        Assert.False(result.Success);
        using var check = sb.Env.Winlogon.Open();
        Assert.Equal(@"C:\Users\Public\evil.exe", check!.GetValue("Shell"));
        Assert.Equal(defaultUserinit + @"C:\Users\Public\other.exe,", check.GetValue("Userinit"));
    }

    [Fact]
    public async Task RestoreWinlogon_WithConfirmedValues_ChangesOnlyWrongValue()
    {
        using var sb = new SecuritySandbox();
        var defaultUserinit = SecurityRules.DefaultUserinit(sb.Env.WindowsDir).ToUpperInvariant(); // default, other case
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", @"C:\Users\Public\evil.exe");
            key.SetValue("Userinit", defaultUserinit);
        }
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        var result = await sb.Service.RestoreWinlogonAsync(report.Winlogon, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        using var check = sb.Env.Winlogon.Open();
        Assert.Equal("explorer.exe", check!.GetValue("Shell"));
        Assert.Equal(defaultUserinit, check.GetValue("Userinit")); // already default: untouched
    }

    [Fact]
    public async Task Ifeo_SameDebuggerInBothViews_IsListedOnce()
    {
        // On real 64-bit Windows the WOW6432Node IFEO key is the same physical key, so one hijack appears in both views.
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Ifeo.Child("taskmgr.exe")))
            key.SetValue("Debugger", @"C:\Users\Public\evil.exe");
        using (var key = sb.Create(sb.Env.IfeoWow.Child("Taskmgr.exe")))
            key.SetValue("Debugger", @"C:\Users\Public\evil.exe");
        using (var key = sb.Create(sb.Env.IfeoWow.Child("game.exe")))
            key.SetValue("Debugger", @"C:\Tools\wrapper.exe");

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        Assert.Equal(2, report.Ifeo.Count);
        var taskmgr = Assert.Single(report.Ifeo, e => e.ImageName.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(taskmgr.IsWow64);
        Assert.Single(report.Ifeo, e => e.ImageName == "game.exe" && e.IsWow64);

        // In the sandbox the two keys are separate: removing one leaves the other, which the next scan shows again.
        Assert.True((await sb.Service.RemoveIfeoDebuggerAsync(taskmgr, CancellationToken.None)).Success);
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Single(after.Ifeo, e => e.ImageName == "Taskmgr.exe" && e.IsWow64);
    }

    [Fact]
    public void FirewallPolicy_IsReadFromPolicyKey()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.FirewallPolicy.Child("StandardProfile")))
            key.SetValue("EnableFirewall", 0, RegistryValueKind.DWord);
        using (var key = sb.Create(sb.Env.FirewallPolicy.Child("PublicProfile")))
            key.SetValue("EnableFirewall", 1, RegistryValueKind.DWord);

        var policy = sb.Service.ReadFirewallPolicy();

        Assert.Equal(0, policy["Private"]);
        Assert.Equal(1, policy["Public"]);
        Assert.Null(policy["Domain"]);
    }

    [Fact]
    public void BuildIssues_FirewallOffOnlyByPolicy_HasNoFix()
    {
        using var sb = new SecuritySandbox();
        var checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();
        checks[checks.FindIndex(c => c.Id == SecurityCheckIds.Firewall)] =
            new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Danger, "Выключен для частной сети", "d");
        var profiles = new[]
        {
            new FirewallProfileState("Private", false, ByPolicy: true), new FirewallProfileState("Public", true),
            new FirewallProfileState("Domain", true),
        };
        var byPolicy = new SecurityReport { FirewallProfiles = profiles, Checks = checks };
        var issue = Assert.Single(sb.Service.BuildIssues(byPolicy));
        Assert.Null(issue.FixAsync);
        Assert.Contains("политик", issue.Details);

        var local = new SecurityReport
        {
            FirewallProfiles = new[] { profiles[0] with { ByPolicy = false }, profiles[1], profiles[2] },
            Checks = checks,
        };
        Assert.NotNull(Assert.Single(sb.Service.BuildIssues(local)).FixAsync);
    }

    [Fact]
    public void BuildIssues_ThreatAllowedByUser_IsNotActiveThreat()
    {
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            Defender = new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0 },
            Threats = new[] { new ThreatDetection { Name = "HackTool:Win32/Keygen", StatusId = 5 } },
            Checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList(),
        };
        Assert.Empty(sb.Service.BuildIssues(report));

        var active = new SecurityReport
        {
            Defender = report.Defender,
            Threats = new[] { new ThreatDetection { Name = "Trojan:Win32/Test", StatusId = 1 } },
            Checks = report.Checks,
        };
        Assert.Equal(IssueSeverity.High, Assert.Single(sb.Service.BuildIssues(active)).Severity);
    }

    [Fact]
    public async Task ViewModel_UnknownChecks_AreNotReportedAsFullyProtected()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "explorer.exe");
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        var vm = new SecurityViewModel(sb.Service);

        await vm.PreviewScanAsync(CancellationToken.None);

        // In the sandbox antivirus, firewall and updates cannot be read (no WMI): they are "Нет данных".
        Assert.Equal(0, vm.DangerCount);
        Assert.Equal(0, vm.WarningCount);
        Assert.True(vm.UnknownCount > 0);
        Assert.Equal(SecurityStatus.Info, vm.OverallStatus);
        Assert.DoesNotContain("хорошо защищён", vm.OverallText);
        Assert.Contains("не удалось", vm.OverallText);
    }

    [Fact]
    public async Task EnableUac_SetsDefaults()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.UacPolicies))
        {
            key.SetValue("EnableLUA", 0, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord);
        }
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Danger, report.Check(SecurityCheckIds.Uac)!.Status);

        var result = await sb.Service.EnableUacAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains("Перезагрузите", result.Message);
        using var check = sb.Env.UacPolicies.Open();
        Assert.Equal(1, check!.GetValue("EnableLUA"));
        Assert.Equal(5, check.GetValue("ConsentPromptBehaviorAdmin"));
    }

    [Fact]
    public async Task EnableUac_KeepsStricterPromptSetting()
    {
        // Regression: "Включить UAC" used to lower "Всегда уведомлять" (2) to the default (5).
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.UacPolicies))
        {
            key.SetValue("EnableLUA", 0, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 2, RegistryValueKind.DWord);
        }

        var result = await sb.Service.EnableUacAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains("Перезагрузите", result.Message);
        using var check = sb.Env.UacPolicies.Open();
        Assert.Equal(1, check!.GetValue("EnableLUA"));
        Assert.Equal(2, check.GetValue("ConsentPromptBehaviorAdmin"));
    }

    [Fact]
    public async Task EnableUac_OnlyPromptsOff_ChangesOnlyPrompt_AndNothingWhenAlreadyOn()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.UacPolicies))
        {
            key.SetValue("EnableLUA", 1, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord);
        }

        var result = await sb.Service.EnableUacAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("Перезагрузите", result.Message);
        using (var check = sb.Env.UacPolicies.Open())
        {
            Assert.Equal(1, check!.GetValue("EnableLUA"));
            Assert.Equal(5, check.GetValue("ConsentPromptBehaviorAdmin"));
        }

        var guid = sb.KeyPath[(sb.KeyPath.LastIndexOf('\\') + 1)..];
        int OwnBackups() => Directory.GetFiles(AppPaths.BackupsDir, "*.reg").Count(f => f.Contains(guid, StringComparison.OrdinalIgnoreCase));
        var backups = OwnBackups();
        Assert.Equal(1, backups);
        var again = await sb.Service.EnableUacAsync(CancellationToken.None);
        Assert.True(again.Success, again.Message);
        Assert.Contains("ничего не нужно", again.Message);
        Assert.Equal(backups, OwnBackups()); // nothing to change: no backup, no write
    }

    [Theory]
    [InlineData(@"file:_C:\Users\a\setup.exe", @"C:\Users\a\setup.exe")]
    [InlineData(@"containerfile:_C:\Users\a\pack.zip", @"C:\Users\a\pack.zip")]
    [InlineData(@"regkey:_HKLM\SOFTWARE\X", @"HKLM\SOFTWARE\X")]
    [InlineData(@"C:\plain\path.exe", @"C:\plain\path.exe")]
    public void CleanResource_StripsDefenderPrefix(string raw, string expected)
        => Assert.Equal(expected, SecurityService.CleanResource(raw));

    [Fact]
    public void BuildIssues_InactiveThirdPartyAntivirus_IsNamed()
    {
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            Defender = new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = false, RealTimeEnabled = false },
            Antiviruses = new[] { SecurityRules.MakeProduct("Kaspersky Anti-Virus", 0x43100) },
            Checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList(),
        };
        var issue = Assert.Single(sb.Service.BuildIssues(report));
        Assert.Equal("Антивирус выключен", issue.Title);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Contains("Kaspersky Anti-Virus установлен", issue.Details);
    }

    [Fact]
    public async Task BuildIssues_HostsAndExtensions_TextsFitTheDashboard()
    {
        using var sb = new SecuritySandbox();
        var ext = Path.Combine(sb.Env.BrowserRoots[0].Path, "Default", "Extensions", new string('a', 32), "1.0_0");
        Directory.CreateDirectory(ext);
        File.WriteAllText(Path.Combine(ext, "manifest.json"), """{ "name": "Search Manager Plus", "version": "1.0" }""");
        var scanned = await sb.Service.ScanAsync(null, CancellationToken.None);
        // The redirect is evaluated in memory: a hosts file with such a line is never written to disk.
        var entries = HostsFile.Parse("1.2.3.4 www.sberbank.ru");
        entries.ForEach(HostsRules.Classify);
        var hosts = new HostsReport { Path = "hosts", Entries = entries };
        var report = new SecurityReport
        {
            Hosts = hosts,
            Extensions = scanned.Extensions,
            BrowsersFound = scanned.BrowsersFound,
            Checks = scanned.Checks.Select(c => c.Id == SecurityCheckIds.Hosts ? SecurityRules.EvaluateHosts(hosts) : c).ToList(),
        };

        var issues = sb.Service.BuildIssues(report);

        var hostsIssue = issues.Single(i => i.Title == "Файл hosts подменяет адреса сайтов");
        Assert.Contains("www.sberbank.ru", hostsIssue.Details);
        Assert.Contains("«Подробнее»", hostsIssue.Details);
        Assert.DoesNotContain("ниже", hostsIssue.Details); // there is no list "below" on the dashboard
        var extIssue = issues.Single(i => i.Title == "Подозрительные расширения в браузере");
        Assert.Contains("Search Manager Plus", extIssue.Details);
        Assert.DoesNotContain("«Как удалить»", extIssue.Details);
    }

    [Fact]
    public async Task RemoveHostsLines_OnTempCopy()
    {
        using var sb = new SecuritySandbox();
        // A harmless custom line (private address, reserved .test name) — never a hijack-looking file on disk.
        File.WriteAllText(sb.Env.HostsPath, "# test\r\n127.0.0.1 localhost\r\n10.0.0.5 old-nas.test\r\n");
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Ok, report.Check(SecurityCheckIds.Hosts)!.Status);
        var custom = report.Hosts.Entries.Where(e => e.Hostnames.Contains("old-nas.test")).ToList();
        Assert.Equal(SecurityStatus.Info, Assert.Single(custom).Risk);

        var result = await sb.Service.RemoveHostsLinesAsync(custom, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("# test\r\n127.0.0.1 localhost\r\n", File.ReadAllText(sb.Env.HostsPath));
    }

    [Fact]
    public async Task Scan_Sandbox_ProducesAllChecksWithoutSystemAccess()
    {
        using var sb = new SecuritySandbox();
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        Assert.Equal(SecurityCheckIds.All.Length, report.Checks.Count);
        Assert.Equal(SecurityCheckIds.All, report.Checks.Select(c => c.Id));
        Assert.All(report.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.State)));
        Assert.Equal(0, report.BrowsersFound);
        Assert.Null(report.Defender);
        Assert.Same(report, sb.Service.LastReport);
    }

    [Fact]
    public async Task SystemActions_AreInertInSandbox()
    {
        using var sb = new SecuritySandbox();
        Assert.False((await sb.Service.EnableFirewallAsync(CancellationToken.None)).Success);
        Assert.False((await sb.Service.EnableRealtimeProtectionAsync(CancellationToken.None)).Success);
        Assert.False((await sb.Service.UpdateSignaturesAsync(CancellationToken.None)).Success);
        Assert.False((await sb.Service.RunDefenderScanAsync(false, CancellationToken.None)).Success);
    }

    [Fact]
    public void BuildIssues_MapsSeverityAndFixes()
    {
        using var sb = new SecuritySandbox();
        var checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();
        void SetStatus(string id, SecurityStatus s, string state = "state")
            => checks[checks.FindIndex(c => c.Id == id)] = new SecurityCheckResult(id, s, state, "details");
        SetStatus(SecurityCheckIds.Firewall, SecurityStatus.Danger, "Выключен для частной сети");
        SetStatus(SecurityCheckIds.Uac, SecurityStatus.Danger);
        SetStatus(SecurityCheckIds.Hosts, SecurityStatus.Danger);
        SetStatus(SecurityCheckIds.Winlogon, SecurityStatus.Danger);
        SetStatus(SecurityCheckIds.Ifeo, SecurityStatus.Danger);
        SetStatus(SecurityCheckIds.Proxy, SecurityStatus.Warning, "Включён прокси 1.2.3.4:80");
        SetStatus(SecurityCheckIds.Updates, SecurityStatus.Warning, "Обновлений не было 90 дней");
        SetStatus(SecurityCheckIds.Extensions, SecurityStatus.Warning);

        var report = new SecurityReport
        {
            Defender = new DefenderStatus
            {
                ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = false, SignatureAgeDays = 10,
            },
            Checks = checks,
        };
        var issues = sb.Service.BuildIssues(report);

        Assert.All(issues, i =>
        {
            Assert.Equal("Security", i.TargetPageKey);
            Assert.Equal("Безопасность", i.Module);
            Assert.False(string.IsNullOrWhiteSpace(i.Title));
            Assert.False(string.IsNullOrWhiteSpace(i.Details));
        });
        var rt = issues.Single(i => i.Title == "Защита в реальном времени выключена");
        Assert.Equal(IssueSeverity.Critical, rt.Severity);
        Assert.NotNull(rt.FixAsync);
        Assert.False(rt.AutoFixSafe);

        var sig = issues.Single(i => i.Title == "Антивирусные базы устарели");
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.True(sig.AutoFixSafe);
        Assert.NotNull(sig.FixAsync);

        Assert.Equal(IssueSeverity.High, issues.Single(i => i.Title == "Брандмауэр Windows выключен").Severity);
        Assert.Equal(IssueSeverity.High, issues.Single(i => i.Title.StartsWith("Контроль учётных записей")).Severity);
        var hosts = issues.Single(i => i.Title.StartsWith("Файл hosts"));
        Assert.Equal(IssueSeverity.High, hosts.Severity);
        Assert.Null(hosts.FixAsync);
        Assert.Equal(IssueSeverity.Critical, issues.Single(i => i.Title.StartsWith("Подменён запуск")).Severity);
        Assert.Equal(IssueSeverity.Critical, issues.Single(i => i.Title.StartsWith("Перехвачен запуск")).Severity);
        Assert.Equal(IssueSeverity.Medium, issues.Single(i => i.Title.StartsWith("Включён прокси")).Severity);
        Assert.Equal(IssueSeverity.Medium, issues.Single(i => i.Title == "Windows давно не обновлялась").Severity);
        Assert.Equal(IssueSeverity.Low, issues.Single(i => i.Title == "Подозрительные расширения в браузере").Severity);
    }

    [Fact]
    public async Task BuildIssues_FixInSandboxChangesNothing()
    {
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            Defender = new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 10 },
            Checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList(),
        };
        var sig = sb.Service.BuildIssues(report).Single();
        var message = await sig.FixAsync!(CancellationToken.None);
        Assert.Contains("тестовом", message);
    }

    [Fact]
    public void ThirdPartyAntivirus_NoDefenderIssues()
    {
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            Defender = new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = false, RealTimeEnabled = false },
            Antiviruses = new[] { SecurityRules.MakeProduct("ESET Security", 266240) },
            Checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList(),
        };
        Assert.Empty(sb.Service.BuildIssues(report));
    }

    [Fact]
    public async Task ViewModel_PreviewScan_PopulatesCards()
    {
        using var sb = new SecuritySandbox();
        File.WriteAllText(sb.Env.HostsPath, "10.0.0.5 old-nas.test\r\n0.0.0.0 ads.example.com\r\n");
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "custom-shell.exe");
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        var vm = new SecurityViewModel(sb.Service);

        await vm.PreviewScanAsync(CancellationToken.None);

        Assert.True(vm.HasData);
        Assert.Equal(SecurityCheckIds.All.Length, vm.Checks.Count);
        Assert.Equal(2, vm.HostsEntries.Count);
        Assert.Equal(0, vm.SelectedHostsCount); // only dangerous lines are pre-selected
        Assert.True(vm.DangerCount >= 1);
        Assert.Equal(SecurityCheckIds.Winlogon, vm.Checks[0].Id); // the danger comes first
        Assert.InRange(vm.Score, 0, 99);
        Assert.Equal(2, vm.Checks.Single(c => c.Id == SecurityCheckIds.Antivirus).ColumnSpan);
    }
}
