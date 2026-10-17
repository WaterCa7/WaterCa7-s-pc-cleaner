using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>
/// "Отключить прокси" changes only what the user confirmed; the page header, the log line and the dashboard count findings
/// the same way; one Russian name for the built-in antivirus; the in-app threat list is not confused with «Журнал защиты».
/// </summary>
[Collection(SecuritySandbox.Collection)]
public class SecurityConsistencyTests
{
    public SecurityConsistencyTests() => TestEnvironment.Init();

    private static List<SecurityCheckResult> AllOk()
        => SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();

    private static List<SecurityCheckResult> With(List<SecurityCheckResult> checks, SecurityCheckResult replacement)
    {
        checks[checks.FindIndex(c => c.Id == replacement.Id)] = replacement;
        return checks;
    }

    // ===================== Proxy: only what the confirmation listed =====================

    [Fact]
    public void SameProxySettings_ComparesEnableServerAndScriptExactly()
    {
        var confirmed = new ProxyState(true, true, "1.2.3.4:8080", null);

        Assert.True(SecurityRules.SameProxySettings(confirmed with { }, confirmed));
        Assert.False(SecurityRules.SameProxySettings(confirmed with { AutoConfigUrl = "http://vpn.example/proxy.pac" }, confirmed));
        Assert.False(SecurityRules.SameProxySettings(confirmed with { Server = "5.6.7.8:3128" }, confirmed));
        Assert.False(SecurityRules.SameProxySettings(confirmed with { Server = "1.2.3.4:8081" }, confirmed));
        Assert.False(SecurityRules.SameProxySettings(confirmed with { Enabled = false }, confirmed));
        Assert.False(SecurityRules.SameProxySettings(confirmed with { Readable = false }, confirmed)); // cannot check = no change

        var pacOnly = new ProxyState(true, false, null, "http://evil.example/proxy.pac");
        Assert.False(SecurityRules.SameProxySettings(pacOnly with { AutoConfigUrl = "http://evil.example/PROXY.pac" }, pacOnly));
        Assert.False(SecurityRules.SameProxySettings(pacOnly with { Enabled = true, Server = "127.0.0.1:10809" }, pacOnly));
    }

    public static IEnumerable<object[]> ChangesAfterTheCheck() => new[]
    {
        new object[] { "a VPN added a PAC script", (Action<RegistryKey>)(k => k.SetValue("AutoConfigURL", "http://vpn.example/proxy.pac")) },
        new object[] { "another proxy server", (Action<RegistryKey>)(k => k.SetValue("ProxyServer", "5.6.7.8:3128")) },
        new object[] { "the proxy was switched off", (Action<RegistryKey>)(k => k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord)) },
    };

    [Theory]
    [MemberData(nameof(ChangesAfterTheCheck))]
    public async Task DisableProxy_SettingsChangedAfterTheCheck_ChangesNothing(string change, Action<RegistryKey> apply)
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.InternetSettings))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "1.2.3.4:8080");
        }
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(SecurityStatus.Warning, report.Check(SecurityCheckIds.Proxy)!.Status);
        using (var key = sb.Create(sb.Env.InternetSettings))
            apply(key);
        string? Snapshot()
        {
            using var key = sb.Env.InternetSettings.Open();
            return string.Join("|", key!.GetValueNames().OrderBy(n => n).Select(n => $"{n}={key.GetValue(n)}"));
        }
        var before = Snapshot();
        var backups = sb.OwnBackups();

        var result = await sb.Service.DisableProxyAsync(report.Proxy, CancellationToken.None);

        Assert.False(result.Success, change);
        Assert.Equal(SecurityService.ProxyChangedMessage, result.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal(backups, sb.OwnBackups()); // refused before the backup: nothing to undo
    }

    [Fact]
    public async Task DisableProxy_ConfirmedSettingsUnchanged_TurnsOffProxyAndScript()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.InternetSettings))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "1.2.3.4:8080");
            key.SetValue("AutoConfigURL", "http://evil.example/proxy.pac");
        }
        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        var result = await sb.Service.DisableProxyAsync(report.Proxy, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        using var after = sb.Env.InternetSettings.Open();
        Assert.Equal(0, after!.GetValue("ProxyEnable"));
        Assert.Null(after.GetValue("AutoConfigURL"));
        Assert.Equal("1.2.3.4:8080", after.GetValue("ProxyServer"));
    }

    // ===================== One count for the page, the log line and the dashboard =====================

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(2, 3)]
    [InlineData(5, 5)]
    [InlineData(21, 22)]
    public void FindingsText_IsWordedLikeTheDashboard(int problems, int tips)
        => Assert.Equal(HealthScoring.FindingsText(problems, tips), SecurityRules.FindingsText(problems, tips));

    [Fact]
    public void CountFindings_IsTheDashboardSplitOfBuildIssues()
    {
        using var sb = new SecuritySandbox();
        var hvciOff = new DeviceGuardState(true, 2, new[] { 0 }, new[] { 0 }, null);
        var checks = AllOk();
        With(checks, new SecurityCheckResult(SecurityCheckIds.Firewall, SecurityStatus.Danger, "Выключен для частной сети", "x"));
        With(checks, new SecurityCheckResult(SecurityCheckIds.Uac, SecurityStatus.Warning, "Запросы UAC отключены", "x"));
        With(checks, new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Warning, "Включён клиент SMBv1", "x"));
        With(checks, SecurityRules.EvaluateMemoryIntegrity(hvciOff));
        // "Неизвестный DNS" is only "к сведению": neither a problem nor a dashboard tip.
        With(checks, new SecurityCheckResult(SecurityCheckIds.Dns, SecurityStatus.Info, "Неизвестный DNS: 10.1.2.3", "x"));
        var report = new SecurityReport { DeviceGuard = hvciOff, Checks = checks };

        var issues = sb.Service.BuildIssues(report);
        var (problems, tips) = sb.Service.CountFindings(report);

        Assert.Equal(issues.Count(HealthScoring.IsProblem), problems);
        Assert.Equal(issues.Count(i => !HealthScoring.IsProblem(i)), tips);
        Assert.Equal((3, 1), (problems, tips));
        Assert.Equal("3 проблемы и 1 совет", SecurityRules.FindingsText(problems, tips));
    }

    [Fact]
    public async Task PageHeaderAndLogLine_UseTheSameSplitAsTheDashboard()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "custom-shell.exe");
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        using (var key = sb.Create(sb.Env.InternetSettings))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "1.2.3.4:8080");
        }
        var messages = new List<string>();
        void OnEntry(LogEntry e)
        {
            lock (messages)
                messages.Add(e.Message);
        }
        var vm = new SecurityViewModel(sb.Service);

        Log.EntryAdded += OnEntry;
        try
        {
            await vm.PreviewScanAsync(CancellationToken.None);
        }
        finally
        {
            Log.EntryAdded -= OnEntry;
        }

        var report = sb.Service.LastReport!;
        var dashboard = sb.Service.BuildIssues(report);
        var expected = HealthScoring.FindingsText(dashboard.Count(HealthScoring.IsProblem), dashboard.Count(i => !HealthScoring.IsProblem(i)));
        Assert.StartsWith("2 проблемы", expected); // Winlogon + proxy
        Assert.Equal("Стоит посмотреть · " + expected, vm.AttentionHeader);
        lock (messages)
        {
            Assert.Contains($"Безопасность: проверено {Format.Count(report.Checks.Count, "параметр", "параметра", "параметров")} — {expected}",
                messages);
            Assert.DoesNotContain(messages, m => m.Contains("требуют внимания:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task PageHeader_OnlyNotesAndMissingData_HasNoCountAndNeverSaysПроблемНет()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "explorer.exe");
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        var vm = new SecurityViewModel(sb.Service);

        await vm.PreviewScanAsync(CancellationToken.None);

        // In the sandbox antivirus, firewall and updates cannot be read: "Нет данных" cards, but nothing for the dashboard.
        Assert.Equal((0, 0), sb.Service.CountFindings(sb.Service.LastReport!));
        Assert.True(vm.HasAttentionChecks);
        Assert.Equal("Стоит посмотреть", vm.AttentionHeader);
    }

    // ===================== One name for the built-in antivirus =====================

    [Fact]
    public void AntivirusTexts_CallItЗащитникWindows_NeverABareDefender()
    {
        using var sb = new SecuritySandbox();
        var vm = new SecurityViewModel(sb.Service);
        var working = new DefenderStatus
        {
            ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0, RunningMode = "Normal",
        };
        var states = new[]
        {
            working,
            new DefenderStatus { ServiceEnabled = true, PolicyDisablesAntivirus = true },
            new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = false },
            new DefenderStatus { ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = false, SignatureAgeDays = 9 },
            new DefenderStatus
            {
                ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0, Exclusions = new[] { @"C:\" },
            },
        };
        var threatSets = new[]
        {
            Array.Empty<ThreatDetection>(),
            new[] { new ThreatDetection { Name = "Trojan:Win32/Test", StatusId = 1, ThreatId = 1 } },
            new[] { new ThreatDetection { Name = "HackTool:Win32/Test", StatusId = 5, ThreatId = 2 } },
        };
        var texts = new List<string>();
        foreach (var d in states)
        {
            foreach (var threats in threatSets)
            {
                var check = SecurityRules.EvaluateAntivirus(d, Array.Empty<SecurityProduct>(), threats);
                texts.Add(check.State);
                texts.Add(check.Details);
                var report = new SecurityReport { Defender = d, Threats = threats, Checks = With(AllOk(), check), MpCmdRunAvailable = true };
                texts.AddRange(sb.Service.BuildIssues(report).SelectMany(i => new[] { i.Title, i.Details }));
                var card = vm.BuildCards(report).Single(c => c.Id == SecurityCheckIds.Antivirus);
                texts.AddRange(card.Actions.SelectMany(a => new[] { a.Label, a.ToolTip ?? "" }));
            }
        }
        var avast = SecurityRules.MakeProduct("Avast Antivirus", 0x41000);
        texts.AddRange(SecurityRules.ActiveAntivirusNames(new[] { avast, SecurityRules.MakeProduct("Windows Defender", 397568) }, working));
        texts.Add(SecurityRules.ThreatRemovalOutcome(2, 0, null).Message);
        texts.Add(SecurityRules.ThreatRemovalOutcome(3, 1, null).Message);
        texts.Add(vm.ThreatsEmptyText);

        Assert.All(texts, t => Assert.DoesNotContain("Defender", t));
        Assert.Contains(texts, t => t.Contains("Защитник Windows", StringComparison.Ordinal));
        // The English product name is mentioned once, in the card's explanation tooltip.
        Assert.Contains("Защитник Windows (Microsoft Defender)", SecurityCheckIds.Explanation(SecurityCheckIds.Antivirus));
    }

    // ===================== In-app threat list vs Windows «Журнал защиты» =====================

    [Fact]
    public void ThreatList_IsCalledНайденныеУгрозы_AndTextsNameWindowsSecurityForЖурналЗащиты()
    {
        using var sb = new SecuritySandbox();
        var vm = new SecurityViewModel(sb.Service);
        var defender = new DefenderStatus
        {
            ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0, RunningMode = "Normal",
        };
        var active = new[] { new ThreatDetection { Name = "Trojan:Win32/Test", StatusId = 1, ThreatId = 1 } };
        var check = SecurityRules.EvaluateAntivirus(defender, Array.Empty<SecurityProduct>(), active);
        var report = new SecurityReport { Defender = defender, Threats = active, Checks = With(AllOk(), check) };

        var card = vm.BuildCards(report).Single(c => c.Id == SecurityCheckIds.Antivirus);

        var list = card.Actions.Single(a => a.Command == vm.ShowDetailsCommand);
        Assert.Equal("Найденные угрозы", list.Label);
        Assert.Equal(SecurityViewModel.ThreatsTab, list.Parameter);
        Assert.DoesNotContain(card.Actions, a => a.Label.Contains("Журнал", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Найденные угрозы (", vm.ThreatsTabHeader);
        // Wherever «Журнал защиты» is mentioned, it is clear that it is in Windows Security.
        Assert.Contains("«Безопасность Windows» → «Журнал защиты»", check.Details);
        Assert.DoesNotContain("«Журнале защиты»", check.Details);
        // Without a working Defender the dashboard issue cannot fix it and sends the user to Windows Security instead.
        var noDefender = new SecurityReport { Threats = active, Checks = report.Checks };
        var issue = sb.Service.BuildIssues(noDefender).Single(i => i.Title.StartsWith("Есть необезвреженные", StringComparison.Ordinal));
        Assert.Null(issue.FixAsync);
        Assert.Contains("«Безопасность Windows» → «Журнал защиты»", issue.Details);
    }
}
