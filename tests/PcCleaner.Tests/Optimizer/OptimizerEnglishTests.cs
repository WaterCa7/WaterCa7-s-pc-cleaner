using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Tests.Core;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>
/// The Speed up page and its dashboard findings in English (the real en.json): recommendations (title, details, labels, steps), the
/// fix results (a failed or admin-only fix is still a failure / still needs administrator rights), the hardware cards and the labels
/// of the model. Power plan names come from Windows and stay as they are. Runs alone (the language is process-wide) and always
/// switches back to Russian; the Russian twin of the main texts is checked outside the English block.
/// </summary>
[Collection(LanguageSequentialCollection.Name)]
public sealed class OptimizerEnglishTests
{
    private static readonly Regex Cyrillic = new("[Ѐ-ӿ]");

    public OptimizerEnglishTests() => TestEnvironment.Init();

    private static void AssertEnglish(params string?[] texts)
    {
        foreach (var text in texts)
            Assert.DoesNotMatch(Cyrillic, text ?? "");
    }

    private static void AssertEnglish(Recommendation rec)
        => AssertEnglish(rec.Title, rec.Explanation, rec.CurrentValue, rec.Gain, rec.SettingsLabel, rec.NavigateLabel, rec.ApplySummary,
            rec.AfterApplyNote, string.Join("\n", rec.Steps));

    private static OptimizerService Service(bool elevated)
    {
        var settings = new AppSettings();
        var power = new PowerPlanSwitcher((_, _) => Task.FromResult(new ProcessResult(0, "", "", false)), settings);
        return new OptimizerService(() => Task.FromResult(GoodDesktop()), _ => Task.FromResult(GoodSettings()),
            new RegistryTweakEngine(@"Software\WaterCa7sPcCleanerTests\Optimizer\never-written-english", settings, null), power, () => elevated);
    }

    // Plan names as an English Windows gives them.
    private static readonly PowerPlan BalancedEn = new(PowerPlans.Balanced, "Balanced");
    private static readonly PowerPlan HighEn = new(PowerPlans.HighPerformance, "High performance");
    private static readonly PowerPlan SaverEn = new(PowerPlans.PowerSaver, "Power saver");

    // ================================================================ recommendations

    [Fact]
    public void ARefreshRateRecommendation_HasEnglishTitleDetailsLabelsAndSteps_AndRussianIsUnchanged()
    {
        var hw = GoodDesktop() with { Displays = new[] { Display(60, 144) } };
        var russian = Evaluate(hw, GoodSettings()).Single(r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal));
        Assert.Equal("Монитор поддерживает 144 Гц, а работает на 60 Гц", russian.Title);

        using (TestLanguage.English())
        {
            var rec = Evaluate(hw, GoodSettings()).Single(r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal));

            Assert.Equal("The monitor supports 144 Hz but runs at 60 Hz", rec.Title);
            Assert.Equal("The picture on the monitor “VG27AQ” is redrawn 60 times per second, although it can do 144 Hz. " +
                         "A higher refresh rate makes mouse movement, scrolling and games noticeably smoother. " +
                         "Switching is safe and takes a minute in the Windows settings.", rec.Explanation);
            Assert.Equal("60 Hz at 2560×1440", rec.CurrentValue);
            Assert.Equal("A smoother picture", rec.Gain);
            Assert.Equal("Open display settings", rec.SettingsLabel);
            Assert.Equal("In the “Choose a refresh rate” list choose 144 Hz.", rec.Steps[2]);
            AssertEnglish(rec);
        }
    }

    [Fact]
    public void APowerPlanRecommendation_HasEnglishTitleDetailsAndSummary_AndKeepsTheNamesOfWindows()
    {
        var settings = GoodSettings() with
        {
            ActivePlan = BalancedEn with { IsActive = true },
            Plans = new[] { BalancedEn with { IsActive = true }, HighEn, SaverEn },
        };
        var service = Service(elevated: false);

        using (TestLanguage.English())
        {
            var rec = Evaluate(GoodDesktop(), settings).Single(r => r.Id == "PowerPlan");

            Assert.Equal("Turn on the power plan “High performance”", rec.Title);
            Assert.Equal("Balanced", rec.CurrentValue);
            Assert.Equal("A faster response and steadier processor speeds", rec.Gain);
            Assert.Equal($"• The power plan “High performance” will be turned on (now “Balanced”).\n• Command: powercfg /setactive {PowerPlans.HighPerformance}", rec.ApplySummary);
            Assert.StartsWith("The plan “Balanced” is active now:", rec.Explanation);
            AssertEnglish(rec);

            var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
            Assert.Equal("Speed up", issue.Module);
            Assert.EndsWith("\nNow: Balanced.", issue.Details);
            Assert.Equal("Fix", issue.FixLabel);
        }
    }

    [Fact]
    public void GameMode_HasEnglishTextsAndASafeFix()
    {
        var settings = GoodSettings() with { GameModeValue = 0 };
        var service = Service(elevated: false);

        using (TestLanguage.English())
        {
            var rec = Evaluate(GoodDesktop(), settings).Single(r => r.Id == "GameMode");

            Assert.Equal("Turn on Windows Game Mode", rec.Title);
            Assert.Equal("Off", rec.CurrentValue);
            Assert.Equal("More resources for games", rec.Gain);
            Assert.Equal("Open settings", rec.SettingsLabel);
            Assert.StartsWith("• Windows Game Mode will be turned on.\n", rec.ApplySummary);
            Assert.Equal("Games", rec.CategoryText);
            Assert.Equal("Can be applied automatically", rec.KindText);
            AssertEnglish(rec);

            var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
            Assert.True(issue.AutoFixSafe);
            Assert.Equal("Turn on Windows Game Mode", issue.Title);
        }
    }

    [Fact]
    public void ARebootAdvice_HasEnglishTitleDetailsAndARestartButton_WithTheWarningInTheDetails()
    {
        var settings = GoodSettings() with { Uptime = TimeSpan.FromDays(8) };
        var service = Service(elevated: false);

        using (TestLanguage.English())
        {
            var rec = Evaluate(GoodDesktop(), settings).Single(r => r.Id == "Uptime");
            Assert.Equal("The computer has not been restarted for 8 days", rec.Title);
            Assert.Equal("Will refresh the system", rec.Gain);
            Assert.Equal("Click “Start” → “Power” → “Restart”.", rec.Steps[1]);

            var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
            Assert.Equal("Restart", issue.FixLabel);
            Assert.False(issue.InFixAll);
            Assert.EndsWith("\nImportant: Save your work in all programs: the computer will restart in a few seconds.", issue.Details);
            AssertEnglish(rec.Title, issue.Details, issue.FixLabel, issue.Consequence);
        }
    }

    [Fact]
    public void TheRecommendationLabels_AreEnglish()
    {
        var rec = new Recommendation { Id = "x", Title = "x", Category = RecommendationCategory.Hardware, Kind = RecommendationKind.AutoFix, NeedsAdmin = true };

        using (TestLanguage.English())
        {
            Assert.Equal("Hardware", rec.CategoryText);
            Assert.Equal("Automatic (administrator rights needed)", rec.KindText);
            Assert.Equal("High impact", new Recommendation { Id = "y", Title = "y", Kind = RecommendationKind.Manual, Impact = RecommendationImpact.High }.ImpactText);
            Assert.Equal("Low impact", new Recommendation { Id = "w", Title = "w", Kind = RecommendationKind.Manual, Impact = RecommendationImpact.Low }.ImpactText);
            Assert.Equal("For information", new Recommendation { Id = "z", Title = "z", Kind = RecommendationKind.Info }.ImpactText);
        }
    }

    // ================================================================ fix results

    [Fact]
    public async Task AnAdminOnlyTweak_WithoutAdministratorRights_SaysSo_AndStillNeedsAdministratorRights()
    {
        var service = Service(elevated: false);
        var rec = new Recommendation
        {
            Id = "Hags",
            Title = "Turn on GPU scheduling",
            Kind = RecommendationKind.AutoFix,
            Severity = IssueSeverity.Medium,
            FixId = TweakIds.Hags,
            NeedsAdmin = true,
        };

        using (TestLanguage.English())
        {
            var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
            var shown = await issue.FixAsync!(CancellationToken.None);

            Assert.Equal("Administrator rights are needed: GPU scheduling can be turned on only with them.", shown);
            Assert.True(HealthScoring.LooksLikeAdminRequired(shown));
            Assert.True(HealthScoring.LooksLikeFailure(shown));

            // The same tweak asked for directly: the message of the engine becomes a failure of the dashboard.
            var result = await service.ApplyAsync(rec, createRestorePoint: false);
            var text = OptimizerService.DashboardResultText(result, "Restart the computer.");

            Assert.False(result.Success);
            Assert.Equal("Could not fix: Changing “Hardware-accelerated GPU scheduling” needs administrator rights. Nothing was changed — restart the program as administrator.", text);
            Assert.True(HealthScoring.LooksLikeFailure(text));
            Assert.True(HealthScoring.LooksLikeAdminRequired(text));
            Assert.DoesNotContain("Restart the computer.", text);
        }
    }

    [Fact]
    public void AFailedFix_KeepsItsOwnFailureSentence_AndAnOtherFailureGetsThePrefix()
    {
        using (TestLanguage.English())
        {
            var own = OptimizerService.DashboardResultText(
                new TweakResult(false, "Could not turn on the plan “High performance”: access denied", OpensWithFailure: true), null);
            var other = OptimizerService.DashboardResultText(new TweakResult(false, "Unknown setting. Nothing was changed."), null);
            var empty = OptimizerService.DashboardResultText(new TweakResult(false, ""), null);

            Assert.Equal("Could not turn on the plan “High performance”: access denied", own);
            Assert.Equal("Could not fix: Unknown setting. Nothing was changed.", other);
            Assert.Equal("Could not fix: the reason is unknown.", empty);
            Assert.StartsWith(OptimizerService.FailurePrefix, other);
            Assert.True(HealthScoring.LooksLikeFailure(own));
            Assert.True(HealthScoring.LooksLikeFailure(other));
            Assert.True(HealthScoring.LooksLikeFailure(empty));
        }
    }

    [Fact]
    public void ASuccessfulFix_IsNotAFailure_InEnglish_AndAddsTheNoteOnlyWhenSomethingChanged()
    {
        using (TestLanguage.English())
        {
            var changed = OptimizerService.DashboardResultText(new TweakResult(true, "“Windows Game Mode”: done.", Changed: true), "Restart the computer.");
            var already = OptimizerService.DashboardResultText(
                new TweakResult(true, "“Windows Game Mode”: the setting is already applied, there is nothing to change."), "Restart the computer.");

            Assert.Equal("“Windows Game Mode”: done. Restart the computer.", changed);
            Assert.Equal("“Windows Game Mode”: the setting is already applied, there is nothing to change.", already);
            Assert.False(HealthScoring.LooksLikeFailure(changed));
            Assert.False(HealthScoring.LooksLikeFailure(already));
        }
    }

    [Fact]
    public async Task ThePowerPlanThatIsGone_IsAFailure_InEnglish()
    {
        var service = Service(elevated: true);
        var rec = new Recommendation
        {
            Id = "PowerPlan",
            Title = "Turn on the plan",
            Kind = RecommendationKind.AutoFix,
            Severity = IssueSeverity.Medium,
            FixId = TweakIds.PowerPlan,
            FixArgument = PowerPlans.HighPerformance,
        };

        using (TestLanguage.English())
        {
            var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
            var text = await issue.FixAsync!(CancellationToken.None);

            Assert.StartsWith("Could not fix: The needed power plan is no longer found in the system.", text);
            Assert.True(HealthScoring.LooksLikeFailure(text), text);
        }
    }

    // ================================================================ nothing Russian is left

    private static IEnumerable<(string Name, HardwareSnapshot Hardware, SettingsSnapshot Settings)> Scenarios()
    {
        var plans = new[] { BalancedEn, HighEn, SaverEn };
        // a weak laptop with almost every problem at once
        yield return ("weak laptop", GoodDesktop() with
        {
            IsLaptop = true,
            HasBattery = true,
            RamModules = new[] { Stick(4, 2400, 2133, "ChannelA-DIMM0", 26, "M471A5244") },
            RamSlots = 2,
            VisibleRamBytes = 4 * Gb - 100L * 1024 * 1024,
            Gpus = new[] { new GpuInfo { Name = "Microsoft Basic Display Adapter", PnpDeviceId = @"ROOT\BASIC", DriverDate = Now.AddYears(-3) } },
            Displays = new[] { Display(60, 144, "Microsoft Basic Display Adapter"), Display(60, 75, "Microsoft Basic Display Adapter", primary: false, device: @"\\.\DISPLAY2") },
            BiosDate = Now.AddYears(-5),
            BootMode = BootMode.LegacyBios,
            SystemDisk = new SystemDiskInfo { DriveLetter = "C", Model = "WDC HDD", MediaKind = DiskMediaKind.Hdd },
            VirtualizationFirmwareEnabled = false,
            VirtualizationSupported = true,
            HypervisorPresent = false,
            Battery = new BatteryInfo { DesignedCapacityMwh = 50000, FullChargedCapacityMwh = 27000, CapacityState = SensorState.Available },
        }, GoodSettings() with
        {
            ActivePlan = SaverEn with { IsActive = true },
            Plans = plans.Select(p => p == SaverEn ? p with { IsActive = true } : p).ToArray(),
            GameModeValue = 0,
            GameDvrValue = 1,
            HistoricalCaptureValue = 1,
            HagsRegistryMode = 1,
            VisualFxSetting = 1,
            TransparencyValue = 1,
            PagefileAutomatic = false,
            PagefileCount = 0,
            PendingReboot = true,
            Uptime = TimeSpan.FromDays(12),
            FastStartupValue = 1,
            MemoryLoadPercent = 92,
            MemoryUsedBytes = 3 * Gb,
            MemoryTotalBytes = 4 * Gb,
            CpuLoadPercent = 96,
            TemperatureC = 91,
            TemperatureState = SensorState.Available,
        });
        // a desktop whose memory runs below its rating in one channel, with a discrete card on an old driver
        yield return ("desktop with XMP off", GoodDesktop() with
        {
            RamModules = new[] { Stick(8, 3200, 2133, "ChannelA-DIMM1"), Stick(8, 3200, 2133, "ChannelA-DIMM2") },
            Gpus = new[] { Rtx3060(Now.AddYears(-2)), IntelUhd() },
            Displays = new[] { Display(60, 144, "Intel(R) UHD Graphics 630") },
            BiosDate = Now.AddYears(-6),
            VirtualizationFirmwareEnabled = false,
            VirtualizationSupported = true,
            HypervisorPresent = false,
        }, GoodSettings() with
        {
            ActivePlan = BalancedEn with { IsActive = true },
            Plans = new[] { BalancedEn with { IsActive = true }, HighEn, SaverEn },
            HagsRegistryMode = 1,
            PagefileUsedMb = 6000,
            PagefileAllocatedMb = 8000,
            PagefilePath = @"C:\pagefile.sys",
        });
        // asymmetric channels
        yield return ("asymmetric channels", GoodDesktop() with
        {
            RamModules = new[] { Stick(8, 3200, 3200, "ChannelA-DIMM1"), Stick(8, 3200, 3200, "ChannelA-DIMM2"), Stick(16, 3200, 3200, "ChannelB-DIMM1"), Stick(16, 3200, 3200, "ChannelB-DIMM2") },
        }, GoodSettings() with { ActivePlan = HighEn with { IsActive = true }, Plans = new[] { BalancedEn, HighEn with { IsActive = true }, SaverEn } });
    }

    [Fact]
    public void NoRussianIsLeft_InTheEnglishRecommendations_Cards_AndDashboardRows()
    {
        var service = Service(elevated: true);
        var seen = 0;
        using (TestLanguage.English())
        {
            foreach (var (name, hw, settings) in Scenarios())
            {
                var items = Evaluate(hw, settings);
                seen += items.Count;
                foreach (var rec in items)
                    AssertEnglishIn(name, rec);
                foreach (var card in HardwareCardBuilder.Build(hw, settings, items, Now))
                    AssertEnglishIn(name, card.Caption, card.Title, card.Primary, card.Badge, card.ToolTipText, card.ActionText, card.ActionToolTip,
                        string.Join("\n", card.Details), card.CopyText);
                foreach (var issue in service.ToHealthIssues(items))
                    AssertEnglishIn(name, issue.Module, issue.Title, issue.Details, issue.Gain, issue.FixLabel, issue.Consequence);
                AssertEnglishIn(name, HardwareCardBuilder.BuildReport(HardwareCardBuilder.Build(hw, settings, items, Now), items));
                AssertEnglishIn(name, OptimizerViewModel.BuildSummaryTitle(items.Count, items.Count(r => r.Kind == RecommendationKind.AutoFix)));
            }
        }
        Assert.True(seen > 40, $"only {seen} recommendations were produced: the scenarios must cover more of the engine");
    }

    private static void AssertEnglishIn(string scenario, Recommendation rec)
    {
        foreach (var text in new[] { rec.Title, rec.Explanation, rec.CurrentValue, rec.Gain, rec.SettingsLabel, rec.NavigateLabel, rec.ApplySummary,
                     rec.AfterApplyNote, rec.CategoryText, rec.ImpactText, rec.KindText }.Concat(rec.Steps))
            AssertEnglishIn($"{scenario} / {rec.Id}", text);
    }

    private static void AssertEnglishIn(string where, params string?[] texts)
    {
        foreach (var text in texts)
            Assert.True(!Cyrillic.IsMatch(text ?? ""), $"{where}: Russian is left in «{text}»");
    }

    [Fact]
    public void TheTextsOfThePage_AreEnglish()
    {
        var needsAdmin = GoodSettings() with { TemperatureState = SensorState.NeedsAdmin };

        using (TestLanguage.English())
        {
            Assert.Equal("Found 3 recommendations, 2 of them can be applied automatically", OptimizerViewModel.BuildSummaryTitle(3, 2));
            Assert.Equal("Found 1 recommendation — we will tell you what to do by hand", OptimizerViewModel.BuildSummaryTitle(1, 0));
            Assert.Equal("Everything is set up well — no recommendations", OptimizerViewModel.BuildSummaryTitle(0, 0));

            var hint = OptimizerViewModel.AdminHint(elevated: false, adminFixes: true, GoodDesktop(), needsAdmin);
            Assert.NotNull(hint);
            Assert.EndsWith("Also, without these rights Windows does not show temperature.", hint);
            Assert.Null(OptimizerViewModel.AdminHint(elevated: true, adminFixes: true, GoodDesktop(), needsAdmin));

            Assert.Equal("A Windows restore point was created before the changes.", OptimizerViewModel.RestorePointText(RestorePointResult.Created));
            AssertEnglish(OptimizerViewModel.RestorePointText(RestorePointResult.NotElevated), OptimizerViewModel.RestorePointText(RestorePointResult.Failed),
                OptimizerViewModel.RestorePointText(RestorePointResult.RecentPointExists));
        }
    }

    // ================================================================ cards and labels

    [Fact]
    public void TheHardwareCards_HaveEnglishCaptionsLabelsAndBadges()
    {
        var hw = GoodDesktop();
        // the plan names are Windows' own (English here): the tests' default ones are Russian
        var settings = GoodSettings() with
        {
            ActivePlan = HighEn with { IsActive = true },
            Plans = new[] { BalancedEn, HighEn with { IsActive = true }, SaverEn },
        };

        using (TestLanguage.English())
        {
            var items = Evaluate(hw, settings);
            var cards = HardwareCardBuilder.Build(hw, settings, items, Now);

            Assert.Equal(new[] { "Processor", "Graphics card", "Memory (RAM)", "System drive", "Monitor" }, cards.Take(5).Select(c => c.Caption).ToArray());
            var cpu = cards[0];
            Assert.Equal("6 cores · 12 threads", cpu.Primary);
            Assert.Contains("Base clock 3.7 GHz", cpu.Details);
            Assert.Contains("Temperature: no data", cpu.Details);
            var memory = cards[2];
            Assert.Equal("Dual-channel mode", memory.Badge);
            Assert.Equal("16 GB", HardwareText.Gb(16L * Gb));
            Assert.Equal("3200 MHz", HardwareText.Mhz(3200));
            Assert.Equal("45.2 Wh", HardwareText.WattHours(45_200));
            var monitor = cards[4];
            Assert.Equal("144 Hz — maximum", monitor.Badge);
            Assert.Equal("2560×1440 · 144 Hz", monitor.Primary);
            Assert.Equal("VG27AQ monitor", monitor.SearchQuery);
            foreach (var card in cards)
                AssertEnglish(card.Caption, card.Title, card.Primary, card.Badge, card.ToolTipText, string.Join("\n", card.Details));
        }
    }

    [Fact]
    public void TheChannelBadgeOfTheMemoryCard_IsTheSameDecision_InBothLanguages_ForTwoAndFourChannels()
    {
        static HardwareSnapshot Layout(params string[] locators)
            => GoodDesktop() with { RamModules = locators.Select(l => Stick(8, 3200, 3200, l)).ToArray(), RamSlots = locators.Length };
        var two = Layout("ChannelA-DIMM1", "ChannelB-DIMM1");
        var four = Layout("ChannelA-DIMM1", "ChannelB-DIMM1", "ChannelC-DIMM1", "ChannelD-DIMM1");

        static (string? Badge, string Title) Memory(HardwareSnapshot hw)
        {
            var settings = GoodSettings();
            var items = Evaluate(hw, settings);
            var fine = Assert.Single(items, r => r.Id == "Ok:RamChannels");
            return (HardwareCardBuilder.Build(hw, settings, items, Now)[2].Badge, fine.Title);
        }

        // Russian: the badge follows the number of channels, and so does the title of the "fine" item the badge used to be read from.
        var (twoRu, twoTitleRu) = Memory(two);
        var (fourRu, fourTitleRu) = Memory(four);
        Assert.Equal("Двухканальный режим", twoRu);
        Assert.Equal("Многоканальный режим", fourRu);
        Assert.Contains("двухканальном", twoTitleRu);
        Assert.Contains("многоканальном", fourTitleRu);

        using (TestLanguage.English())
        {
            var (twoEn, twoTitleEn) = Memory(two);
            var (fourEn, fourTitleEn) = Memory(four);
            Assert.Equal("Dual-channel mode", twoEn);
            Assert.Equal("Multi-channel mode", fourEn);
            Assert.Equal("The memory runs in dual-channel mode", twoTitleEn);
            Assert.Equal("The memory runs in multi-channel mode", fourTitleEn);
        }
    }

    [Fact]
    public void AMonitorNameThatTheProgramMadeUp_IsNotSearchable_InEitherLanguage()
    {
        var hw = GoodDesktop() with
        {
            Displays = new[] { Display() with { MonitorName = "Monitor 2", MonitorNameIsGeneric = true } },
        };

        using (TestLanguage.English())
        {
            var monitor = HardwareCardBuilder.Build(hw, GoodSettings(), Evaluate(hw, GoodSettings()), Now).Single(c => c.Caption == "Monitor");
            Assert.Null(monitor.SearchQuery);
        }

        var russian = HardwareCardBuilder.Build(hw, GoodSettings(), Evaluate(hw, GoodSettings()), Now).Single(c => c.Caption == "Монитор");
        Assert.Null(russian.SearchQuery);
    }

    [Fact]
    public void TheCardOfThePowerPlan_ShowsTheNameOfWindows_AndOwnLabelsInEnglish()
    {
        var settings = GoodSettings() with
        {
            ActivePlan = HighEn with { IsActive = true },
            Plans = new[] { BalancedEn, HighEn with { IsActive = true }, SaverEn },
        };

        using (TestLanguage.English())
        {
            var hw = GoodDesktop();
            var card = HardwareCardBuilder.Build(hw, settings, Evaluate(hw, settings), Now).Single(c => c.Caption == "Power options");

            Assert.Equal("High performance", card.Title);
            Assert.Equal("High performance", card.Badge);
            Assert.Equal("Desktop computer", card.Primary);
            Assert.Contains("Game Mode: on", card.Details);
            Assert.StartsWith("Power plans on this computer:\n", card.ToolTipText);
        }
    }

    [Fact]
    public void ThePlanNames_OfTheProgram_AreEnglish_WhenWindowsGivesNone()
    {
        using (TestLanguage.English())
        {
            Assert.Equal("High performance", PowerPlans.DisplayName(new PowerPlan(PowerPlans.HighPerformance, "")));
            Assert.Equal("Ultimate Performance", PowerPlans.DisplayName(new PowerPlan(PowerPlans.UltimatePerformance, "")));
            Assert.Equal("Balanced", PowerPlans.DisplayName(new PowerPlan(PowerPlans.Balanced, "")));
            Assert.Equal("Power saver", PowerPlans.DisplayName(new PowerPlan(PowerPlans.PowerSaver, "")));
            Assert.Equal("Моя схема", PowerPlans.DisplayName(new PowerPlan("00000000-0000-0000-0000-000000000001", "Моя схема"))); // from Windows
        }
    }

    [Fact]
    public void TheTweakTitles_FollowTheLanguage()
    {
        Assert.Equal("Игровой режим Windows", RegistryTweaks.GameMode.Title);

        using (TestLanguage.English())
        {
            Assert.Equal("Windows Game Mode", RegistryTweaks.GameMode.Title);
            Assert.Equal("Hardware-accelerated GPU scheduling", RegistryTweaks.Hags.Title);
            Assert.Equal("Power plan", OptimizerService.TweakTitle(TweakIds.PowerPlan));
        }

        Assert.Equal("Игровой режим Windows", RegistryTweaks.GameMode.Title);
    }
}
