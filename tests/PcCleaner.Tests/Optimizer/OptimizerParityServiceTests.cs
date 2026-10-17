using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Modules.Processes;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>Dashboard mapping, battery report and page actions for the phase-2 checks (nothing on the system is changed).</summary>
[Collection("OptimizerState")]
public sealed class OptimizerParityServiceTests : IDisposable
{
    private readonly string _sandbox = $@"Software\WaterCa7sPcCleanerTests\Optimizer\{Guid.NewGuid():N}";
    private readonly AppSettings _settings = new();

    public OptimizerParityServiceTests() => TestEnvironment.Init();

    // The shared parent keys are removed at exit by TestEnvironment.
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);

    private OptimizerService Create(HardwareSnapshot hw, SettingsSnapshot s, BatteryReportGenerator? report = null) => new(
        () => Task.FromResult(hw),
        _ => Task.FromResult(s),
        new RegistryTweakEngine(_sandbox, _settings, (_, _) => throw new InvalidOperationException("nothing may be changed")),
        new PowerPlanSwitcher((_, _) => Task.FromResult(new ProcessResult(1, "", "", false)), _settings),
        () => false,
        report);

    private static HardwareSnapshot TroubledLaptop() => GoodDesktop() with
    {
        IsLaptop = true,
        HasBattery = true,
        BootMode = BootMode.LegacyBios,
        VirtualizationFirmwareEnabled = false,
        HypervisorPresent = false,
        Battery = new BatteryInfo { DesignedCapacityMwh = 50000, FullChargedCapacityMwh = 26000, CapacityState = SensorState.Available },
    };

    private static SettingsSnapshot TroubledSettings() => GoodSettings() with
    {
        ActivePlan = Balanced with { IsActive = true },
        OnAcPower = false,
        CpuLoadPercent = 97,
        TemperatureC = 92,
        TemperatureState = SensorState.Available,
        FastStartupValue = 1,
        PagefileUsedMb = 2600,
        PagefileAllocatedMb = 4096,
    };

    [Fact]
    public async Task Analyze_MapsTheNewChecks_LikeTheExistingOnes()
    {
        var service = Create(TroubledLaptop(), TroubledSettings());
        var issues = await service.AnalyzeAsync(CancellationToken.None);

        HealthIssue Find(string title) => issues.Single(i => i.Title == title);
        var expected = new (string Title, IssueSeverity Severity)[]
        {
            ("Компьютер загружается в режиме Legacy BIOS", IssueSeverity.Medium),
            ("Аппаратная виртуализация выключена в BIOS", IssueSeverity.Info),
            ("Высокая температура", IssueSeverity.High),
            ("Батарею стоит заменить", IssueSeverity.Medium),
            ("Включён быстрый запуск Windows", IssueSeverity.Info),
            ("Активно используется файл подкачки", IssueSeverity.Low),
            ("Процессор загружен более чем на 90%", IssueSeverity.Medium),
        };
        foreach (var (title, severity) in expected)
        {
            var issue = Find(title);
            Assert.Equal(severity, issue.Severity);
            Assert.Equal("Optimizer", issue.TargetPageKey);
            Assert.Equal("Ускорение", issue.Module);
            Assert.Contains("Сейчас:", issue.Details);
            Assert.Null(issue.FixAsync); // manual advice: the dashboard never "fixes" BIOS, hardware or Windows settings by itself
            Assert.False(issue.AutoFixSafe);
        }
    }

    [Fact]
    public async Task Analyze_XmpOff_IsOneHighIssue()
    {
        var hw = GoodDesktop() with
        {
            RamModules = new[]
            {
                Stick(rated: 2133, configured: 2133, locator: "ChannelA-DIMM1", part: "HX432C16FB3/8"),
                Stick(rated: 2133, configured: 2133, locator: "ChannelB-DIMM1", part: "HX432C16FB3/8"),
            },
        };
        var issues = await Create(hw, GoodSettings()).AnalyzeAsync(CancellationToken.None);
        var memory = Assert.Single(issues, i => i.Title.Contains("Память", StringComparison.Ordinal));
        Assert.Equal("Память работает без XMP/EXPO", memory.Title);
        Assert.Equal(IssueSeverity.High, memory.Severity);
        Assert.Contains("2133 МГц из 3200 МГц", memory.Details);
    }

    // ===================== one owner per symptom / safe batch =====================

    [Fact]
    public async Task Dashboard_HighRamLoad_IsReportedOnce_ByProcesses()
    {
        const long total = 16L << 30;
        var s = GoodSettings() with { MemoryLoadPercent = 90, MemoryUsedBytes = total * 9 / 10, MemoryTotalBytes = total };
        var service = Create(GoodDesktop(), s);

        // The Optimizer page still shows the advice...
        var (_, _, items) = await service.EvaluateAsync(false, CancellationToken.None);
        Assert.NotNull(items.Find("RamLoad"));

        // ...but on the dashboard the same memory counter is reported only by "Процессы", which names the consumers.
        var optimizer = await service.AnalyzeAsync(CancellationToken.None);
        Assert.DoesNotContain(optimizer, i => i.Title.StartsWith("Оперативная память почти заполнена", StringComparison.Ordinal));
        var processes = ProcessIssues.Build(Array.Empty<ProcessIssues.Evaluated>(),
            new MemoryStatus((ulong)total, (ulong)(total / 10), 90), selfPid: 1);

        var ram = Assert.Single(processes.Concat(optimizer),
            i => i.Title.Contains("оперативн", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Процессы", ram.Module);
        Assert.Equal(IssueSeverity.Medium, ram.Severity);
    }

    [Fact]
    public async Task Dashboard_GameDvrFix_IsNotInTheSafeBatch_AndItsConsequenceIsShownBeforehand()
    {
        var s = GoodSettings() with { GameModeValue = 0, GameDvrValue = 1, AppCaptureValue = 1 };
        var issues = await Create(GoodDesktop(), s).AnalyzeAsync(CancellationToken.None);

        var dvr = issues.Single(i => i.Title.Contains("Xbox Game Bar", StringComparison.Ordinal));
        Assert.NotNull(dvr.FixAsync); // still fixable one by one, after its own confirmation...
        Assert.False(dvr.AutoFixSafe); // ...but "Исправить безопасное" must not take clip recording away silently
        Assert.Contains("Важно: Скриншоты и запись клипов через Xbox Game Bar будут недоступны", dvr.Details);

        var gameMode = issues.Single(i => i.Title.Contains("игровой режим", StringComparison.OrdinalIgnoreCase));
        Assert.True(gameMode.AutoFixSafe);
        Assert.DoesNotContain("Важно:", gameMode.Details);
    }

    [Fact]
    public void Dashboard_ConsequenceNote_IsShownOnlyWhenTheDashboardCanFix()
    {
        var service = Create(GoodDesktop(), GoodSettings()); // not elevated
        var rec = new Recommendation
        {
            Id = "Hags", Title = "HAGS", Explanation = "x", Kind = RecommendationKind.AutoFix, FixId = TweakIds.Hags,
            NeedsAdmin = true, AfterApplyNote = "Изменение вступит в силу после перезагрузки компьютера.",
        };
        var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
        Assert.Null(issue.FixAsync);
        Assert.DoesNotContain("Важно:", issue.Details);
    }

    // ===================== battery report =====================

    [Fact]
    public async Task BatteryReport_RunsPowercfgIntoTheGivenFolder()
    {
        var dir = TestEnvironment.NewTempDir("battery");
        string? args = null;
        var generator = new BatteryReportGenerator((a, _) =>
        {
            args = a;
            File.WriteAllText(Path.Combine(dir, BatteryReportGenerator.FileName), "<html>report</html>");
            return Task.FromResult(new ProcessResult(0, "Отчёт о времени работы батареи сохранён", "", false));
        }, () => dir);

        var result = await generator.CreateAsync();

        Assert.True(result.Success, result.Message);
        var expectedPath = Path.Combine(dir, BatteryReportGenerator.FileName);
        Assert.Equal(expectedPath, result.Path);
        Assert.Equal($"/batteryreport /output \"{expectedPath}\"", args);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task BatteryReport_Failure_IsAMessage_NotAnException()
    {
        var dir = TestEnvironment.NewTempDir("battery");
        var generator = new BatteryReportGenerator(
            (_, _) => Task.FromResult(new ProcessResult(1, "", "Батарея не обнаружена.", false)), () => dir);

        var result = await generator.CreateAsync();

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.Contains("Батарея не обнаружена", result.Message);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task BatteryReport_ThatWroteNoFile_IsAFailure()
    {
        var dir = TestEnvironment.NewTempDir("battery");
        var generator = new BatteryReportGenerator((_, _) => Task.FromResult(new ProcessResult(0, "", "", false)), () => dir);
        Assert.False((await generator.CreateAsync()).Success);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task BatteryCardAction_ShowsTheResult()
    {
        // Failing generator: the page must explain it and must not try to open anything.
        var report = new BatteryReportGenerator(
            (_, _) => Task.FromResult(new ProcessResult(1, "", "Батарея не обнаружена.", false)), () => TestEnvironment.NewTempDir("battery"));
        var vm = new OptimizerViewModel(Create(TroubledLaptop(), TroubledSettings(), report));
        await vm.PreviewScanAsync(CancellationToken.None);

        var card = vm.HardwareCards.Single(c => c.Caption == "Батарея");
        Assert.True(vm.CardActionCommand.CanExecute(card));
        await vm.CardActionCommand.ExecuteAsync(card);

        Assert.Contains("Батарея не обнаружена", vm.Banner);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Page_ShowsPlatformCard_AndNineBalancedCardsOnALaptop()
    {
        var vm = new OptimizerViewModel(Create(TroubledLaptop(), TroubledSettings()));
        await vm.PreviewScanAsync(CancellationToken.None);

        Assert.Equal(9, vm.HardwareCards.Count);
        var platform = vm.HardwareCards.Single(c => c.Caption == "Прошивка и платформа");
        Assert.Equal("Legacy BIOS · виртуализация выключена", platform.Primary);
        Assert.Equal(Tone.Warning, platform.BadgeTone);
        Assert.Contains(platform.Details, d => d.StartsWith("BIOS 3405", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.HardwareCards, c => c.Caption == "Материнская плата");
        // The CPU-load advice links to the Processes page.
        var cpu = vm.AllItems.Single(r => r.Id == "CpuLoad");
        Assert.True(vm.NavigateCommand.CanExecute(cpu));
    }

    [Theory]
    [InlineData(RestorePointResult.Created, "создана")]
    [InlineData(RestorePointResult.RecentPointExists, "меньше суток")]
    [InlineData(RestorePointResult.NotElevated, "администратора")]
    [InlineData(RestorePointResult.Failed, "не удалось")]
    public void ApplyAllSummary_SaysWhatHappenedToTheRestorePoint(RestorePointResult result, string text)
        => Assert.Contains(text, OptimizerViewModel.RestorePointText(result), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AdminHint_ExplainsWhatIsHiddenWithoutElevation()
    {
        var noTemp = GoodSettings() with { TemperatureState = SensorState.NeedsAdmin };
        Assert.Null(OptimizerViewModel.AdminHint(elevated: true, adminFixes: true, GoodDesktop(), noTemp));
        Assert.Null(OptimizerViewModel.AdminHint(elevated: false, adminFixes: false, GoodDesktop(), GoodSettings()));

        var tempOnly = OptimizerViewModel.AdminHint(false, false, GoodDesktop(), noTemp);
        Assert.Contains("не показывает температуру", tempOnly);

        var laptop = TroubledLaptop() with { Battery = new BatteryInfo { CapacityState = SensorState.NeedsAdmin } };
        var both = OptimizerViewModel.AdminHint(false, true, laptop, noTemp);
        Assert.Contains("температуру и износ батареи", both);
        Assert.Contains("«Нужны права администратора»", both);
    }

    [Fact]
    public void ApplyAllSummary_IsSilentWhenRestorePointsAreOffInSettings()
        => Assert.Null(OptimizerViewModel.RestorePointText(RestorePointResult.DisabledInSettings));
}
