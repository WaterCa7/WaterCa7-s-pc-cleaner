using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>Collects the hardware snapshot (WMI, registry, Win32). Read-only.</summary>
internal static class HardwareProbe
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(15);
    private const string DisplayClassKey = @"Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly int[] LaptopChassis = { 8, 9, 10, 14, 30, 31, 32 };

    public static async Task<HardwareSnapshot> CollectAsync()
    {
        Task<List<Dictionary<string, object?>>> Q(string query, string scope = Wmi.Cimv2)
            => Task.Run(() => Wmi.Query(query, scope, WmiTimeout));

        var cpuTask = Q("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed FROM Win32_Processor");
        var ramTask = Q("SELECT * FROM Win32_PhysicalMemory");
        var arrayTask = Q("SELECT * FROM Win32_PhysicalMemoryArray");
        var gpuTask = Q("SELECT * FROM Win32_VideoController");
        var boardTask = Q("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var biosTask = Q("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        var osTask = Q("SELECT Caption, Version, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
        var batteryTask = Q("SELECT DeviceID FROM Win32_Battery");
        var csTask = Q("SELECT PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var enclosureTask = Q("SELECT ChassisTypes FROM Win32_SystemEnclosure");
        var monitorNamesTask = Q("SELECT InstanceName, UserFriendlyName FROM WmiMonitorID", Wmi.WmiRoot);
        var diskTask = Task.Run(GetSystemDisk);
        var displaysTask = Task.Run(NativeProbes.GetDisplays);
        var vramTask = Task.Run(ReadVramByName);

        await Task.WhenAll(cpuTask, ramTask, arrayTask, gpuTask, boardTask, biosTask, osTask, batteryTask, csTask, enclosureTask,
            monitorNamesTask, diskTask, displaysTask, vramTask).ConfigureAwait(false);

        var cpu = cpuTask.Result.FirstOrDefault() is { } c
            ? new CpuInfo
            {
                Name = c.Str("Name") ?? "",
                Cores = cpuTask.Result.Sum(r => (int)(r.Long("NumberOfCores") ?? 0)),
                LogicalProcessors = cpuTask.Result.Sum(r => (int)(r.Long("NumberOfLogicalProcessors") ?? 0)),
                MaxClockMhz = (int)(c.Long("MaxClockSpeed") ?? 0),
                CurrentClockMhz = (int)(c.Long("CurrentClockSpeed") ?? 0),
            }
            : null;
        if (cpu is { LogicalProcessors: 0 })
            cpu = cpu with { LogicalProcessors = Environment.ProcessorCount };

        var modules = ramTask.Result
            .Select(r => new RamModule
            {
                CapacityBytes = r.Long("Capacity") ?? 0,
                RatedMhz = (int)(r.Long("Speed") ?? 0),
                ConfiguredMhz = (int)(r.Long("ConfiguredClockSpeed") ?? 0),
                Manufacturer = Clean(r.Str("Manufacturer")),
                PartNumber = Clean(r.Str("PartNumber")),
                BankLabel = Clean(r.Str("BankLabel")),
                DeviceLocator = Clean(r.Str("DeviceLocator")),
                MemoryType = (int)(r.Long("SMBIOSMemoryType") ?? 0),
            })
            .Where(m => m.CapacityBytes > 0)
            .ToList();

        // Use = 3 is system memory; other arrays (flash, cache) are ignored.
        var arrays = arrayTask.Result.Where(a => (a.Long("Use") ?? 3) == 3).ToList();
        var slots = (int)arrays.Sum(a => a.Long("MemoryDevices") ?? 0);

        var vram = vramTask.Result;
        var gpus = gpuTask.Result
            .Select(r =>
            {
                var name = r.Str("Name") ?? r.Str("Caption") ?? "";
                var adapterRam = r.Long("AdapterRAM") ?? 0;
                if (adapterRam < 0)
                    adapterRam += 1L << 32; // uint32 reported as negative int
                return new GpuInfo
                {
                    Name = name,
                    DriverVersion = r.Str("DriverVersion"),
                    DriverDate = r.Date("DriverDate"),
                    VideoProcessor = r.Str("VideoProcessor"),
                    VramBytes = vram.TryGetValue(name, out var v) && v > 0 ? v : adapterRam,
                    CurrentWidth = (int)(r.Long("CurrentHorizontalResolution") ?? 0),
                    CurrentHeight = (int)(r.Long("CurrentVerticalResolution") ?? 0),
                    CurrentRefreshHz = (int)(r.Long("CurrentRefreshRate") ?? 0),
                    PnpDeviceId = r.Str("PNPDeviceID"),
                };
            })
            .Where(g => g.Name.Length > 0)
            .ToList();

        var monitorNames = monitorNamesTask.Result
            .Select(r => (Instance: r.Str("InstanceName") ?? "", Name: DecodeWmiString(r.TryGetValue("UserFriendlyName", out var n) ? n : null)))
            .Where(m => m.Instance.Length > 0 && !string.IsNullOrWhiteSpace(m.Name))
            .ToList();
        var rawDisplays = displaysTask.Result;
        var displays = rawDisplays
            .Select((d, i) => new DisplayInfo
            {
                DeviceName = d.DeviceName,
                AdapterName = d.AdapterName,
                MonitorName = MonitorName(d, monitorNames, rawDisplays.Count, i),
                Width = d.Width,
                Height = d.Height,
                CurrentHz = d.CurrentHz,
                MaxHzAtCurrentResolution = d.MaxHzAtCurrentResolution,
                IsPrimary = d.IsPrimary,
            })
            .OrderByDescending(d => d.IsPrimary)
            .ToList();

        var board = boardTask.Result.FirstOrDefault();
        var bios = biosTask.Result.FirstOrDefault();
        var os = osTask.Result.FirstOrDefault();
        var cs = csTask.Result.FirstOrDefault();
        var pcSystemType = (int)(cs?.Long("PCSystemType") ?? 0);
        var hasBattery = batteryTask.Result.Count > 0;
        var chassisLaptop = enclosureTask.Result.Any(r => r.TryGetValue("ChassisTypes", out var v) && v is Array arr &&
                                                           arr.Cast<object>().Any(x => LaptopChassis.Contains(Convert.ToInt32(x, CultureInfo.InvariantCulture))));
        var isLaptop = pcSystemType == 2 || (pcSystemType is not (1 or 3) && (chassisLaptop || hasBattery));

        var (displayVersion, ubr) = ReadOsDisplayVersion();
        var build = os?.Str("BuildNumber");

        return new HardwareSnapshot
        {
            Cpu = cpu,
            RamModules = modules,
            RamSlots = slots,
            VisibleRamBytes = (long)SystemStats.Memory().TotalBytes,
            Gpus = gpus,
            Displays = displays,
            BoardManufacturer = Clean(board?.Str("Manufacturer")),
            BoardProduct = Clean(board?.Str("Product")),
            BiosVersion = Clean(bios?.Str("SMBIOSBIOSVersion")),
            BiosDate = bios?.Date("ReleaseDate"),
            OsCaption = Clean(os?.Str("Caption")),
            OsVersion = os?.Str("Version"),
            OsBuild = build is null ? null : ubr is null ? build : $"{build}.{ubr}",
            OsDisplayVersion = displayVersion,
            OsInstallDate = os?.Date("InstallDate"),
            LastBoot = os?.Date("LastBootUpTime"),
            SystemDisk = diskTask.Result,
            HasBattery = hasBattery,
            IsLaptop = isLaptop,
            CollectedAt = DateTime.Now,
        };
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = s.Trim();
        return s.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               s == "0" || s.Equals("Undefined", StringComparison.OrdinalIgnoreCase)
            ? null
            : s;
    }

    /// <summary>WMI uint16[] strings (WmiMonitorID.UserFriendlyName) -> text.</summary>
    internal static string? DecodeWmiString(object? value)
    {
        if (value is not Array arr)
            return value?.ToString();
        var chars = arr.Cast<object>()
            .Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture))
            .TakeWhile(x => x != 0)
            .Select(x => (char)x)
            .ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Matches "DISPLAY\TRG2711\5&amp;108ccec1&amp;0&amp;UID4354_0" (WmiMonitorID) with
    /// "\\?\DISPLAY#TRG2711#5&amp;108ccec1&amp;0&amp;UID4354#{guid}" (EnumDisplayDevices interface name).
    /// </summary>
    internal static bool MonitorIdsMatch(string wmiInstance, string? deviceInterface)
    {
        if (string.IsNullOrEmpty(deviceInterface) || string.IsNullOrEmpty(wmiInstance))
            return false;
        var id = Regex.Replace(wmiInstance, @"_\d+$", "").Replace('\\', '#');
        return deviceInterface.Contains(id, StringComparison.OrdinalIgnoreCase);
    }

    private static string? MonitorName(RawDisplay d, List<(string Instance, string? Name)> names, int displayCount, int index)
    {
        var match = names.FirstOrDefault(n => MonitorIdsMatch(n.Instance, d.MonitorDeviceId));
        if (!string.IsNullOrWhiteSpace(match.Name))
            return match.Name;
        if (displayCount == 1 && names.Count == 1)
            return names[0].Name;
        var desc = d.MonitorDescription;
        if (!string.IsNullOrWhiteSpace(desc) &&
            !desc.Contains("PnP", StringComparison.OrdinalIgnoreCase) &&
            !desc.Contains("Generic", StringComparison.OrdinalIgnoreCase) &&
            !desc.Contains("Универсальный", StringComparison.OrdinalIgnoreCase))
            return desc;
        return displayCount > 1 ? $"Монитор {index + 1}" : null;
    }

    /// <summary>Real VRAM from the display class registry key (AdapterRAM in WMI is capped at 4 GB).</summary>
    private static Dictionary<string, long> ReadVramByName()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var controlSet in new[] { "CurrentControlSet", "ControlSet001" })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var cls = root.OpenSubKey($@"SYSTEM\{controlSet}\{DisplayClassKey}");
                if (cls is null)
                    continue;
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    try
                    {
                        using var key = cls.OpenSubKey(sub);
                        if (key?.GetValue("DriverDesc") is not string desc || result.ContainsKey(desc))
                            continue;
                        var bytes = ToLong(key.GetValue("HardwareInformation.qwMemorySize"))
                                    ?? ToLong(key.GetValue("HardwareInformation.MemorySize"));
                        if (bytes is > 0)
                            result[desc] = bytes.Value;
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // some subkeys are protected; skip them
                    }
                }
                if (result.Count > 0)
                    break;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn($"Ускорение: нет доступа к сведениям о видеопамяти: {ex.Message}");
            }
        }
        return result;
    }

    internal static long? ToLong(object? value) => value switch
    {
        long l => l,
        int i => (uint)i,
        byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        _ => null,
    };

    private static (string? DisplayVersion, string? Ubr) ReadOsDisplayVersion()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null)
                return (null, null);
            var display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
            var ubr = key.GetValue("UBR") is int u ? u.ToString(CultureInfo.InvariantCulture) : null;
            return (display, ubr);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    private static SystemDiskInfo? GetSystemDisk()
    {
        var letter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':', '\\').ToUpperInvariant();
        if (letter.Length != 1)
            letter = "C";

        var partitions = Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Wmi.Storage, WmiTimeout);
        var partition = partitions.FirstOrDefault(p => string.Equals(DriveLetterOf(p), letter, StringComparison.OrdinalIgnoreCase));
        if (partition?.Long("DiskNumber") is not { } diskNumber)
            return new SystemDiskInfo { DriveLetter = letter, MediaKind = DiskMediaKind.Unknown };

        var physical = Wmi.Query("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, SpindleSpeed FROM MSFT_PhysicalDisk",
            Wmi.Storage, WmiTimeout);
        var pd = physical.FirstOrDefault(p => p.Str("DeviceId") == diskNumber.ToString(CultureInfo.InvariantCulture));
        if (pd is null)
        {
            // Storage Spaces / RAID: the virtual disk number does not map to a physical disk.
            var disk = Wmi.Query($"SELECT FriendlyName, Size, BusType FROM MSFT_Disk WHERE Number = {diskNumber}", Wmi.Storage, WmiTimeout)
                .FirstOrDefault();
            return new SystemDiskInfo
            {
                DriveLetter = letter,
                Model = disk?.Str("FriendlyName"),
                SizeBytes = disk?.Long("Size") ?? 0,
                BusType = BusTypeName((int)(disk?.Long("BusType") ?? 0)),
                MediaKind = DiskMediaKind.Unknown,
            };
        }

        var busType = (int)(pd.Long("BusType") ?? 0);
        return new SystemDiskInfo
        {
            DriveLetter = letter,
            Model = pd.Str("FriendlyName"),
            SizeBytes = pd.Long("Size") ?? 0,
            BusType = BusTypeName(busType),
            MediaKind = MediaKindOf((int)(pd.Long("MediaType") ?? 0), busType, pd.Long("SpindleSpeed")),
        };
    }

    private static string? DriveLetterOf(Dictionary<string, object?> partition)
    {
        if (!partition.TryGetValue("DriveLetter", out var v) || v is null)
            return null;
        var s = v is char ch ? ch.ToString() : v.ToString();
        s = s?.Trim('\0', ' ');
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>MSFT_PhysicalDisk MediaType: 3 = HDD, 4 = SSD, 5 = SCM; NVMe and zero spindle speed also mean SSD.</summary>
    internal static DiskMediaKind MediaKindOf(int mediaType, int busType, long? spindleSpeed) => mediaType switch
    {
        3 => DiskMediaKind.Hdd,
        4 or 5 => DiskMediaKind.Ssd,
        _ when busType == 17 => DiskMediaKind.Ssd,
        _ when spindleSpeed == 0 => DiskMediaKind.Ssd,
        _ when spindleSpeed is > 0 and < 100000 => DiskMediaKind.Hdd,
        _ => DiskMediaKind.Unknown,
    };

    internal static string? BusTypeName(int busType) => busType switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => null,
    };
}

/// <summary>Collects the settings snapshot (powercfg, registry, WMI). Read-only.</summary>
internal sealed class SettingsProbe
{
    private readonly PowerPlanSwitcher _power;

    public SettingsProbe(PowerPlanSwitcher power) => _power = power;

    public async Task<SettingsSnapshot> CollectAsync(CancellationToken ct)
    {
        var activeTask = _power.GetActiveAsync(ct);
        var listTask = _power.ListAsync(ct);
        var pagefileTask = Task.Run(() =>
        {
            var cs = Wmi.Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").FirstOrDefault();
            var usage = Wmi.Query("SELECT Name FROM Win32_PageFileUsage");
            return (Automatic: cs?.Bool("AutomaticManagedPagefile"), Count: cs is null ? -1 : usage.Count);
        }, ct);
        var hagsTask = Task.Run(NativeProbes.QueryHagsCaps, ct);
        var registryTask = Task.Run(ReadRegistry, ct);

        await Task.WhenAll(activeTask, listTask, pagefileTask, hagsTask, registryTask).ConfigureAwait(false);

        var active = activeTask.Result;
        var plans = listTask.Result;
        if (active is not null)
        {
            plans = plans.Select(p => p with { IsActive = p.Guid == active.Guid }).ToList();
            if (string.IsNullOrWhiteSpace(active.Name))
                active = plans.FirstOrDefault(p => p.Guid == active.Guid) ?? active;
        }

        var reg = registryTask.Result;
        var mem = SystemStats.Memory();
        var hags = hagsTask.Result;
        return new SettingsSnapshot
        {
            ActivePlan = active,
            Plans = plans,
            GameModeValue = reg.GameMode,
            GameDvrValue = reg.GameDvr,
            AppCaptureValue = reg.AppCapture,
            HistoricalCaptureValue = reg.Historical,
            HagsRegistryMode = reg.Hags,
            HagsSupported = hags?.Supported,
            HagsActive = hags?.Enabled,
            VisualFxSetting = reg.VisualFx,
            TransparencyValue = reg.Transparency,
            PagefileAutomatic = pagefileTask.Result.Automatic,
            PagefileCount = pagefileTask.Result.Count,
            PendingReboot = reg.PendingReboot,
            Uptime = SystemStats.Uptime,
            OnAcPower = NativeProbes.IsOnAcPower(),
            MemoryLoadPercent = (int)mem.LoadPercent,
            MemoryUsedBytes = (long)mem.UsedBytes,
            MemoryTotalBytes = (long)mem.TotalBytes,
            CollectedAt = DateTime.Now,
        };
    }

    private sealed record RegistryValues(int? GameMode, int? GameDvr, int? AppCapture, int? Historical, int? Hags, int? VisualFx,
        int? Transparency, bool PendingReboot);

    private static RegistryValues ReadRegistry()
    {
        var hkcu = RegistryHive.CurrentUser;
        var hklm = RegistryHive.LocalMachine;
        return new RegistryValues(
            ReadInt(hkcu, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
            ReadInt(hkcu, @"System\GameConfigStore", "GameDVR_Enabled"),
            ReadInt(hkcu, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
            ReadInt(hkcu, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"),
            ReadInt(hklm, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
            ReadInt(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting"),
            ReadInt(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency"),
            KeyExists(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") ||
            KeyExists(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"));
    }

    internal static int? ReadInt(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
                byte[] { Length: >= 4 } b => BitConverter.ToInt32(b, 0),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Ускорение: не удалось прочитать {(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{subKey}\\{name}: {ex.Message}");
            return null;
        }
    }

    private static bool KeyExists(RegistryHive hive, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey);
            return key is not null;
        }
        catch (SecurityException)
        {
            return true; // the key exists but is protected
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
