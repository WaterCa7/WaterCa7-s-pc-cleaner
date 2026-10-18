using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>
/// The dashboard gets only the text of a Security fix and marks the issue "Исправлено" unless the text reads as a failure
/// (HealthScoring.LooksLikeFailure). A failed action (firewall or real-time protection still off) must never pass as fixed,
/// and a successful one must not look failed.
/// </summary>
[Collection(SecuritySandbox.Collection)]
public class SecurityDashboardFixTests
{
    public SecurityDashboardFixTests() => TestEnvironment.Init();

    public static IEnumerable<object[]> FailedResults() => new[]
    {
        // The real failure texts of the dashboard fixes...
        new object[] { SecurityService.NeedAdminMessage },
        new object[] { "Недоступно в тестовом окружении." },
        new object[] { "Недоступно в тестовом окружении — ничего не изменено." },
        new object[] { "Команда выполнена, но брандмауэр всё ещё выключен для: частной сети. Вероятно, его отключает другая программа." },
        new object[] { "Windows не дала включить защиту (возможно, мешает «Защита от подделки» или другой антивирус)." },
        new object[] { "Защиту не удалось включить: она отключена групповой политикой." },
        new object[] { "Раздел реестра с настройками UAC не найден." },
        new object[] { "Не удалось обновить базы (код 0x80070005). Нужны права администратора — или обновите базы в «Безопасности Windows»." },
        new object[] { "Не удалось отключить SMBv1: Windows не ответила вовремя." },
        new object[] { SecurityRules.ThreatRemovalOutcome(3, 1, null).Message },
        // ...and failures whose wording alone would read as success: the flag decides, not the words.
        new object[] { SecurityService.FirewallEnabledMessage },
        new object[] { SecurityService.RealtimeEnabledMessage },
        new object[] { "Готово." },
        new object[] { "" },
    };

    [Theory]
    [MemberData(nameof(FailedResults))]
    public void DashboardText_FailedAction_IsNeverReportedAsFixed(string message)
    {
        var text = SecurityService.DashboardText(SecurityActionResult.Fail(message));

        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.StartsWith("Не удалось", text);
        Assert.Contains(message.Trim(), text); // the reason is kept
        Assert.DoesNotContain("Не удалось исправить. Не удалось", text);
    }

    [Fact]
    public void DashboardText_FailureThatSaysSo_IsNotPrefixedTwice()
    {
        const string message = "Не удалось изменить настройки UAC: нет доступа — нужны права администратора, или объект защищён Windows.";
        Assert.Equal(message, SecurityService.DashboardText(SecurityActionResult.Fail(message)));
        Assert.Equal("Не удалось исправить.", SecurityService.DashboardText(SecurityActionResult.Fail("  ")));
        Assert.Equal("Не удалось исправить. Проверка отменена.",
            SecurityService.DashboardText(new SecurityActionResult(false, "Проверка отменена.", Cancelled: true)));
    }

    public static IEnumerable<object[]> SuccessfulResults()
    {
        yield return new object[] { SecurityService.FirewallEnabledMessage };
        yield return new object[] { SecurityService.RealtimeEnabledMessage };
        yield return new object[] { SecurityService.SignaturesUpdatedMessage };
        yield return new object[] { SecurityRules.ThreatRemovalOutcome(2, 0, null).Message };
        yield return new object[] { "Необезвреженных угроз уже нет — делать ничего не нужно." };
        yield return new object[] { "UAC уже включён и запрашивает разрешение — менять ничего не нужно." };
        // DisableSmb1Async requires administrator rights, so the restore point is never "NotElevated" there.
        foreach (var point in Enum.GetValues<RestorePointResult>().Where(p => p != RestorePointResult.NotElevated))
        {
            yield return new object[] { SecurityRules.Smb1DisabledText(true, point) };
            yield return new object[] { SecurityRules.Smb1DisabledText(false, point) };
        }
    }

    [Theory]
    [MemberData(nameof(SuccessfulResults))]
    public void DashboardText_SuccessfulAction_IsPassedOnAndCountsAsFixed(string message)
    {
        var text = SecurityService.DashboardText(SecurityActionResult.Ok(message));

        Assert.Equal(message, text);
        Assert.False(HealthScoring.LooksLikeFailure(text), text);
    }

    /// <summary>A report with every dashboard issue that has a fix (Defender, threats, firewall, UAC, SMBv1).</summary>
    private static SecurityReport ReportWithEveryFix(SecurityStatus uac = SecurityStatus.Danger)
    {
        var checks = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();
        void Set(string id, SecurityStatus status, string state)
            => checks[checks.FindIndex(c => c.Id == id)] = new SecurityCheckResult(id, status, state, "details");
        Set(SecurityCheckIds.Firewall, SecurityStatus.Danger, "Выключен для частной сети");
        Set(SecurityCheckIds.Uac, uac, uac == SecurityStatus.Danger ? "UAC отключён" : "Запросы UAC отключены");
        Set(SecurityCheckIds.Smb1, SecurityStatus.Danger, "SMBv1 включён");
        return new SecurityReport
        {
            Defender = new DefenderStatus
            {
                ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = false, SignatureAgeDays = 10,
            },
            Threats = new[] { new ThreatDetection { Name = "Trojan:Win32/Test", StatusId = 1 } },
            FirewallProfiles = new[]
            {
                new FirewallProfileState("Private", false), new FirewallProfileState("Public", true),
                new FirewallProfileState("Domain", true),
            },
            Checks = checks,
        };
    }

    [Fact]
    public async Task BuildIssues_EveryFixThatFails_IsNotReportedAsFixed()
    {
        using var sb = new SecuritySandbox();
        // In the sandbox every system action fails without starting anything. The UAC fix works on the sandbox registry,
        // so it is checked separately (BuildIssues_UacFix_SuccessCountsAsFixed).
        var direct = new Dictionary<string, Func<CancellationToken, Task<SecurityActionResult>>>
        {
            ["Защита в реальном времени выключена"] = sb.Service.EnableRealtimeProtectionAsync,
            ["Антивирусные базы устарели"] = sb.Service.UpdateSignaturesAsync,
            ["Есть необезвреженные угрозы: 1"] = sb.Service.RemoveActiveThreatsAsync,
            ["Брандмауэр Windows выключен"] = sb.Service.EnableFirewallAsync,
            ["Включён устаревший протокол SMBv1"] = sb.Service.DisableSmb1Async,
        };

        var issues = sb.Service.BuildIssues(ReportWithEveryFix()).Where(i => i.FixAsync is not null).ToList();

        Assert.Equal(direct.Keys.Append("Контроль учётных записей (UAC) отключён").OrderBy(t => t),
            issues.Select(i => i.Title).OrderBy(t => t));
        foreach (var (title, action) in direct)
        {
            var result = await action(CancellationToken.None);
            Assert.False(result.Success, title); // sandbox: system actions are never started
            var text = await issues.Single(i => i.Title == title).FixAsync!(CancellationToken.None);
            Assert.Equal(SecurityService.DashboardText(result), text);
            Assert.True(HealthScoring.LooksLikeFailure(text), $"{title}: {text}");
        }
    }

    [Fact]
    public void BuildIssues_FixesThatNeedARestart_SaySoInFixAll()
    {
        using var sb = new SecuritySandbox();

        var issues = sb.Service.BuildIssues(ReportWithEveryFix());
        var promptOnly = sb.Service.BuildIssues(ReportWithEveryFix(SecurityStatus.Warning)).Single(i => i.Title.Contains("UAC"));

        Assert.Equal("После включения UAC нужно перезагрузить компьютер.", issues.Single(i => i.Title.Contains("UAC")).Consequence);
        Assert.Equal("После отключения SMBv1 нужно перезагрузить компьютер.",
            issues.Single(i => i.Title == "Включён устаревший протокол SMBv1").Consequence);
        Assert.Null(promptOnly.Consequence); // turning the prompt back on needs no restart
        Assert.Equal(2, issues.Count(i => i.Consequence is not null));
    }

    [Theory]
    [InlineData(0, SecurityStatus.Danger, "Перезагрузите")]   // UAC off: "UAC включён. Перезагрузите компьютер…"
    [InlineData(1, SecurityStatus.Warning, "Запросы UAC снова включены")] // only the prompt was off
    public async Task BuildIssues_UacFix_SuccessCountsAsFixed(int enableLua, SecurityStatus uac, string expected)
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.UacPolicies))
        {
            key.SetValue("EnableLUA", enableLua, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord);
        }
        var issue = sb.Service.BuildIssues(ReportWithEveryFix(uac)).Single(i => i.Title.Contains("UAC"));

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Contains(expected, text);
        Assert.False(HealthScoring.LooksLikeFailure(text), text);
        using var check = sb.Env.UacPolicies.Open();
        Assert.Equal(1, check!.GetValue("EnableLUA"));
        Assert.Equal(5, check.GetValue("ConsentPromptBehaviorAdmin"));
    }

    /// <summary>
    /// UAC settings that cannot be read (no access to the key) are not "UAC is already on": the fix fails, changes nothing
    /// and the dashboard keeps the problem open.
    /// </summary>
    [Fact]
    public async Task BuildIssues_UacFix_SettingsUnreadable_IsNotReportedAsFixed()
    {
        using var sb = new SecuritySandbox();
        var loc = sb.Env.UacPolicies;
        using (var key = sb.Create(loc))
        {
            key.SetValue("EnableLUA", 0, RegistryValueKind.DWord);
            key.SetValue("ConsentPromptBehaviorAdmin", 0, RegistryValueKind.DWord);
        }
        // Only the sandbox key under HKCU\Software\WaterCa7sPcCleanerTests: reading its values is denied for a moment.
        var deny = new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.QueryValues, AccessControlType.Deny);
        ChangeRule(loc, deny, add: true);
        string text;
        try
        {
            var issue = sb.Service.BuildIssues(ReportWithEveryFix()).Single(i => i.Title.Contains("UAC"));
            text = await issue.FixAsync!(CancellationToken.None);
        }
        finally
        {
            ChangeRule(loc, deny, add: false);
        }

        Assert.Equal(SecurityService.UacUnreadableMessage, text);
        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.Empty(sb.OwnBackups()); // refused before the backup: nothing changed
        using var check = loc.Open();
        Assert.Equal(0, check!.GetValue("EnableLUA"));
        Assert.Equal(0, check.GetValue("ConsentPromptBehaviorAdmin"));
    }

    private static void ChangeRule(RegLocation loc, RegistryAccessRule rule, bool add)
    {
        using var root = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
        using var key = root.OpenSubKey(loc.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
        var acl = key.GetAccessControl();
        if (add)
            acl.AddAccessRule(rule);
        else
            acl.RemoveAccessRule(rule);
        key.SetAccessControl(acl);
    }
}
