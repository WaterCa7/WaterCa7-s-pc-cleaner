using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Network;
using PcCleaner.Tests.Core;
using Xunit;
using static PcCleaner.Tests.Network.NetworkTestData;

namespace PcCleaner.Tests.Network;

/// <summary>
/// The Network page and its dashboard findings in English (the real en.json): the issue texts of the findings, the DNS fix and its
/// results (success, DNS still silent, flush failed), the units and words of the page, the plural words of the converter, the names of
/// programs in the reasons. Runs alone (the language is process-wide) and always switches back to Russian; the Russian twin of the
/// main texts is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class NetworkEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    public NetworkEnglishTests() => TestEnvironment.Init();

    private static void AssertEnglish(params string?[] texts)
    {
        foreach (var text in texts)
            Assert.DoesNotMatch(Cyrillic, text ?? "");
    }

    private static LatencyReport Latency(double? ms)
        => new() { Targets = NetworkRules.LatencyTargets.Select(t => new LatencyResult(t, new double?[] { ms, ms, ms })).ToList() };

    private static readonly AdapterInfo Ethernet = Adapter("Ethernet", AdapterKind.Ethernet, 1_000_000_000, "Intel(R) Ethernet Connection I219-V");

    private static NetworkSnapshot DnsFailedSnapshot()
        => new() { Latency = Latency(20), Dns = new DnsReport(new[] { new DnsLookup("a", null), new DnsLookup("b", null) }) };

    private static readonly DnsReport DnsAnswers = new(new[] { new DnsLookup("example.com", 15), new DnsLookup("wikipedia.org", 20) });
    private static readonly DnsReport DnsSilent = new(new[] { new DnsLookup("example.com", null), new DnsLookup("wikipedia.org", null) });

    private static NetworkService ServiceWithFakes(bool flushOk, string flushOutput, int exitCode, DnsReport dnsAfterFlush)
        => new()
        {
            RunTool = (_, _, _, _) => Task.FromResult((flushOk, flushOutput, exitCode)),
            DnsProbe = _ => Task.FromResult(dnsAfterFlush),
        };

    // ================================================================ findings

    [Fact]
    public void NoInternet_HasEnglishTitleDetailsAndModule_AndRussianIsUnchanged()
    {
        var snapshot = new NetworkSnapshot { Adapters = new[] { Ethernet }, Latency = Latency(null) };
        var russian = Assert.Single(NetworkRules.BuildIssues(snapshot));
        Assert.Equal("Нет доступа в интернет", russian.Title);
        Assert.StartsWith("Ни один из проверочных серверов (Cloudflare, Google, Яндекс, Microsoft) не ответил. Что сделать:", russian.Details);

        using (TestLanguage.English())
        {
            var issue = Assert.Single(NetworkRules.BuildIssues(snapshot));

            Assert.Equal("Network", issue.Module);
            Assert.Equal("No internet access", issue.Title);
            Assert.Equal("None of the test servers (Cloudflare, Google, Yandex, Microsoft) answered. What to do: check the cable or the Wi-Fi " +
                         "connection; turn the router off for 30 seconds and on again. If that does not help, go to the Network page and press " +
                         "“Reset network settings”.", issue.Details);
            Assert.Equal("ms-settings:network-status", issue.SettingsUri);
            AssertEnglish(issue.Module, issue.Title, issue.Details, issue.FixLabel);
        }
    }

    [Fact]
    public void OpenPort_HasEnglishTitleAndDetails()
    {
        var snapshot = Snapshot(Listen("0.0.0.0", 3306, MySql.Pid), Listen("0.0.0.0", 135, Svchost.Pid), Listen("0.0.0.0", 445, NetworkTestData.System.Pid));
        var findings = NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot });
        Assert.Equal("Открыт порт 3306 (mysqld.exe)", Assert.Single(findings, f => f.Kind == FindingKind.RiskyPort).Issue.Title);

        using (TestLanguage.English())
        {
            findings = NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot });

            var port = Assert.Single(findings, f => f.Kind == FindingKind.RiskyPort).Issue;
            Assert.Equal("Port 3306 is open (mysqld.exe)", port.Title);
            Assert.StartsWith("Database: MySQL / MariaDB. A database accepts connections from the network.", port.Details);
            Assert.Equal(IssueSeverity.Medium, port.Severity);

            var windows = Assert.Single(findings, f => f.Kind == FindingKind.WindowsPorts).Issue;
            Assert.Equal("Standard Windows ports are open", windows.Title);
            Assert.StartsWith("Ports 135, 445 accept connections from the local network", windows.Details);
            AssertEnglish(port.Title, port.Details, windows.Title, windows.Details);
        }
    }

    [Fact]
    public async Task DnsFailed_OnTheDashboard_HasAnEnglishIssue_AndTheFixResultsAreRecognised()
    {
        using var _ = TestLanguage.English();

        var working = ServiceWithFakes(true, "Successfully flushed the DNS Resolver Cache.", 0, DnsAnswers);
        var issue = Assert.Single(working.BuildIssues(DnsFailedSnapshot()));
        Assert.Equal("DNS is not answering — websites do not open", issue.Title);
        Assert.Equal("Clear DNS cache", issue.FixLabel);
        Assert.StartsWith("The computer could not find the addresses of websites (example.com, wikipedia.org…), although the internet connection works. " +
                          "The “Clear DNS cache” button clears the cache", issue.Details);
        AssertEnglish(issue.Title, issue.FixLabel, issue.Details);

        var fixedText = await issue.FixAsync!(CancellationToken.None);
        Assert.Equal("The DNS cache was cleared — website addresses are found again.", fixedText);
        Assert.False(HealthScoring.LooksLikeFailure(fixedText));

        var silent = ServiceWithFakes(true, "Successfully flushed the DNS Resolver Cache.", 0, DnsSilent);
        var silentText = await Assert.Single(silent.BuildIssues(DnsFailedSnapshot())).FixAsync!(CancellationToken.None);
        Assert.Equal("Could not restore DNS: the cache was cleared, but website addresses are still not found — change the DNS server in the network settings.", silentText);
        Assert.True(HealthScoring.LooksLikeFailure(silentText));
        Assert.False(HealthScoring.LooksLikeAdminRequired(silentText));

        var broken = ServiceWithFakes(false, "Could not flush the DNS Resolver Cache: Function failed during execution.", 1, DnsAnswers);
        var brokenText = await Assert.Single(broken.BuildIssues(DnsFailedSnapshot())).FixAsync!(CancellationToken.None);
        Assert.StartsWith("Could not clear the DNS cache: ", brokenText);
        Assert.True(HealthScoring.LooksLikeFailure(brokenText));
    }

    [Fact]
    public async Task TheDnsFixResults_AreTheSameCaseInRussian()
    {
        var silent = ServiceWithFakes(true, "ok", 0, DnsSilent);
        var text = await Assert.Single(silent.BuildIssues(DnsFailedSnapshot())).FixAsync!(CancellationToken.None);
        Assert.StartsWith("Не удалось восстановить DNS:", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task TheNetworkReset_WithoutAdministratorRights_IsAdminOnlyInEnglish()
    {
        if (Admin.IsElevated)
            return; // the message below is what a non-elevated run gets

        using var _ = TestLanguage.English();
        var (ok, message) = await new NetworkService().ResetNetworkAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Resetting the network settings needs administrator rights — nothing was changed.", message);
        Assert.True(HealthScoring.LooksLikeFailure(message));
        Assert.True(HealthScoring.LooksLikeAdminRequired(message));
    }

    [Fact]
    public async Task TheNetworkReset_StepsAreReportedInEnglish()
    {
        using var _ = TestLanguage.English();
        var (ok, message) = await NetworkService.ResetStepsAsync(
            (args, _) => Task.FromResult(args == "winsock reset" ? (true, "OK", 0) : (false, "Resetting , failed.\r\nAccess is denied.", 1)),
            CancellationToken.None);

        Assert.False(ok);
        Assert.StartsWith("Done: the Winsock catalog was reset (netsh winsock reset). The command “netsh int ip reset” ended with an error:", message);
        Assert.Contains("Restart the computer", message);
        AssertEnglish(message);
    }

    [Fact]
    public void AllFindings_HaveNoRussianLeft_InEnglish()
    {
        var vpn = Adapter("Tunnel", AdapterKind.Vpn, 0, "OpenVPN TAP-Windows Adapter");
        var slowCable = Adapter("Ethernet 2", AdapterKind.Ethernet, 100_000_000, "Realtek PCIe GbE Family Controller");
        var wifi = new WifiReport
        {
            Interfaces = new[]
            {
                new WifiInterfaceInfo
                {
                    Name = "Wi-Fi", State = "connected", IsConnected = true, Ssid = "Home", SignalPercent = 30, BandGhz = 2.4,
                    Authentication = "WEP", Cipher = "WEP",
                },
            },
        };
        var rdpOwner = new ProcessIdentity(1000, "svchost.exe", @"C:\Windows\System32\svchost.exe", "Windows service host", true);
        var rdp = new ProcessIdentity(1100, "termsrv.exe", @"C:\Windows\System32\termsrv.exe", "Remote Desktop", true);
        var sockets = new SocketSnapshot(
            new[]
            {
                Listen("0.0.0.0", 3306, MySql.Pid), Listen("0.0.0.0", 3389, rdp.Pid), Listen("0.0.0.0", 135, rdpOwner.Pid),
                Connection("45.155.205.10", 4444, Chrome.Pid), Connection("45.155.205.11", 4444, Chrome.Pid),
                Connection("45.155.205.12", 3333, Miner.Pid),
            },
            new Dictionary<int, ProcessIdentity>
            {
                [MySql.Pid] = MySql, [rdp.Pid] = rdp, [rdpOwner.Pid] = rdpOwner, [Chrome.Pid] = Chrome, [Miner.Pid] = Miner,
            },
            DateTime.Now)
        {
            ProgramWarnings = new Dictionary<string, string> { [Miner.Path!] = "no digital signature; the file is hidden" },
        };
        var scenarios = new[]
        {
            new NetworkSnapshot { Adapters = new[] { vpn }, Latency = new LatencyReport { CaptivePortal = true, Targets = Latency(null).Targets } },
            new NetworkSnapshot { Adapters = new[] { Ethernet, vpn }, Latency = new LatencyReport { Intercepted = true, Targets = Latency(180).Targets } },
            new NetworkSnapshot { Latency = Latency(20), Dns = new DnsReport(new[] { new DnsLookup("a", 180), new DnsLookup("b", 220) }) },
            new NetworkSnapshot
            {
                Adapters = new[] { slowCable }, Wifi = wifi, UploadBytesPerSecond = 3 * 1024 * 1024, Sockets = sockets,
                Firewall = new FirewallReport
                {
                    Profiles = new Dictionary<string, bool?> { ["Public"] = false, ["Private"] = false },
                    Networks = new[] { new ConnectedNetwork("Home", "Public"), new ConnectedNetwork("Work", "Private") },
                },
            },
        };

        using var _ = TestLanguage.English();
        var count = 0;
        foreach (var snapshot in scenarios)
        {
            foreach (var finding in NetworkRules.BuildFindings(snapshot, flushDns: _ => Task.FromResult("")))
            {
                AssertEnglish(finding.Issue.Module, finding.Issue.Title, finding.Issue.Details, finding.Issue.FixLabel);
                count++;
            }
        }
        Assert.True(count >= 9, "the scenarios should produce the findings of all kinds, got " + count);
    }

    // ================================================================ words, units, plurals

    [Fact]
    public void Units_AndWordsOfTheRatings_AreEnglish()
    {
        using var _ = TestLanguage.English();

        Assert.Equal("< 1 ms", LatencyMath.FormatMs(0.4));
        Assert.Equal("2.4 ms", LatencyMath.FormatMs(2.4));
        Assert.Equal("23 ms", LatencyMath.FormatMs(23));
        Assert.Equal("—", LatencyMath.FormatMs(null));
        Assert.Equal("100 Mbps", NetworkRules.FormatLinkSpeed(100_000_000));
        Assert.Equal("2.5 Gbps", NetworkRules.FormatLinkSpeed(2_500_000_000));
        Assert.Equal(Format.Bytes(1024 * 1024) + "/s", LatencyMath.FormatRate(1024 * 1024));
        Assert.EndsWith("MB/s", LatencyMath.FormatRate(1024 * 1024));
        Assert.Equal((NetTone.Ok, "excellent"), LatencyMath.RateInternet(20));
        Assert.Equal((NetTone.Danger, "no response"), LatencyMath.RateInternet(null));
        Assert.Equal("2.4 GHz", NetworkRules.BandText(2.4));
        Assert.Equal("Excellent signal", NetworkRules.SignalQuality(90).Text);
        Assert.Equal("Established", NetworkRules.StateText(TcpState.Established));
        Assert.Equal("Virtual", NetworkRules.KindText(AdapterKind.Virtual));
        Assert.Equal("Unusual port", NetworkRules.RemoteServiceName(7777));
        Assert.Equal("Torrent", NetworkRules.RemoteServiceName(6885));
        Assert.Equal("HTTPS — web server", NetworkRules.ServiceName(SocketProtocol.Tcp, 443));
        Assert.Equal("Critical", NetworkRules.SeverityText(IssueSeverity.Critical));
        Assert.Equal("Minor", NetworkRules.SeverityText(IssueSeverity.Low));
        Assert.Equal("Yandex", NetworkRules.LatencyTargets.Select(t => t.Name).First(n => n.StartsWith('Y')));
        Assert.Equal("Yandex", NetworkRules.DnsLabel(System.Net.IPAddress.Parse("77.88.8.8")).Label);
        Assert.Equal("Router / provider", NetworkRules.DnsLabel(System.Net.IPAddress.Parse("192.168.1.1")).Label);
    }

    [Fact]
    public void TheUnitsAndWords_StayRussianInRussian()
    {
        Assert.Equal("2,4 мс", LatencyMath.FormatMs(2.4));
        Assert.Equal("100 Мбит/с", NetworkRules.FormatLinkSpeed(100_000_000));
        Assert.Equal("2,4 ГГц", NetworkRules.BandText(2.4));
        Assert.Equal("Яндекс", NetworkRules.LatencyTargets.Select(t => t.Name).First(n => n.StartsWith('Я')));
        Assert.Equal("Необычный порт", NetworkRules.UnusualPortText);
    }

    [Fact]
    public void ThePluralConverter_UsesTheEnglishWords_AndTheRussianForms()
    {
        Assert.Equal("1 соединение", PluralConverter.Words("connection", 1));
        Assert.Equal("2 соединения", PluralConverter.Words("connection", 2));
        Assert.Equal("5 соединений", PluralConverter.Words("connection", 5));
        Assert.Equal("21 порт", PluralConverter.Words("port", 21));
        Assert.Equal("7", PluralConverter.Words("unknown", 7));

        using var _ = TestLanguage.English();

        Assert.Equal("1 connection", PluralConverter.Words("connection", 1));
        Assert.Equal("2 connections", PluralConverter.Words("connection", 2));
        Assert.Equal("1 port", PluralConverter.Words("port", 1));
        Assert.Equal("1,200 ports", PluralConverter.Words("port", 1200));
        var converter = new PluralConverter();
        Assert.Equal("3 connections", converter.Convert(3, typeof(string), "connection", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("5", converter.Convert(5, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ================================================================ names of programs in the reasons

    [Theory]
    [InlineData("xmrig.exe", "XMRig")]
    [InlineData("t-rex.exe", "T-Rex")]
    [InlineData("nicehash.exe", "NiceHash")]
    [InlineData("anydesk.exe", "AnyDesk")]
    [InlineData("teamviewer.exe", "TeamViewer")]
    [InlineData("rustdesk.exe", "RustDesk")]
    [InlineData("radmin.exe", "Radmin")]
    [InlineData("ammyy.exe", "Ammyy")]
    [InlineData("mimikatz.exe", "Mimikatz")]
    [InlineData("psexec.exe", "PsExec")]
    public void AProductNameAtTheStartOfAReason_KeepsItsCapitals(string file, string name)
    {
        using var _ = TestLanguage.English();
        var reason = KnownPups.MatchRiskyTool(file)!.Value.Reason;
        Assert.StartsWith(name, reason);

        var text = NetworkRules.ProgramWarningText(new[] { reason, "No digital signature", "Hidden or encoded PowerShell launch", "T" });

        Assert.StartsWith(name, text); // not "xMRig", "anyDesk", "radmin"…
        Assert.Contains("; no digital signature; hidden or encoded PowerShell launch; ", text); // plain words are lower-cased
    }

    [Fact]
    public void TheReasonsInRussian_AreStillLowerCased()
        => Assert.Equal("нет цифровой подписи; файл скрыт", NetworkRules.ProgramWarningText(new[] { "Нет цифровой подписи", "Файл скрыт" }));

    // ================================================================ the page

    [Fact]
    public void ThePageAndItsRows_AreEnglish()
    {
        using var _ = TestLanguage.English();
        var vm = new NetworkViewModel(new NetworkService());

        Assert.Equal("Network", vm.Title);
        Assert.Equal("Connection speed, latency, Wi-Fi, open ports and program connections", vm.Subtitle);
        Assert.Equal("Checking the network…", vm.StatusTitle);
        Assert.Equal("Connections", vm.ConnectionsTabHeader);

        vm.ApplySockets(Snapshot(
            Connection("192.168.1.20", 445, Chrome.Pid, localPort: 50001),
            Connection("192.168.1.20", 445, Chrome.Pid, localPort: 50002),
            Connection("10.0.0.5", 22, Spotify.Pid, TcpState.SynSent),
            Listen("0.0.0.0", 3306, MySql.Pid)));

        Assert.StartsWith("Connections · 3", vm.ConnectionsTabHeader);
        var chrome = vm.ConnectionRows.OfType<ConnectionGroupItem>().Single(g => g.ProcessName == "chrome.exe");
        Assert.Equal("Established", chrome.StateText);
        Assert.Equal("The program has 2 connections", chrome.CountToolTip);
        var merged = vm.ConnectionRows.OfType<ConnectionItem>().Single(c => c.RemotePort == 445);
        Assert.Equal("local network", merged.HostName);
        Assert.Equal("2 identical connections with 192.168.1.20:445", merged.CountToolTip);
        Assert.Contains("Remote address: 192.168.1.20:445", merged.ToolTip);
        var port = vm.PortsView.Cast<PortItem>().Single(p => p.Port == 3306);
        Assert.Equal("from the network", port.AccessText);
        Assert.Equal("Risky", port.KindText);
        Assert.Contains("Address 0.0.0.0: ", port.AccessToolTip);
        AssertEnglish(chrome.StateToolTip, chrome.ToolTip, chrome.CopyText, merged.ToolTip, merged.CopyText, port.ToolTip, port.CopyText,
            vm.PortsHiddenNote, vm.ConnectionsEmptyText, vm.PortsEmptyText, vm.SocketsHint);
    }

    [Fact]
    public void TheHostOfAPublicAddress_IsPendingInEnglish_AndPendingStaysTheStateNotTheText()
    {
        var row = Assert.Single(NetworkRules.BuildConnections(Snapshot(Connection("142.250.74.14", 443, Chrome.Pid)), includeLoopback: false));

        var russian = new ConnectionItem(row);
        Assert.Equal("определяю…", russian.HostName);
        Assert.True(russian.IsHostPending);

        using var _ = TestLanguage.English();
        var item = new ConnectionItem(row);
        Assert.Equal("resolving…", item.HostName);
        Assert.True(item.IsHostPending);
        Assert.Equal("resolving…", item.HostText);

        item.HostName = "lh3.example.net";
        Assert.False(item.IsHostPending);
        Assert.Equal("lh3.example.net", item.HostText);
        Assert.Contains("(lh3.example.net)", item.ToolTip);

        item.HostName = ConnectionItem.NoHost;
        Assert.False(item.IsHostPending);
        Assert.DoesNotContain("(—)", item.ToolTip);
    }

    [Fact]
    public void TheWifiParser_StillReadsTheRussianWordsOfNetsh_InEnglish()
    {
        using var _ = TestLanguage.English();
        const string output = "    Имя                    : Wi-Fi\r\n    Состояние              : подключено\r\n    SSID                   : Home\r\n" +
                              "    Сигнал                 : 70%\r\n    Проверка подлинности   : WPA2-Personal\r\n    Идентификатор GUID     : {11111111-2222-3333-4444-555555555555}\r\n";
        var info = Assert.Single(WlanParser.Parse(output));
        Assert.True(info.IsConnected);
        Assert.Equal(70, info.SignalPercent);
        Assert.True(NetworkRules.IsWeakWifiSecurity("Открытая", "")); // netsh prints the value in the Windows language
        Assert.True(NetworkRules.IsWeakWifiSecurity("Открытая сеть", ""));
        Assert.True(NetworkRules.IsWeakWifiSecurity("Open", ""));
    }

    [Fact]
    public void AdapterRows_AreEnglish()
    {
        using var _ = TestLanguage.English();
        var item = new AdapterItem(Ethernet, isPrimary: true);

        Assert.Equal("IPv4 address", item.Fields[0].Label);
        Assert.Equal("Gateway (router)", item.Fields[1].Label);
        Assert.Equal("A wired connection by cable", item.KindToolTip);
        Assert.Equal("1 Gbps", item.SpeedText);
        item.UpdateTraffic(new TrafficCounters(0, 0), new TrafficCounters(2048, 1024), 1);
        Assert.StartsWith("Total: ↓ 2 KB · ↑ 1 KB", item.Totals);
        AssertEnglish(item.KindText, item.KindToolTip, item.SpeedToolTip, item.SlowLinkText, item.Totals,
            string.Join(" ", item.Fields.SelectMany(f => new[] { f.Label, f.Value, f.ToolTip ?? "" })));
    }
}
