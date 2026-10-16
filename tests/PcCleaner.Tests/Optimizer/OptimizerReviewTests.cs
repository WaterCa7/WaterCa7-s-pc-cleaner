using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>Regression tests for the phase-2 review fixes (synthetic data only, nothing on the system is read or changed).</summary>
public class OptimizerReviewTests
{
    public OptimizerReviewTests() => TestEnvironment.Init();

    private static HardwareSnapshot WithSticks(HardwareSnapshot hw, int rated, int configured, string part, int type = 26)
        => hw with
        {
            RamModules = new[]
            {
                Stick(rated: rated, configured: configured, locator: "ChannelA-DIMM1", part: part, type: type),
                Stick(rated: rated, configured: configured, locator: "ChannelB-DIMM1", part: part, type: type),
            },
        };

    // ===================== standard (JEDEC-only) memory =====================

    [Theory]
    [InlineData("M378A1K43CB2-CTD", true)] // Samsung OEM
    [InlineData("M471A1K43DB1-CWE", true)]
    [InlineData("HMA81GU6CJR8N-XN", true)] // SK Hynix
    [InlineData("HMCG78AEBUA081N-BA", true)] // SK Hynix DDR5
    [InlineData("MTA8ATF1G64AZ-3G2E1", true)] // Micron
    [InlineData("KVR32N22S8/8", true)] // Kingston ValueRAM
    [InlineData("KCP426NS8/8", true)] // Kingston Client Premier
    [InlineData("CT8G4DFS832A.C8FBR2", true)] // Crucial basic
    [InlineData("CT2K8G4DFS824A", true)]
    [InlineData("HX432C16FB3/8", false)] // HyperX: XMP kit
    [InlineData("KF436C17BB/8", false)] // FURY
    [InlineData("F4-3600C16D-16GTZNC", false)] // G.Skill
    [InlineData("CMK16GX4M2B3200C16", false)] // Corsair
    [InlineData("BL8G32C16U4B.M8FE1", false)] // Ballistix
    [InlineData("CP16G60C36U5B", false)] // Crucial Pro DDR5 (EXPO/XMP)
    [InlineData("99U5701-011.A00G", false)]
    [InlineData(null, false)]
    public void StandardModules_AreRecognised(string? part, bool jedecOnly)
        => Assert.Equal(jedecOnly, HardwareText.IsJedecOnlyPart(part));

    [Theory]
    [InlineData("M378A1K43DB2-CWE")] // Samsung DDR4-3200 OEM
    [InlineData("HMA81GU6DJR8N-XN")] // SK Hynix DDR4-3200 OEM
    [InlineData("KVR32N22S8/8")] // Kingston ValueRAM DDR4-3200
    public void StandardModulesHeldBackByTheProcessor_AreNotCalledXmpOff(string part)
    {
        // A typical office PC: Core i5-10400 (max DDR4-2666) with standard DDR4-3200 modules that have no XMP profile.
        var office = GoodDesktop() with
        {
            Cpu = new CpuInfo { Name = "Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz", Cores = 6, LogicalProcessors = 12 },
        };
        var hw = WithSticks(office, rated: 3200, configured: 2666, part: part);
        var items = Evaluate(hw, GoodSettings());

        var rec = Assert.Single(items, r => r.Id is "RamSpeed" or "RamBaseSpeed" or "Ok:RamSpeed");
        Assert.Equal("RamSpeed", rec.Id);
        Assert.Equal(RecommendationKind.Info, rec.Kind);
        Assert.Equal(IssueSeverity.Info, rec.Severity);
        Assert.DoesNotContain("XMP/EXPO", rec.Title);
        Assert.Contains("без профиля XMP/EXPO", rec.Explanation);
        Assert.Equal("2666 МГц из 3200 МГц", rec.CurrentValue);
        Assert.False(RecommendationEngine.IsXmpOff(hw));

        var card = HardwareCardBuilder.Build(hw, GoodSettings(), items, Now).Single(c => c.Caption == "Оперативная память");
        Assert.Equal("Ниже паспортной частоты", card.Badge);
        Assert.Equal(Tone.Info, card.BadgeTone);
    }

    [Fact]
    public void StandardDdr5At4800_DoesNotAskToCheckTheSticker()
    {
        // SK Hynix DDR5 OEM: the speed is not encoded in a known way, but it is a standard module — nothing to switch on.
        var hw = WithSticks(GoodDesktop(), rated: 4800, configured: 4800, part: "HMCG78AEBUA081N-BA", type: 34);
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("RamBaseSpeed"));
        Assert.NotNull(items.Find("Ok:RamSpeed"));
    }

    [Fact]
    public void XmpKitAtJedecSpeed_IsStillXmpOff()
    {
        var hw = WithSticks(GoodDesktop(), rated: 2133, configured: 2133, part: "HX432C16FB3/8");
        var rec = Evaluate(hw, GoodSettings()).Find("RamSpeed");
        Assert.Equal("Память работает без XMP/EXPO", rec!.Title);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
        Assert.True(RecommendationEngine.IsXmpOff(hw));
    }

    [Fact]
    public void MixedStandardAndXmpModules_KeepTheXmpAdvice()
    {
        var hw = GoodDesktop() with
        {
            RamModules = new[]
            {
                Stick(rated: 2133, configured: 2133, locator: "ChannelA-DIMM1", part: "HX432C16FB3/8"),
                Stick(rated: 2133, configured: 2133, locator: "ChannelB-DIMM1", part: "M378A1K43DB2-CWE"),
            },
        };
        Assert.Equal(RecommendationKind.Manual, Evaluate(hw, GoodSettings()).Find("RamSpeed")!.Kind);
    }

    // ===================== virtualization =====================

    [Fact]
    public void ProcessorWithoutVirtualization_GetsNoBiosAdvice()
    {
        var hw = GoodDesktop() with { VirtualizationFirmwareEnabled = false, HypervisorPresent = false, VirtualizationSupported = false };
        var items = Evaluate(hw, GoodSettings());
        Assert.DoesNotContain(items, r => r.Id.Contains("Virtualization", StringComparison.Ordinal));

        var card = HardwareCardBuilder.Build(hw, GoodSettings(), items, Now).Single(c => c.Caption == "Прошивка и платформа");
        Assert.EndsWith("без виртуализации", card.Primary);
        Assert.Contains("не поддерживает", card.ToolTipText);
    }

    [Fact]
    public void VirtualizationSwitchedOffInBios_IsStillExplained()
    {
        var hw = GoodDesktop() with { VirtualizationFirmwareEnabled = false, HypervisorPresent = false, VirtualizationSupported = true };
        Assert.NotNull(Evaluate(hw, GoodSettings()).Find("Virtualization"));
    }

    [Fact]
    public void RunningHypervisor_IsNotMistakenForAnUnsupportedProcessor()
    {
        // While Hyper-V runs, Windows reports both VirtualizationFirmwareEnabled and VMMonitorModeExtensions as false.
        var hw = GoodDesktop() with { VirtualizationFirmwareEnabled = false, HypervisorPresent = true, VirtualizationSupported = false };
        Assert.False(hw.VirtualizationUnsupported);
        Assert.NotNull(Evaluate(hw, GoodSettings()).Find("Ok:Virtualization"));
    }

    // ===================== legacy BIOS =====================

    [Fact]
    public void LegacyBiosSteps_WarnAboutBitLockerBeforeConverting()
    {
        var rec = Evaluate(GoodDesktop() with { BootMode = BootMode.LegacyBios }, GoodSettings()).Find("BootMode")!;
        var bitLocker = rec.Steps.ToList().FindIndex(s => s.Contains("BitLocker", StringComparison.Ordinal));
        var convert = rec.Steps.ToList().FindIndex(s => s.Contains("mbr2gpt /convert", StringComparison.Ordinal));
        Assert.InRange(bitLocker, 0, convert - 1);
        Assert.Contains("ключ восстановления", rec.Steps[bitLocker]);
    }

    // ===================== battery capacities =====================

    [Fact]
    public void BatteryCapacities_ArePairedByInstance()
    {
        // Two batteries, but only one reported its full-charge capacity: compare only that one.
        var (designed, full) = HardwareText.BatteryCapacities(
            new (string?, long)[] { (@"ACPI\PNP0C0A\1_0", 23000), (@"ACPI\PNP0C0A\2_0", 48000) },
            new (string?, long)[] { (@"ACPI\PNP0C0A\2_0", 40000) });
        Assert.Equal(48000, designed);
        Assert.Equal(40000, full);
        Assert.Equal(17.0, RecommendationEngine.BatteryWearPercent(designed, full));

        var both = HardwareText.BatteryCapacities(
            new (string?, long)[] { ("A", 23000), ("B", 48000) },
            new (string?, long)[] { ("b", 40000), ("a", 20000) });
        Assert.Equal((71000L, 60000L), both);
    }

    [Theory]
    [InlineData(1, 1, 50000L, 40000L)] // one battery without instance names
    [InlineData(2, 1, 0L, 0L)] // lists do not match: unknown, never a false "heavy wear"
    [InlineData(0, 1, 0L, 0L)]
    public void BatteryCapacities_WithoutInstanceNames(int designedCount, int fullCount, long designed, long full)
    {
        var d = Enumerable.Repeat(((string?)null, 50000L), designedCount).ToList();
        var f = Enumerable.Repeat(((string?)null, 40000L), fullCount).ToList();
        Assert.Equal((designed, full), HardwareText.BatteryCapacities(d, f));
    }

    // ===================== CPU load: our own helper processes =====================

    [Fact]
    public void ProcessTree_IncludesChildrenAndGrandchildren_ButNotStaleParents()
    {
        var processes = new (uint Pid, uint Parent)[]
        {
            (100, 4), // this app
            (200, 100), // powershell.exe started by another module
            (210, 200), // its conhost.exe
            (300, 999), // unrelated
            (400, 100), // stale parent id: created before us, so not our child (PID reuse)
            (0, 0),
        };
        var times = new Dictionary<uint, (long Created, long Cpu)>
        {
            [100] = (1000, 50), [200] = (2000, 30), [210] = (2001, 5), [300] = (1500, 900), [400] = (500, 700),
        };
        var tree = NativeProbes.ProcessTreeCpu(100, processes, pid => times.TryGetValue(pid, out var t) ? t : null);

        Assert.Equal(new uint[] { 100, 200, 210 }, tree.Keys.Select(k => k.Pid).OrderBy(p => p).ToArray());
        Assert.Equal(85, tree.Values.Sum());
    }

    [Fact]
    public void OwnCpuTracker_CountsOnlyWhatOurTreeUsedDuringTheSample()
    {
        var start = DateTime.UtcNow.ToFileTimeUtc();
        var polls = new Queue<Dictionary<(uint Pid, long Created), long>>(new[]
        {
            // first poll: this app + a PowerShell that already ran for a while + one we missed (no reading)
            new Dictionary<(uint Pid, long Created), long> { [(100, 1)] = 1_000, [(200, 2)] = 5_000 },
            // PowerShell keeps running, a new helper appears, an old process we never saw at the start shows up
            new Dictionary<(uint Pid, long Created), long>
            {
                [(100, 1)] = 1_400, [(200, 2)] = 7_000, [(300, start + 10_000_000)] = 900, [(400, 3)] = 50_000,
            },
            // PowerShell has exited (its last reading is kept), the helper used a bit more
            new Dictionary<(uint Pid, long Created), long> { [(100, 1)] = 1_500, [(300, start + 10_000_000)] = 1_000 },
        });
        var tracker = new NativeProbes.OwnCpuTracker(() => polls.Dequeue());
        Assert.Equal(0, tracker.Used);
        tracker.Poll();
        tracker.Poll();
        tracker.Poll();

        // app 500 + PowerShell 2 000 + new helper 1 000; the process missed at the start is not subtracted
        Assert.Equal(3_500, tracker.Used);
    }

    [Fact]
    public void CpuLoad_WithoutOurHelperProcesses_IsNotReportedAsBusy()
    {
        // 4 logical CPUs × 2 s; 5% idle; our tree (app + PowerShell of other modules) used 30% of the machine.
        const ulong total = 80_000_000;
        var load = HardwareText.CpuLoadPercent(total / 20, total, 0, ownBusy: (long)(total * 3 / 10))!.Value;
        Assert.Equal(65, load, 3);
        Assert.Null(Evaluate(GoodDesktop(), GoodSettings() with { CpuLoadPercent = load }).Find("CpuLoad"));
    }

    // ===================== admin hint =====================

    [Fact]
    public void AdminHint_DoesNotPromiseASensorTheBoardMayNotHave()
    {
        var hint = OptimizerViewModel.AdminHint(false, false, GoodDesktop(), GoodSettings() with { TemperatureState = SensorState.NeedsAdmin });
        Assert.Contains("попробует", hint);
        Assert.DoesNotContain("и это", hint);
    }
}
