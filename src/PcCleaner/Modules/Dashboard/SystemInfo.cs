using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

/// <summary>Static facts about the computer shown in "Об этом компьютере".</summary>
public sealed record StaticSystemInfo(
    string WindowsName,
    string WindowsVersion,
    string ComputerName,
    string CpuName,
    int LogicalProcessors,
    ulong InstalledRamBytes)
{
    /// <summary>Video adapters (Win32_VideoController), virtual ones dropped when a real one exists.</summary>
    public IReadOnlyList<string> Gpus { get; init; } = Array.Empty<string>();
}

/// <summary>One sample of the live tiles.</summary>
public sealed record LiveSnapshot(
    double? CpuPercent,
    MemoryStatus? Memory,
    string? SystemDriveName,
    long SystemDriveTotal,
    long SystemDriveFree,
    TimeSpan Uptime);

/// <summary>Read-only system information for the dashboard (registry reads and cheap Win32 calls only).</summary>
public static class SystemInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static StaticSystemInfo ReadStatic()
    {
        string? productName = null, displayVersion = null, build = null;
        int? ubr = null;
        string? cpu = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var key = hklm.OpenSubKey(CurrentVersionKey))
            {
                productName = key?.GetValue("ProductName") as string;
                displayVersion = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
                build = key?.GetValue("CurrentBuildNumber") as string ?? key?.GetValue("CurrentBuild") as string;
                ubr = key?.GetValue("UBR") as int?;
            }
            using (var key = hklm.OpenSubKey(CpuKey))
                cpu = key?.GetValue("ProcessorNameString") as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Главная: не удалось прочитать сведения о системе из реестра: {ex.Message}");
        }

        var buildNumber = int.TryParse(build, out var b) ? b : (int?)null;
        return new StaticSystemInfo(
            WindowsName(productName, buildNumber),
            WindowsVersionText(displayVersion, buildNumber, ubr),
            SafeMachineName(),
            CleanCpuName(cpu),
            Environment.ProcessorCount,
            InstalledRam())
        {
            Gpus = ReadGpus(),
        };
    }

    /// <summary>Names of the video adapters (read-only WMI query; empty when WMI is unavailable).</summary>
    private static IReadOnlyList<string> ReadGpus()
    {
        try
        {
            var rows = Wmi.Query("SELECT Name FROM Win32_VideoController", Wmi.Cimv2, TimeSpan.FromSeconds(10));
            return CleanGpuNames(rows.Select(r => r.Str("Name")));
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось получить сведения о видеокарте: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Trims, removes "(R)/(TM)" noise and duplicates; drops virtual/remote display adapters (Parsec, Remote Display,
    /// Basic Display...) when at least one real adapter is present.
    /// </summary>
    public static IReadOnlyList<string> CleanGpuNames(IEnumerable<string?> names)
    {
        var list = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var name = Regex.Replace(raw.Replace('\0', ' '), @"\((R|TM)\)", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+", " ").Trim();
            if (name.Length > 0 && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
                list.Add(name);
        }
        var real = list.Where(n => !VirtualAdapter.IsMatch(n)).ToList();
        return real.Count > 0 ? real : list;
    }

    private static readonly Regex VirtualAdapter = new(
        @"Remote Display|Virtual Display|Basic Display|Basic Render|Indirect Display|Parsec|Virtual Monitor|Virtual Desktop|spacedesk|Citrix|Hyper-V Video",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// "Windows 10 Home" with build ≥ 22000 → "Windows 11 Домашняя" (ProductName was never updated for Windows 11).
    /// </summary>
    public static string WindowsName(string? productName, int? build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (build >= 22000 && name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            name = Regex.Replace(name, "Windows 10", "Windows 11", RegexOptions.IgnoreCase);
        return TranslateEdition(name);
    }

    private static readonly (string En, string Ru)[] Editions =
    {
        ("Home Single Language", "Домашняя для одного языка"),
        ("Home", "Домашняя"),
        ("Professional", "Pro"),
        ("Enterprise", "Корпоративная"),
        ("Education", "для образовательных учреждений"),
    };

    private static string TranslateEdition(string name)
    {
        foreach (var (en, ru) in Editions)
        {
            var pattern = $@"\b{Regex.Escape(en)}\b";
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
                return Regex.Replace(name, pattern, ru, RegexOptions.IgnoreCase);
        }
        return name;
    }

    /// <summary>"24H2, сборка 26100.2033".</summary>
    public static string WindowsVersionText(string? displayVersion, int? build, int? ubr)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(displayVersion))
            parts.Add(displayVersion.Trim());
        if (build is not null)
            parts.Add(ubr is > 0 ? $"сборка {build}.{ubr}" : $"сборка {build}");
        return parts.Count == 0 ? Environment.OSVersion.Version.ToString() : string.Join(", ", parts);
    }

    /// <summary>Trims the padding and the repeated spaces some CPUs have in ProcessorNameString.</summary>
    public static string CleanCpuName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Неизвестный процессор";
        var s = Regex.Replace(raw.Replace('\0', ' '), @"\s+", " ").Trim();
        s = Regex.Replace(s, @"\((R|TM)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(CPU|Processor)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return "—";
        }
    }

    /// <summary>Physically installed RAM (e.g. 16 ГБ instead of the 15,8 ГБ visible to Windows).</summary>
    private static ulong InstalledRam()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0)
                return kb * 1024;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // fall back to the visible memory below
        }
        return SystemStats.Memory().TotalBytes;
    }

    /// <summary>Takes one live sample. May block ~250 ms on the very first CPU measurement: call off the UI thread.</summary>
    public static LiveSnapshot ReadLive()
    {
        double? cpu = null;
        try
        {
            cpu = SystemStats.CpuUsagePercent();
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось измерить загрузку процессора: {ex.Message}");
        }

        MemoryStatus? memory = null;
        try
        {
            var m = SystemStats.Memory();
            if (m.TotalBytes > 0)
                memory = m;
        }
        catch (Exception ex)
        {
            Log.Warn($"Главная: не удалось получить сведения о памяти: {ex.Message}");
        }

        var (driveName, total, free) = ReadSystemDrive();
        return new LiveSnapshot(cpu, memory, driveName, total, free, SystemStats.Uptime);
    }

    /// <summary>Free/total space of the drive Windows is installed on ("C:"). Name is null when it cannot be read.</summary>
    public static (string? Name, long Total, long Free) ReadSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (drive.IsReady)
                return (drive.Name.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Главная: не удалось прочитать сведения о системном диске: {ex.Message}");
        }
        return (null, 0, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}
