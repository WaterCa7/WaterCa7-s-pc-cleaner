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
            sb.Env.ExplorerKey, sb.Env.SmartScreenPolicy, sb.Env.TerminalServer, sb.Env.DefenderPolicy,
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
    public async Task RemoveHostsLines_OnTempCopy()
    {
        using var sb = new SecuritySandbox();
        File.WriteAllText(sb.Env.HostsPath, "# test\r\n127.0.0.1 localhost\r\n1.2.3.4 www.sberbank.ru\r\n");
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Danger, report.Check(SecurityCheckIds.Hosts)!.Status);
        var bad = report.Hosts.Entries.Where(e => e.Risk == SecurityStatus.Danger).ToList();

        var result = await sb.Service.RemoveHostsLinesAsync(bad, CancellationToken.None);

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
        File.WriteAllText(sb.Env.HostsPath, "1.2.3.4 vk.com\r\n0.0.0.0 ads.example.com\r\n");
        var vm = new SecurityViewModel(sb.Service);

        await vm.PreviewScanAsync(CancellationToken.None);

        Assert.True(vm.HasData);
        Assert.Equal(SecurityCheckIds.All.Length, vm.Checks.Count);
        Assert.Equal(2, vm.HostsEntries.Count);
        Assert.Equal(1, vm.SelectedHostsCount); // the dangerous line is pre-selected
        Assert.True(vm.DangerCount >= 1);
        Assert.InRange(vm.Score, 0, 99);
        Assert.Equal(2, vm.Checks.Single(c => c.Id == SecurityCheckIds.Antivirus).ColumnSpan);
    }
}
