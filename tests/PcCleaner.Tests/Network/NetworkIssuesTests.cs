using PcCleaner.Core;
using PcCleaner.Modules.Network;
using Xunit;
using static PcCleaner.Tests.Network.NetworkTestData;

namespace PcCleaner.Tests.Network;

public class NetworkIssuesTests
{
    public NetworkIssuesTests() => TestEnvironment.Init();

    private static LatencyReport Latency(double? ms)
        => new()
        {
            Targets = NetworkRules.LatencyTargets.Select(t => new LatencyResult(t, new double?[] { ms, ms, ms })).ToList(),
        };

    private static readonly AdapterInfo Ethernet = Adapter("Ethernet", AdapterKind.Ethernet, 1_000_000_000, "Intel(R) Ethernet Connection I219-V");

    private static WifiReport Wifi(int signal, double band = 5, string auth = "WPA2-Personal")
        => new()
        {
            Interfaces = new[]
            {
                new WifiInterfaceInfo
                {
                    Name = "Wi-Fi", State = "connected", IsConnected = true, Ssid = "Home", SignalPercent = signal, BandGhz = band,
                    Authentication = auth, Cipher = "CCMP",
                },
            },
        };

    [Fact]
    public void HealthyNetwork_HasNoProblems()
    {
        var issues = NetworkRules.BuildIssues(new NetworkSnapshot
        {
            Adapters = new[] { Ethernet },
            Latency = Latency(20),
            Dns = new DnsReport(new[] { new DnsLookup("example.com", 15), new DnsLookup("github.com", 20) }),
            Wifi = Wifi(90),
            UploadBytesPerSecond = 50_000,
        });
        Assert.DoesNotContain(issues, i => i.Severity > IssueSeverity.Info);
        Assert.All(issues, i =>
        {
            Assert.Equal("Сеть", i.Module);
            Assert.Equal("Network", i.TargetPageKey);
            Assert.Null(i.FixAsync);
        });
    }

    [Fact]
    public void NoTargetReachable_IsHighNoInternet()
    {
        var issues = NetworkRules.BuildIssues(new NetworkSnapshot { Adapters = new[] { Ethernet }, Latency = Latency(null) });
        var issue = Assert.Single(issues);
        Assert.Equal("Нет доступа в интернет", issue.Title);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Contains("роутер", issue.Details);
        Assert.Contains("Сбросить сетевые настройки", issue.Details);
    }

    [Fact]
    public void NoInternet_SuppressesDnsAndLatencyNoise()
    {
        var issues = NetworkRules.BuildIssues(new NetworkSnapshot
        {
            Adapters = new[] { Ethernet },
            Latency = Latency(null),
            Dns = new DnsReport(new[] { new DnsLookup("example.com", null) }),
        });
        Assert.Single(issues);
    }

    [Fact]
    public void HighLatency_IsLow()
    {
        var issue = Assert.Single(NetworkRules.BuildIssues(new NetworkSnapshot { Adapters = new[] { Ethernet }, Latency = Latency(140) }));
        Assert.Equal("Высокая задержка", issue.Title);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Contains("140 мс", issue.Details);
    }

    [Fact]
    public void SlowDns_IsLow_AndFailedDns_IsHigh()
    {
        var slow = NetworkRules.BuildIssues(new NetworkSnapshot
        {
            Latency = Latency(20),
            Dns = new DnsReport(new[] { new DnsLookup("a", 180), new DnsLookup("b", 220), new DnsLookup("c", 90) }),
        });
        var slowIssue = Assert.Single(slow);
        Assert.Equal("Медленный DNS", slowIssue.Title);
        Assert.Equal(IssueSeverity.Low, slowIssue.Severity);
        Assert.Contains("1.1.1.1", slowIssue.Details);
        Assert.Contains("77.88.8.8", slowIssue.Details);

        var failed = NetworkRules.BuildIssues(new NetworkSnapshot
        {
            Latency = Latency(20),
            Dns = new DnsReport(new[] { new DnsLookup("a", null), new DnsLookup("b", null) }),
        });
        Assert.Equal(IssueSeverity.High, Assert.Single(failed).Severity);
    }

    [Fact]
    public void SlowWiredAdapter_IsLowWithCableAdvice()
    {
        var slow = Adapter("Ethernet", AdapterKind.Ethernet, 100_000_000, "Realtek PCIe GbE Family Controller");
        var issue = Assert.Single(NetworkRules.BuildIssues(new NetworkSnapshot { Adapters = new[] { slow } }));
        Assert.Equal("Адаптер Ethernet работает на 100 Мбит/с", issue.Title);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Contains("Cat5e", issue.Details);
    }

    [Fact]
    public void WifiRules()
    {
        var weak = NetworkRules.BuildIssues(new NetworkSnapshot { Wifi = Wifi(35) });
        Assert.Contains(weak, i => i.Title == "Слабый сигнал Wi-Fi" && i.Severity == IssueSeverity.Low);

        var band = NetworkRules.BuildIssues(new NetworkSnapshot { Wifi = Wifi(80, band: 2.4) });
        var bandIssue = Assert.Single(band);
        Assert.Equal(IssueSeverity.Info, bandIssue.Severity);
        Assert.Contains("5 ГГц", bandIssue.Details);

        var open = NetworkRules.BuildIssues(new NetworkSnapshot { Wifi = Wifi(80, auth: "Open") });
        Assert.Contains(open, i => i.Title == "Сеть Wi-Fi без надёжного пароля" && i.Severity == IssueSeverity.Medium);

        var wep = NetworkRules.BuildIssues(new NetworkSnapshot { Wifi = Wifi(80, auth: "WEP") });
        Assert.Contains(wep, i => i.Title == "Сеть Wi-Fi без надёжного пароля" && i.Details.Contains("WEP"));
    }

    [Fact]
    public void HighUpload_IsMediumAndNamesBusyPrograms()
    {
        var issues = NetworkRules.BuildIssues(new NetworkSnapshot
        {
            UploadBytesPerSecond = 3 * 1024 * 1024,
            Sockets = Snapshot(Connection("142.250.74.14", 443, Chrome.Pid), Connection("142.250.74.15", 443, Chrome.Pid)),
        });
        var issue = Assert.Single(issues);
        Assert.Equal("Высокая исходящая активность", issue.Title);
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
        Assert.Contains("chrome.exe", issue.Details);

        Assert.Empty(NetworkRules.BuildIssues(new NetworkSnapshot { UploadBytesPerSecond = 1024 * 1024 }));
    }

    [Fact]
    public void RiskyPort_IsMedium_WindowsPorts_AreOneInfo_SuspiciousConnection_IsHigh()
    {
        var snapshot = Snapshot(
            Listen("0.0.0.0", 3306, MySql.Pid),
            Listen("::", 3306, MySql.Pid),
            Listen("127.0.0.1", 6379, MySql.Pid), // local only: fine
            Listen("0.0.0.0", 135, Svchost.Pid),
            Listen("0.0.0.0", 445, NetworkTestData.System.Pid),
            Connection("45.155.205.10", 3333, Miner.Pid),
            Connection("45.155.205.11", 3333, Miner.Pid),
            Connection("142.250.74.14", 443, Chrome.Pid));

        var findings = NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot });

        var port = Assert.Single(findings, f => f.Kind == FindingKind.RiskyPort);
        Assert.Equal("Открыт порт 3306 (mysqld.exe)", port.Issue.Title);
        Assert.Equal(IssueSeverity.Medium, port.Issue.Severity);
        Assert.Equal("3306", port.Argument);

        var windows = Assert.Single(findings, f => f.Kind == FindingKind.WindowsPorts);
        Assert.Equal(IssueSeverity.Info, windows.Issue.Severity);
        Assert.Contains("135", windows.Issue.Details);
        Assert.Contains("445", windows.Issue.Details);
        Assert.Contains("роутере", windows.Issue.Details);

        var connection = Assert.Single(findings, f => f.Kind == FindingKind.SuspiciousConnection);
        Assert.Equal(IssueSeverity.High, connection.Issue.Severity);
        Assert.Equal("xmr.exe подключён к 45.155.205.10:3333", connection.Issue.Title);
        Assert.Contains("майнинг", connection.Issue.Details);
        Assert.Contains("ещё 1", connection.Issue.Details);

        Assert.Equal(FindingKind.SuspiciousConnection, findings[0].Kind); // most severe first
    }

    [Fact]
    public void TrustedProgram_IsNotReported()
    {
        var path = MySql.Path!;
        AppServices.Settings.AddTrusted(path);
        try
        {
            var findings = NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = Snapshot(Listen("0.0.0.0", 3306, MySql.Pid)) });
            Assert.DoesNotContain(findings, f => f.Kind == FindingKind.RiskyPort);
        }
        finally
        {
            AppServices.Settings.RemoveTrusted(path);
        }
    }
}
