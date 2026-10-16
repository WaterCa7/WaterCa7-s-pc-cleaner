using System.Net.NetworkInformation;
using PcCleaner.Modules.Network;
using Xunit;
using static PcCleaner.Tests.Network.NetworkTestData;

namespace PcCleaner.Tests.Network;

public class PortRulesTests
{
    [Theory]
    [InlineData(21)]
    [InlineData(23)]
    [InlineData(3389)]
    [InlineData(5900)]
    [InlineData(5985)]
    [InlineData(5986)]
    [InlineData(1433)]
    [InlineData(3306)]
    [InlineData(5432)]
    [InlineData(6379)]
    [InlineData(27017)]
    [InlineData(11211)]
    public void RiskyPorts_OnTheNetwork_AreRisky(int port)
    {
        var (kind, service, explanation) = NetworkRules.ClassifyPort(SocketProtocol.Tcp, port, isNetwork: true, MySql);
        Assert.Equal(PortKind.Risky, kind);
        Assert.False(string.IsNullOrWhiteSpace(service));
        Assert.False(string.IsNullOrWhiteSpace(explanation));
    }

    [Fact]
    public void RiskyPort_OnLoopbackOnly_IsSafe()
        => Assert.Equal(PortKind.LocalOnly, NetworkRules.ClassifyPort(SocketProtocol.Tcp, 3306, isNetwork: false, MySql).Kind);

    [Theory]
    [InlineData(SocketProtocol.Tcp, 135)]
    [InlineData(SocketProtocol.Tcp, 139)]
    [InlineData(SocketProtocol.Tcp, 445)]
    [InlineData(SocketProtocol.Tcp, 5040)]
    [InlineData(SocketProtocol.Tcp, 5357)]
    [InlineData(SocketProtocol.Tcp, 7680)]
    [InlineData(SocketProtocol.Tcp, 49664)]
    [InlineData(SocketProtocol.Tcp, 49670)]
    [InlineData(SocketProtocol.Udp, 137)]
    [InlineData(SocketProtocol.Udp, 5353)]
    [InlineData(SocketProtocol.Udp, 1900)]
    public void WindowsDefaults_AreWindows(SocketProtocol protocol, int port)
        => Assert.Equal(PortKind.Windows, NetworkRules.ClassifyPort(protocol, port, isNetwork: true, Svchost).Kind);

    [Fact]
    public void WindowsDynamicPort_OfASystemProcess_IsWindows()
        => Assert.Equal(PortKind.Windows, NetworkRules.ClassifyPort(SocketProtocol.Udp, 58824, isNetwork: true, Svchost).Kind);

    [Fact]
    public void KernelSystemProcess_IsWindows()
        => Assert.Equal(PortKind.Windows, NetworkRules.ClassifyPort(SocketProtocol.Tcp, 2869, isNetwork: true, NetworkTestData.System).Kind);

    [Fact]
    public void OtherProgramOnTheNetwork_IsApp()
    {
        var (kind, service, _) = NetworkRules.ClassifyPort(SocketProtocol.Tcp, 57621, isNetwork: true, Spotify);
        Assert.Equal(PortKind.App, kind);
        Assert.Contains("Spotify", service);
    }

    [Theory]
    [InlineData(4444)]
    [InlineData(1337)]
    [InlineData(31337)]
    [InlineData(6666)]
    [InlineData(6667)]
    [InlineData(3333)]
    [InlineData(5555)]
    [InlineData(7777)]
    [InlineData(14444)]
    [InlineData(14433)]
    [InlineData(45700)]
    [InlineData(9050)]
    [InlineData(9150)]
    public void SuspiciousRemotePorts_HaveAnExplanation(int port)
        => Assert.False(string.IsNullOrWhiteSpace(NetworkRules.SuspiciousRemotePortReason(port)));

    [Theory]
    [InlineData(443)]
    [InlineData(80)]
    [InlineData(53)]
    [InlineData(5228)]
    public void NormalRemotePorts_AreNotSuspicious(int port)
        => Assert.Null(NetworkRules.SuspiciousRemotePortReason(port));

    [Fact]
    public void BuildConnections_FlagsSuspiciousPortsOnlyOnPublicAddresses()
    {
        var snapshot = Snapshot(
            Connection("45.155.205.10", 4444, Miner.Pid),
            Connection("192.168.1.50", 4444, Miner.Pid), // LAN: not flagged
            Connection("142.250.74.14", 443, Chrome.Pid));

        var rows = NetworkRules.BuildConnections(snapshot, includeLoopback: false);

        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.IsSuspicious);
        Assert.Equal("45.155.205.10", rows.Single(r => r.IsSuspicious).RemoteAddress.ToString());
    }

    [Fact]
    public void BuildConnections_SkipsListeningClosingAndLoopbackUnlessAsked()
    {
        var snapshot = Snapshot(
            Listen("0.0.0.0", 445, 4),
            Connection("142.250.74.14", 443, Chrome.Pid, TcpState.TimeWait),
            Connection("142.250.74.14", 443, Chrome.Pid, TcpState.CloseWait),
            Connection("1.1.1.1", 443, Chrome.Pid, TcpState.FinWait2), // e.g. left over from a closed latency check
            Connection("1.1.1.1", 443, Chrome.Pid, TcpState.LastAck),
            Connection("127.0.0.1", 9222, Chrome.Pid),
            Connection("142.250.74.14", 443, Chrome.Pid),
            Connection("142.250.74.15", 443, Chrome.Pid, TcpState.SynSent));

        var rows = NetworkRules.BuildConnections(snapshot, includeLoopback: false);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.State == TcpState.Established);
        Assert.Contains(rows, r => r.State == TcpState.SynSent);
        Assert.Equal(3, NetworkRules.BuildConnections(snapshot, includeLoopback: true).Count);
    }

    [Fact]
    public void AggregateConnections_MergesIdenticalOnesWithACount()
    {
        var snapshot = Snapshot(
            Connection("160.79.104.10", 443, Chrome.Pid, localPort: 50001),
            Connection("160.79.104.10", 443, Chrome.Pid, localPort: 50002),
            Connection("160.79.104.10", 443, Chrome.Pid, localPort: 50003),
            Connection("160.79.104.10", 443, Chrome.Pid, TcpState.SynSent),
            Connection("1.1.1.1", 443, Chrome.Pid));

        var rows = NetworkRules.AggregateConnections(NetworkRules.BuildConnections(snapshot, includeLoopback: false));

        Assert.Equal(3, rows.Count); // established and opening connections to one address stay separate rows
        Assert.Equal(3, rows.Single(r => r.RemotePort == 443 && r.State == TcpState.Established && r.RemoteAddress.ToString() == "160.79.104.10").Count);
        Assert.Equal(5, rows.Sum(r => r.Count));
    }

    [Fact]
    public void BuildPorts_MergesIPv4AndIPv6AndSeparatesLoopback()
    {
        var snapshot = Snapshot(
            Listen("0.0.0.0", 135, Svchost.Pid),
            Listen("::", 135, Svchost.Pid),
            Listen("127.0.0.1", 3306, MySql.Pid),
            Listen("0.0.0.0", 3306, MySql.Pid),
            Udp("0.0.0.0", 5353, Chrome.Pid),
            Connection("1.1.1.1", 443, Chrome.Pid));

        var ports = NetworkRules.BuildPorts(snapshot);

        var rpc = Assert.Single(ports, p => p.Port == 135);
        Assert.Equal(2, rpc.Addresses.Count);
        Assert.Equal(PortKind.Windows, rpc.Kind);
        Assert.Equal(2, ports.Count(p => p.Port == 3306));
        Assert.Equal(PortKind.Risky, ports.Single(p => p.Port == 3306 && p.IsNetwork).Kind);
        Assert.Equal(PortKind.LocalOnly, ports.Single(p => p.Port == 3306 && !p.IsNetwork).Kind);
        Assert.Contains(ports, p => p.Protocol == SocketProtocol.Udp && p.Port == 5353);
        Assert.DoesNotContain(ports, p => p.Port == 50000); // outgoing connections are not open ports
        Assert.Equal(PortKind.Risky, ports[0].Kind); // risky first
    }
}
