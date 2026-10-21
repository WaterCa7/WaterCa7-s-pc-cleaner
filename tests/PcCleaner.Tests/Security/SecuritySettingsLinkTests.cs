using PcCleaner.Core;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>
/// Security issues that the user finishes in Windows settings carry the page ("Открыть настройки" on the dashboard row).
/// A "windowsdefender:" or "ms-settings:" address is not an http(s) one, so the row never says "Открыть страницу".
/// </summary>
[Collection(SecuritySandbox.Collection)]
public class SecuritySettingsLinkTests
{
    public SecuritySettingsLinkTests() => TestEnvironment.Init();

    private static List<SecurityCheckResult> AllOk()
        => SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();

    private static List<SecurityCheckResult> With(string id, SecurityStatus status, string state)
    {
        var checks = AllOk();
        checks[checks.FindIndex(c => c.Id == id)] = new SecurityCheckResult(id, status, state, "details");
        return checks;
    }

    private static DefenderStatus Defender(bool antivirusEnabled = true, IReadOnlyList<string>? exclusions = null) => new()
    {
        ServiceEnabled = true, AntivirusEnabled = antivirusEnabled, RealTimeEnabled = antivirusEnabled, SignatureAgeDays = 0,
        Exclusions = exclusions ?? Array.Empty<string>(),
    };

    public static IEnumerable<object[]> Cases()
    {
        static object[] Case(string name, Func<SecurityReport> report, string title, string uri) => new object[] { name, report, title, uri };

        yield return Case("antivirus off", () => new SecurityReport { Defender = Defender(antivirusEnabled: false), Checks = AllOk() },
            "Антивирус выключен", "windowsdefender:");
        yield return Case("antivirus off, no Defender data",
            () => new SecurityReport
            {
                Antiviruses = new[] { SecurityRules.MakeProduct("Kaspersky Anti-Virus", 0x43100) },
                Checks = AllOk(),
            },
            "Антивирус выключен", "windowsdefender:");
        yield return Case("wide exclusions",
            () => new SecurityReport { Defender = Defender(exclusions: new[] { @"C:\" }), Checks = AllOk() },
            "Слишком широкие исключения антивируса", "windowsdefender://threatsettings");
        yield return Case("windows update is old",
            () => new SecurityReport { Checks = With(SecurityCheckIds.Updates, SecurityStatus.Warning, "Обновлений не было 90 дней") },
            "Windows давно не обновлялась", "ms-settings:windowsupdate");
        yield return Case("windows update service is off",
            () => new SecurityReport
            {
                Updates = new UpdatesState(null, 0, null, ServiceDisabled: true), // the title comes from this, not from the words of the card
                Checks = With(SecurityCheckIds.Updates, SecurityStatus.Warning, "Служба обновлений отключена"),
            },
            "Служба обновлений Windows отключена", "ms-settings:windowsupdate");
        yield return Case("SmartScreen",
            () => new SecurityReport { Checks = With(SecurityCheckIds.SmartScreen, SecurityStatus.Warning, "SmartScreen выключен") },
            "SmartScreen выключен", "windowsdefender://appbrowser");
        yield return Case("remote desktop",
            () => new SecurityReport { Checks = With(SecurityCheckIds.Rdp, SecurityStatus.Warning, "Удалённый рабочий стол включён") },
            "Удалённый рабочий стол включён", "ms-settings:remotedesktop");
        yield return Case("laptop drive is not encrypted",
            () => new SecurityReport
            {
                BitLocker = new BitLockerState("C:", DriveEncryption.Off, IsLaptop: true, IsHomeEdition: false),
                Checks = AllOk(),
            },
            "Системный диск ноутбука не зашифрован", "ms-settings:deviceencryption");
        yield return Case("BitLocker is suspended on a laptop",
            () => new SecurityReport
            {
                BitLocker = new BitLockerState("C:", DriveEncryption.Suspended, IsLaptop: true, IsHomeEdition: false),
                Checks = AllOk(),
            },
            "Защита BitLocker на ноутбуке приостановлена", "ms-settings:deviceencryption");
        yield return Case("memory integrity",
            () => new SecurityReport
            {
                DeviceGuard = new DeviceGuardState(true, 2, new[] { 0 }, new[] { 0 }, null),
                Checks = With(SecurityCheckIds.MemoryIntegrity, SecurityStatus.Info, "Целостность памяти выключена"),
            },
            "Можно включить «Целостность памяти»", "windowsdefender://coreisolation");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Issue_OpensTheMatchingWindowsPage(string name, Func<SecurityReport> report, string title, string uri)
    {
        using var sb = new SecuritySandbox();

        var issue = Assert.Single(sb.Service.BuildIssues(report()));

        Assert.Equal(title, issue.Title);
        Assert.Equal(uri, issue.SettingsUri);
        Assert.False(Uri.TryCreate(issue.SettingsUri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https", name);
    }

    [Fact]
    public void Issue_WithNothingToOpen_HasNoSettingsLink()
    {
        // An issue with its own fix button (the firewall) does not get a link: only the ones listed above send the user to Windows.
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            FirewallProfiles = new[] { new FirewallProfileState("Private", false), new FirewallProfileState("Public", true) },
            Checks = With(SecurityCheckIds.Firewall, SecurityStatus.Danger, "Выключен для частной сети"),
        };

        var issue = Assert.Single(sb.Service.BuildIssues(report));

        Assert.NotNull(issue.FixAsync);
        Assert.Null(issue.SettingsUri);
    }
}
