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
        var restorePoints = RecordRestorePoints(sb);
        // The entry points to an HKLM test key that does not exist: without the rights the fix stops before opening it.
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
        Assert.Empty(restorePoints); // no point for a fix that cannot run
    }

    /// <summary>Two Danger hijacks (taskmgr.exe and regedit.exe) in the 64-bit view.</summary>
    private static void SetTwoHijacks(SecuritySandbox sb)
    {
        foreach (var image in new[] { "taskmgr.exe", "regedit.exe" })
        {
            using var key = sb.Create(sb.Env.Ifeo.Child(image));
            key.SetValue("Debugger", Evil);
        }
    }

    [Fact]
    public async Task Ifeo_Fix_SubkeyGoneSinceTheScan_CountsAsRemoved()
    {
        using var sb = new SecuritySandbox();
        SetTwoHijacks(sb);
        RecordRestorePoints(sb);
        var (issue, _) = await ScanIssueAsync(sb, IfeoTitle);
        // A cleaner (or the user) deletes the whole subkey after the scan: the hijack is gone, so there is nothing to fail.
        using (var root = sb.Env.Ifeo.Open(writable: true))
            root!.DeleteSubKeyTree("regedit.exe");

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        Assert.Contains("regedit.exe", text);
        Assert.Contains("taskmgr.exe", text);
        Assert.Null(ValueOf(sb.Env.Ifeo.Child("taskmgr.exe"), "Debugger"));
        Assert.Null(sb.Env.Ifeo.Child("regedit.exe").Open());
    }

    [Fact]
    public async Task RemoveIfeoDebugger_SubkeyGone_IsNotAFailure()
    {
        using var sb = new SecuritySandbox();
        var gone = new IfeoEntry
        {
            ImageName = "regedit.exe", Debugger = Evil, Location = sb.Env.Ifeo.Child("regedit.exe"), Risk = SecurityStatus.Danger,
        };

        var result = await sb.Service.RemoveIfeoDebuggerAsync(gone, CancellationToken.None);

        Assert.True(result.Success, result.Message); // the hijack the user wanted removed does not exist any more
        Assert.Contains("regedit.exe", result.Message);
        Assert.Empty(sb.OwnBackups()); // nothing to back up
    }

    [Fact]
    public async Task Ifeo_Fix_StoppedAfterSomeHijacksWereRemoved_ReturnsWhatWasDone()
    {
        using var sb = new SecuritySandbox();
        SetTwoHijacks(sb);
        RecordRestorePoints(sb);
        using var cts = new CancellationTokenSource();
        var removeOne = sb.Service.RemoveIfeoHijackAsync;
        sb.Service.RemoveIfeoHijackAsync = async (entry, ct) =>
        {
            var result = await removeOne(entry, ct);
            cts.Cancel(); // "Отмена" right after the first hijack is gone
            return result;
        };
        var (issue, _) = await ScanIssueAsync(sb, IfeoTitle);

        var text = await issue.FixAsync!(cts.Token);

        Assert.True(cts.IsCancellationRequested); // the Dashboard keys on it
        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.StartsWith("Не удалось", text);
        var images = new[] { "taskmgr.exe", "regedit.exe" };
        var left = Assert.Single(images, i => ValueOf(sb.Env.Ifeo.Child(i), "Debugger") is not null);
        var removed = images.Single(i => i != left);
        Assert.Contains($"остался для: {left}", text);
        Assert.Contains($"убран для: {removed}", text);
    }

    [Fact]
    public async Task Ifeo_Fix_StoppedAfterSomeHijacksWereRemoved_DashboardShowsTheTextAndKeepsTheIssueOpen()
    {
        using var sb = new SecuritySandbox();
        SetTwoHijacks(sb);
        RecordRestorePoints(sb);
        using var cts = new CancellationTokenSource();
        var removeOne = sb.Service.RemoveIfeoHijackAsync;
        sb.Service.RemoveIfeoHijackAsync = async (entry, ct) =>
        {
            var result = await removeOne(entry, ct);
            cts.Cancel();
            return result;
        };
        var vm = new DashboardViewModel(new IHealthContributor[] { sb.Service }, new AppSettings())
        {
            CreateRestorePointAsync = (_, _) => Task.FromResult(false),
        };
        await vm.ScanCoreAsync(CancellationToken.None, persist: false);
        var item = vm.Groups.SelectMany(g => g.AllItems).Single(i => i.Issue.Title == IfeoTitle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.FixOneCoreAsync(item, cts.Token));

        Assert.StartsWith("Не удалось убрать перехват запуска", item.Issue.FixResult);
        Assert.Contains(DashboardViewModel.InterruptedNote, item.Issue.FixResult);
        Assert.False(item.Issue.IsFixed);
        Assert.True(item.Issue.CanFix);
        Assert.True(item.FixFailed);
    }

    [Fact]
    public async Task Ifeo_Fix_StoppedBeforeAnyHijackWasRemoved_IsAPlainCancellation()
    {
        using var sb = new SecuritySandbox();
        SetTwoHijacks(sb);
        using var cts = new CancellationTokenSource();
        RecordRestorePoints(sb, () => cts.Cancel()); // "Отмена" while the restore point is being made
        var (issue, _) = await ScanIssueAsync(sb, IfeoTitle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issue.FixAsync!(cts.Token));

        Assert.Equal(Evil, ValueOf(sb.Env.Ifeo.Child("taskmgr.exe"), "Debugger"));
        Assert.Equal(Evil, ValueOf(sb.Env.Ifeo.Child("regedit.exe"), "Debugger"));
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

    private static void SetProxyServer(SecuritySandbox sb, string server, string? script = null)
    {
        using var key = sb.Create(sb.Env.InternetSettings);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", server);
        if (script is not null)
            key.SetValue("AutoConfigURL", script);
    }

    [Theory]
    [InlineData("127.0.0.1:10809")]
    [InlineData("localhost:8080")]
    [InlineData("LocalHost:3128")]
    [InlineData("[::1]:8080")]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:8080")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2:8080")]
    [InlineData("http=127.0.0.1:8080;https=[::1]:8080;socks=localhost:1080")]
    public async Task Proxy_OnThisComputer_StaysOutOfFixAll_ButKeepsItsOwnButton(string server)
    {
        using var sb = new SecuritySandbox();
        SetProxyServer(sb, server);

        var (issue, _) = await ScanIssueAsync(sb, "Включён прокси");

        Assert.False(issue.InFixAll); // a VPN or a blocking-bypass program usually sets it: not switched off in bulk
        Assert.NotNull(issue.FixAsync);
        Assert.True(issue.CanFix);
        Assert.Equal("Выключить прокси", issue.FixLabel);
        Assert.Contains("VPN", issue.Details);
        Assert.Contains("«Исправить всё»", issue.Details);
        // The button itself still works.
        var text = await issue.FixAsync!(CancellationToken.None);
        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        using var check = sb.Env.InternetSettings.Open();
        Assert.Equal(0, check!.GetValue("ProxyEnable"));
    }

    [Theory]
    [InlineData("1.2.3.4:8080")]
    [InlineData("proxy.example.com:3128")]
    [InlineData("[2001:db8::1]:8080")] // contains "::1" but is not the loopback address
    [InlineData("localhost.evil.example:8080")] // a name that only starts like a local one
    [InlineData("127.0.0.1.evil.example:8080")]
    [InlineData("evil-localhost:8080")]
    [InlineData("127.0.0.1@evil.example:8080")]
    [InlineData("http=127.0.0.1:8080;https=ads.example:443")] // one remote host in the list is enough
    [InlineData("https=ads.example:443;http=127.0.0.1:8080")]
    public async Task Proxy_OnAnotherComputer_StaysInFixAll(string server)
    {
        using var sb = new SecuritySandbox();
        SetProxyServer(sb, server);

        var (issue, _) = await ScanIssueAsync(sb, "Включён прокси");

        Assert.True(issue.InFixAll);
        Assert.NotNull(issue.FixAsync);
        Assert.DoesNotContain("«Исправить всё»", issue.Details);
    }

    [Fact]
    public async Task Proxy_SetupScript_StaysInFixAllEvenWithALocalServer()
    {
        // A script is not a program on this computer: the proxy check treats it first, and so does the fix.
        using var sb = new SecuritySandbox();
        SetProxyServer(sb, "127.0.0.1:10809", script: "http://evil.example/proxy.pac");

        var (issue, _) = await ScanIssueAsync(sb, "Используется скрипт настройки прокси");

        Assert.True(issue.InFixAll);
        Assert.NotNull(issue.FixAsync);
    }

    [Theory]
    [InlineData("127.0.0.1:10809", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("1.2.3.4:8080", false)]
    [InlineData("[2001:db8::1]:8080", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData(" http = 127.0.0.1:8080 ; https = LOCALHOST:8080 ", true)]
    [InlineData("localhost.evil.example:8080", false)]
    [InlineData("127.0.0.1.evil.example:8080", false)]
    [InlineData("http=127.0.0.1:8080;https=ads.example:443", false)]
    [InlineData("http=127.0.0.1:8080;", true)]
    [InlineData(";", false)]
    [InlineData("0.0.0.0:8080", false)]
    public void ProxyCheck_RecognisesAProxyOnThisComputer(string server, bool local)
    {
        var check = SecurityRules.EvaluateProxy(new ProxyState(true, true, server, null));

        Assert.Equal(SecurityStatus.Warning, check.Status);
        Assert.Equal(local, check.Details.Contains("на этом компьютере"));
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
