using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

public class RecommendationEngineTests
{
    [Fact]
    public void WellTunedDesktop_HasNoActionableRecommendations()
    {
        var items = Evaluate(GoodDesktop(), GoodSettings());
        Assert.Empty(items.Actionable());
        Assert.Contains(items, r => r.Id == "Ok:RamSpeed");
        Assert.Contains(items, r => r.Id == "Ok:RamChannels");
        Assert.Contains(items, r => r.Id == "Ok:SystemSsd");
        Assert.Contains(items, r => r.Id == "Ok:PowerPlan");
        Assert.Contains(items, r => r.Id == "Link:Startup" && r.NavigateKey == "Startup");
        Assert.Contains(items, r => r.Id == "Link:Cleaner" && r.NavigateKey == "Cleaner");
    }

    // ----- XMP / memory speed -----

    [Fact]
    public void RamBelowRatedByMoreThanTenPercent_RecommendsXmp()
    {
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(rated: 3200, configured: 2666, locator: "ChannelA-DIMM1"), Stick(rated: 3200, configured: 2666, locator: "ChannelB-DIMM1") },
        };
        var rec = Evaluate(hw, GoodSettings()).Find("RamSpeed");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Manual, rec!.Kind);
        Assert.Equal(RecommendationImpact.High, rec.Impact);
        Assert.Equal("Память работает без XMP/EXPO", rec.Title); // 2666 is a JEDEC speed for DDR4
        Assert.Contains("2666", rec.CurrentValue);
        Assert.Contains("3200", rec.CurrentValue);
        Assert.NotEmpty(rec.Steps);
    }

    [Fact]
    public void RamWithinTenPercentOfRated_DoesNotRecommendXmp()
    {
        // 3200 vs 2933 is ~9% — below the threshold, could be a CPU/board limit.
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(rated: 3200, configured: 2933, locator: "ChannelA-DIMM1"), Stick(rated: 3200, configured: 2933, locator: "ChannelB-DIMM1") },
        };
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("RamSpeed"));
        Assert.NotNull(items.Find("Ok:RamSpeed"));
    }

    [Fact]
    public void RamReportedAsHalfClock_IsNotTreatedAsSlow()
    {
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(rated: 3200, configured: 1600, locator: "ChannelA-DIMM1"), Stick(rated: 3200, configured: 1600, locator: "ChannelB-DIMM1") },
        };
        Assert.Null(Evaluate(hw, GoodSettings()).Find("RamSpeed"));
    }

    [Fact]
    public void SlowRamOnLaptop_IsOnlyInfo()
    {
        var hw = GoodDesktop() with
        {
            IsLaptop = true,
            RamModules = new[] { Stick(rated: 3200, configured: 2400, locator: "ChannelA-DIMM0"), Stick(rated: 3200, configured: 2400, locator: "ChannelB-DIMM0") },
        };
        var rec = Evaluate(hw, GoodSettings()).Find("RamSpeed");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Info, rec!.Kind);
        Assert.Equal(IssueSeverity.Info, rec.Severity);
    }

    [Fact]
    public void HighRatedKitAtJedecSpeed_IsNotMistakenForHalfClockReport()
    {
        // Regression: DDR4-4400 at its JEDEC 2133 was "doubled" to 4266 and reported as running at rated speed.
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(rated: 4400, configured: 2133, locator: "ChannelA-DIMM1"), Stick(rated: 4400, configured: 2133, locator: "ChannelB-DIMM1") },
        };
        var items = Evaluate(hw, GoodSettings());
        var rec = items.Find("RamSpeed");
        Assert.NotNull(rec);
        Assert.Equal("2133 МГц из 4400 МГц", rec!.CurrentValue);
        Assert.DoesNotContain(items, r => r.CurrentValue?.Contains("4266", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Ddr5HalfClockReport_IsNotTreatedAsSlow()
    {
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(rated: 6000, configured: 3000, type: 34, locator: "ChannelA-DIMM1"), Stick(rated: 6000, configured: 3000, type: 34, locator: "ChannelB-DIMM1") },
        };
        Assert.Null(Evaluate(hw, GoodSettings()).Find("RamSpeed"));
    }

    [Fact]
    public void MemoryCard_UsesTheSameHalfClockCorrection()
    {
        // Regression: the card said "1600 МГц из 3200 МГц" while the engine (correctly) found nothing wrong.
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(rated: 3200, configured: 1600, locator: "ChannelA-DIMM1"), Stick(rated: 3200, configured: 1600, locator: "ChannelB-DIMM1") },
        };
        var items = Evaluate(hw, GoodSettings());
        var card = HardwareCardBuilder.Build(hw, GoodSettings(), items, Now).Single(c => c.Caption == "Оперативная память");
        Assert.Equal("3200 МГц", card.Primary);
        Assert.NotEqual(Tone.Warning, card.BadgeTone);
    }

    [Fact]
    public void Ddr4AtJedecBaseSpeed_SuggestsCheckingXmpAsInfo()
    {
        // OEM part number that does not tell the rated speed: only a hint to check the module sticker.
        var hw = GoodDesktop() with
        {
            RamModules = new[]
            {
                Stick(rated: 2133, configured: 2133, locator: "ChannelA-DIMM1", part: "99U5701-011.A00G"),
                Stick(rated: 2133, configured: 2133, locator: "ChannelB-DIMM1", part: "99U5701-011.A00G"),
            },
        };
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("RamSpeed"));
        var rec = items.Find("RamBaseSpeed");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Info, rec!.Kind);
    }

    // ----- channels -----

    [Fact]
    public void SingleStick_RecommendsDualChannel()
    {
        var hw = GoodDesktop() with { RamModules = new[] { Stick(gb: 16) } };
        var rec = Evaluate(hw, GoodSettings()).Find("RamSingle");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationImpact.Medium, rec!.Impact);
        Assert.Equal(RecommendationKind.Manual, rec.Kind);
    }

    [Fact]
    public void SingleStickOnSingleSlotBoard_IsNotRecommended()
    {
        var hw = GoodDesktop() with { RamModules = new[] { Stick(gb: 16) }, RamSlots = 1 };
        Assert.Null(Evaluate(hw, GoodSettings()).Find("RamSingle"));
    }

    [Fact]
    public void TwoSticksInOneChannel_AreFlagged()
    {
        var hw = GoodDesktop() with { RamModules = new[] { Stick(locator: "ChannelA-DIMM0"), Stick(locator: "ChannelA-DIMM1") } };
        var rec = Evaluate(hw, GoodSettings()).Find("RamOneChannel");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationImpact.Medium, rec!.Impact);
    }

    [Fact]
    public void IntelControllerSlotNames_AreTwoChannels_NotOne()
    {
        // Regression: Intel 12th gen+ names dual channel "Controller0-ChannelA-DIMM1" + "Controller1-ChannelA-DIMM1";
        // the module told users to move correctly installed sticks.
        var hw = GoodDesktop() with
        {
            RamModules = new[] { Stick(locator: "Controller0-ChannelA-DIMM1"), Stick(locator: "Controller1-ChannelA-DIMM1") },
        };
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("RamOneChannel"));
        Assert.NotNull(items.Find("Ok:RamChannels"));

        var sameController = GoodDesktop() with
        {
            RamModules = new[] { Stick(locator: "Controller0-ChannelA-DIMM0"), Stick(locator: "Controller0-ChannelA-DIMM1") },
        };
        Assert.NotNull(Evaluate(sameController, GoodSettings()).Find("RamOneChannel"));
    }

    [Fact]
    public void IntelControllerSlotNames_FourBalancedSticks_AreFine()
    {
        var hw = GoodDesktop() with
        {
            RamModules = new[]
            {
                Stick(gb: 8, locator: "Controller0-ChannelA-DIMM0"), Stick(gb: 16, locator: "Controller0-ChannelA-DIMM1"),
                Stick(gb: 8, locator: "Controller1-ChannelA-DIMM0"), Stick(gb: 16, locator: "Controller1-ChannelA-DIMM1"),
            },
        };
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("RamOneChannel"));
        Assert.Null(items.Find("RamAsymmetric"));
        Assert.NotNull(items.Find("Ok:RamChannels"));
    }

    [Fact]
    public void SolderedLpddr_IsNeverCalledSingleChannel()
    {
        // LPDDR is soldered and multi-channel by design even when Windows lists a single module.
        var hw = GoodDesktop() with { IsLaptop = true, RamModules = new[] { Stick(gb: 16, type: 35, locator: "Controller0-ChannelA") }, RamSlots = 0 };
        Assert.Null(Evaluate(hw, GoodSettings()).Find("RamSingle"));

        var oneChannel = GoodDesktop() with
        {
            IsLaptop = true,
            RamModules = new[] { Stick(gb: 8, type: 30, locator: "ChannelA-DIMM0"), Stick(gb: 8, type: 30, locator: "ChannelA-DIMM1") },
        };
        Assert.Null(Evaluate(oneChannel, GoodSettings()).Find("RamOneChannel"));
    }

    [Fact]
    public void DifferentAmountPerChannel_SuggestsTheSwapThatBalancesThem()
    {
        // 8 + 8 GB in channel A and 16 + 16 GB in channel B: swapping one pair gives 24 GB per channel (full dual channel).
        var hw = GoodDesktop() with
        {
            RamModules = new[]
            {
                Stick(gb: 8, locator: "ChannelA-DIMM0"), Stick(gb: 8, locator: "ChannelA-DIMM1"),
                Stick(gb: 16, locator: "ChannelB-DIMM0"), Stick(gb: 16, locator: "ChannelB-DIMM1"),
            },
        };
        var rec = Evaluate(hw, GoodSettings()).Find("RamAsymmetric");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Manual, rec!.Kind);
        Assert.Equal(RecommendationImpact.Low, rec.Impact);
        Assert.Contains("32 ГБ", rec.Explanation); // 2 × 16 GB in dual channel now
        Assert.Contains("по 24 ГБ (8 ГБ + 16 ГБ)", rec.Explanation);
        Assert.Contains(rec.Steps, s => s.Contains("8 ГБ из канала A", StringComparison.Ordinal) &&
                                        s.Contains("16 ГБ из канала B", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentAmountPerChannel_WithoutABalancingSwap_IsInfo()
    {
        var hw = GoodDesktop() with { RamModules = new[] { Stick(gb: 8, locator: "ChannelA-DIMM1"), Stick(gb: 16, locator: "ChannelB-DIMM1") } };
        var rec = Evaluate(hw, GoodSettings()).Find("RamAsymmetric");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Info, rec!.Kind);
        Assert.Contains("16 ГБ", rec.Explanation); // 2 × 8 GB in dual channel
        Assert.Contains("8 ГБ", rec.Explanation);  // the rest in single channel
        Assert.Empty(rec.Steps);
    }

    [Theory]
    [InlineData(new[] { 8, 8 }, new[] { 16, 16 }, 8, 16)]
    [InlineData(new[] { 16, 16 }, new[] { 8, 8 }, 16, 8)]
    [InlineData(new[] { 4, 16 }, new[] { 8, 4 }, 0, 0)]    // 20 vs 12 GB: no single swap evens them out
    [InlineData(new[] { 8 }, new[] { 16 }, 0, 0)]          // one module per channel cannot be balanced by swapping
    [InlineData(new[] { 8, 16 }, new[] { 16, 8 }, 0, 0)]   // already equal
    public void BalancingSwap_FindsAPairThatEqualisesTheChannels(int[] a, int[] b, int fromA, int fromB)
    {
        var channelA = a.Select((gb, i) => Stick(gb: gb, locator: $"ChannelA-DIMM{i}")).ToList();
        var channelB = b.Select((gb, i) => Stick(gb: gb, locator: $"ChannelB-DIMM{i}")).ToList();
        var swap = RecommendationEngine.BalancingSwap(channelA, channelB);
        if (fromA == 0)
        {
            Assert.Null(swap);
            return;
        }
        Assert.NotNull(swap);
        Assert.Equal(fromA * Gb, swap!.Value.FromA.CapacityBytes);
        Assert.Equal(fromB * Gb, swap.Value.FromB.CapacityBytes);
        var newA = channelA.Sum(m => m.CapacityBytes) - swap.Value.FromA.CapacityBytes + swap.Value.FromB.CapacityBytes;
        var newB = channelB.Sum(m => m.CapacityBytes) - swap.Value.FromB.CapacityBytes + swap.Value.FromA.CapacityBytes;
        Assert.Equal(newA, newB);
    }

    // ----- amount -----

    [Theory]
    [InlineData(4, "RamLow", RecommendationImpact.High)]
    [InlineData(8, "RamGaming", RecommendationImpact.Low)]
    public void LowRamAmount_IsRecommended(int totalGb, string id, RecommendationImpact impact)
    {
        var half = totalGb / 2;
        var hw = GoodDesktop() with { RamModules = new[] { Stick(gb: half, locator: "ChannelA-DIMM1"), Stick(gb: half, locator: "ChannelB-DIMM1") } };
        var rec = Evaluate(hw, GoodSettings()).Find(id);
        Assert.NotNull(rec);
        Assert.Equal(impact, rec!.Impact);
    }

    [Fact]
    public void SixteenGb_IsEnough()
    {
        var items = Evaluate(GoodDesktop(), GoodSettings());
        Assert.Null(items.Find("RamLow"));
        Assert.Null(items.Find("RamGaming"));
        Assert.NotNull(items.Find("Ok:RamAmount"));
    }

    [Fact]
    public void WithoutModuleInfo_VisibleMemoryIsUsed()
    {
        var hw = GoodDesktop() with { RamModules = Array.Empty<RamModule>(), VisibleRamBytes = 4 * Gb - 200 * 1024 * 1024 };
        Assert.NotNull(Evaluate(hw, GoodSettings()).Find("RamLow"));
    }

    [Fact]
    public void HighMemoryLoad_LinksToProcesses()
    {
        var s = GoodSettings() with { MemoryLoadPercent = 92 };
        var rec = Evaluate(GoodDesktop(), s).Find("RamLoad");
        Assert.NotNull(rec);
        Assert.Equal("Processes", rec!.NavigateKey);
    }

    // ----- disk -----

    [Fact]
    public void SystemDriveOnHdd_RecommendsSsd()
    {
        var hw = GoodDesktop() with { SystemDisk = new SystemDiskInfo { DriveLetter = "C", Model = "ST1000DM010", MediaKind = DiskMediaKind.Hdd } };
        var rec = Evaluate(hw, GoodSettings()).Find("SystemHdd");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationImpact.High, rec!.Impact);
        Assert.Equal(IssueSeverity.High, rec.Severity);
    }

    [Fact]
    public void UnknownDiskType_GivesNoAdvice()
    {
        var hw = GoodDesktop() with { SystemDisk = new SystemDiskInfo { DriveLetter = "C", MediaKind = DiskMediaKind.Unknown } };
        var items = Evaluate(hw, GoodSettings());
        Assert.Null(items.Find("SystemHdd"));
        Assert.Null(items.Find("Ok:SystemSsd"));
    }

    // ----- refresh rate -----

    [Theory]
    [InlineData(60, 144, true, RecommendationImpact.High)]
    [InlineData(60, 75, true, RecommendationImpact.High)]
    [InlineData(120, 144, true, RecommendationImpact.Low)]        // +20%: a small gain
    [InlineData(144, 165, true, RecommendationImpact.Low)]        // a 165 Hz monitor left at 144 Hz is worth a hint
    [InlineData(59, 60, false, RecommendationImpact.Low)]
    [InlineData(165, 175, false, RecommendationImpact.Low)]       // +6%: not worth advice
    [InlineData(144, 144, false, RecommendationImpact.Low)]
    public void RefreshRateRule(int current, int max, bool expected, RecommendationImpact impact)
    {
        var hw = GoodDesktop() with { Displays = new[] { Display(current, max) } };
        var rec = Evaluate(hw, GoodSettings()).FirstOrDefault(r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal));
        Assert.Equal(expected, rec is not null);
        if (rec is not null)
        {
            Assert.Equal(impact, rec.Impact);
            Assert.Equal("ms-settings:display-advanced", rec.SettingsUri);
            Assert.Contains($"{max} Гц", rec.Title);
            Assert.Contains($"{current} Гц", rec.Title);
        }
    }

    [Theory]
    [InlineData(144, 165, true, Tone.Warning)]
    [InlineData(59, 60, false, Tone.Neutral)]
    [InlineData(144, 144, false, Tone.Good)]
    public void MonitorCard_AgreesWithTheRefreshRule(int current, int max, bool recommended, Tone tone)
    {
        // Regression: a 165 Hz monitor at 144 Hz showed "144 Гц — максимум" next to "Поддерживает до 165 Гц".
        var hw = GoodDesktop() with { Displays = new[] { Display(current, max) } };
        var items = Evaluate(hw, GoodSettings());
        var card = HardwareCardBuilder.Build(hw, GoodSettings(), items, Now).Single(c => c.Caption == "Монитор");
        Assert.Equal(recommended, items.Any(r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal)));
        Assert.Equal(tone, card.BadgeTone);
        if (max > current)
            Assert.DoesNotContain("максимум", card.Badge);
    }

    [Fact]
    public void MonitorCard_WithDefaultRefreshRate_DoesNotClaimOneHertz()
    {
        // EnumDisplaySettings reports 0/1 Hz for "hardware default" (remote and virtual displays).
        var hw = GoodDesktop() with { Displays = new[] { Display(1, 1) } };
        var items = Evaluate(hw, GoodSettings());
        var card = HardwareCardBuilder.Build(hw, GoodSettings(), items, Now).Single(c => c.Caption == "Монитор");
        Assert.DoesNotContain("Гц", card.Primary);
        Assert.Null(card.Badge);
        Assert.DoesNotContain(items, r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal) || r.Id == "Ok:Refresh");
    }

    [Fact]
    public void RefreshRateOnLaptop_IsMediumImpact()
    {
        var hw = GoodDesktop() with { IsLaptop = true, Displays = new[] { Display(60, 165) } };
        var rec = Evaluate(hw, GoodSettings()).First(r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal));
        Assert.Equal(RecommendationImpact.Medium, rec.Impact);
    }

    [Fact]
    public void MonitorOnIntegratedGpu_WithDiscreteCard_IsFlagged()
    {
        var hw = GoodDesktop() with
        {
            Gpus = new[] { Rtx3060(), IntelUhd() },
            Displays = new[] { Display(adapter: "Intel(R) UHD Graphics 630") },
        };
        Assert.NotNull(Evaluate(hw, GoodSettings()).Find("MonitorOnIgpu"));
        Assert.Null(Evaluate(hw with { IsLaptop = true }, GoodSettings()).Find("MonitorOnIgpu"));
    }

    // ----- GPU driver -----

    [Fact]
    public void OldGpuDriver_RecommendsVendorSite()
    {
        var hw = GoodDesktop() with { Gpus = new[] { Rtx3060(Now.AddMonths(-7)) } };
        var rec = Evaluate(hw, GoodSettings()).FirstOrDefault(r => r.Id.StartsWith("GpuDriver:", StringComparison.Ordinal));
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Manual, rec!.Kind);
        Assert.Equal(RecommendationImpact.Medium, rec.Impact);
        Assert.Equal(GpuClassifier.NvidiaDriverUrl, rec.SettingsUri);
        Assert.Contains("560.94", rec.CurrentValue); // 32.0.15.6094 -> NVIDIA 560.94
    }

    [Fact]
    public void FreshGpuDriver_IsFine()
    {
        var hw = GoodDesktop() with { Gpus = new[] { Rtx3060(Now.AddMonths(-5)) } };
        var items = Evaluate(hw, GoodSettings());
        Assert.DoesNotContain(items, r => r.Id.StartsWith("GpuDriver:", StringComparison.Ordinal));
        Assert.NotNull(items.Find("Ok:GpuDriver"));
    }

    [Fact]
    public void OldIntegratedDriverNextToDiscreteCard_IsLowImpact()
    {
        var hw = GoodDesktop() with { Gpus = new[] { Rtx3060(), IntelUhd(Now.AddYears(-2)) } };
        var rec = Evaluate(hw, GoodSettings()).First(r => r.Id.StartsWith("GpuDriver:", StringComparison.Ordinal));
        Assert.Equal(RecommendationImpact.Low, rec.Impact);
        Assert.Equal(GpuClassifier.IntelDriverUrl, rec.SettingsUri);
    }

    [Fact]
    public void BasicDisplayAdapter_MeansMissingDriver()
    {
        var hw = GoodDesktop() with
        {
            Gpus = new[] { new GpuInfo { Name = "Microsoft Basic Display Adapter", PnpDeviceId = @"PCI\VEN_10DE&DEV_2504&SUBSYS_1" } },
        };
        var rec = Evaluate(hw, GoodSettings()).FirstOrDefault(r => r.Id.StartsWith("GpuNoDriver:", StringComparison.Ordinal));
        Assert.NotNull(rec);
        Assert.Equal(RecommendationImpact.High, rec!.Impact);
        Assert.Equal(GpuClassifier.NvidiaDriverUrl, rec.SettingsUri);
    }

    [Fact]
    public void VirtualAdapters_AreIgnored()
    {
        var hw = GoodDesktop() with
        {
            Gpus = new[] { Rtx3060(), new GpuInfo { Name = "Parsec Virtual Display Adapter", DriverDate = Now.AddYears(-3) } },
        };
        Assert.Single(Evaluate(hw, GoodSettings()).Where(r => r.Id.Contains("GpuDriver", StringComparison.Ordinal)));
    }

    // ----- BIOS -----

    [Fact]
    public void BiosOlderThanThreeYears_IsInfoOnly()
    {
        var rec = Evaluate(GoodDesktop() with { BiosDate = Now.AddYears(-4) }, GoodSettings()).Find("BiosOld");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Info, rec!.Kind);
        Assert.Equal(IssueSeverity.Info, rec.Severity);
        Assert.Null(Evaluate(GoodDesktop() with { BiosDate = Now.AddYears(-2) }, GoodSettings()).Find("BiosOld"));
    }

    // ----- power plan -----

    [Fact]
    public void BalancedOnDesktop_OffersHighPerformanceAutoFix()
    {
        var s = GoodSettings() with { ActivePlan = Balanced with { IsActive = true } };
        var rec = Evaluate(GoodDesktop(), s).Find("PowerPlan");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.AutoFix, rec!.Kind);
        Assert.Equal(TweakIds.PowerPlan, rec.FixId);
        Assert.Equal(PowerPlans.HighPerformance, rec.FixArgument);
        Assert.Equal(RecommendationImpact.Medium, rec.Impact);
        Assert.True(rec.IsSafe);
        Assert.False(rec.NeedsAdmin);
    }

    [Fact]
    public void PowerSaverOnDesktop_IsHighImpact_AndPrefersUltimateWhenAvailable()
    {
        var s = GoodSettings() with
        {
            ActivePlan = Saver with { IsActive = true },
            Plans = new[] { Balanced, High, Saver, UltimateCopy },
        };
        var rec = Evaluate(GoodDesktop(), s).Find("PowerPlan");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationImpact.High, rec!.Impact);
        Assert.Equal(UltimateCopy.Guid, rec.FixArgument); // copy found by its standard name
    }

    [Fact]
    public void BalancedOnDesktop_WithoutPerformancePlans_GivesManualSteps()
    {
        var s = GoodSettings() with { ActivePlan = Balanced with { IsActive = true }, Plans = new[] { Balanced } };
        var rec = Evaluate(GoodDesktop(), s).Find("PowerPlan");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.Manual, rec!.Kind);
        Assert.Equal("ms-settings:powersleep", rec.SettingsUri);
    }

    [Fact]
    public void VendorPlanOnDesktop_IsLeftAlone()
    {
        var custom = new PowerPlan("9897998c-92de-4669-853f-b7cd3ecb2790", "AMD Ryzen Balanced", true);
        var s = GoodSettings() with { ActivePlan = custom, Plans = new[] { custom, Balanced, High } };
        var items = Evaluate(GoodDesktop(), s);
        Assert.Null(items.Find("PowerPlan"));
        Assert.Null(items.Find("Ok:PowerPlan"));
    }

    [Fact]
    public void BalancedOnLaptopOnAc_OnlyMentionsHighPerformance()
    {
        var hw = GoodDesktop() with { IsLaptop = true, HasBattery = true };
        var s = GoodSettings() with { ActivePlan = Balanced with { IsActive = true }, OnAcPower = true };
        var items = Evaluate(hw, s);
        Assert.Null(items.Find("PowerPlan"));
        var info = items.Find("PowerPlanLaptopAc");
        Assert.NotNull(info);
        Assert.Equal(RecommendationKind.Info, info!.Kind);
        Assert.Null(info.FixId);
    }

    [Fact]
    public void BalancedOnLaptopOnBattery_IsFine()
    {
        var hw = GoodDesktop() with { IsLaptop = true, HasBattery = true };
        var s = GoodSettings() with { ActivePlan = Balanced with { IsActive = true }, OnAcPower = false };
        var items = Evaluate(hw, s);
        Assert.Null(items.Find("PowerPlan"));
        Assert.Null(items.Find("PowerPlanLaptopAc"));
        Assert.NotNull(items.Find("Ok:PowerPlan"));
    }

    [Fact]
    public void PowerSaverOnLaptop_RecommendsBalanced_NotMarkedSafe()
    {
        var hw = GoodDesktop() with { IsLaptop = true };
        var s = GoodSettings() with { ActivePlan = Saver with { IsActive = true }, Plans = new[] { Balanced, High, Saver } };
        var rec = Evaluate(hw, s).Find("PowerPlan");
        Assert.NotNull(rec);
        Assert.Equal(PowerPlans.Balanced, rec!.FixArgument);
        Assert.False(rec.IsSafe);
    }

    [Fact]
    public void HighPerformanceOnLaptopBattery_SuggestsBalanced()
    {
        var hw = GoodDesktop() with { IsLaptop = true };
        var s = GoodSettings() with { OnAcPower = false };
        var rec = Evaluate(hw, s).Find("PowerPlan");
        Assert.NotNull(rec);
        Assert.Equal(PowerPlans.Balanced, rec!.FixArgument);
        Assert.Equal(RecommendationImpact.Low, rec.Impact);
    }

    // ----- Game Mode / Game Bar / HAGS -----

    [Fact]
    public void GameModeOff_IsSafeAutoFix_MissingValueMeansOn()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { GameModeValue = 0 }).Find("GameMode");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.AutoFix, rec!.Kind);
        Assert.Equal(TweakIds.GameMode, rec.FixId);
        Assert.True(rec.IsSafe);

        var missing = Evaluate(GoodDesktop(), GoodSettings() with { GameModeValue = null });
        Assert.Null(missing.Find("GameMode"));
        Assert.NotNull(missing.Find("Ok:GameMode"));
    }

    [Fact]
    public void GameBarRecording_DefaultsToOn_AndIsSafeAutoFix()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { GameDvrValue = null, AppCaptureValue = null }).Find("GameDvr");
        Assert.NotNull(rec);
        Assert.True(rec!.IsSafe);
        Assert.Contains("FPS", rec.Explanation);

        Assert.Null(Evaluate(GoodDesktop(), GoodSettings() with { GameDvrValue = 0, AppCaptureValue = 1 }).Find("GameDvr"));
    }

    [Fact]
    public void GameBar_OnlyRealBackgroundRecordingIsMediumImpact()
    {
        // Regression: with "Record what happened" off (the Windows default) the module claimed background recording was on.
        var standby = Evaluate(GoodDesktop(), GoodSettings() with { GameDvrValue = 1, AppCaptureValue = null, HistoricalCaptureValue = null }).Find("GameDvr");
        Assert.NotNull(standby);
        Assert.Equal(RecommendationImpact.Low, standby!.Impact);
        Assert.DoesNotContain("фоновую", standby.Title);
        Assert.Contains("фоновая запись выключена", standby.CurrentValue);

        var background = Evaluate(GoodDesktop(), GoodSettings() with { GameDvrValue = 1, AppCaptureValue = 1, HistoricalCaptureValue = 1 }).Find("GameDvr");
        Assert.NotNull(background);
        Assert.Equal(RecommendationImpact.Medium, background!.Impact);
        Assert.Contains("фоновую запись", background.Title);
        Assert.True(background.IsSafe);
    }

    [Fact]
    public void HagsOff_OnSupportedGpu_NeedsAdminAndRestart()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { HagsRegistryMode = 1, HagsActive = false }).Find("Hags");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationKind.AutoFix, rec!.Kind);
        Assert.True(rec.NeedsAdmin);
        Assert.True(rec.NeedsRestart);
        Assert.False(rec.IsSafe);
    }

    [Fact]
    public void HagsSupport_FallsBackToGpuHeuristic()
    {
        var unknown = GoodSettings() with { HagsSupported = null, HagsActive = null, HagsRegistryMode = 1 };
        Assert.NotNull(Evaluate(GoodDesktop(), unknown).Find("Hags"));

        var oldGpu = GoodDesktop() with
        {
            Gpus = new[] { new GpuInfo { Name = "NVIDIA GeForce GTX 960", DriverVersion = "30.0.14.7141", DriverDate = Now, PnpDeviceId = @"PCI\VEN_10DE&DEV_1401" } },
        };
        Assert.Null(Evaluate(oldGpu, unknown).Find("Hags"));
    }

    [Fact]
    public void HagsEnabledInRegistry_ButNotYetActive_IsPendingRestart()
    {
        var items = Evaluate(GoodDesktop(), GoodSettings() with { HagsRegistryMode = 2, HagsActive = false });
        Assert.Null(items.Find("Hags"));
        Assert.Equal("Включится после перезагрузки", items.Find("Ok:Hags")?.CurrentValue);
    }

    // ----- visual effects -----

    [Fact]
    public void WeakPc_GetsVisualEffectsAndTransparencyTweaks()
    {
        var hw = GoodDesktop() with { Cpu = new CpuInfo { Name = "Intel Celeron N4020", Cores = 2, LogicalProcessors = 2 } };
        var items = Evaluate(hw, GoodSettings() with { VisualFxSetting = 0 });
        var fx = items.Find("VisualFx");
        Assert.NotNull(fx);
        Assert.Equal(RecommendationKind.AutoFix, fx!.Kind);
        Assert.Equal(TweakIds.VisualEffects, fx.FixId);
        Assert.NotNull(items.Find("Transparency"));
    }

    [Fact]
    public void StrongPc_KeepsVisualEffects()
    {
        var items = Evaluate(GoodDesktop(), GoodSettings() with { VisualFxSetting = 0 });
        Assert.Null(items.Find("VisualFx"));
        Assert.Null(items.Find("Transparency"));
    }

    // ----- maintenance -----

    [Fact]
    public void LongUptime_SuggestsRestart()
    {
        var rec = Evaluate(GoodDesktop(), GoodSettings() with { Uptime = TimeSpan.FromDays(9) }).Find("Uptime");
        Assert.NotNull(rec);
        Assert.Equal(RecommendationImpact.Low, rec!.Impact);
        Assert.Contains("9 дней", rec.Title);
    }

    [Fact]
    public void PendingReboot_IsMedium_AndReplacesUptimeAdvice()
    {
        var items = Evaluate(GoodDesktop(), GoodSettings() with { PendingReboot = true, Uptime = TimeSpan.FromDays(9) });
        Assert.Equal(RecommendationImpact.Medium, items.Find("PendingReboot")?.Impact);
        Assert.Null(items.Find("Uptime"));
    }

    [Theory]
    [InlineData(8, RecommendationImpact.Medium)]
    [InlineData(32, RecommendationImpact.Low)]
    public void DisabledPagefile_IsRecommendedByRamAmount(int totalGb, RecommendationImpact impact)
    {
        var half = totalGb / 2;
        var hw = GoodDesktop() with { RamModules = new[] { Stick(gb: half, locator: "ChannelA-DIMM1"), Stick(gb: half, locator: "ChannelB-DIMM1") } };
        var rec = Evaluate(hw, GoodSettings() with { PagefileAutomatic = false, PagefileCount = 0 }).Find("Pagefile");
        Assert.NotNull(rec);
        Assert.Equal(impact, rec!.Impact);
        Assert.Equal(RecommendationKind.Manual, rec.Kind);
    }

    // ----- ordering -----

    [Fact]
    public void Results_AreSortedByImpact_GoodItemsLast()
    {
        var hw = GoodDesktop() with
        {
            SystemDisk = new SystemDiskInfo { DriveLetter = "C", MediaKind = DiskMediaKind.Hdd },
            BiosDate = Now.AddYears(-5),
        };
        var s = GoodSettings() with { GameModeValue = 0, Uptime = TimeSpan.FromDays(10) };
        var items = Evaluate(hw, s);
        var actionable = items.Where(r => r.Kind != RecommendationKind.Good).ToList();
        Assert.Equal("SystemHdd", actionable[0].Id);
        var firstGood = items.FindIndex(r => r.Kind == RecommendationKind.Good);
        Assert.True(items.Skip(firstGood).All(r => r.Kind == RecommendationKind.Good));
        var impacts = actionable.Where(r => r.Kind is RecommendationKind.AutoFix or RecommendationKind.Manual).Select(r => (int)r.Impact).ToList();
        Assert.Equal(impacts.OrderByDescending(x => x), impacts);
    }

    [Fact]
    public void EveryRecommendation_HasRussianTitleAndExplanation()
    {
        var hw = GoodDesktop() with
        {
            Cpu = new CpuInfo { Name = "Intel Pentium", Cores = 2, LogicalProcessors = 2 },
            RamModules = new[] { Stick(gb: 4, rated: 3200, configured: 2133) },
            SystemDisk = new SystemDiskInfo { DriveLetter = "C", MediaKind = DiskMediaKind.Hdd },
            Displays = new[] { Display(60, 144) },
            Gpus = new[] { Rtx3060(Now.AddYears(-1)) },
            BiosDate = Now.AddYears(-6),
        };
        var s = GoodSettings() with
        {
            ActivePlan = Saver with { IsActive = true }, GameModeValue = 0, GameDvrValue = 1, AppCaptureValue = 1, HagsRegistryMode = 1,
            VisualFxSetting = 0, PagefileAutomatic = false, PagefileCount = 0, PendingReboot = true, MemoryLoadPercent = 95,
        };
        var items = Evaluate(hw, s).Actionable();
        Assert.True(items.Count >= 12, $"Only {items.Count} recommendations");
        foreach (var r in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title), r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Explanation), r.Id);
            Assert.Matches("[А-Яа-яЁё]", r.Title);
            if (r.Kind == RecommendationKind.AutoFix)
            {
                Assert.NotNull(r.FixId);
                Assert.False(string.IsNullOrWhiteSpace(r.ApplySummary), r.Id);
            }
        }
        Assert.Equal(items.Count, items.Select(r => r.Id).Distinct().Count());
    }
}
