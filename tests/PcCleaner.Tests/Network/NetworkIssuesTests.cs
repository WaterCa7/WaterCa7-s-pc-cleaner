using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
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
            Assert.Null(i.SettingsUri);
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
    public void RiskyPort_IsMedium_WindowsPorts_AreOneInfo_UnusualPortAlone_IsMedium()
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

        // The port alone proves nothing (games and chats use some of these ports): "worth a look", not an alarm.
        var connection = Assert.Single(findings, f => f.Kind == FindingKind.SuspiciousConnection);
        Assert.Equal(IssueSeverity.Medium, connection.Issue.Severity);
        Assert.Equal("xmr.exe подключён к 45.155.205.10:3333", connection.Issue.Title);
        Assert.Contains("майнинг", connection.Issue.Details);
        Assert.Contains("ещё 1", connection.Issue.Details);
        Assert.Contains("игра или чат", connection.Issue.Details);
        Assert.Contains("значком щита «Проверить IP»", connection.Issue.Details);
        Assert.DoesNotContain("кнопкой", connection.Issue.Details);
    }

    [Fact]
    public void GameOnPort7777_IsMedium_AndNamedUnusualNotDangerous()
    {
        var game = new ProcessIdentity(7700, "Terraria.exe", @"C:\Games\Terraria\Terraria.exe", "Terraria", false);
        var snapshot = new SocketSnapshot(new[] { Connection("51.38.10.20", 7777, game.Pid) },
            new Dictionary<int, ProcessIdentity> { [game.Pid] = game }, DateTime.Now);

        var row = Assert.Single(NetworkRules.BuildConnections(snapshot, includeLoopback: false));
        Assert.True(row.IsSuspicious);
        Assert.False(row.IsHighRisk);
        Assert.Equal("Необычный порт", NetworkRules.RemoteServiceName(7777));
        Assert.Equal("Необычный порт", new ConnectionItem(row).SuspiciousBadgeText);

        var finding = Assert.Single(NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot }));
        Assert.Equal(IssueSeverity.Medium, finding.Issue.Severity);
    }

    [Fact]
    public void StrongPort_IsHigh()
    {
        var snapshot = Snapshot(Connection("45.155.205.10", 4444, Chrome.Pid));
        var row = Assert.Single(NetworkRules.BuildConnections(snapshot, includeLoopback: false));
        Assert.True(row.IsHighRisk);
        Assert.Equal("Подозрительно", new ConnectionItem(row).SuspiciousBadgeText);
        var finding = Assert.Single(NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot }));
        Assert.Equal(IssueSeverity.High, finding.Issue.Severity);
        Assert.Equal(FindingKind.SuspiciousConnection, finding.Kind);
    }

    [Fact]
    public void UnusualPort_AndUnusualProgram_IsHigh()
    {
        var snapshot = Snapshot(Connection("45.155.205.10", 3333, Miner.Pid)) with
        {
            ProgramWarnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Miner.Path!] = "нет цифровой подписи; файл лежит прямо в корне AppData/ProgramData",
            },
        };

        var row = Assert.Single(NetworkRules.BuildConnections(snapshot, includeLoopback: false));
        Assert.True(row.IsHighRisk);
        Assert.NotNull(row.ProgramWarning);

        var finding = Assert.Single(NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot }));
        Assert.Equal(IssueSeverity.High, finding.Issue.Severity);
        Assert.Contains("Сама программа тоже вызывает вопросы: нет цифровой подписи", finding.Issue.Details);
        Assert.DoesNotContain("игра или чат", finding.Issue.Details);
    }

    [Fact]
    public void ProgramWarnings_OnlyForProgramsOnUnusualPorts_AndOnlyWhenReallySuspicious()
    {
        var unsignedGame = new ProcessIdentity(7700, "game.exe", @"C:\Games\game.exe", "Game", false);
        var processes = new Dictionary<int, ProcessIdentity> { [unsignedGame.Pid] = unsignedGame, [Miner.Pid] = Miner, [Chrome.Pid] = Chrome };
        var snapshot = new SocketSnapshot(new[]
        {
            Connection("51.38.10.20", 7777, unsignedGame.Pid),
            Connection("45.155.205.10", 3333, Miner.Pid),
            Connection("142.250.74.14", 443, Chrome.Pid),
        }, processes, DateTime.Now);
        var asked = new List<string>();

        var warnings = NetworkRules.ProgramWarnings(snapshot, path =>
        {
            asked.Add(path);
            return path == Miner.Path
                ? new SuspicionResult(50, new[] { "Нет цифровой подписи", "Файл лежит прямо в корне AppData/ProgramData" })
                : new SuspicionResult(20, new[] { "Нет цифровой подписи" }); // unsigned alone: many games are
        });

        Assert.DoesNotContain(Chrome.Path!, asked); // port 443: not checked at all
        var warning = Assert.Single(warnings);
        Assert.Equal(Miner.Path, warning.Key);
        Assert.Equal("нет цифровой подписи; файл лежит прямо в корне AppData/ProgramData", warning.Value);
    }

    [Fact]
    public void TrustedProgram_OnUnusualPort_IsNeitherMarkedNorReported()
    {
        var path = Miner.Path!;
        AppServices.Settings.AddTrusted(path);
        try
        {
            var snapshot = Snapshot(Connection("45.155.205.10", 4444, Miner.Pid));
            Assert.False(Assert.Single(NetworkRules.BuildConnections(snapshot, includeLoopback: false)).IsSuspicious);
            Assert.Empty(NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = snapshot }));
        }
        finally
        {
            AppServices.Settings.RemoveTrusted(path);
        }
    }

    [Fact]
    public void WindowsRemoteDesktop_IsLowAndPageOnly_OtherProgramOn3389_IsMedium()
    {
        var rdp = NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = Snapshot(Listen("0.0.0.0", 3389, Svchost.Pid)) });
        var finding = Assert.Single(rdp, f => f.Kind == FindingKind.RiskyPort);
        Assert.Equal(IssueSeverity.Low, finding.Issue.Severity); // same as the Security module's "Удалённый рабочий стол включён"
        Assert.True(finding.PageOnly);
        Assert.Contains("«Безопасность»", finding.Issue.Details);
        Assert.DoesNotContain(NetworkRules.BuildIssues(new NetworkSnapshot { Sockets = Snapshot(Listen("0.0.0.0", 3389, Svchost.Pid)) }),
            i => i.Title.Contains("3389")); // the dashboard gets it once, from Security

        var other = NetworkRules.BuildIssues(new NetworkSnapshot { Sockets = Snapshot(Listen("0.0.0.0", 3389, MySql.Pid)) });
        var issue = Assert.Single(other, i => i.Title.Contains("3389"));
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
    }

    private static FirewallReport Firewall(string network, string profile, bool? enabled, string? thirdParty = null)
        => new()
        {
            Profiles = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Domain"] = profile == "Domain" ? enabled : true,
                ["Private"] = profile == "Private" ? enabled : true,
                ["Public"] = profile == "Public" ? enabled : true,
            },
            Networks = new[] { new ConnectedNetwork(network, profile) },
            ThirdPartyFirewall = thirdParty,
        };

    private static SocketSnapshot WindowsPortsSnapshot()
        => Snapshot(Listen("0.0.0.0", 135, Svchost.Pid), Listen("0.0.0.0", 445, NetworkTestData.System.Pid));

    [Fact]
    public void FirewallOffForConnectedNetwork_ReplacesThe_ItIsNormal_Tip()
    {
        var snapshot = new NetworkSnapshot { Sockets = WindowsPortsSnapshot(), Firewall = Firewall("HomeNet 5G", "Private", false) };

        var findings = NetworkRules.BuildFindings(snapshot);

        Assert.DoesNotContain(findings, f => f.Kind == FindingKind.WindowsPorts);
        var off = Assert.Single(findings, f => f.Kind == FindingKind.FirewallOff);
        Assert.Equal(IssueSeverity.High, off.Issue.Severity); // the page no longer says "Сеть в порядке"
        Assert.Equal("Брандмауэр выключен для сети «HomeNet 5G»", off.Issue.Title);
        Assert.Contains("частной", off.Issue.Details);
        Assert.Contains("135, 445", off.Issue.Details);
        Assert.DoesNotContain("Это нормально", off.Issue.Details);
        Assert.True(off.PageOnly);
        // Security already puts "Брандмауэр Windows выключен" on the dashboard.
        Assert.Empty(NetworkRules.BuildIssues(snapshot));
    }

    [Fact]
    public void FirewallOn_ThirdPartyFirewall_UnknownState_OrOtherProfileOff_KeepTheTip()
    {
        foreach (var firewall in new[]
                 {
                     Firewall("HomeNet 5G", "Private", true),
                     Firewall("HomeNet 5G", "Private", false, thirdParty: "Kaspersky Internet Security"),
                     Firewall("HomeNet 5G", "Private", null),
                     new FirewallReport
                     {
                         Profiles = new Dictionary<string, bool?> { ["Public"] = false, ["Private"] = true },
                         Networks = new[] { new ConnectedNetwork("HomeNet 5G", "Private") },
                     },
                     null,
                 })
        {
            var findings = NetworkRules.BuildFindings(new NetworkSnapshot { Sockets = WindowsPortsSnapshot(), Firewall = firewall });
            Assert.DoesNotContain(findings, f => f.Kind == FindingKind.FirewallOff);
            Assert.Equal(IssueSeverity.Info, Assert.Single(findings, f => f.Kind == FindingKind.WindowsPorts).Issue.Severity);
        }
    }

    [Fact]
    public void FirewallOffOnlyForDomainNetwork_IsLow()
    {
        var findings = NetworkRules.BuildFindings(new NetworkSnapshot
        {
            Sockets = WindowsPortsSnapshot(),
            Firewall = Firewall("corp.example.com", "Domain", false),
        });
        Assert.Equal(IssueSeverity.Low, Assert.Single(findings, f => f.Kind == FindingKind.FirewallOff).Issue.Severity);
    }

    [Theory]
    [InlineData(0L, "Public")]
    [InlineData(1L, "Private")]
    [InlineData(2L, "Domain")]
    [InlineData(null, null)]
    public void ProfileOfCategory(long? category, string? profile) => Assert.Equal(profile, NetworkRules.ProfileOfCategory(category));

    [Fact]
    public void SeverityBadges_AreNotBareLowHighWords()
    {
        // "Низко · Высокая задержка" read as a contradiction.
        Assert.Equal("Мелочь", NetworkRules.SeverityText(IssueSeverity.Low));
        Assert.Equal("Внимание", NetworkRules.SeverityText(IssueSeverity.Medium));
        Assert.Equal("Важно", NetworkRules.SeverityText(IssueSeverity.High));
        Assert.Equal("Совет", NetworkRules.SeverityText(IssueSeverity.Info));
        var item = new FindingItem { Issue = new HealthIssue { Module = "Сеть", Title = "Высокая задержка", Severity = IssueSeverity.Low } };
        Assert.Equal("Мелочь", item.SeverityText);
        Assert.False(string.IsNullOrWhiteSpace(item.SeverityToolTip));
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

    // ===================== dashboard buttons: «Сбросить кэш DNS» and the network status page =====================

    private static NetworkSnapshot NoInternetSnapshot() => new() { Adapters = new[] { Ethernet }, Latency = Latency(null) };

    private static NetworkSnapshot DnsFailedSnapshot()
        => new() { Latency = Latency(20), Dns = new DnsReport(new[] { new DnsLookup("a", null), new DnsLookup("b", null) }) };

    /// <summary>A service whose console tool (ipconfig) is a fake that records what it was asked to run.</summary>
    private static (NetworkService Service, List<(string Exe, string Args)> Calls) ServiceWithFakeIpconfig(bool ok, string output, int exitCode)
    {
        var calls = new List<(string Exe, string Args)>();
        var service = new NetworkService
        {
            RunTool = (exe, args, _, _) =>
            {
                calls.Add((exe, args));
                return Task.FromResult((ok, output, exitCode));
            },
        };
        return (service, calls);
    }

    [Fact]
    public void NoInternet_LinksToTheNetworkStatusPage()
    {
        var issue = Assert.Single(NetworkRules.BuildIssues(NoInternetSnapshot()));
        Assert.Equal("ms-settings:network-status", issue.SettingsUri);
        Assert.Null(issue.FixAsync); // nothing to run: the page only shows the connection
    }

    [Fact]
    public void OtherNetworkProblems_HaveNoSettingsLink()
    {
        var issues = new[]
            {
                NetworkRules.BuildIssues(new NetworkSnapshot { Adapters = new[] { Ethernet }, Latency = Latency(140) }),
                NetworkRules.BuildIssues(DnsFailedSnapshot()),
                NetworkRules.BuildIssues(new NetworkSnapshot { Wifi = Wifi(35) }),
            }
            .SelectMany(list => list)
            .ToList();
        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Null(i.SettingsUri));
    }

    [Fact]
    public void DnsFailed_WithoutAFlushToRun_HasNoFix()
    {
        // The rules are pure: the fix comes from the service (BuildIssues(snapshot, flushDns)).
        var issue = Assert.Single(NetworkRules.BuildIssues(DnsFailedSnapshot()));
        Assert.Null(issue.FixAsync);
        Assert.Equal(HealthIssue.DefaultFixLabel, issue.FixLabel);
    }

    [Fact]
    public async Task DnsFailed_OnTheDashboard_FlushesTheDnsCache()
    {
        var (service, calls) = ServiceWithFakeIpconfig(true, "Windows IP Configuration\r\nSuccessfully flushed the DNS Resolver Cache.", 0);

        var issue = Assert.Single(service.BuildIssues(DnsFailedSnapshot()));

        Assert.Equal("DNS не отвечает — сайты не открываются", issue.Title);
        Assert.NotNull(issue.FixAsync);
        Assert.Equal("Сбросить кэш DNS", issue.FixLabel);
        Assert.True(issue.InFixAll);
        Assert.False(issue.OnlyOpensTool);
        Assert.Empty(calls); // building the issues (the analysis) runs nothing

        var text = await issue.FixAsync!(CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), call.Exe);
        Assert.Equal("/flushdns", call.Args);
        Assert.StartsWith("Кэш DNS очищен", text);
        Assert.False(HealthScoring.LooksLikeFailure(text));
    }

    [Fact]
    public async Task DnsFlushThatFails_IsReadAsAFailureByTheDashboard()
    {
        var (service, calls) = ServiceWithFakeIpconfig(false, "Не удается очистить кэш сопоставителя DNS: Ошибка при выполнении функции.", 1);
        var issue = Assert.Single(service.BuildIssues(DnsFailedSnapshot()));

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.Single(calls);
        Assert.StartsWith("Не удалось очистить кэш DNS:", text);
        Assert.True(HealthScoring.LooksLikeFailure(text));
        Assert.False(HealthScoring.LooksLikeAdminRequired(text));
    }

    [Fact]
    public void SlowDns_StaysAdvice_WithoutAFlushButton()
    {
        var (service, _) = ServiceWithFakeIpconfig(true, "", 0);
        var issue = Assert.Single(service.BuildIssues(new NetworkSnapshot
        {
            Latency = Latency(20),
            Dns = new DnsReport(new[] { new DnsLookup("a", 180), new DnsLookup("b", 220), new DnsLookup("c", 90) }),
        }));
        Assert.Equal("Медленный DNS", issue.Title);
        Assert.Null(issue.FixAsync);
    }
}
