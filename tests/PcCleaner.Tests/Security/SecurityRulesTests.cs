using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

public class AvProductStateTests
{
    [Theory]
    [InlineData(397568, true, true)] // Windows Defender: on, up to date (0x61100)
    [InlineData(393472, false, true)] // off, up to date (0x60100)
    [InlineData(397584, true, false)] // on, out of date (0x61110)
    [InlineData(266240, true, true)] // 0x41000: third-party AV on, up to date
    [InlineData(262144, false, true)] // 0x40000: off
    [InlineData(393488, false, false)] // 0x60110: off, out of date
    public void Decode_ReadsScannerAndSignatureBits(long state, bool enabled, bool upToDate)
    {
        var s = AvProductState.Decode(state);
        Assert.Equal(enabled, s.Enabled);
        Assert.Equal(upToDate, s.UpToDate);
    }

    [Fact]
    public void Decode_SnoozedAndExpired()
    {
        Assert.True(AvProductState.Decode(0x62100).Snoozed);
        Assert.True(AvProductState.Decode(0x63100).Expired);
        Assert.False(AvProductState.Decode(0x62100).Enabled);
    }

    [Fact]
    public void MakeProduct_DetectsMicrosoft()
    {
        Assert.True(SecurityRules.MakeProduct("Windows Defender", 397568).IsMicrosoft);
        Assert.False(SecurityRules.MakeProduct("Kaspersky Internet Security", 266240).IsMicrosoft);
    }

    [Theory]
    [InlineData("Windows Defender", true)]
    [InlineData("Microsoft Defender Antivirus", true)]
    [InlineData("Защитник Windows", true)]
    [InlineData("Брандмауэр Защитника Windows", true)]
    [InlineData("Bitdefender Antivirus Free", false)] // regression: "defender" inside "Bitdefender"
    [InlineData("Bitdefender Firewall", false)]
    [InlineData("ESET Security", false)]
    [InlineData("", false)]
    public void IsMicrosoftProduct_WholeWordsOnly(string name, bool expected)
        => Assert.Equal(expected, SecurityRules.IsMicrosoftProduct(name));
}

public class WinlogonRulesTests
{
    private const string Win = @"C:\Windows";

    [Theory]
    [InlineData("explorer.exe", true)]
    [InlineData("Explorer.EXE", true)]
    [InlineData(" explorer.exe ", true)]
    [InlineData(@"C:\Windows\explorer.exe", true)]
    [InlineData(@"%SystemRoot%\explorer.exe", true)]
    [InlineData("explorer.exe, C:\\Users\\a\\AppData\\evil.exe", false)]
    [InlineData(@"C:\ProgramData\shell.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Shell(string? value, bool ok) => Assert.Equal(ok, SecurityRules.IsShellDefault(value, Win));

    [Theory]
    [InlineData(@"C:\Windows\system32\userinit.exe,", true)]
    [InlineData(@"C:\WINDOWS\system32\userinit.exe,", true)]
    [InlineData(@"c:\windows\System32\USERINIT.EXE", true)]
    [InlineData(@"%SystemRoot%\system32\userinit.exe,", true)]
    [InlineData(@"C:\Windows\system32\userinit.exe,C:\Users\a\AppData\Roaming\svc.exe,", false)]
    [InlineData(@"C:\Users\a\userinit.exe,", false)]
    [InlineData("", false)]
    public void Userinit(string? value, bool ok) => Assert.Equal(ok, SecurityRules.IsUserinitDefault(value, Win));

    [Fact]
    public void Evaluate_DefaultIsOk_HijackIsDanger()
    {
        var ok = SecurityRules.EvaluateWinlogon(new WinlogonState(true, "explorer.exe", @"C:\Windows\system32\userinit.exe,", null), Win);
        Assert.Equal(SecurityStatus.Ok, ok.Status);

        var bad = SecurityRules.EvaluateWinlogon(new WinlogonState(true, "explorer.exe", @"C:\Windows\system32\userinit.exe,", @"C:\evil.exe"), Win);
        Assert.Equal(SecurityStatus.Danger, bad.Status);
        Assert.Contains("evil.exe", bad.Details);

        var unreadable = SecurityRules.EvaluateWinlogon(new WinlogonState(false, null, null, null), Win);
        Assert.Equal(SecurityStatus.Unknown, unreadable.Status);
    }
}

public class IfeoRulesTests
{
    [Theory]
    [InlineData("taskmgr.exe", @"C:\Users\a\AppData\Local\Temp\x.exe", SecurityStatus.Danger)]
    [InlineData("MsMpEng.exe", "svchost.exe", SecurityStatus.Danger)]
    [InlineData("regedit.exe", "ntsd -d", SecurityStatus.Danger)]
    [InlineData("avp.exe", "rundll32.exe", SecurityStatus.Danger)]
    [InlineData("mbam.exe", "x.exe", SecurityStatus.Danger)]
    [InlineData("sethc.exe", "cmd.exe", SecurityStatus.Danger)]
    [InlineData("myapp.exe", @"C:\Windows\system32\vsjitdebugger.exe", SecurityStatus.Warning)]
    [InlineData("taskmgr.exe", @"""C:\Tools\procexp64.exe""", SecurityStatus.Warning)]
    [InlineData("game.exe", @"C:\Tools\wrapper.exe", SecurityStatus.Warning)]
    [InlineData("ekrn.exe", "x.exe", SecurityStatus.Danger)]
    [InlineData("esets_svc.exe", "x.exe", SecurityStatus.Danger)]
    [InlineData("systemreset.exe", "x.exe", SecurityStatus.Danger)]
    [InlineData("passwordreset.exe", @"C:\Tools\wrapper.exe", SecurityStatus.Warning)] // regression: "eset" inside "reset"
    public void Classify(string image, string debugger, SecurityStatus expected)
        => Assert.Equal(expected, SecurityRules.ClassifyIfeo(image, debugger).Risk);

    [Fact]
    public void Evaluate_PicksWorst()
    {
        var entries = new List<IfeoEntry>
        {
            Make("game.exe", SecurityStatus.Warning),
            Make("taskmgr.exe", SecurityStatus.Danger),
        };
        var r = SecurityRules.EvaluateIfeo(entries, partial: false, readable: true);
        Assert.Equal(SecurityStatus.Danger, r.Status);
        Assert.Contains("taskmgr.exe", r.State);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateIfeo(new List<IfeoEntry>(), false, true).Status);
        Assert.Contains("администратор", SecurityRules.EvaluateIfeo(new List<IfeoEntry>(), true, true).Details);
    }

    private static IfeoEntry Make(string name, SecurityStatus risk) => new()
    {
        ImageName = name,
        Debugger = "x.exe",
        Location = new RegLocation(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\X\" + name),
        Risk = risk,
        Reason = "test",
    };
}

public class ProxyUacFirewallRulesTests
{
    [Fact]
    public void Proxy_EnabledServer_IsWarning()
    {
        var r = SecurityRules.EvaluateProxy(new ProxyState(true, true, "1.2.3.4:8080", null));
        Assert.Equal(SecurityStatus.Warning, r.Status);
        Assert.Contains("1.2.3.4:8080", r.State);
    }

    [Fact]
    public void Proxy_DisabledServer_IsOk()
        => Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateProxy(new ProxyState(true, false, "1.2.3.4:8080", null)).Status);

    [Fact]
    public void Proxy_Pac_IsWarning()
        => Assert.Equal(SecurityStatus.Warning,
            SecurityRules.EvaluateProxy(new ProxyState(true, false, null, "http://evil.example/proxy.pac")).Status);

    [Fact]
    public void Proxy_LocalServer_MentionsVpn()
        => Assert.Contains("VPN", SecurityRules.EvaluateProxy(new ProxyState(true, true, "127.0.0.1:10809", null)).Details);

    [Fact]
    public void Proxy_Nothing_IsOk()
        => Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateProxy(new ProxyState(true, false, null, null)).Status);

    [Theory]
    [InlineData(0, 5, SecurityStatus.Danger)]
    [InlineData(1, 0, SecurityStatus.Warning)]
    [InlineData(1, 5, SecurityStatus.Ok)]
    [InlineData(null, null, SecurityStatus.Ok)]
    public void Uac(int? lua, int? consent, SecurityStatus expected)
        => Assert.Equal(expected, SecurityRules.EvaluateUac(new UacState(true, lua, consent, 1)).Status);

    [Fact]
    public void Firewall_ProfileStates()
    {
        var none = Array.Empty<SecurityProduct>();
        var allOn = new[] { new FirewallProfileState("Private", true), new FirewallProfileState("Public", true), new FirewallProfileState("Domain", true) };
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateFirewall(allOn, none).Status);

        var privateOff = new[] { new FirewallProfileState("Private", false), new FirewallProfileState("Public", true), new FirewallProfileState("Domain", false) };
        var r = SecurityRules.EvaluateFirewall(privateOff, none);
        Assert.Equal(SecurityStatus.Danger, r.Status);
        Assert.Equal("Выключен для частной и доменной сетей", r.State);

        var domainOnly = new[] { new FirewallProfileState("Private", true), new FirewallProfileState("Public", true), new FirewallProfileState("Domain", false) };
        Assert.Equal(SecurityStatus.Warning, SecurityRules.EvaluateFirewall(domainOnly, none).Status);

        var thirdParty = new[] { SecurityRules.MakeProduct("ESET Firewall", 266240) };
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateFirewall(privateOff, thirdParty).Status);
        Assert.Equal(SecurityStatus.Unknown, SecurityRules.EvaluateFirewall(Array.Empty<FirewallProfileState>(), none).Status);
    }

    [Fact]
    public void Firewall_GroupPolicyOverridesLocalSetting()
    {
        var local = new[] { new FirewallProfileState("Private", true), new FirewallProfileState("Public", true), new FirewallProfileState("Domain", false) };
        var policy = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["Private"] = 0, ["Public"] = null, ["Domain"] = 1 };

        var effective = SecurityRules.ApplyFirewallPolicy(local, policy);

        Assert.Equal(new FirewallProfileState("Private", false, ByPolicy: true), effective[0]);
        Assert.Equal(new FirewallProfileState("Public", true), effective[1]);
        Assert.Equal(new FirewallProfileState("Domain", true, ByPolicy: true), effective[2]);
        var r = SecurityRules.EvaluateFirewall(effective, Array.Empty<SecurityProduct>());
        Assert.Equal(SecurityStatus.Danger, r.Status);
        Assert.Equal("Выключен для частной сети", r.State);
        Assert.Contains("групповой политикой", r.Details);
        Assert.Equal("StandardProfile", SecurityRules.FirewallPolicySubKey("Private"));
        Assert.True(new SecurityReport { FirewallProfiles = effective }.FirewallOffOnlyByPolicy);
        Assert.False(new SecurityReport { FirewallProfiles = local }.FirewallOffOnlyByPolicy);
    }

    [Theory]
    [InlineData((ushort)0, false)]
    [InlineData((ushort)1, true)]
    [InlineData((ushort)2, true)]
    public void GpoBoolean(ushort value, bool expected) => Assert.Equal(expected, SecurityRules.ParseGpoBoolean(value));

    [Fact]
    public void SmartScreenAndRdp()
    {
        Assert.Equal(SecurityStatus.Warning, SecurityRules.EvaluateSmartScreen(new SmartScreenState("Off", null)).Status);
        Assert.Equal(SecurityStatus.Warning, SecurityRules.EvaluateSmartScreen(new SmartScreenState("Warn", 0)).Status);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateSmartScreen(new SmartScreenState(null, null)).Status);
        Assert.Equal(SecurityStatus.Warning, SecurityRules.EvaluateRdp(new RdpState(0)).Status);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateRdp(new RdpState(1)).Status);
    }
}

public class AntivirusRulesTests
{
    private static DefenderStatus Defender(bool rt = true, int? sigAge = 0, bool av = true) => new()
    {
        ServiceEnabled = true,
        AntivirusEnabled = av,
        RealTimeEnabled = rt,
        SignatureAgeDays = sigAge,
        TamperProtected = true,
    };

    [Fact]
    public void Defender_On_IsOk()
        => Assert.Equal(SecurityStatus.Ok,
            SecurityRules.EvaluateAntivirus(Defender(), Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>()).Status);

    [Fact]
    public void Realtime_Off_IsDanger()
        => Assert.Equal(SecurityStatus.Danger,
            SecurityRules.EvaluateAntivirus(Defender(rt: false), Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>()).Status);

    [Fact]
    public void OldSignatures_IsWarning()
        => Assert.Equal(SecurityStatus.Warning,
            SecurityRules.EvaluateAntivirus(Defender(sigAge: 9), Array.Empty<SecurityProduct>(), Array.Empty<ThreatDetection>()).Status);

    [Fact]
    public void ThirdPartyAv_MakesPassiveDefenderOk()
    {
        var products = new[] { SecurityRules.MakeProduct("Windows Defender", 393472), SecurityRules.MakeProduct("Kaspersky", 266240) };
        var r = SecurityRules.EvaluateAntivirus(Defender(rt: false, av: false), products, Array.Empty<ThreatDetection>());
        Assert.Equal(SecurityStatus.Ok, r.Status);
        Assert.Contains("Kaspersky", r.State);
    }

    [Fact]
    public void Bitdefender_IsThirdPartyAntivirus()
    {
        // Regression: "Bitdefender" was taken for Microsoft Defender, so a protected PC was reported as unprotected.
        var products = new[] { SecurityRules.MakeProduct("Windows Defender", 393472), SecurityRules.MakeProduct("Bitdefender Antivirus Free", 266240) };
        var r = SecurityRules.EvaluateAntivirus(Defender(rt: false, av: false), products, Array.Empty<ThreatDetection>());
        Assert.Equal(SecurityStatus.Ok, r.Status);
        Assert.Contains("Bitdefender", r.State);
        Assert.True(new SecurityReport { Antiviruses = products }.HasThirdPartyAntivirus);
    }

    [Fact]
    public void InactiveThirdPartyAntivirus_IsNamed_NotDescribedAsMissing()
    {
        var products = new[] { SecurityRules.MakeProduct("Kaspersky Anti-Virus", 0x43100) }; // licence expired
        var r = SecurityRules.EvaluateAntivirus(Defender(rt: false, av: false), products, Array.Empty<ThreatDetection>());
        Assert.Equal(SecurityStatus.Danger, r.Status);
        Assert.Contains("Kaspersky Anti-Virus установлен, но его защита не работает (срок лицензии истёк)", r.Details);
        Assert.DoesNotContain("Другого антивируса не найдено", r.Details);
        Assert.Null(SecurityRules.InactiveThirdPartyText(new[] { SecurityRules.MakeProduct("Windows Defender", 393472) }));
    }

    [Fact]
    public void ActiveThreat_IsDanger_ResolvedIsNot()
    {
        var active = new[] { new ThreatDetection { Name = "Trojan:Win32/Test", StatusId = 1 } };
        Assert.Equal(SecurityStatus.Danger, SecurityRules.EvaluateAntivirus(Defender(), Array.Empty<SecurityProduct>(), active).Status);
        var removed = new[] { new ThreatDetection { Name = "Trojan:Win32/Test", StatusId = 4 } };
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateAntivirus(Defender(), Array.Empty<SecurityProduct>(), removed).Status);
    }

    [Fact]
    public void ThreatAllowedByUser_IsNotDanger()
    {
        var allowed = new ThreatDetection { Name = "HackTool:Win32/Keygen", StatusId = 5 };
        Assert.False(allowed.IsActive);
        Assert.Equal(SecurityStatus.Warning, allowed.Risk);
        var r = SecurityRules.EvaluateAntivirus(Defender(), Array.Empty<SecurityProduct>(), new[] { allowed });
        Assert.Equal(SecurityStatus.Ok, r.Status);
        Assert.Contains("Keygen", r.Details);
        Assert.True(new ThreatDetection { StatusId = 0 }.IsActive);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData(@"C:\Users\Bob", true)]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp", true)]
    [InlineData(".exe", true)]
    [InlineData("powershell.exe", true)]
    [InlineData(@"D:\Games\SteamLibrary", false)]
    [InlineData(@"C:\Projects\build", false)]
    public void DangerousExclusions(string path, bool expected) => Assert.Equal(expected, SecurityRules.IsDangerousExclusion(path));

    [Fact]
    public void NormalizeAge_TreatsUintMaxAsNever()
    {
        Assert.Null(SecurityRules.NormalizeAge(4294967295));
        Assert.Equal(3, SecurityRules.NormalizeAge(3));
        Assert.Equal("ни разу", SecurityRules.AgoText(null));
        Assert.Equal("сегодня", SecurityRules.AgoText(0));
        Assert.Equal("5 дней назад", SecurityRules.AgoText(5));
    }
}

public class DnsAndUpdatesRulesTests
{
    [Theory]
    [InlineData("8.8.8.8", "Google Public DNS", DnsKind.Known)]
    [InlineData("8.8.4.4", "Google Public DNS", DnsKind.Known)]
    [InlineData("1.1.1.1", "Cloudflare", DnsKind.Known)]
    [InlineData("9.9.9.9", "Quad9", DnsKind.Known)]
    [InlineData("77.88.8.8", "Яндекс DNS", DnsKind.Known)]
    [InlineData("94.140.14.14", "AdGuard DNS", DnsKind.Known)]
    [InlineData("208.67.222.222", "OpenDNS", DnsKind.Known)]
    [InlineData("2001:4860:4860::8888", "Google Public DNS", DnsKind.Known)]
    [InlineData("192.168.1.1", "Роутер или локальная сеть", DnsKind.Private)]
    [InlineData("10.0.1.1", "Роутер или локальная сеть", DnsKind.Private)]
    [InlineData("172.20.0.1", "Роутер или локальная сеть", DnsKind.Private)]
    [InlineData("fe80::1", "Роутер или локальная сеть", DnsKind.Private)]
    public void LabelKnownAndPrivate(string ip, string label, DnsKind kind)
    {
        var (l, k) = SecurityRules.LabelDns(ip);
        Assert.Equal(label, l);
        Assert.Equal(kind, k);
    }

    [Fact]
    public void UnknownPublicDns_IsInfo()
    {
        Assert.Equal(DnsKind.Unknown, SecurityRules.LabelDns("85.21.192.3").Kind);
        var servers = new[] { new DnsServerInfo("Wi-Fi", "85.21.192.3", "?", DnsKind.Unknown) };
        Assert.Equal(SecurityStatus.Info, SecurityRules.EvaluateDns(servers, true).Status);
        var good = new[] { new DnsServerInfo("Wi-Fi", "192.168.0.1", "Роутер или локальная сеть", DnsKind.Private) };
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateDns(good, true).Status);
    }

    [Theory]
    [InlineData("10/15/2026", 2026, 10, 15)]
    [InlineData("1/5/2024", 2024, 1, 5)]
    [InlineData("01/05/2024", 2024, 1, 5)]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData("2023-12-31", 2023, 12, 31)]
    public void QfeDate_Parses(string text, int y, int m, int d)
        => Assert.Equal(new DateTime(y, m, d), SecurityRules.ParseQfeDate(text));

    [Fact]
    public void QfeDate_HexFiletimeAndGarbage()
    {
        var expected = DateTime.FromFileTimeUtc(0x01d4c8a1b2c3d4e5).ToLocalTime().Date;
        Assert.Equal(expected, SecurityRules.ParseQfeDate("01d4c8a1b2c3d4e5"));
        Assert.Null(SecurityRules.ParseQfeDate(""));
        Assert.Null(SecurityRules.ParseQfeDate(null));
        Assert.Null(SecurityRules.ParseQfeDate("вчера"));
    }

    [Fact]
    public void Updates_OldIsWarning_RecentIsOk()
    {
        var now = new DateTime(2026, 10, 16);
        Assert.Equal(SecurityStatus.Warning,
            SecurityRules.EvaluateUpdates(new UpdatesState(now.AddDays(-90), 10, "KB1", false), now).Status);
        Assert.Equal(SecurityStatus.Ok,
            SecurityRules.EvaluateUpdates(new UpdatesState(now.AddDays(-3), 10, "KB1", false), now).Status);
        Assert.Equal(SecurityStatus.Warning,
            SecurityRules.EvaluateUpdates(new UpdatesState(now.AddDays(-3), 10, "KB1", true), now).Status);
        Assert.Equal(SecurityStatus.Unknown,
            SecurityRules.EvaluateUpdates(new UpdatesState(null, 0, null, null), now).Status);
    }

    [Theory]
    [InlineData(1, "Удалена 1 строка", "будет удалена 1 строка")]
    [InlineData(3, "Удалены 3 строки", "будут удалены 3 строки")]
    [InlineData(5, "Удалено 5 строк", "будут удалены 5 строк")]
    [InlineData(21, "Удалена 21 строка", "будет удалена 21 строка")]
    public void HostsLineCountTexts(int n, string removed, string toRemove)
    {
        Assert.Equal(removed, SecurityRules.LinesRemovedText(n));
        Assert.Equal(toRemove, SecurityRules.LinesToRemoveText(n));
    }

    [Fact]
    public void Extensions_UnreadableIsUnknown_NotMissingBrowsers()
    {
        Assert.Equal(SecurityStatus.Unknown, SecurityRules.EvaluateExtensions(new List<BrowserExtension>(), -1).Status);
        Assert.Equal("Браузеры не найдены", SecurityRules.EvaluateExtensions(new List<BrowserExtension>(), 0).State);
    }

    [Fact]
    public void Score_PenalizesProblems()
    {
        var ok = SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "", "")).ToList();
        Assert.Equal(100, SecurityRules.Score(ok));
        ok[1] = ok[1] with { Status = SecurityStatus.Danger };
        Assert.Equal(80, SecurityRules.Score(ok));
    }
}
