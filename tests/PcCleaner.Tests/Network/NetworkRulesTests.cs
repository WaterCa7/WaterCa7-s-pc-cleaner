using System.Net;
using System.Net.NetworkInformation;
using PcCleaner.Modules.Network;
using Xunit;
using static PcCleaner.Tests.Network.NetworkTestData;

namespace PcCleaner.Tests.Network;

public class DnsLabelTests
{
    [Theory]
    [InlineData("8.8.8.8", "Google")]
    [InlineData("8.8.4.4", "Google")]
    [InlineData("1.1.1.1", "Cloudflare")]
    [InlineData("1.0.0.1", "Cloudflare")]
    [InlineData("9.9.9.9", "Quad9")]
    [InlineData("77.88.8.8", "Яндекс")]
    [InlineData("77.88.8.1", "Яндекс")]
    [InlineData("77.88.8.88", "Яндекс")]
    [InlineData("94.140.14.14", "AdGuard")]
    [InlineData("94.140.15.15", "AdGuard")]
    [InlineData("208.67.222.222", "OpenDNS")]
    [InlineData("208.67.220.220", "OpenDNS")]
    [InlineData("2001:4860:4860::8888", "Google")]
    public void KnownResolvers(string address, string label)
    {
        var (text, kind) = NetworkRules.DnsLabel(IPAddress.Parse(address));
        Assert.Equal(label, text);
        Assert.Equal(DnsKind.Known, kind);
    }

    [Theory]
    [InlineData("192.168.0.1")]
    [InlineData("10.0.1.1")]
    [InlineData("172.20.0.1")]
    [InlineData("fe80::1")]
    public void PrivateAddresses_AreRouterOrProvider(string address)
    {
        var (text, kind) = NetworkRules.DnsLabel(IPAddress.Parse(address));
        Assert.Equal("Роутер / провайдер", text);
        Assert.Equal(DnsKind.Router, kind);
    }

    [Fact]
    public void VpnAdapter_PrivateDns_IsVpn()
        => Assert.Equal(DnsKind.Vpn, NetworkRules.DnsLabel(IPAddress.Parse("10.6.7.2"), adapterIsVpn: true).Kind);

    [Fact]
    public void Loopback_IsLocalProgram()
        => Assert.Equal(DnsKind.Local, NetworkRules.DnsLabel(IPAddress.Parse("127.0.0.1")).Kind);

    [Fact]
    public void UnknownPublic_IsProviderOrOther()
    {
        var (text, kind) = NetworkRules.DnsLabel(IPAddress.Parse("5.3.3.3"));
        Assert.Equal(DnsKind.Other, kind);
        Assert.Contains("Провайдер", text);
    }

    [Fact]
    public void DnsServers_SkipsWindowsPlaceholderAndDuplicates()
    {
        var adapter = Adapter("Ethernet", AdapterKind.Ethernet, 1_000_000_000) with
        {
            DnsServers = new[]
            {
                IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("fec0:0:0:ffff::1"),
            },
        };
        var list = NetworkRules.DnsServers(new[] { adapter });
        var entry = Assert.Single(list);
        Assert.Equal("Cloudflare", entry.Label);
        Assert.Equal("Ethernet", entry.AdapterName);
    }
}

public class LatencyMathTests
{
    [Fact]
    public void Median_IgnoresFailures()
    {
        Assert.Equal(20, LatencyMath.Median(new double?[] { 10, null, 30 }));
        Assert.Equal(7, LatencyMath.Median(new double?[] { 5, 50, 7 }));
        Assert.Equal(12, LatencyMath.Median(new double?[] { null, 12, null }));
        Assert.Null(LatencyMath.Median(new double?[] { null, null, null }));
        Assert.Null(LatencyMath.Median(Array.Empty<double?>()));
    }

    [Fact]
    public void Report_NoTargetReachable_MeansNoInternet()
    {
        var report = new LatencyReport
        {
            Targets = NetworkRules.LatencyTargets.Select(t => new LatencyResult(t, new double?[] { null, null, null })).ToList(),
        };
        Assert.False(report.InternetReachable);
        Assert.Null(report.MedianMs);
    }

    [Fact]
    public void Report_MedianOfTargetMedians()
    {
        var t = NetworkRules.LatencyTargets;
        var report = new LatencyReport
        {
            Targets = new[]
            {
                new LatencyResult(t[0], new double?[] { 10, 12, 11 }), // 11
                new LatencyResult(t[1], new double?[] { 30, null, 40 }), // 35
                new LatencyResult(t[2], new double?[] { null, null, null }), // unreachable
                new LatencyResult(t[3], new double?[] { 60, 70, 65 }), // 65
            },
        };
        Assert.True(report.InternetReachable);
        Assert.Equal(35, report.MedianMs);
    }

    [Fact]
    public void Report_Intercepted_UsesHttpCheck()
    {
        var t = NetworkRules.LatencyTargets;
        var instant = t.Select(x => new LatencyResult(x, new double?[] { 0.1, 0.1, 0.1 })).ToList();
        Assert.Equal(45, new LatencyReport { Targets = instant, Intercepted = true, HttpMedianMs = 45 }.MedianMs);
        Assert.False(new LatencyReport { Targets = instant, Intercepted = true, HttpMedianMs = null }.InternetReachable);
    }

    [Theory]
    [InlineData(0.4, "< 1 мс")]
    [InlineData(2.44, "2,4 мс")]
    [InlineData(23.4, "23 мс")]
    public void FormatMs(double ms, string expected) => Assert.Equal(expected, LatencyMath.FormatMs(ms));

    [Fact]
    public void Rate_HandlesCounterReset()
    {
        Assert.Equal(1000, LatencyMath.Rate(1000, 3000, 2));
        Assert.Equal(0, LatencyMath.Rate(5000, 100, 1));
        Assert.Equal(0, LatencyMath.Rate(0, 100, 0));
    }
}

public class AdapterRulesTests
{
    [Theory]
    [InlineData(100_000_000, "100 Мбит/с")]
    [InlineData(1_000_000_000, "1 Гбит/с")]
    [InlineData(2_500_000_000, "2,5 Гбит/с")]
    [InlineData(866_700_000, "866,7 Мбит/с")]
    [InlineData(-1, "—")]
    public void FormatLinkSpeed(long bps, string expected) => Assert.Equal(expected, NetworkRules.FormatLinkSpeed(bps));

    [Theory]
    [InlineData(AdapterKind.Ethernet, "Intel(R) Ethernet Connection (11) I219-V", 100_000_000, true)]
    [InlineData(AdapterKind.Ethernet, "Intel(R) Ethernet Connection (11) I219-V", 10_000_000, true)]
    [InlineData(AdapterKind.Ethernet, "Intel(R) Ethernet Connection (11) I219-V", 1_000_000_000, false)]
    [InlineData(AdapterKind.Ethernet, "Realtek PCIe GbE Family Controller", 100_000_000, true)]
    [InlineData(AdapterKind.Ethernet, "Realtek PCIe GbE Family Controller", 500_000_000, true)]
    [InlineData(AdapterKind.Ethernet, "Realtek PCIe 2.5GbE Family Controller", 1_000_000_000, false)]
    [InlineData(AdapterKind.Ethernet, "Some Gigabit Adapter", 500_000_000, true)]
    [InlineData(AdapterKind.Ethernet, "ASIX AX88772 USB 2.0 to Fast Ethernet Adapter", 100_000_000, false)]
    [InlineData(AdapterKind.Ethernet, "Realtek USB 10/100 LAN", 100_000_000, false)]
    [InlineData(AdapterKind.Ethernet, "Intel(R) Ethernet Connection", -1, false)]
    [InlineData(AdapterKind.WiFi, "Intel(R) Wi-Fi 6 AX201", 100_000_000, false)]
    [InlineData(AdapterKind.Vpn, "TAP-Windows Adapter V9", 100_000_000, false)]
    public void IsSlowWiredLink(AdapterKind kind, string description, long speed, bool expected)
        => Assert.Equal(expected, NetworkRules.IsSlowWiredLink(kind, description, speed));

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Intel(R) Ethernet Connection (11) I219-V", AdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", AdapterKind.WiFi)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Подключение по локальной сети* 9", "Microsoft Wi-Fi Direct Virtual Adapter", AdapterKind.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", AdapterKind.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "outline-tap0", "TAP-Windows Adapter V9", AdapterKind.Vpn)]
    [InlineData((NetworkInterfaceType)53, "happ-default-tun", "SocksTunnel Tunnel", AdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "WireGuard", "WireGuard Tunnel", AdapterKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ethernet, "Сетевое подключение Bluetooth", "Bluetooth Device (Personal Area Network)", AdapterKind.Other)]
    public void DetectKind(NetworkInterfaceType type, string name, string description, AdapterKind expected)
        => Assert.Equal(expected, NetworkRules.DetectKind(type, name, description));

    [Fact]
    public void PrimaryAdapterAndRouter_PreferPhysicalOverVpn()
    {
        var vpn = Adapter("tun", AdapterKind.Vpn, 0, gateway: "10.6.7.2");
        var wifi = Adapter("Wi-Fi", AdapterKind.WiFi, 866_700_000, gateway: "10.0.1.1");
        var list = new[] { vpn, wifi };
        Assert.Same(wifi, NetworkRules.PrimaryAdapter(list));
        Assert.Equal(IPAddress.Parse("10.0.1.1"), NetworkRules.RouterAddress(list));
        Assert.Same(vpn, NetworkRules.ActiveVpn(list));
    }

    [Theory]
    [InlineData("Open", null, true)]
    [InlineData("Открытая", null, true)]
    [InlineData("WEP", null, true)]
    [InlineData("Shared", "WEP", true)]
    [InlineData("WPA2-Personal", "CCMP", false)]
    [InlineData("WPA3-Personal", "GCMP", false)]
    public void WeakWifiSecurity(string auth, string? cipher, bool expected)
        => Assert.Equal(expected, NetworkRules.IsWeakWifiSecurity(auth, cipher));

    [Theory]
    [InlineData("802.11ax", 5.0, "Wi-Fi 6 (802.11ax)")]
    [InlineData("802.11ax", 6.0, "Wi-Fi 6E (802.11ax)")]
    [InlineData("802.11ac", 5.0, "Wi-Fi 5 (802.11ac)")]
    [InlineData("802.11n", 2.4, "Wi-Fi 4 (802.11n)")]
    [InlineData("802.11g", 2.4, "802.11g")]
    public void RadioText(string radio, double band, string expected) => Assert.Equal(expected, NetworkRules.RadioText(radio, band));
}
