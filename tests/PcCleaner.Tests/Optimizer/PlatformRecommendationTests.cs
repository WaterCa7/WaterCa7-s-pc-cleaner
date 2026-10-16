using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>Memory part numbers, XMP/EXPO detection and the firmware / sensor / battery rules (synthetic snapshots only).</summary>
public class PlatformRecommendationTests
{
    private static readonly string[] MemorySpeedIds = { "RamSpeed", "RamBaseSpeed" };

    private static List<Recommendation> MemorySpeedItems(IEnumerable<Recommendation> items)
        => items.Where(r => MemorySpeedIds.Contains(r.Id)).ToList();

    private static HardwareSnapshot WithSticks(HardwareSnapshot hw, int rated, int configured, string part, int type = 26)
        => hw with
        {
            RamModules = new[]
            {
                Stick(rated: rated, configured: configured, locator: "ChannelA-DIMM1", part: part, type: type),
                Stick(rated: rated, configured: configured, locator: "ChannelB-DIMM1", part: part, type: type),
            },
        };

    // ===================== part numbers =====================

    [Theory]
    // Kingston
    [InlineData("HX432C16FB3/8", 3200, "DDR4")]
    [InlineData("KHX3200C16D4/16GX", 3200, "DDR4")]
    [InlineData("KF436C17BB/8", 3600, "DDR4")]
    [InlineData("KF560C40BBK2-32", 6000, "DDR5")]
    [InlineData("KVR26N19S8/8", 2666, "DDR4")]
    [InlineData("HX318C10FB/8", 1866, "DDR3")]
    // Corsair
    [InlineData("CMK16GX4M2B3200C16", 3200, "DDR4")]
    [InlineData("CMSX16GX4M2A2666C18", 2666, "DDR4")]
    [InlineData("CMK32GX5M2B5600C36", 5600, "DDR5")]
    // G.Skill
    [InlineData("F4-3600C16D-16GTZNC", 3600, "DDR4")]
    [InlineData("F5-6000J3038F16GX2-TZ5RK", 6000, "DDR5")]
    // Crucial / Ballistix
    [InlineData("CT8G4DFS832A.C8FBR2", 3200, "DDR4")]
    [InlineData("CT16G4DFRA266", 2666, "DDR4")]
    [InlineData("CT2K8G4DFS824A", 2400, "DDR4")]
    [InlineData("CT16G48C40U5.M8A1", 4800, "DDR5")]
    [InlineData("BL8G32C16U4B.M8FE1", 3200, "DDR4")]
    [InlineData("BLS8G4D240FSB", 2400, "DDR4")]
    // OEM modules (JEDEC speed)
    [InlineData("M378A1K43CB2-CTD", 2666, "DDR4")]
    [InlineData("HMA81GU6CJR8N-XN", 3200, "DDR4")]
    [InlineData("MTA8ATF1G64AZ-3G2E1", 3200, "DDR4")]
    // Others
    [InlineData("TF3D416G3200HC16FDC01", 3200, "DDR4")]
    [InlineData("AX4U320038G16A-ST10", 3200, "DDR4")]
    [InlineData("PVS416G320C6K", 3200, "DDR4")]
    public void PartNumbers_TellTheRatedSpeed(string part, int speed, string type)
    {
        var decoded = HardwareText.DecodeMemoryPart(part);
        Assert.NotNull(decoded);
        Assert.Equal(speed, decoded!.Value.Speed);
        Assert.Equal(type, decoded.Value.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("99U5701-011.A00G")]
    [InlineData("Undefined")]
    [InlineData("0x80AD")]
    public void UnknownPartNumbers_AreNotGuessed(string? part)
        => Assert.Null(HardwareText.RatedSpeedFromPartNumber(part));

    // ===================== XMP / EXPO (JEDEC speed) =====================

    [Fact]
    public void Ddr4AtJedecSpeed_WithFasterPartNumber_IsXmpOff()
    {
        // Windows reports the SPD JEDEC speed (2133) for both values; only the part number says the kit is DDR4-3200.
        var hw = WithSticks(GoodDesktop(), rated: 2133, configured: 2133, part: "F4-3200C16D-16GVKB");
        var items = Evaluate(hw, GoodSettings());

        var rec = Assert.Single(MemorySpeedItems(items));
        Assert.Equal("RamSpeed", rec.Id);
        Assert.Equal("Память работает без XMP/EXPO", rec.Title);
        Assert.Equal(RecommendationKind.Manual, rec.Kind);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
        Assert.Equal(IssueSeverity.High, rec.Severity);
        Assert.Equal("2133 МГц из 3200 МГц", rec.CurrentValue);
        Assert.Contains("F4-3200C16D-16GVKB", rec.Explanation);
        Assert.Contains(rec.Steps, s => s.Contains("BIOS", StringComparison.Ordinal));
        Assert.Null(items.Find("Ok:RamSpeed"));
    }

    [Theory]
    [InlineData("HX432C16FB3/8")]
    [InlineData("KHX3200C16D4/16GX")]
    [InlineData("CMK16GX4M2B3200C16")]
    [InlineData("F4-3200C16D-16GIS")]
    [InlineData("BL8G32C16U4B.M8FE1")]
    public void KingstonCorsairGskillCrucialKits_At2400_AreXmpOff(string part)
    {
        var hw = WithSticks(GoodDesktop(), rated: 2400, configured: 2400, part: part);
        var rec = Assert.Single(MemorySpeedItems(Evaluate(hw, GoodSettings())));
        Assert.Equal("Память работает без XMP/EXPO", rec.Title);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
    }

    [Fact]
    public void Ddr5At4800_RatedFor6000_IsXmpOff()
    {
        var hw = WithSticks(GoodDesktop(), rated: 6000, configured: 4800, part: "Unknown", type: 34);
        var rec = Assert.Single(MemorySpeedItems(Evaluate(hw, GoodSettings())));
        Assert.Equal("Память работает без XMP/EXPO", rec.Title);
        Assert.Equal("4800 МГц из 6000 МГц", rec.CurrentValue);
        Assert.Contains("DDR5", rec.Explanation);
    }

    [Fact]
    public void Ddr5At4800_WithExpoKitPartNumber_IsXmpOff()
    {
        var hw = WithSticks(GoodDesktop(), rated: 4800, configured: 4800, part: "KF560C40BBK2-32", type: 34);
        var rec = Assert.Single(MemorySpeedItems(Evaluate(hw, GoodSettings())));
        Assert.Equal("Память работает без XMP/EXPO", rec.Title);
        Assert.Equal("4800 МГц из 6000 МГц", rec.CurrentValue);
    }

    [Theory]
    [InlineData("KVR26N19S8/8")] // ValueRAM DDR4-2666
    [InlineData("M378A1K43CB2-CTD")] // Samsung OEM DDR4-2666
    [InlineData("99U5701-011.A00G")] // unknown — Windows says 2666 is the rating
    public void Ddr4Rated2666_Running2666_IsFine(string part)
    {
        var hw = WithSticks(GoodDesktop(), rated: 2666, configured: 2666, part: part);
        var items = Evaluate(hw, GoodSettings());
        Assert.Empty(MemorySpeedItems(items));
        Assert.NotNull(items.Find("Ok:RamSpeed"));
    }

    [Fact]
    public void JedecModuleWithKnownPartNumber_DoesNotAskToCheckTheSticker()
    {
        // DDR4-2400 OEM module at 2400: the part number proves there is no faster profile.
        var hw = WithSticks(GoodDesktop(), rated: 2400, configured: 2400, part: "HMA81GU6CJR8N-UH");
        var items = Evaluate(hw, GoodSettings());
        Assert.Empty(MemorySpeedItems(items));
        Assert.NotNull(items.Find("Ok:RamSpeed"));
    }

    [Fact]
    public void SlowNonJedecSpeed_IsTheGenericBelowRatedAdvice()
    {
        var hw = WithSticks(GoodDesktop(), rated: 3600, configured: 2933, part: "F4-3600C16D-16GTZNC");
        var rec = Assert.Single(MemorySpeedItems(Evaluate(hw, GoodSettings())));
        Assert.Equal("RamSpeed", rec.Id);
        Assert.Contains("2933", rec.Title);
        Assert.Contains("3600", rec.Title);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
    }

    [Fact]
    public void LaptopAtJedecSpeed_IsOnlyInfo()
    {
        var hw = WithSticks(GoodDesktop() with { IsLaptop = true }, rated: 2666, configured: 2666, part: "KF432S20IB/8");
        var rec = Assert.Single(MemorySpeedItems(Evaluate(hw, GoodSettings())));
        Assert.Equal(RecommendationKind.Info, rec.Kind);
        Assert.Equal(IssueSeverity.Info, rec.Severity);
        Assert.False(RecommendationEngine.IsXmpOff(hw));
    }

    [Theory]
    [InlineData(2133, 2133, "F4-3200C16D-16GVKB")]
    [InlineData(3200, 2666, "KF432C16BB/8")]
    [InlineData(2133, 2133, "99U5701-011.A00G")]
    [InlineData(3200, 2933, "KF432C16BB/8")]
    [InlineData(3600, 2933, "F4-3600C16D-16GTZNC")]
    [InlineData(3200, 1600, "KF432C16BB/8")]
    public void ThereIsNeverMoreThanOneMemorySpeedRecommendation(int rated, int configured, string part)
    {
        var items = Evaluate(WithSticks(GoodDesktop(), rated, configured, part), GoodSettings());
        Assert.True(MemorySpeedItems(items).Count + items.Count(r => r.Id == "Ok:RamSpeed") == 1);
    }

    [Fact]
    public void MemoryCard_ShowsDdrType_AndXmpBadge()
    {
        var hw = WithSticks(GoodDesktop(), rated: 2133, configured: 2133, part: "CMK16GX4M2B3200C16", type: 0);
        var items = Evaluate(hw, GoodSettings());
        var card = HardwareCardBuilder.Build(hw, GoodSettings(), items, Now).Single(c => c.Caption == "Оперативная память");
        Assert.Equal("16 ГБ DDR4", card.Title); // SMBIOS type unknown: taken from the part number
        Assert.Equal("2133 МГц из 3200 МГц", card.Primary);
        Assert.Equal("Без XMP/EXPO", card.Badge);
        Assert.Equal(Tone.Warning, card.BadgeTone);
    }

    // ===================== boot mode =====================

    [Theory]
    [InlineData(1, BootMode.LegacyBios)]
    [InlineData(2, BootMode.Uefi)]
    [InlineData(0, BootMode.Unknown)]
    [InlineData(3, BootMode.Unknown)]
    [InlineData(null, BootMode.Unknown)]
    public void FirmwareType_IsMappedToBootMode(int? value, BootMode expected)
        => Assert.Equal(expected, HardwareText.BootModeFrom(value));

    [Fact]
    public void LegacyBios_IsMediumManual_WithMbr2GptSteps()
    {
        var rec = Evaluate(GoodDesktop() with { BootMode = BootMode.LegacyBios }, GoodSettings()).Find("BootMode");
        Assert.NotNull(rec);
        Assert.Equal("Компьютер загружается в режиме Legacy BIOS", rec!.Title);
        Assert.Equal(RecommendationKind.Manual, rec.Kind);
        Assert.Equal(RecommendationImpact.Medium, rec.Impact);
        Assert.Null(rec.FixId); // never automatic
        Assert.Contains(rec.Steps, s => s.Contains("mbr2gpt /validate /allowFullOS", StringComparison.Ordinal));
        Assert.Contains(rec.Steps, s => s.Contains("mbr2gpt /convert /allowFullOS", StringComparison.Ordinal));
        Assert.Contains("рисков", rec.Steps[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows 11", rec.Explanation);
        Assert.StartsWith("https://", rec.SettingsUri);
    }

    [Fact]
    public void Uefi_IsAPassedCheck_UnknownGivesNothing()
    {
        Assert.NotNull(Evaluate(GoodDesktop() with { BootMode = BootMode.Uefi }, GoodSettings()).Find("Ok:BootMode"));
        var unknown = Evaluate(GoodDesktop(), GoodSettings());
        Assert.DoesNotContain(unknown, r => r.Id.EndsWith("BootMode", StringComparison.Ordinal));
    }

    // ===================== virtualization =====================

    [Fact]
    public void RunningHypervisor_MeansVirtualizationIsOn_EvenIfFirmwareFlagSaysNo()
    {
        var hw = GoodDesktop() with { VirtualizationFirmwareEnabled = false, HypervisorPresent = true };
        Assert.True(hw.VirtualizationEnabled);
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("Virtualization"));
        Assert.NotNull(items.Find("Ok:Virtualization"));
    }

    [Fact]
    public void VirtualizationOff_IsInfoWithBiosSteps()
    {
        var hw = GoodDesktop() with { VirtualizationFirmwareEnabled = false, HypervisorPresent = false };
        var rec = Evaluate(hw, GoodSettings()).Find("Virtualization");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Info, rec!.Kind);
        Assert.Equal(IssueSeverity.Info, rec.Severity);
        Assert.Contains("SVM", rec.Explanation); // AMD CPU in the test PC
        Assert.Contains("WSL", rec.Explanation);
        Assert.NotEmpty(rec.Steps);
    }

    [Fact]
    public void UnknownVirtualization_GivesNoAdvice()
        => Assert.DoesNotContain(Evaluate(GoodDesktop(), GoodSettings()), r => r.Id.Contains("Virtualization", StringComparison.Ordinal));

    // ===================== GPU driver =====================

    [Theory]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Базовый видеоадаптер (Майкрософт)")]
    public void BasicDisplayAdapter_IsHighWithVendorLink(string name)
    {
        var gpu = new GpuInfo { Name = name, PnpDeviceId = @"PCI\VEN_10DE&DEV_2504&SUBSYS_00001458&REV_A1\4&1" };
        var rec = Evaluate(GoodDesktop() with { Gpus = new[] { gpu } }, GoodSettings())
            .Single(r => r.Id.StartsWith("GpuNoDriver:", StringComparison.Ordinal));
        Assert.Equal("Не установлен драйвер видеокарты", rec.Title);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
        Assert.Equal(GpuClassifier.NvidiaDriverUrl, rec.SettingsUri);
    }

    // ===================== temperature =====================

    [Theory]
    [InlineData(3032L, 30.05)]
    [InlineData(3582L, 85.05)]
    [InlineData(2982L, 25.05)]
    public void ThermalZoneTenthsOfKelvin_AreConvertedToCelsius(long raw, double celsius)
        => Assert.InRange(HardwareText.KelvinTenthsToCelsius(raw)!.Value, celsius - 0.06, celsius + 0.06);

    [Theory]
    [InlineData(0L)]
    [InlineData(2732L)] // 0 °C — a placeholder, not a reading
    [InlineData(4500L)] // 177 °C — nonsense
    [InlineData(null)]
    public void ImpossibleThermalValues_AreIgnored(long? raw)
        => Assert.Null(HardwareText.KelvinTenthsToCelsius(raw));

    [Fact]
    public void HighTemperature_IsHighManual()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { TemperatureC = 91, TemperatureState = SensorState.Available })
            .Find("Temperature");
        Assert.NotNull(rec);
        Assert.Equal("Высокая температура", rec!.Title);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
        Assert.Equal("91 °C", rec.CurrentValue);
        Assert.Contains(rec.Steps, s => s.Contains("пыл", StringComparison.Ordinal));
        Assert.Contains(rec.Steps, s => s.Contains("термопаст", StringComparison.Ordinal));
        Assert.Contains("троттлинг", rec.Explanation);
    }

    [Fact]
    public void NormalTemperature_IsFine_MissingSensorSaysNothing()
    {
        Assert.NotNull(Evaluate(GoodDesktop(), GoodSettings() with { TemperatureC = 48, TemperatureState = SensorState.Available })
            .Find("Ok:Temperature"));
        Assert.DoesNotContain(Evaluate(GoodDesktop(), GoodSettings() with { TemperatureState = SensorState.NeedsAdmin }),
            r => r.Id.Contains("Temperature", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuCard_ExplainsWhyTemperatureIsMissing()
    {
        var s = GoodSettings() with { TemperatureState = SensorState.NeedsAdmin };
        var card = HardwareCardBuilder.Build(GoodDesktop(), s, Evaluate(GoodDesktop(), s), Now).Single(c => c.Caption == "Процессор");
        Assert.Contains("Температура: нет данных", card.Details);
        Assert.Contains("администратора", card.ToolTipText);

        var hot = GoodSettings() with { TemperatureC = 90, TemperatureState = SensorState.Available };
        var hotCard = HardwareCardBuilder.Build(GoodDesktop(), hot, Evaluate(GoodDesktop(), hot), Now).Single(c => c.Caption == "Процессор");
        Assert.Contains("Температура: 90 °C", hotCard.Details);
        Assert.Equal(Tone.Danger, hotCard.BadgeTone);
    }

    // ===================== battery =====================

    [Theory]
    [InlineData(50000L, 30000L, 40.0)]
    [InlineData(50000L, 50000L, 0.0)]
    [InlineData(50000L, 52000L, 0.0)] // new battery above its design capacity
    [InlineData(57000L, 39900L, 30.0)]
    public void BatteryWear_IsLostCapacity(long designed, long full, double wear)
        => Assert.Equal(wear, RecommendationEngine.BatteryWearPercent(designed, full));

    [Theory]
    [InlineData(0L, 30000L)]
    [InlineData(50000L, 0L)]
    public void BatteryWear_UnknownCapacity_IsNull(long designed, long full)
        => Assert.Null(RecommendationEngine.BatteryWearPercent(designed, full));

    private static HardwareSnapshot Laptop(long designed, long full) => GoodDesktop() with
    {
        IsLaptop = true,
        HasBattery = true,
        Battery = new BatteryInfo
        {
            Name = "DELL 7FHHV7C", DesignedCapacityMwh = designed, FullChargedCapacityMwh = full, CapacityState = SensorState.Available,
        },
    };

    [Theory]
    [InlineData(28000L, "Battery", RecommendationImpact.Medium, "Батарею стоит заменить")] // 44% wear
    [InlineData(33000L, "Battery", RecommendationImpact.Low, "Батарея изношена на 34%")] // 34%
    [InlineData(45000L, "Ok:Battery", RecommendationImpact.Low, "Батарея в хорошем состоянии")] // 10%
    public void BatteryWearRule(long full, string id, RecommendationImpact impact, string title)
    {
        var rec = Evaluate(Laptop(50000, full), GoodSettings()).Find(id);
        Assert.NotNull(rec);
        Assert.Equal(title, rec!.Title);
        Assert.Equal(impact, rec.Impact);
        if (id == "Battery")
        {
            Assert.Equal(RecommendationKind.Manual, rec.Kind);
            Assert.Equal(RecommendationEngine.BatteryReportUri, rec.SettingsUri);
        }
    }

    [Fact]
    public void Battery_OnlyOnLaptops_AndOnlyWithKnownCapacity()
    {
        // A desktop with a UPS reports Win32_Battery too: no battery advice there.
        var ups = Laptop(50000, 20000) with { IsLaptop = false };
        Assert.DoesNotContain(Evaluate(ups, GoodSettings()), r => r.Id.Contains("Battery", StringComparison.Ordinal));
        Assert.DoesNotContain(HardwareCardBuilder.Build(ups, GoodSettings(), Evaluate(ups, GoodSettings()), Now), c => c.Caption == "Батарея");

        var unknown = Laptop(0, 0) with { Battery = new BatteryInfo { CapacityState = SensorState.NeedsAdmin } };
        Assert.DoesNotContain(Evaluate(unknown, GoodSettings()), r => r.Id.Contains("Battery", StringComparison.Ordinal));
    }

    [Fact]
    public void BatteryCard_ShowsCharge_Wear_AndTheReportAction()
    {
        var hw = Laptop(50000, 28000);
        var s = GoodSettings() with { BatteryChargePercent = 76, BatteryState = BatteryChargeState.Discharging, OnAcPower = false };
        var card = HardwareCardBuilder.Build(hw, s, Evaluate(hw, s), Now).Single(c => c.Caption == "Батарея");
        Assert.Equal("Заряд 76%", card.Title);
        Assert.Equal("Работает от батареи", card.Primary);
        Assert.Contains("Износ: 44%", card.Details);
        Assert.Equal(Tone.Warning, card.BadgeTone);
        Assert.True(card.HasAction);
        Assert.Equal(HardwareCardActions.BatteryReport, card.ActionId);

        var noAdmin = Laptop(0, 0) with { Battery = new BatteryInfo { CapacityState = SensorState.NeedsAdmin } };
        var noAdminCard = HardwareCardBuilder.Build(noAdmin, s, Evaluate(noAdmin, s), Now).Single(c => c.Caption == "Батарея");
        Assert.Contains("Износ: нет данных", noAdminCard.Details);
        Assert.Contains("администратора", noAdminCard.ToolTipText);
    }

    // ===================== fast startup =====================

    [Fact]
    public void FastStartup_IsInfoWithManualSteps_NoAutoFix()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { FastStartupValue = 1 }).Find("FastStartup");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Info, rec!.Kind);
        Assert.Null(rec.FixId);
        Assert.NotEmpty(rec.Steps);
        Assert.Contains("не полностью", rec.Explanation);
        Assert.Contains("вторая система", rec.Explanation);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, null)]
    [InlineData(1, 0)] // hibernation off: fast startup cannot work
    public void FastStartupOff_GivesNothing(int? hiberboot, int? hibernate)
        => Assert.Null(Evaluate(GoodDesktop(), GoodSettings() with { FastStartupValue = hiberboot, HibernateValue = hibernate })
            .Find("FastStartup"));

    // ===================== page file pressure =====================

    [Fact]
    public void BusyPagefile_IsLow_AndReplacesThePassedCheck()
    {
        var s = GoodSettings() with { PagefileUsedMb = 1700, PagefileAllocatedMb = 3072, PagefilePath = @"C:\pagefile.sys" };
        Assert.Equal(55.3, s.PagefileUsagePercent!.Value, 1);
        var items = Evaluate(GoodDesktop(), s);
        var rec = items.Find("PagefileUsage");
        Assert.NotNull(rec);
        Assert.Equal("Активно используется файл подкачки", rec!.Title);
        Assert.Equal(RecommendationImpact.Low, rec.Impact);
        Assert.Equal("Processes", rec.NavigateKey);
        Assert.Contains(rec.Steps, x => x.Contains("SSD", StringComparison.Ordinal));
        Assert.Null(items.Find("Ok:Pagefile"));
    }

    [Fact]
    public void PagefileOnHdd_IsMentioned()
    {
        var hw = GoodDesktop() with { SystemDisk = new SystemDiskInfo { DriveLetter = "C", MediaKind = DiskMediaKind.Hdd } };
        var s = GoodSettings() with { PagefileUsedMb = 2500, PagefileAllocatedMb = 3072, PagefilePath = @"c:\pagefile.sys" };
        Assert.Contains("HDD", Evaluate(hw, s).Find("PagefileUsage")!.Explanation);
    }

    [Fact]
    public void QuietPagefile_IsFine()
    {
        var items = Evaluate(GoodDesktop(), GoodSettings() with { PagefileUsedMb = 62, PagefileAllocatedMb = 3072 });
        Assert.Null(items.Find("PagefileUsage"));
        Assert.NotNull(items.Find("Ok:Pagefile"));
    }

    // ===================== CPU load =====================

    [Fact]
    public void CpuLoadAbove90_IsMedium_AndOpensProcesses()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { CpuLoadPercent = 96.4 }).Find("CpuLoad");
        Assert.NotNull(rec);
        Assert.Equal("Процессор загружен более чем на 90%", rec!.Title);
        Assert.Equal(RecommendationImpact.Medium, rec.Impact);
        Assert.Equal("Processes", rec.NavigateKey);
        Assert.True(rec.HasNavigate);
        Assert.Equal("Загрузка 96%", rec.CurrentValue);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(35.0)]
    [InlineData(null)]
    public void NormalCpuLoad_GivesNothing(double? load)
        => Assert.Null(Evaluate(GoodDesktop(), GoodSettings() with { CpuLoadPercent = load }).Find("CpuLoad"));

    [Fact]
    public void CpuLoad_ExcludesThisAppsOwnWork()
    {
        // 8 logical CPUs × 2 s = 160 000 000 ticks; 10% idle; this app used 30% of the machine.
        const ulong total = 160_000_000;
        Assert.Equal(90, HardwareText.CpuLoadPercent(total / 10, total, 0)!.Value, 3);
        Assert.Equal(60, HardwareText.CpuLoadPercent(total / 10, total, 0, ownBusy: (long)(total * 3 / 10))!.Value, 3);
        Assert.Null(HardwareText.CpuLoadPercent(0, 0, 0));
    }

    // ===================== scope / dashboard =====================

    [Fact]
    public void NoMemoryIntegrityAdvice_TheSecurityPageOwnsIt()
    {
        var hw = GoodDesktop() with { VirtualizationFirmwareEnabled = false, HypervisorPresent = true, BootMode = BootMode.LegacyBios };
        var items = Evaluate(hw, GoodSettings() with { FastStartupValue = 1 });
        Assert.DoesNotContain(items, r =>
            r.Title.Contains("целостност", StringComparison.OrdinalIgnoreCase) ||
            r.Explanation.Contains("целостност", StringComparison.OrdinalIgnoreCase) ||
            r.Title.Contains("VBS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1006, 8, 4)] // desktop at the default window size: 4 × 2
    [InlineData(1006, 9, 3)] // laptop (battery card): 3 × 3, no lonely card
    [InlineData(730, 8, 3)] // minimum window size
    [InlineData(730, 9, 3)]
    [InlineData(1006, 10, 4)]
    [InlineData(0, 9, 1)]
    public void HardwareGrid_UsesBalancedColumns(double width, int count, int columns)
        => Assert.Equal(columns, BalancedColumnsConverter.Columns(width, count, 225));
}
