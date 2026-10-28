using System.IO;
using System.Management;
using PcCleaner.Core.Account;

namespace PcCleaner.Core.AccountWindows;

/// <summary>
/// Windows: <c>serial of the board | ProcessorId | serial of the disk with the Windows folder</c> (contract «Номер компьютера»),
/// read once by WMI (it takes up to a second: the first use should be off the UI thread). A part WMI cannot give is «-»: any
/// failure of a part is caught there, so the Lazy never caches an exception.
/// </summary>
public sealed class WmiHwHash : IHwHash
{
    private readonly Lazy<string> _value = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Value => _value.Value;

    private static string Compute() => HwHash.Of(
        First("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"),
        First("SELECT ProcessorId FROM Win32_Processor", "ProcessorId"),
        SystemDiskSerial());

    private static string? First(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    if (o[property]?.ToString() is { Length: > 0 } value)
                        return value;
                }
            }
        }
        catch (Exception)
        {
            // the part is «-»
        }
        return null;
    }

    private static string? SystemDiskSerial()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
        try
        {
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (ManagementBaseObject partition in partitions.Get())
            {
                using (partition)
                {
                    using var disks = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                    foreach (ManagementBaseObject disk in disks.Get())
                    {
                        using (disk)
                        {
                            if (disk["SerialNumber"]?.ToString() is { Length: > 0 } serial)
                                return serial;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // the part is «-»
        }
        return null;
    }
}
