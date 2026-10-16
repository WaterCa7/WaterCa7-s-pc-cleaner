using System.Net;
using System.Net.Sockets;

namespace PcCleaner.Modules.Network;

/// <summary>Classification of IPv4/IPv6 addresses: public internet vs local network ranges.</summary>
public static class NetAddress
{
    public static AddressScope Classify(IPAddress? address)
    {
        if (address is null)
            return AddressScope.Unspecified;
        if (address.IsIPv4MappedToIPv6)
            return Classify(address.MapToIPv4());
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ClassifyV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => ClassifyV6(address),
            _ => AddressScope.Reserved,
        };
    }

    /// <summary>True for addresses on the internet (not LAN, loopback, link-local, CGNAT, multicast or reserved).</summary>
    public static bool IsPublic(IPAddress? address) => Classify(address) == AddressScope.Public;

    public static bool IsLoopback(IPAddress? address) => Classify(address) == AddressScope.Loopback;

    /// <summary>0.0.0.0 or :: — "listen on every interface".</summary>
    public static bool IsAny(IPAddress? address)
        => address is not null && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));

    private static AddressScope ClassifyV4(byte[] b)
    {
        if (b.Length != 4)
            return AddressScope.Reserved;
        return b[0] switch
        {
            0 => AddressScope.Unspecified,
            10 => AddressScope.Private,
            127 => AddressScope.Loopback,
            100 when b[1] is >= 64 and <= 127 => AddressScope.Cgnat, // 100.64.0.0/10, provider NAT
            169 when b[1] == 254 => AddressScope.LinkLocal,
            172 when b[1] is >= 16 and <= 31 => AddressScope.Private,
            192 when b[1] == 168 => AddressScope.Private,
            192 when b[1] == 0 && b[2] is 0 or 2 => AddressScope.Reserved, // IETF / TEST-NET-1
            198 when b[1] is 18 or 19 => AddressScope.Reserved, // benchmarking
            198 when b[1] == 51 && b[2] == 100 => AddressScope.Reserved, // TEST-NET-2
            203 when b[1] == 0 && b[2] == 113 => AddressScope.Reserved, // TEST-NET-3
            >= 224 and <= 239 => AddressScope.Multicast,
            >= 240 => AddressScope.Reserved,
            _ => AddressScope.Public,
        };
    }

    private static AddressScope ClassifyV6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Any))
            return AddressScope.Unspecified;
        if (address.Equals(IPAddress.IPv6Loopback))
            return AddressScope.Loopback;
        if (address.IsIPv6Multicast)
            return AddressScope.Multicast;
        if (address.IsIPv6LinkLocal)
            return AddressScope.LinkLocal;
        var b = address.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC)
            return AddressScope.Private; // fc00::/7 unique local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)
            return AddressScope.Private; // fec0::/10 site-local (deprecated)
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
            return AddressScope.Reserved; // 2001:db8::/32 documentation
        if ((b[0] & 0xE0) == 0x20)
            return AddressScope.Public; // 2000::/3 global unicast
        return AddressScope.Reserved;
    }

    /// <summary>"1.2.3.4:443" or "[2a00::1]:443".</summary>
    public static string Endpoint(IPAddress address, int port)
        => address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6
            ? $"[{Text(address)}]:{port}"
            : $"{Text(address)}:{port}";

    /// <summary>Address without the IPv6 scope id ("fe80::1" instead of "fe80::1%12").</summary>
    public static string Text(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4().ToString();
        var text = address.ToString();
        var percent = text.IndexOf('%');
        return percent > 0 ? text[..percent] : text;
    }
}
