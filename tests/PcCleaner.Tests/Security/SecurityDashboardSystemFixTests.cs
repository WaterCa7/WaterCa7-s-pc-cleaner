using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>
/// The dashboard fixes of the hosts file, Winlogon, IFEO hijacks and the proxy. They run on the SecuritySandbox (HKCU test
/// keys and a temp hosts file); the restore point is only recorded, never created.
/// </summary>
[Collection(SecuritySandbox.Collection)]
public class SecurityDashboardSystemFixTests
{
    private const string HostsTitle = "Файл hosts подменяет адреса сайтов";
    private const string WinlogonTitle = "Подменён запуск при входе в Windows";
    private const string IfeoTitle = "Перехвачен запуск системных программ";
    private const string Evil = @"C:\Users\Public\evil.exe";

    public SecurityDashboardSystemFixTests() => TestEnvironment.Init();

    private static async Task<(HealthIssue Issue, SecurityReport Report)> ScanIssueAsync(SecuritySandbox sb, string titlePrefix)
    {
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        var issue = sb.Service.BuildIssues(report).Single(i => i.Title.StartsWith(titlePrefix, StringComparison.Ordinal));
        return (issue, report);
    }

    /// <summary>Replaces the restore point: the descriptions asked for are collected, <paramref name="atCall"/> runs at that moment.</summary>
    private static List<string> RecordRestorePoints(SecuritySandbox sb, Action? atCall = null)
    {
        var calls = new List<string>();
        sb.Service.CreateRestorePointAsync = (description, _) =>
        {
            calls.Add(description);
            atCall?.Invoke();
            return Task.FromResult(true);
        };
        return calls;
    }

    private static string? ValueOf(RegLocation loc, string name)
    {
        using var key = loc.Open();
        return key?.GetValue(name)?.ToString();
    }

    // ===================== hosts =====================

    private static void WriteHosts(SecuritySandbox sb, params string[] lines)
        => File.WriteAllText(sb.Env.HostsPath, string.Join("\r\n", lines) + "\r\n");

    [Fact]
    public async Task Hosts_Fix_RemovesTheHijackLinesAndKeepsTheOthers()
    {
        using var sb = new SecuritySandbox();
        // Popular sites on a foreign address are a hijack (Danger); blocking an antivirus site is only a Warning.
        WriteHosts(sb, "# test hosts", "127.0.0.1 localhost", "203.0.113.9 github.com", "0.0.0.0 avast.test",
            "203.0.113.9 www.vk.com", "10.0.0.5 old-nas.test");
        var (issue, report) = await ScanIssueAsync(sb, HostsTitle);
        Assert.Equal(2, report.Hosts.Entries.Count(e => e.Risk == SecurityStatus.Danger));
        Assert.Equal("Убрать подмену", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.Equal(IssueSeverity.High, issue.Severity);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        Assert.Equal("# test hosts\r\n127.0.0.1 localhost\r\n0.0.0.0 avast.test\r\n10.0.0.5 old-nas.test\r\n",
            File.ReadAllText(sb.Env.HostsPath));
        Assert.Contains(AppPaths.BackupsDir, text); // the copy of the old file is named in the result
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.NotEqual(SecurityStatus.Danger, after.Check(SecurityCheckIds.Hosts)!.Status);
    }

    [Fact]
    public async Task Hosts_Fix_FileChangedAfterTheScan_ChangesNothing()
    {
        using var sb = new SecuritySandbox();
        WriteHosts(sb, "127.0.0.1 localhost", "203.0.113.9 github.com");
        var (issue, _) = await ScanIssueAsync(sb, HostsTitle);
        // Something adds a line at the top: the line numbers of the scan no longer fit.
        WriteHosts(sb, "# added later", "127.0.0.1 localhost", "203.0.113.9 github.com");
        var changed = File.ReadAllText(sb.Env.HostsPath);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.StartsWith("Не удалось", text);
        Assert.Equal(changed, File.ReadAllText(sb.Env.HostsPath));
    }

    [Fact]
    public async Task Hosts_WarningOnly_HasNoFix()
    {
        using var sb = new SecuritySandbox();
        WriteHosts(sb, "127.0.0.1 localhost", "0.0.0.0 avast.test");

        var (issue, _) = await ScanIssueAsync(sb, "В файле hosts есть записи");

        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Null(issue.FixAsync);
    }

    // ===================== Winlogon =====================

    private static void SetBrokenWinlogon(SecuritySandbox sb)
    {
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", @"explorer.exe, " + Evil);
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir) + Evil + ",");
            key.SetValue("AutoRestartShell", 1, RegistryValueKind.DWord);
        }
        using (var key = sb.Create(sb.Env.UserWinlogon))
            key.SetValue("Shell", Evil);
    }

    [Fact]
    public async Task Winlogon_Fix_CreatesTheRestorePointFirstThenRestoresTheDefaults()
    {
        using var sb = new SecuritySandbox();
        SetBrokenWinlogon(sb);
        string? shellWhenRestorePointWasMade = null;
        var restorePoints = RecordRestorePoints(sb, () => shellWhenRestorePointWasMade = ValueOf(sb.Env.Winlogon, "Shell"));
        var (issue, _) = await ScanIssueAsync(sb, WinlogonTitle);
        Assert.Equal("Восстановить", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        Assert.Equal("Восстановление параметров входа (Winlogon)", Assert.Single(restorePoints));
        Assert.Equal(@"explorer.exe, " + Evil, shellWhenRestorePointWasMade); // the point comes before the change
        Assert.Equal("explorer.exe", ValueOf(sb.Env.Winlogon, "Shell"));
        Assert.Equal(SecurityRules.DefaultUserinit(sb.Env.WindowsDir), ValueOf(sb.Env.Winlogon, "Userinit"));
        Assert.Equal("1", ValueOf(sb.Env.Winlogon, "AutoRestartShell"));
        Assert.Null(ValueOf(sb.Env.UserWinlogon, "Shell"));
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Ok, after.Check(SecurityCheckIds.Winlogon)!.Status);
    }

    [Fact]
    public async Task Winlogon_Fix_OnlyTheUserShellIsWrong_NeedsNoRestorePoint()
    {
        // The per-user Shell is in HKCU: like on the Security page, only the machine-wide values get a restore point.
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "explorer.exe");
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        using (var key = sb.Create(sb.Env.UserWinlogon))
            key.SetValue("Shell", Evil);
        var restorePoints = RecordRestorePoints(sb);
        var (issue, _) = await ScanIssueAsync(sb, WinlogonTitle);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        Assert.Empty(restorePoints);
        Assert.Null(ValueOf(sb.Env.UserWinlogon, "Shell"));
    }

    [Fact]
    public async Task Winlogon_Fix_ValuesChangedAfterTheScan_ChangesNothing()
    {
        using var sb = new SecuritySandbox();
        SetBrokenWinlogon(sb);
        RecordRestorePoints(sb);
        var (issue, _) = await ScanIssueAsync(sb, WinlogonTitle);
        using (var key = sb.Env.Winlogon.Open(writable: true))
            key!.SetValue("Userinit", @"C:\Users\Public\other.exe,");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.StartsWith("Не удалось", text);
        Assert.Equal(@"explorer.exe, " + Evil, ValueOf(sb.Env.Winlogon, "Shell"));
        Assert.Equal(@"C:\Users\Public\other.exe,", ValueOf(sb.Env.Winlogon, "Userinit"));
        Assert.Equal(Evil, ValueOf(sb.Env.UserWinlogon, "Shell"));
    }

    // ===================== IFEO =====================

    [Fact]
    public async Task Ifeo_Fix_RemovesEveryDangerousHijackAfterTheRestorePoint()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Ifeo.Child("taskmgr.exe")))
        {
            key.SetValue("Debugger", Evil);
            key.SetValue("GlobalFlag", 512, RegistryValueKind.DWord);
        }
        using (var key = sb.Create(sb.Env.IfeoWow.Child("regedit.exe")))
            key.SetValue("Debugger", Evil);
        using (var key = sb.Create(sb.Env.Ifeo.Child("game.exe"))) // only a Warning: not part of the fix
            key.SetValue("Debugger", @"C:\Tools\wrapper.exe");
        var debuggersWhenRestorePointWasMade = new List<string?>();
        var restorePoints = RecordRestorePoints(sb, () =>
        {
            debuggersWhenRestorePointWasMade.Add(ValueOf(sb.Env.Ifeo.Child("taskmgr.exe"), "Debugger"));
            debuggersWhenRestorePointWasMade.Add(ValueOf(sb.Env.IfeoWow.Child("regedit.exe"), "Debugger"));
        });
        var (issue, report) = await ScanIssueAsync(sb, IfeoTitle);
        Assert.Equal(3, report.Ifeo.Count);
        Assert.Equal("Убрать перехват", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        Assert.Equal("Удаление перехвата запуска программ (IFEO)", Assert.Single(restorePoints));
        Assert.Equal(new string?[] { Evil, Evil }, debuggersWhenRestorePointWasMade); // the point comes before the changes
        Assert.Null(ValueOf(sb.Env.Ifeo.Child("taskmgr.exe"), "Debugger"));
        Assert.Equal("512", ValueOf(sb.Env.Ifeo.Child("taskmgr.exe"), "GlobalFlag"));
        Assert.Null(ValueOf(sb.Env.IfeoWow.Child("regedit.exe"), "Debugger"));
        Assert.Equal(@"C:\Tools\wrapper.exe", ValueOf(sb.Env.Ifeo.Child("game.exe"), "Debugger"));
        Assert.Contains("taskmgr.exe", text);
        Assert.Contains("regedit.exe", text);
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Warning, after.Check(SecurityCheckIds.Ifeo)!.Status); // only the harmless wrapper is left
    }

    [Fact]
    public async Task Ifeo_Fix_OneHijackChangedAfterTheScan_RemovesTheOthersAndReportsTheFailure()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Ifeo.Child("taskmgr.exe")))
            key.SetValue("Debugger", Evil);
        using (var key = sb.Create(sb.Env.Ifeo.Child("regedit.exe")))
            key.SetValue("Debugger", Evil);
        RecordRestorePoints(sb);
        var (issue, _) = await ScanIssueAsync(sb, IfeoTitle);
        using (var key = sb.Env.Ifeo.Child("regedit.exe").Open(writable: true))
            key!.SetValue("Debugger", @"C:\Users\Public\other.exe");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.StartsWith("Не удалось", text);
        Assert.Contains("regedit.exe", text);
        Assert.Contains("taskmgr.exe", text); // what was removed is named too
        Assert.Null(ValueOf(sb.Env.Ifeo.Child("taskmgr.exe"), "Debugger"));
        Assert.Equal(@"C:\Users\Public\other.exe", ValueOf(sb.Env.Ifeo.Child("regedit.exe"), "Debugger")); // never removed unseen
    }

    [Fact]
    public async Task Ifeo_Fix_WithoutAdministratorRights_ChangesNothingAndSaysSo()
    {
        if (Admin.IsElevated)
            return; // only meaningful in a normal (non-elevated) test run
        using var sb = new SecuritySandbox();
        // The entry points to an HKLM test key that does not exist: without the rights the action stops before opening it.
        var hijack = new IfeoEntry
        {
            ImageName = "taskmgr.exe",
            Debugger = Evil,
            Location = new RegLocation(RegistryHive.LocalMachine, @"SOFTWARE\WaterCa7sPcCleanerTests\Missing\taskmgr.exe"),
            Risk = SecurityStatus.Danger,
        };
        var checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();
        checks[checks.FindIndex(c => c.Id == SecurityCheckIds.Ifeo)] =
            SecurityRules.EvaluateIfeo(new[] { hijack }, partial: false, readable: true);
        var issue = sb.Service.BuildIssues(new SecurityReport { Ifeo = new[] { hijack }, Checks = checks })
            .Single(i => i.Title == IfeoTitle);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(HealthScoring.LooksLikeAdminRequired(text), text);
        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.Equal(SecurityService.DashboardText(SecurityActionResult.Fail(SecurityService.NeedAdminMessage)), text); // said once
    }

    [Fact]
    public async Task Ifeo_WarningOnly_HasNoFix()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Ifeo.Child("game.exe")))
            key.SetValue("Debugger", @"C:\Tools\wrapper.exe");

        var (issue, _) = await ScanIssueAsync(sb, "Найден перехват запуска");

        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Null(issue.FixAsync);
    }

    // ===================== proxy =====================

    private static void SetProxy(SecuritySandbox sb)
    {
        using var key = sb.Create(sb.Env.InternetSettings);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", "1.2.3.4:8080");
        key.SetValue("AutoConfigURL", "http://evil.example/proxy.pac");
        key.SetValue("Other", "keep");
    }

    [Fact]
    public async Task Proxy_Fix_TurnsTheProxyOffAndKeepsTheServerAddress()
    {
        using var sb = new SecuritySandbox();
        SetProxy(sb);
        var (issue, _) = await ScanIssueAsync(sb, "Используется скрипт настройки прокси");
        Assert.Equal("Выключить прокси", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.Equal(IssueSeverity.Medium, issue.Severity);

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        using (var key = sb.Env.InternetSettings.Open())
        {
            Assert.Equal(0, key!.GetValue("ProxyEnable"));
            Assert.Null(key.GetValue("AutoConfigURL"));
            Assert.Equal("1.2.3.4:8080", key.GetValue("ProxyServer"));
            Assert.Equal("keep", key.GetValue("Other"));
        }
        var after = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Ok, after.Check(SecurityCheckIds.Proxy)!.Status);
    }

    [Fact]
    public async Task Proxy_Fix_SettingsChangedAfterTheScan_ChangesNothing()
    {
        using var sb = new SecuritySandbox();
        SetProxy(sb);
        var (issue, _) = await ScanIssueAsync(sb, "Используется скрипт настройки прокси");
        // A VPN switches to its own proxy after the scan: it was not in the confirmation, so it stays.
        using (var key = sb.Env.InternetSettings.Open(writable: true))
            key!.SetValue("ProxyServer", "127.0.0.1:10809");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.StartsWith("Не удалось", text);
        using var check = sb.Env.InternetSettings.Open();
        Assert.Equal(1, check!.GetValue("ProxyEnable"));
        Assert.Equal("127.0.0.1:10809", check.GetValue("ProxyServer"));
        Assert.Equal("http://evil.example/proxy.pac", check.GetValue("AutoConfigURL"));
    }
}
