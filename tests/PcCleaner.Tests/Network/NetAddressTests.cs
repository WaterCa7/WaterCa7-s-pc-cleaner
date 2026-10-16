using System.Net;
using PcCleaner.Modules.Network;
using Xunit;

namespace PcCleaner.Tests.Network;

public class NetAddressTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("172.32.0.1", true)] // just outside 172.16/12
    [InlineData("100.128.0.1", true)] // just outside CGNAT 100.64/10
    [InlineData("100.63.255.255", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("100.64.0.1", false)] // CGNAT
    [InlineData("100.127.255.254", false)]
    [InlineData("169.254.10.20", false)] // link-local
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.251", false)] // multicast
    [InlineData("255.255.255.255", false)]
    [InlineData("192.0.2.1", false)] // documentation
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("2a00:1450:4010:c05::64", true)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)] // link-local
    [InlineData("fd12:3456::1", false)] // unique local
    [InlineData("fec0::1", false)] // site-local
    [InlineData("ff02::1", false)] // multicast
    [InlineData("2001:db8::1", false)] // documentation
    [InlineData("::ffff:192.168.1.1", false)] // IPv4-mapped private
    [InlineData("::ffff:8.8.8.8", true)] // IPv4-mapped public
    public void IsPublic(string address, bool expected)
        => Assert.Equal(expected, NetAddress.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("100.64.0.1", AddressScope.Cgnat)]
    [InlineData("169.254.1.1", AddressScope.LinkLocal)]
    [InlineData("fe80::abcd", AddressScope.LinkLocal)]
    [InlineData("127.0.0.53", AddressScope.Loopback)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("192.168.50.2", AddressScope.Private)]
    [InlineData("fd00::2", AddressScope.Private)]
    [InlineData("239.255.255.250", AddressScope.Multicast)]
    public void Classify(string address, AddressScope expected)
        => Assert.Equal(expected, NetAddress.Classify(IPAddress.Parse(address)));

    [Fact]
    public void Endpoint_BracketsIPv6AndDropsScope()
    {
        Assert.Equal("1.2.3.4:443", NetAddress.Endpoint(IPAddress.Parse("1.2.3.4"), 443));
        Assert.Equal("[2a00::1]:443", NetAddress.Endpoint(IPAddress.Parse("2a00::1"), 443));
        Assert.Equal("[fe80::1]:5357", NetAddress.Endpoint(IPAddress.Parse("fe80::1%12"), 5357));
        Assert.Equal("8.8.8.8:53", NetAddress.Endpoint(IPAddress.Parse("::ffff:8.8.8.8"), 53));
    }

    [Fact]
    public void IsAny_RecognizesBothFamilies()
    {
        Assert.True(NetAddress.IsAny(IPAddress.Any));
        Assert.True(NetAddress.IsAny(IPAddress.IPv6Any));
        Assert.False(NetAddress.IsAny(IPAddress.Loopback));
        Assert.False(NetAddress.IsAny(null));
    }
}
