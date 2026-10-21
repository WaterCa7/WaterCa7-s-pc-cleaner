using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Network;

/// <summary>
/// TCP/UDP tables with owning process ids via GetExtendedTcpTable / GetExtendedUdpTable (works without admin rights).
/// Parsing is separated from the P/Invoke so it can be tested on synthetic buffers.
/// </summary>
public static class IpTables
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNotSupported = 50;

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 DWORDs)
    internal const int Tcp4RowSize = 24;
    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
    internal const int Tcp6RowSize = 56;
    // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
    internal const int Udp4RowSize = 12;
    // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScope, localPort, pid
    internal const int Udp6RowSize = 28;

    /// <summary>Reads all four tables (TCP/UDP × IPv4/IPv6). Never throws; a table that cannot be read is skipped.</summary>
    public static List<SocketEntry> ReadAll()
    {
        var all = new List<SocketEntry>();
        Add(all, ReadTable(tcp: true, AfInet), ParseTcp4);
        Add(all, ReadTable(tcp: true, AfInet6), ParseTcp6);
        Add(all, ReadTable(tcp: false, AfInet), ParseUdp4);
        Add(all, ReadTable(tcp: false, AfInet6), ParseUdp6);
        return all;
    }

    private static void Add(List<SocketEntry> all, byte[]? buffer, Func<byte[], List<SocketEntry>> parse)
    {
        if (buffer is null)
            return;
        try
        {
            all.AddRange(parse(buffer));
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            Log.Warn(T.F("Сеть: не удалось разобрать таблицу соединений: {0}", ex.Message));
        }
    }

    /// <summary>Port DWORDs hold the port in network byte order in their low 16 bits; the upper bytes are undefined.</summary>
    internal static int ReadPort(ReadOnlySpan<byte> buffer, int offset) => (buffer[offset] << 8) | buffer[offset + 1];

    private static int Count(ReadOnlySpan<byte> buffer, int rowSize)
    {
        if (buffer.Length < 4)
            return 0;
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var fits = (buffer.Length - 4) / rowSize;
        return (int)Math.Min(declared, (uint)fits);
    }

    private static int ReadPid(ReadOnlySpan<byte> buffer, int offset)
        => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]), int.MaxValue);

    private static TcpState ReadState(ReadOnlySpan<byte> buffer, int offset)
    {
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        return raw is >= 1 and <= 12 ? (TcpState)raw : TcpState.Unknown;
    }

    private static IPAddress V6(ReadOnlySpan<byte> buffer, int offset, int scopeOffset)
    {
        var bytes = buffer.Slice(offset, 16);
        var scope = BinaryPrimitives.ReadUInt32LittleEndian(buffer[scopeOffset..]);
        var address = new IPAddress(bytes);
        // Scope ids only make sense for link-local addresses (fe80::/10).
        return address.IsIPv6LinkLocal && scope is > 0 and <= int.MaxValue ? new IPAddress(bytes, scope) : address;
    }

    public static List<SocketEntry> ParseTcp4(byte[] data)
    {
        ReadOnlySpan<byte> buffer = data;
        var count = Count(buffer, Tcp4RowSize);
        var rows = new List<SocketEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var o = 4 + i * Tcp4RowSize;
            rows.Add(new SocketEntry(
                SocketProtocol.Tcp,
                new IPAddress(buffer.Slice(o + 4, 4)),
                ReadPort(buffer, o + 8),
                new IPAddress(buffer.Slice(o + 12, 4)),
                ReadPort(buffer, o + 16),
                ReadState(buffer, o),
                ReadPid(buffer, o + 20)));
        }
        return rows;
    }

    public static List<SocketEntry> ParseTcp6(byte[] data)
    {
        ReadOnlySpan<byte> buffer = data;
        var count = Count(buffer, Tcp6RowSize);
        var rows = new List<SocketEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var o = 4 + i * Tcp6RowSize;
            rows.Add(new SocketEntry(
                SocketProtocol.Tcp,
                V6(buffer, o, o + 16),
                ReadPort(buffer, o + 20),
                V6(buffer, o + 24, o + 40),
                ReadPort(buffer, o + 44),
                ReadState(buffer, o + 48),
                ReadPid(buffer, o + 52)));
        }
        return rows;
    }

    public static List<SocketEntry> ParseUdp4(byte[] data)
    {
        ReadOnlySpan<byte> buffer = data;
        var count = Count(buffer, Udp4RowSize);
        var rows = new List<SocketEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var o = 4 + i * Udp4RowSize;
            rows.Add(new SocketEntry(
                SocketProtocol.Udp,
                new IPAddress(buffer.Slice(o, 4)),
                ReadPort(buffer, o + 4),
                null,
                0,
                TcpState.Unknown,
                ReadPid(buffer, o + 8)));
        }
        return rows;
    }

    public static List<SocketEntry> ParseUdp6(byte[] data)
    {
        ReadOnlySpan<byte> buffer = data;
        var count = Count(buffer, Udp6RowSize);
        var rows = new List<SocketEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var o = 4 + i * Udp6RowSize;
            rows.Add(new SocketEntry(
                SocketProtocol.Udp,
                V6(buffer, o, o + 16),
                ReadPort(buffer, o + 20),
                null,
                0,
                TcpState.Unknown,
                ReadPid(buffer, o + 24)));
        }
        return rows;
    }

    private static byte[]? ReadTable(bool tcp, int family)
    {
        var size = 0;
        var rc = Call(tcp, IntPtr.Zero, ref size, family);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (rc == ErrorNotSupported)
                return null; // e.g. IPv6 is switched off
            if (rc != ErrorInsufficientBuffer && rc != NoError)
                break;
            // The table can grow between the two calls: leave some room.
            size += 4096;
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                var actual = size;
                rc = Call(tcp, ptr, ref actual, family);
                if (rc == NoError)
                {
                    var bytes = new byte[Math.Min(actual, size)];
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                    return bytes;
                }
                if (rc == ErrorInsufficientBuffer)
                    size = Math.Max(actual, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        Log.Warn(T.F("Сеть: таблица {0} ({1}) недоступна, код {2}", tcp ? "TCP" : "UDP", family == AfInet ? "IPv4" : "IPv6", rc));
        return null;
    }

    private static uint Call(bool tcp, IntPtr buffer, ref int size, int family)
        => tcp
            ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
            : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
