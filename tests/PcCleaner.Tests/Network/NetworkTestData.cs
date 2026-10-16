using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using PcCleaner.Modules.Network;

namespace PcCleaner.Tests.Network;

/// <summary>Synthetic TCP/UDP tables, processes and snapshots for the Network tests.</summary>
internal static class NetworkTestData
{
    public static readonly ProcessIdentity Chrome = new(1200, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome", false);
    public static readonly ProcessIdentity Svchost = new(900, "svchost.exe", @"C:\Windows\System32\svchost.exe", "Хост-процесс служб Windows", true);
    public static readonly ProcessIdentity System = new(4, "System", null, "Ядро Windows (System)", true);
    public static readonly ProcessIdentity MySql = new(3300, "mysqld.exe", @"C:\Program Files\MySQL\bin\mysqld.exe", "MySQL Server", false);
    public static readonly ProcessIdentity Miner = new(6666, "xmr.exe", @"C:\Users\Test\AppData\Roaming\xmr.exe", "xmr", false);
    public static readonly ProcessIdentity Spotify = new(5700, "Spotify.exe", @"C:\Users\Test\AppData\Roaming\Spotify\Spotify.exe", "Spotify", false);

    /// <summary>Writes a port the way the TCP/UDP tables do: network byte order in the low 16 bits, garbage above.</summary>
    private static void WritePort(byte[] buffer, int offset, int port)
    {
        buffer[offset] = (byte)(port >> 8);
        buffer[offset + 1] = (byte)port;
        buffer[offset + 2] = 0xAB; // undefined upper bytes must be ignored
        buffer[offset + 3] = 0xCD;
    }

    public static byte[] Tcp4Table(params (TcpState State, string Local, int LocalPort, string Remote, int RemotePort, int Pid)[] rows)
    {
        var buffer = new byte[4 + rows.Length * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var o = 4 + i * 24;
            var r = rows[i];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o), (uint)r.State);
            IPAddress.Parse(r.Local).GetAddressBytes().CopyTo(buffer, o + 4);
            WritePort(buffer, o + 8, r.LocalPort);
            IPAddress.Parse(r.Remote).GetAddressBytes().CopyTo(buffer, o + 12);
            WritePort(buffer, o + 16, r.RemotePort);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o + 20), (uint)r.Pid);
        }
        return buffer;
    }

    public static byte[] Tcp6Table(params (TcpState State, string Local, uint LocalScope, int LocalPort, string Remote, int RemotePort, int Pid)[] rows)
    {
        var buffer = new byte[4 + rows.Length * 56];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var o = 4 + i * 56;
            var r = rows[i];
            IPAddress.Parse(r.Local).GetAddressBytes().CopyTo(buffer, o);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o + 16), r.LocalScope);
            WritePort(buffer, o + 20, r.LocalPort);
            IPAddress.Parse(r.Remote).GetAddressBytes().CopyTo(buffer, o + 24);
            WritePort(buffer, o + 44, r.RemotePort);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o + 48), (uint)r.State);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o + 52), (uint)r.Pid);
        }
        return buffer;
    }

    public static byte[] Udp4Table(params (string Local, int Port, int Pid)[] rows)
    {
        var buffer = new byte[4 + rows.Length * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var o = 4 + i * 12;
            IPAddress.Parse(rows[i].Local).GetAddressBytes().CopyTo(buffer, o);
            WritePort(buffer, o + 4, rows[i].Port);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o + 8), (uint)rows[i].Pid);
        }
        return buffer;
    }

    public static byte[] Udp6Table(params (string Local, int Port, int Pid)[] rows)
    {
        var buffer = new byte[4 + rows.Length * 28];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var o = 4 + i * 28;
            IPAddress.Parse(rows[i].Local).GetAddressBytes().CopyTo(buffer, o);
            WritePort(buffer, o + 20, rows[i].Port);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o + 24), (uint)rows[i].Pid);
        }
        return buffer;
    }

    public static SocketEntry Listen(string address, int port, int pid)
        => new(SocketProtocol.Tcp, IPAddress.Parse(address), port, IPAddress.Any, 0, TcpState.Listen, pid);

    public static SocketEntry Udp(string address, int port, int pid)
        => new(SocketProtocol.Udp, IPAddress.Parse(address), port, null, 0, TcpState.Unknown, pid);

    public static SocketEntry Connection(string remote, int remotePort, int pid, TcpState state = TcpState.Established, int localPort = 50000)
        => new(SocketProtocol.Tcp, IPAddress.Parse("192.168.1.10"), localPort, IPAddress.Parse(remote), remotePort, state, pid);

    public static SocketSnapshot Snapshot(params SocketEntry[] entries)
    {
        var processes = new[] { Chrome, Svchost, System, MySql, Miner, Spotify }.ToDictionary(p => p.Pid);
        return new SocketSnapshot(entries, processes, DateTime.Now);
    }

    public static AdapterInfo Adapter(string name, AdapterKind kind, long speedBps, string description = "", string? gateway = "192.168.1.1")
        => new()
        {
            Id = "{" + name + "}",
            Name = name,
            Description = description,
            Kind = kind,
            Type = kind == AdapterKind.WiFi ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet,
            SpeedBps = speedBps,
            IPv4 = new[] { IPAddress.Parse("192.168.1.10") },
            Gateways = gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) },
            DnsServers = new[] { IPAddress.Parse("192.168.1.1") },
        };
}
