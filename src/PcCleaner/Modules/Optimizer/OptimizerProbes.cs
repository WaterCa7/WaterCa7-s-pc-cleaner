using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core.Localization;
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
        var batteryTask = Q("SELECT DeviceID, Name FROM Win32_Battery");
        var csTask = Q("SELECT PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var enclosureTask = Q("SELECT ChassisTypes FROM Win32_SystemEnclosure");
        var monitorNamesTask = Q("SELECT InstanceName, UserFriendlyName FROM WmiMonitorID", Wmi.WmiRoot);
        // Separate queries: if an old provider lacks these properties, only this detail is lost, not the whole CPU/PC info.
        var vtxTask = Q("SELECT VirtualizationFirmwareEnabled, VMMonitorModeExtensions FROM Win32_Processor");
        var hypervisorTask = Q("SELECT HypervisorPresent FROM Win32_ComputerSystem");
        var diskTask = Task.Run(GetSystemDisk);
        var displaysTask = Task.Run(NativeProbes.GetDisplays);
        var vramTask = Task.Run(ReadVramByName);
        var bootTask = Task.Run(ReadBootMode);
        var batteryInfoTask = Task.Run(async () => ReadBatterySafe(await batteryTask.ConfigureAwait(false)));

        await Task.WhenAll(cpuTask, ramTask, arrayTask, gpuTask, boardTask, biosTask, osTask, batteryTask, csTask, enclosureTask,
            monitorNamesTask, vtxTask, hypervisorTask, diskTask, displaysTask, vramTask, bootTask, batteryInfoTask).ConfigureAwait(false);

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
            .Select((d, i) =>
            {
                var (monitorName, isGeneric) = MonitorName(d, monitorNames, rawDisplays.Count, i);
                return new DisplayInfo
                {
                    DeviceName = d.DeviceName,
                    AdapterName = d.AdapterName,
                    MonitorName = monitorName,
                    MonitorNameIsGeneric = isGeneric,
                    Width = d.Width,
                    Height = d.Height,
                    CurrentHz = d.CurrentHz,
                    MaxHzAtCurrentResolution = d.MaxHzAtCurrentResolution,
                    IsPrimary = d.IsPrimary,
                };
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
            BootMode = bootTask.Result,
            // Several CPU sockets: enabled only if every socket says so.
            VirtualizationFirmwareEnabled = vtxTask.Result.Select(r => r.Bool("VirtualizationFirmwareEnabled")).ToList() is { Count: > 0 } vtx &&
                                            vtx.All(v => v is not null)
                ? vtx.All(v => v == true)
                : null,
            HypervisorPresent = hypervisorTask.Result.FirstOrDefault()?.Bool("HypervisorPresent"),
            VirtualizationSupported = vtxTask.Result.Select(r => r.Bool("VMMonitorModeExtensions")).ToList() is { Count: > 0 } vmx &&
                                      vmx.All(v => v is not null)
                ? vmx.All(v => v == true)
                : null,
            Battery = batteryInfoTask.Result,
            CollectedAt = DateTime.Now,
        };
    }

    /// <summary>GetFirmwareType, falling back to HKLM\SYSTEM\CurrentControlSet\Control PEFirmwareType.</summary>
    private static BootMode ReadBootMode()
    {
        var mode = HardwareText.BootModeFrom(NativeProbes.GetFirmwareTypeValue());
        if (mode != BootMode.Unknown)
            return mode;
        return HardwareText.BootModeFrom(SettingsProbe.ReadInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "PEFirmwareType"));
    }

    /// <summary>
    /// Design and full-charge capacity from root\wmi (summed over all batteries). Some laptops give these classes only to
    /// administrators — then the state says so and the card explains it. Null without a battery; an unexpected failure
    /// only loses the battery details, never the whole snapshot.
    /// </summary>
    private static BatteryInfo? ReadBatterySafe(List<Dictionary<string, object?>> win32Batteries)
    {
        if (win32Batteries.Count == 0)
            return null;
        try
        {
            return ReadBattery(win32Batteries);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            Log.Warn(T.F("Ускорение: не удалось прочитать сведения о батарее: {0}", ErrorText.For(ex)), ex);
            return new BatteryInfo { CapacityState = SensorState.Unknown };
        }
    }

    private static BatteryInfo ReadBattery(List<Dictionary<string, object?>> win32Batteries)
    {
        var name = win32Batteries.Select(b => Clean(b.Str("Name"))).FirstOrDefault(n => n is not null);
        var timeout = TimeSpan.FromSeconds(8);
        var (designRows, designState) = WmiSensorQuery.Query("SELECT InstanceName, DesignedCapacity FROM BatteryStaticData", Wmi.WmiRoot,
            timeout);
        var (fullRows, fullState) = WmiSensorQuery.Query("SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity",
            Wmi.WmiRoot, timeout);
        var (designed, full) = HardwareText.BatteryCapacities(
            designRows.Select(r => (r.Str("InstanceName"), r.Long("DesignedCapacity") ?? 0)).ToList(),
            fullRows.Select(r => (r.Str("InstanceName"), r.Long("FullChargedCapacity") ?? 0)).ToList());
        var state = designed > 0 && full > 0
            ? SensorState.Available
            : designState == SensorState.NeedsAdmin || fullState == SensorState.NeedsAdmin
                ? SensorState.NeedsAdmin
                : designState == SensorState.Unknown || fullState == SensorState.Unknown
                    ? SensorState.Unknown
                    : SensorState.NotSupported;
        return new BatteryInfo
        {
            Name = name,
            DesignedCapacityMwh = state == SensorState.Available ? designed : 0,
            FullChargedCapacityMwh = state == SensorState.Available ? full : 0,
            CapacityState = state,
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

    /// <summary>The name of the monitor and whether the program made it up (Windows gave no usable name).</summary>
    private static (string? Name, bool IsGeneric) MonitorName(RawDisplay d, List<(string Instance, string? Name)> names, int displayCount, int index)
    {
        var match = names.FirstOrDefault(n => MonitorIdsMatch(n.Instance, d.MonitorDeviceId));
        if (!string.IsNullOrWhiteSpace(match.Name))
            return (match.Name, false);
        if (displayCount == 1 && names.Count == 1)
            return (names[0].Name, false);
        var desc = d.MonitorDescription;
        // l10n:data-begin — the Russian word of the description of a generic monitor, matched and never shown
        if (!string.IsNullOrWhiteSpace(desc) &&
            !desc.Contains("PnP", StringComparison.OrdinalIgnoreCase) &&
            !desc.Contains("Generic", StringComparison.OrdinalIgnoreCase) &&
            !desc.Contains("Универсальный", StringComparison.OrdinalIgnoreCase))
            return (desc, false);
        // l10n:data-end
        return displayCount > 1 ? (T.F("Монитор {0}", index + 1), true) : (null, false);
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
                Log.Warn(T.F("Ускорение: нет доступа к сведениям о видеопамяти: {0}", ErrorText.For(ex)), ex);
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

/// <summary>Collects the settings snapshot (powercfg, registry, WMI, live sensors). Read-only.</summary>
internal sealed class SettingsProbe
{
    /// <summary>Length of the CPU load sample.</summary>
    public static readonly TimeSpan CpuSampleDuration = TimeSpan.FromSeconds(2);

    /// <summary>A fresh sample is reused for this long (e.g. reloading right after "Применить" does not wait 2 s again).</summary>
    private static readonly TimeSpan CpuSampleReuse = TimeSpan.FromSeconds(20);

    private readonly PowerPlanSwitcher _power;
    private readonly Func<Task>? _beforeCpuSample;
    private readonly object _cpuLock = new();
    private (DateTime At, double? Value)? _lastCpu;

    /// <param name="power">Reads the power plans.</param>
    /// <param name="beforeCpuSample">
    /// Awaited before the CPU is sampled — the service waits for its own hardware scan (WMI) so it is not measured as load.
    /// </param>
    public SettingsProbe(PowerPlanSwitcher power, Func<Task>? beforeCpuSample = null)
    {
        _power = power;
        _beforeCpuSample = beforeCpuSample;
    }

    public async Task<SettingsSnapshot> CollectAsync(CancellationToken ct)
    {
        var activeTask = _power.GetActiveAsync(ct);
        var listTask = _power.ListAsync(ct);
        var pagefileTask = Task.Run(() =>
        {
            var cs = Wmi.Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").FirstOrDefault();
            var usage = Wmi.Query("SELECT Name, CurrentUsage, AllocatedBaseSize FROM Win32_PageFileUsage");
            // A configured page file (Win32_PageFileSetting) also counts: "disabled" must never be reported just because
            // the usage list came back empty (e.g. the file is enabled but waits for a restart).
            var configured = Wmi.Query("SELECT Name FROM Win32_PageFileSetting");
            return (Automatic: cs?.Bool("AutomaticManagedPagefile"), Count: cs is null ? -1 : Math.Max(usage.Count, configured.Count),
                UsedMb: usage.Sum(u => Math.Max(0, u.Long("CurrentUsage") ?? 0)),
                AllocatedMb: usage.Sum(u => Math.Max(0, u.Long("AllocatedBaseSize") ?? 0)),
                Path: usage.Select(u => u.Str("Name")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));
        }, ct);
        var hagsTask = Task.Run(NativeProbes.QueryHagsCaps, ct);
        var registryTask = Task.Run(ReadRegistry, ct);
        var temperatureTask = Task.Run(ReadTemperature, ct);
        var cpuTask = SampleCpuAsync(ct);

        await Task.WhenAll(activeTask, listTask, pagefileTask, hagsTask, registryTask, temperatureTask, cpuTask).ConfigureAwait(false);

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
        var power = NativeProbes.GetPowerStatus();
        var pagefile = pagefileTask.Result;
        var (temperature, temperatureState) = temperatureTask.Result;
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
            PagefileAutomatic = pagefile.Automatic,
            PagefileCount = pagefile.Count,
            PagefileUsedMb = pagefile.UsedMb,
            PagefileAllocatedMb = pagefile.AllocatedMb,
            PagefilePath = pagefile.Path,
            PendingReboot = reg.PendingReboot,
            Uptime = SystemStats.Uptime,
            FastStartupValue = reg.FastStartup,
            HibernateValue = reg.Hibernate,
            OnAcPower = power.OnAc,
            BatteryChargePercent = power.BatteryChargePercent,
            BatteryState = power.BatteryState,
            MemoryLoadPercent = (int)mem.LoadPercent,
            MemoryUsedBytes = (long)mem.UsedBytes,
            MemoryTotalBytes = (long)mem.TotalBytes,
            CpuLoadPercent = cpuTask.Result,
            TemperatureC = temperature,
            TemperatureState = temperatureState,
            CollectedAt = DateTime.Now,
        };
    }

    /// <summary>2-second CPU sample (taken after the caller's hardware scan finished), reused for a short while.</summary>
    private async Task<double?> SampleCpuAsync(CancellationToken ct)
    {
        lock (_cpuLock)
        {
            if (_lastCpu is { } last && DateTime.UtcNow - last.At < CpuSampleReuse)
                return last.Value;
        }
        if (_beforeCpuSample is not null)
        {
            try
            {
                await _beforeCpuSample().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A failed or slow hardware scan must not block the measurement.
            }
        }
        var value = await NativeProbes.SampleCpuLoadAsync(CpuSampleDuration, ct).ConfigureAwait(false);
        lock (_cpuLock)
            _lastCpu = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>Highest valid ACPI thermal zone (root\wmi; usually needs administrator rights).</summary>
    private static (double? Celsius, SensorState State) ReadTemperature()
    {
        var (rows, state) = WmiSensorQuery.Query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", Wmi.WmiRoot,
            TimeSpan.FromSeconds(6));
        if (state != SensorState.Available)
            return (null, state);
        var values = rows.Select(r => HardwareText.KelvinTenthsToCelsius(r.Long("CurrentTemperature"))).OfType<double>().ToList();
        // Thermal zones that only return placeholders are as good as no sensor.
        return values.Count == 0 ? (null, SensorState.NotSupported) : (values.Max(), SensorState.Available);
    }

    private sealed record RegistryValues(int? GameMode, int? GameDvr, int? AppCapture, int? Historical, int? Hags, int? VisualFx,
        int? Transparency, bool PendingReboot, int? FastStartup, int? Hibernate);

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
            KeyExists(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
            ReadInt(hklm, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"),
            ReadInt(hklm, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"));
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
            Log.Warn(T.F("Ускорение: не удалось прочитать {0}\\{1}\\{2}: {3}", (hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU"), subKey, name, ErrorText.For(ex)), ex);
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
