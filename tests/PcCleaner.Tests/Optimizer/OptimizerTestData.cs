using PcCleaner.Modules.Optimizer;

namespace PcCleaner.Tests.Optimizer;

/// <summary>Synthetic snapshots: a well-tuned gaming desktop, modified per test with "with { ... }".</summary>
internal static class OptimizerTestData
{
    public const long Gb = 1024L * 1024 * 1024;

    public static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0);

    public static RamModule Stick(long gb = 8, int rated = 3200, int configured = 3200, string locator = "ChannelA-DIMM1",
        int type = 26, string part = "KF432C16")
        => new()
        {
            CapacityBytes = gb * Gb,
            RatedMhz = rated,
            ConfiguredMhz = configured,
            DeviceLocator = locator,
            BankLabel = "BANK 0",
            Manufacturer = "Kingston",
            PartNumber = part,
            MemoryType = type,
        };

    public static GpuInfo Rtx3060(DateTime? driverDate = null) => new()
    {
        Name = "NVIDIA GeForce RTX 3060",
        DriverVersion = "32.0.15.6094",
        DriverDate = driverDate ?? Now.AddMonths(-1),
        VramBytes = 12 * Gb,
        PnpDeviceId = @"PCI\VEN_10DE&DEV_2504&SUBSYS_00001458&REV_A1\4&1",
    };

    public static GpuInfo IntelUhd(DateTime? driverDate = null) => new()
    {
        Name = "Intel(R) UHD Graphics 630",
        DriverVersion = "31.0.101.2111",
        DriverDate = driverDate ?? Now.AddMonths(-1),
        VramBytes = 128L * 1024 * 1024,
        PnpDeviceId = @"PCI\VEN_8086&DEV_3E92&SUBSYS_D0001458&REV_00\3&1",
    };

    public static DisplayInfo Display(int current = 144, int max = 144, string adapter = "NVIDIA GeForce RTX 3060", bool primary = true,
        string device = @"\\.\DISPLAY1")
        => new()
        {
            DeviceName = device,
            MonitorName = "VG27AQ",
            AdapterName = adapter,
            Width = 2560,
            Height = 1440,
            CurrentHz = current,
            MaxHzAtCurrentResolution = max,
            IsPrimary = primary,
        };

    public static HardwareSnapshot GoodDesktop() => new()
    {
        Cpu = new CpuInfo { Name = "AMD Ryzen 5 5600X 6-Core Processor", Cores = 6, LogicalProcessors = 12, MaxClockMhz = 3700 },
        RamModules = new[] { Stick(locator: "ChannelA-DIMM1"), Stick(locator: "ChannelB-DIMM1") },
        RamSlots = 4,
        VisibleRamBytes = 16 * Gb - 300 * 1024 * 1024,
        Gpus = new[] { Rtx3060() },
        Displays = new[] { Display() },
        BoardManufacturer = "ASUSTeK COMPUTER INC.",
        BoardProduct = "TUF GAMING B550-PLUS",
        BiosVersion = "3405",
        BiosDate = Now.AddYears(-1),
        OsCaption = "Microsoft Windows 11 Pro",
        OsBuild = "26100.2033",
        SystemDisk = new SystemDiskInfo { DriveLetter = "C", Model = "Samsung SSD 980 1TB", MediaKind = DiskMediaKind.Ssd, BusType = "NVMe" },
        IsLaptop = false,
    };

    public static readonly PowerPlan Balanced = new(PowerPlans.Balanced, "Сбалансированная");
    public static readonly PowerPlan High = new(PowerPlans.HighPerformance, "Высокая производительность");
    public static readonly PowerPlan Saver = new(PowerPlans.PowerSaver, "Экономия энергии");
    public static readonly PowerPlan UltimateCopy = new("38156909-5918-4777-864e-fbf99c75df8b", "Максимальная производительность");

    public static SettingsSnapshot GoodSettings() => new()
    {
        ActivePlan = High with { IsActive = true },
        Plans = new[] { Balanced, High with { IsActive = true }, Saver },
        GameModeValue = 1,
        GameDvrValue = 0,
        AppCaptureValue = 0,
        HagsRegistryMode = 2,
        HagsSupported = true,
        HagsActive = true,
        VisualFxSetting = null,
        TransparencyValue = 1,
        PagefileAutomatic = true,
        PagefileCount = 1,
        PendingReboot = false,
        Uptime = TimeSpan.FromHours(10),
        OnAcPower = null,
        MemoryLoadPercent = 40,
        MemoryUsedBytes = 6 * Gb,
        MemoryTotalBytes = 16 * Gb,
    };

    public static List<Recommendation> Evaluate(HardwareSnapshot hw, SettingsSnapshot s)
        => RecommendationEngine.Evaluate(hw, s, Now);

    /// <summary>Recommendations the user should act on (not passed checks, not page links).</summary>
    public static List<Recommendation> Actionable(this IEnumerable<Recommendation> items)
        => items.Where(r => r.Kind is not (RecommendationKind.Good or RecommendationKind.Navigate)).ToList();

    public static Recommendation? Find(this IEnumerable<Recommendation> items, string id)
        => items.FirstOrDefault(r => r.Id == id);
}
