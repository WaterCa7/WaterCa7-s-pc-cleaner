using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

public enum DiskMediaKind
{
    Unknown,
    Hdd,
    Ssd,
}

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Microsoft,
}

public enum PowerPlanKind
{
    Custom,
    PowerSaver,
    Balanced,
    HighPerformance,
    UltimatePerformance,
}

/// <summary>How the firmware boots Windows (GetFirmwareType / PEFirmwareType).</summary>
public enum BootMode
{
    Unknown,
    LegacyBios,
    Uefi,
}

/// <summary>Whether an optional reading (temperature, battery capacity) could be taken, and if not — why.</summary>
public enum SensorState
{
    /// <summary>Not queried or an unexpected error.</summary>
    Unknown,
    Available,

    /// <summary>Windows gives the value only to elevated programs.</summary>
    NeedsAdmin,

    /// <summary>The firmware/driver does not report it.</summary>
    NotSupported,
}

public enum BatteryChargeState
{
    Unknown,
    Charging,
    Discharging,

    /// <summary>On AC power but not charging (e.g. a charge limit is set).</summary>
    PluggedIn,
    Full,
}

/// <summary>Static battery data (root\wmi BatteryStaticData / BatteryFullChargedCapacity).</summary>
public sealed record BatteryInfo
{
    public string? Name { get; init; }

    /// <summary>Capacity of a new battery, mWh (0 = unknown).</summary>
    public long DesignedCapacityMwh { get; init; }

    /// <summary>What the battery holds now when fully charged, mWh (0 = unknown).</summary>
    public long FullChargedCapacityMwh { get; init; }

    /// <summary>Why the capacities are missing, if they are.</summary>
    public SensorState CapacityState { get; init; }

    /// <summary>Lost capacity in percent (0..100), or null when Windows did not report the capacities.</summary>
    public double? WearPercent => RecommendationEngine.BatteryWearPercent(DesignedCapacityMwh, FullChargedCapacityMwh);
}

public sealed record CpuInfo
{
    public string Name { get; init; } = "";
    public int Cores { get; init; }
    public int LogicalProcessors { get; init; }
    public int MaxClockMhz { get; init; }
    public int CurrentClockMhz { get; init; }
}

public sealed record RamModule
{
    public long CapacityBytes { get; init; }

    /// <summary>Rated speed from SPD (Win32_PhysicalMemory.Speed), MT/s.</summary>
    public int RatedMhz { get; init; }

    /// <summary>Speed the memory currently runs at (ConfiguredClockSpeed), MT/s.</summary>
    public int ConfiguredMhz { get; init; }

    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
    public string? BankLabel { get; init; }
    public string? DeviceLocator { get; init; }

    /// <summary>SMBIOSMemoryType (26 = DDR4, 34 = DDR5...).</summary>
    public int MemoryType { get; init; }

    /// <summary>Memory channel letter ("A", "B"...) parsed from the slot name, or null if unknown.</summary>
    public string? Channel => HardwareText.ParseChannel(DeviceLocator, BankLabel);
}

public sealed record GpuInfo
{
    public string Name { get; init; } = "";
    public string? DriverVersion { get; init; }
    public DateTime? DriverDate { get; init; }
    public string? VideoProcessor { get; init; }

    /// <summary>Dedicated video memory (registry qwMemorySize, falls back to AdapterRAM). 0 if unknown.</summary>
    public long VramBytes { get; init; }

    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public int CurrentRefreshHz { get; init; }
    public string? PnpDeviceId { get; init; }

    public GpuVendor Vendor => GpuClassifier.Vendor(Name, PnpDeviceId);

    /// <summary>Remote / virtual / mirror adapters that should not get driver advice.</summary>
    public bool IsVirtual => GpuClassifier.IsVirtual(Name, PnpDeviceId);

    /// <summary>Windows fallback driver: the real driver is not installed.</summary>
    public bool IsBasicDriver => GpuClassifier.IsBasicDriver(Name);

    public bool IsIntegrated => GpuClassifier.IsIntegrated(Name);
}

public sealed record DisplayInfo
{
    public string DeviceName { get; init; } = "";
    public string? MonitorName { get; init; }

    /// <summary>Name of the video adapter the monitor is connected to.</summary>
    public string? AdapterName { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public int CurrentHz { get; init; }

    /// <summary>Highest refresh rate the monitor offers at the current resolution.</summary>
    public int MaxHzAtCurrentResolution { get; init; }

    public bool IsPrimary { get; init; }
}

public sealed record SystemDiskInfo
{
    public string DriveLetter { get; init; } = "C";
    public string? Model { get; init; }
    public DiskMediaKind MediaKind { get; init; }
    public string? BusType { get; init; }
    public long SizeBytes { get; init; }
}

public sealed record HardwareSnapshot
{
    public CpuInfo? Cpu { get; init; }
    public IReadOnlyList<RamModule> RamModules { get; init; } = Array.Empty<RamModule>();

    /// <summary>Number of memory slots on the board (0 if unknown).</summary>
    public int RamSlots { get; init; }

    /// <summary>Memory visible to Windows (fallback when module info is not available).</summary>
    public long VisibleRamBytes { get; init; }

    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = Array.Empty<DisplayInfo>();

    public string? BoardManufacturer { get; init; }
    public string? BoardProduct { get; init; }
    public string? BiosVersion { get; init; }
    public DateTime? BiosDate { get; init; }

    public string? OsCaption { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }

    /// <summary>Feature update name, e.g. "24H2".</summary>
    public string? OsDisplayVersion { get; init; }

    public DateTime? OsInstallDate { get; init; }
    public DateTime? LastBoot { get; init; }

    public SystemDiskInfo? SystemDisk { get; init; }

    public bool HasBattery { get; init; }
    public bool IsLaptop { get; init; }

    /// <summary>UEFI or legacy BIOS boot.</summary>
    public BootMode BootMode { get; init; }

    /// <summary>Win32_Processor.VirtualizationFirmwareEnabled (VT-x / SVM enabled in BIOS); null if unknown.</summary>
    public bool? VirtualizationFirmwareEnabled { get; init; }

    /// <summary>Win32_ComputerSystem.HypervisorPresent; null if unknown.</summary>
    public bool? HypervisorPresent { get; init; }

    /// <summary>Laptop battery capacities (null on desktops or when there is no battery).</summary>
    public BatteryInfo? Battery { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Hardware virtualization is on. A running hypervisor (Hyper-V, WSL 2, memory integrity) hides VT-x from Windows,
    /// so VirtualizationFirmwareEnabled reads false although it is enabled — a hypervisor means "on".
    /// </summary>
    public bool? VirtualizationEnabled => HypervisorPresent == true ? true : VirtualizationFirmwareEnabled;

    public long TotalRamBytes => RamModules.Count > 0 ? RamModules.Sum(m => m.CapacityBytes) : VisibleRamBytes;

    public IEnumerable<GpuInfo> RealGpus => Gpus.Where(g => !g.IsVirtual);
}

public sealed record PowerPlan(string Guid, string Name, bool IsActive = false)
{
    public PowerPlanKind Kind => PowerPlans.Classify(Guid, Name);
}

public sealed record SettingsSnapshot
{
    public PowerPlan? ActivePlan { get; init; }
    public IReadOnlyList<PowerPlan> Plans { get; init; } = Array.Empty<PowerPlan>();

    /// <summary>HKCU\Software\Microsoft\GameBar AutoGameModeEnabled (null = value missing = on).</summary>
    public int? GameModeValue { get; init; }

    /// <summary>HKCU\System\GameConfigStore GameDVR_Enabled (null = missing).</summary>
    public int? GameDvrValue { get; init; }

    /// <summary>HKCU\...\GameDVR AppCaptureEnabled (null = missing).</summary>
    public int? AppCaptureValue { get; init; }

    /// <summary>HKCU\...\GameDVR HistoricalCaptureEnabled (background recording, null = missing).</summary>
    public int? HistoricalCaptureValue { get; init; }

    /// <summary>HKLM\...\GraphicsDrivers HwSchMode: 2 = on, 1 = off, null = default.</summary>
    public int? HagsRegistryMode { get; init; }

    /// <summary>Driver reports hardware-accelerated GPU scheduling support (null = unknown).</summary>
    public bool? HagsSupported { get; init; }

    /// <summary>HAGS is active in the current boot session (null = unknown).</summary>
    public bool? HagsActive { get; init; }

    /// <summary>HKCU\...\VisualEffects VisualFXSetting: 0 auto, 1 appearance, 2 performance, 3 custom.</summary>
    public int? VisualFxSetting { get; init; }

    /// <summary>HKCU\...\Personalize EnableTransparency (null = missing = on).</summary>
    public int? TransparencyValue { get; init; }

    public bool? PagefileAutomatic { get; init; }

    /// <summary>Number of page files in use or configured (Win32_PageFileUsage / Win32_PageFileSetting), -1 if unknown.</summary>
    public int PagefileCount { get; init; } = -1;

    /// <summary>Win32_PageFileUsage: used megabytes summed over all page files.</summary>
    public long PagefileUsedMb { get; init; }

    /// <summary>Win32_PageFileUsage: allocated megabytes summed over all page files.</summary>
    public long PagefileAllocatedMb { get; init; }

    /// <summary>Path of the first page file, e.g. "C:\pagefile.sys".</summary>
    public string? PagefilePath { get; init; }

    public bool PendingReboot { get; init; }
    public TimeSpan Uptime { get; init; }

    /// <summary>HKLM\...\Session Manager\Power HiberbootEnabled (1 = fast startup on, null = missing).</summary>
    public int? FastStartupValue { get; init; }

    /// <summary>HKLM\...\Control\Power HibernateEnabled (0 = hibernation off, which also turns fast startup off).</summary>
    public int? HibernateValue { get; init; }

    /// <summary>true = plugged in, false = on battery, null = unknown/desktop.</summary>
    public bool? OnAcPower { get; init; }

    public int MemoryLoadPercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }

    /// <summary>Total CPU load over a 2-second sample, without this app's own work; null if not measured.</summary>
    public double? CpuLoadPercent { get; init; }

    /// <summary>Highest valid ACPI thermal zone temperature, °C; null when not available (see <see cref="TemperatureState"/>).</summary>
    public double? TemperatureC { get; init; }

    public SensorState TemperatureState { get; init; }

    /// <summary>Battery charge 0..100 (null if unknown or no battery).</summary>
    public int? BatteryChargePercent { get; init; }

    public BatteryChargeState BatteryState { get; init; }

    public DateTime CollectedAt { get; init; } = DateTime.Now;

    public bool GameModeEnabled => GameModeValue is null || GameModeValue.Value != 0;

    /// <summary>Game Bar recording is effectively on only when neither master switch is off.</summary>
    public bool GameDvrActive => (GameDvrValue ?? 1) != 0 && (AppCaptureValue ?? 1) != 0;

    public bool TransparencyEnabled => TransparencyValue is null || TransparencyValue.Value != 0;

    public bool PagefileDisabled => PagefileAutomatic == false && PagefileCount == 0;

    /// <summary>How full the page files are (0..100), or null when the sizes are unknown.</summary>
    public double? PagefileUsagePercent => PagefileAllocatedMb > 0
        ? Math.Clamp(100.0 * PagefileUsedMb / PagefileAllocatedMb, 0, 100)
        : null;

    /// <summary>Fast startup works only when it is switched on and hibernation is not disabled.</summary>
    public bool FastStartupEnabled => FastStartupValue == 1 && HibernateValue != 0;
}

public enum RecommendationKind
{
    /// <summary>One-click Apply / Revert.</summary>
    AutoFix,

    /// <summary>Step-by-step instructions (BIOS, hardware, settings the app should not touch).</summary>
    Manual,

    /// <summary>Good to know, nothing to change right now.</summary>
    Info,

    /// <summary>Link to another page of the app.</summary>
    Navigate,

    /// <summary>Check passed — shown in the "already fine" list.</summary>
    Good,
}

public enum RecommendationImpact
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum RecommendationCategory
{
    Hardware,
    Windows,
    Games,
    Maintenance,
}

/// <summary>Badge / accent colour used by the view.</summary>
public enum Tone
{
    Neutral,
    Good,
    Info,
    Warning,
    Danger,
}

public sealed record StepItem(int Number, string Text);

/// <summary>
/// One piece of advice produced by <see cref="RecommendationEngine"/>. Immutable data + a little UI state.
/// </summary>
public sealed class Recommendation : ObservableObject
{
    private bool _isExpanded;
    private bool _canRevert;
    private string? _resultText;
    private Tone _resultTone = Tone.Good;

    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>2-3 plain sentences: why it matters and what to expect.</summary>
    public string Explanation { get; init; } = "";

    public RecommendationCategory Category { get; init; }
    public RecommendationImpact Impact { get; init; } = RecommendationImpact.Low;
    public IssueSeverity Severity { get; init; } = IssueSeverity.Info;
    public RecommendationKind Kind { get; init; } = RecommendationKind.Info;

    /// <summary>What is configured now, e.g. "60 Гц при 1920×1080".</summary>
    public string? CurrentValue { get; init; }

    /// <summary>Short gain for the dashboard, e.g. "Плавнее изображение".</summary>
    public string? Gain { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>ms-settings: URI, control panel applet or web page to open.</summary>
    public string? SettingsUri { get; init; }

    public string? SettingsLabel { get; init; }

    /// <summary>Page key to open (e.g. "Startup").</summary>
    public string? NavigateKey { get; init; }

    public string? NavigateLabel { get; init; }

    /// <summary>Tweak id for Apply/Revert (see <see cref="TweakIds"/>).</summary>
    public string? FixId { get; init; }

    /// <summary>Tweak parameter, e.g. the power plan GUID to activate.</summary>
    public string? FixArgument { get; init; }

    /// <summary>Human-readable list of exact changes (shown in the confirmation).</summary>
    public string? ApplySummary { get; init; }

    /// <summary>Note shown after applying, e.g. "Нужна перезагрузка".</summary>
    public string? AfterApplyNote { get; init; }

    public bool NeedsAdmin { get; init; }
    public bool NeedsRestart { get; init; }

    /// <summary>Low-risk and reversible: included in "Применить все рекомендуемые" and dashboard auto-fix.</summary>
    public bool IsSafe { get; init; }

    // ----- UI state -----

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                OnPropertyChanged(nameof(ShowSteps));
        }
    }

    /// <summary>The "Как сделать" instructions are open.</summary>
    public bool ShowSteps => IsExpanded && HasSteps;

    /// <summary>The app changed this setting earlier and remembers the previous value.</summary>
    public bool CanRevert
    {
        get => _canRevert;
        set
        {
            if (Set(ref _canRevert, value))
                OnPropertyChanged(nameof(ShowApply));
        }
    }

    public string? ResultText
    {
        get => _resultText;
        set => Set(ref _resultText, value);
    }

    public Tone ResultTone
    {
        get => _resultTone;
        set => Set(ref _resultTone, value);
    }

    public bool IsAutoFix => Kind == RecommendationKind.AutoFix;
    public bool ShowApply => Kind == RecommendationKind.AutoFix;
    public bool HasSteps => Steps.Count > 0;
    public bool HasSettings => !string.IsNullOrEmpty(SettingsUri);
    public bool HasNavigate => !string.IsNullOrEmpty(NavigateKey);
    public bool HasCurrentValue => !string.IsNullOrWhiteSpace(CurrentValue);

    public IReadOnlyList<StepItem> StepItems => Steps.Select((s, i) => new StepItem(i + 1, s)).ToList();

    public string CategoryText => Category switch
    {
        RecommendationCategory.Hardware => "Железо",
        RecommendationCategory.Windows => "Windows",
        RecommendationCategory.Games => "Игры",
        RecommendationCategory.Maintenance => "Обслуживание",
        _ => "",
    };

    public string ImpactText => Kind == RecommendationKind.Info
        ? "Для сведения"
        : Impact switch
        {
            RecommendationImpact.High => "Высокий эффект",
            RecommendationImpact.Medium => "Средний эффект",
            _ => "Низкий эффект",
        };

    public Tone ImpactTone => Kind == RecommendationKind.Info
        ? Tone.Neutral
        : Impact switch
        {
            RecommendationImpact.High => Tone.Danger,
            RecommendationImpact.Medium => Tone.Warning,
            _ => Tone.Info,
        };

    public string KindText => Kind switch
    {
        RecommendationKind.AutoFix => NeedsAdmin ? "Автоматически (нужны права администратора)" : "Можно применить автоматически",
        RecommendationKind.Manual => "Вручную по инструкции",
        RecommendationKind.Navigate => "В другом разделе",
        RecommendationKind.Good => "В порядке",
        _ => "Для сведения",
    };

    public string CategoryIcon => Category switch
    {
        RecommendationCategory.Hardware => "\uE950",
        RecommendationCategory.Games => "\uE7FC",
        RecommendationCategory.Maintenance => "\uE912",
        _ => "\uE977",
    };

    public override string ToString() => $"{Id}: {Title}";
}

/// <summary>Ids of the reversible tweaks the module can apply.</summary>
public static class TweakIds
{
    public const string PowerPlan = "PowerPlan";
    public const string GameMode = "GameMode";
    public const string GameDvr = "GameDvr";
    public const string Hags = "Hags";
    public const string VisualEffects = "VisualFx";
    public const string Transparency = "Transparency";

    public static readonly IReadOnlyList<string> All = new[] { PowerPlan, GameMode, GameDvr, Hags, VisualEffects, Transparency };
}

/// <summary>Result of applying or reverting a tweak.</summary>
public sealed record TweakResult(bool Success, string Message, bool Changed = false);
