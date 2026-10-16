using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using PcCleaner.Modules.Network;
using Xunit;

namespace PcCleaner.Tests.Network;

public class IpTablesTests
{
    [Fact]
    public void ParseTcp4_ReadsAddressesPortsStateAndPid()
    {
        var buffer = NetworkTestData.Tcp4Table(
            (TcpState.Established, "192.168.1.10", 51234, "93.184.216.34", 443, 1200),
            (TcpState.Listen, "0.0.0.0", 3389, "0.0.0.0", 0, 900));

        var rows = IpTables.ParseTcp4(buffer);

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal(SocketProtocol.Tcp, first.Protocol);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), first.LocalAddress);
        Assert.Equal(51234, first.LocalPort);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), first.RemoteAddress);
        Assert.Equal(443, first.RemotePort);
        Assert.Equal(TcpState.Established, first.State);
        Assert.Equal(1200, first.Pid);
        Assert.Equal(TcpState.Listen, rows[1].State);
        Assert.Equal(3389, rows[1].LocalPort);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(3389)]
    [InlineData(49664)]
    [InlineData(65535)]
    public void ReadPort_UsesNetworkByteOrderOfTheLowWordOnly(int port)
    {
        var buffer = NetworkTestData.Udp4Table(("0.0.0.0", port, 1));
        Assert.Equal(port, IpTables.ParseUdp4(buffer)[0].LocalPort);
    }

    [Fact]
    public void ParseTcp6_ReadsAddressesScopeAndState()
    {
        var buffer = NetworkTestData.Tcp6Table(
            (TcpState.Established, "2001:db8::10", 0, 50001, "2a00:1450:4010:c05::64", 443, 1200),
            (TcpState.Listen, "fe80::1", 12, 5357, "::", 0, 4));

        var rows = IpTables.ParseTcp6(buffer);

        Assert.Equal(2, rows.Count);
        Assert.Equal(IPAddress.Parse("2001:db8::10"), rows[0].LocalAddress);
        Assert.Equal(50001, rows[0].LocalPort);
        Assert.Equal(IPAddress.Parse("2a00:1450:4010:c05::64"), rows[0].RemoteAddress);
        Assert.Equal(443, rows[0].RemotePort);
        Assert.Equal(TcpState.Established, rows[0].State);
        Assert.Equal(1200, rows[0].Pid);

        Assert.True(rows[1].LocalAddress.IsIPv6LinkLocal);
        Assert.Equal(12, rows[1].LocalAddress.ScopeId);
        Assert.Equal(5357, rows[1].LocalPort);
        Assert.Equal(TcpState.Listen, rows[1].State);
        Assert.Equal(4, rows[1].Pid);
    }

    [Fact]
    public void ParseUdp4AndUdp6_ReadLocalEndpointAndPid()
    {
        var v4 = IpTables.ParseUdp4(NetworkTestData.Udp4Table(("0.0.0.0", 5353, 900), ("127.0.0.1", 1900, 901)));
        var v6 = IpTables.ParseUdp6(NetworkTestData.Udp6Table(("::", 5355, 902)));

        Assert.Equal(2, v4.Count);
        Assert.Equal(SocketProtocol.Udp, v4[0].Protocol);
        Assert.Equal(5353, v4[0].LocalPort);
        Assert.Equal(900, v4[0].Pid);
        Assert.Null(v4[0].RemoteAddress);
        Assert.Equal(IPAddress.Loopback, v4[1].LocalAddress);
        Assert.Single(v6);
        Assert.Equal(IPAddress.IPv6Any, v6[0].LocalAddress);
        Assert.Equal(5355, v6[0].LocalPort);
        Assert.Equal(902, v6[0].Pid);
    }

    [Fact]
    public void Parse_TruncatedOrEmptyBuffers_NeverThrow()
    {
        Assert.Empty(IpTables.ParseTcp4(Array.Empty<byte>()));
        Assert.Empty(IpTables.ParseUdp6(new byte[3]));

        // The header claims 5 rows but only one fits.
        var buffer = NetworkTestData.Tcp4Table((TcpState.Established, "10.0.0.2", 1000, "1.1.1.1", 443, 7));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 5);
        var rows = IpTables.ParseTcp4(buffer);
        Assert.Single(rows);
        Assert.Equal(7, rows[0].Pid);
    }

    [Fact]
    public void ParseTcp4_UnknownStateValue_BecomesUnknown()
    {
        var buffer = NetworkTestData.Tcp4Table(((TcpState)99, "10.0.0.2", 1000, "1.1.1.1", 443, 7));
        Assert.Equal(TcpState.Unknown, IpTables.ParseTcp4(buffer)[0].State);
    }

    [Fact]
    public void ReadAll_RealSystem_ReturnsSomeSockets()
    {
        // Read-only: the same call the page makes. Every Windows PC has listening RPC/SMB ports.
        var all = IpTables.ReadAll();
        Assert.NotEmpty(all);
        Assert.Contains(all, e => e.Protocol == SocketProtocol.Tcp && e.State == TcpState.Listen);
        Assert.All(all, e => Assert.InRange(e.LocalPort, 0, 65535));
    }
}
