using System.Buffers.Binary;

namespace PcCleaner.Modules.Startup;

/// <summary>
/// Task-Manager-compatible enabled/disabled flags stored under
/// ...\Explorer\StartupApproved\{Run|Run32|StartupFolder}.
/// Format: REG_BINARY of 12 bytes. Byte 0: 0x02/0x06 = enabled, 0x03/0x07 (low bit set) = disabled.
/// Bytes 4..11: FILETIME (UTC) of the moment the item was disabled (may be zero).
/// A missing value means "enabled".
/// </summary>
public static class StartupApproved
{
    public const int Length = 12;

    public const byte EnabledFlag = 0x02;

    public const byte DisabledFlag = 0x03;

    /// <summary>True if the value means "enabled" (missing/empty values are enabled too).</summary>
    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 0x01) == 0;

    /// <summary>Local time the entry was disabled, or null if enabled / unknown.</summary>
    public static DateTime? DisabledAt(byte[]? data)
    {
        if (IsEnabled(data) || data!.Length < Length)
            return null;
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4, 8));
        if (fileTime <= 0)
            return null;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Value written to disable an entry: 0x03 followed by the FILETIME of <paramref name="timeUtc"/>.</summary>
    public static byte[] CreateDisabled(DateTime timeUtc)
    {
        var data = new byte[Length];
        data[0] = DisabledFlag;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4, 8), timeUtc.ToUniversalTime().ToFileTimeUtc());
        return data;
    }

    /// <summary>Value written to (re-)enable an entry: 0x02 followed by zeros.</summary>
    public static byte[] CreateEnabled()
    {
        var data = new byte[Length];
        data[0] = EnabledFlag;
        return data;
    }
}
