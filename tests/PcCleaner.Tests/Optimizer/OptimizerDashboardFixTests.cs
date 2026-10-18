using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Tests.Core;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>
/// What the dashboard gets from the Optimizer beyond the plain fixes: the settings links of the advice, the
/// «Перезагрузить» button, and the GPU scheduling row without administrator rights. Windows is never restarted and no
/// setting is changed (restart seams are fakes, the registry engine is a sandbox that refuses to write).
/// </summary>
[Collection("SystemRestart")]
public sealed class OptimizerDashboardFixTests : IDisposable
{
    private const string RebootPending = "Windows ждёт перезагрузки, чтобы завершить обновление";
    private const string LongUptime = "Компьютер не перезагружался";
    private const string HagsAdminText = "Нужны права администратора: планирование GPU включается только с ними.";

    private readonly string _sandbox = $@"Software\WaterCa7sPcCleanerTests\Optimizer\{Guid.NewGuid():N}";
    private readonly AppSettings _settings = new();

    public OptimizerDashboardFixTests() => TestEnvironment.Init();

    // The shared parent keys are removed at exit by TestEnvironment.
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);

    private OptimizerService Create(HardwareSnapshot hw, SettingsSnapshot s, bool elevated = false) => new(
        () => Task.FromResult(hw),
        _ => Task.FromResult(s),
        new RegistryTweakEngine(_sandbox, _settings, (_, _) => throw new InvalidOperationException("nothing may be changed")),
        new PowerPlanSwitcher((_, _) => throw new InvalidOperationException("must not run"), _settings),
        () => elevated);

    /// <summary>A laptop with most of the checks failing, so many kinds of advice are produced at once.</summary>
    private static HardwareSnapshot TroubledHardware() => GoodDesktop() with
    {
        IsLaptop = true,
        HasBattery = true,
        BiosDate = Now.AddYears(-5),
        BootMode = BootMode.LegacyBios,
        VirtualizationFirmwareEnabled = false,
        HypervisorPresent = false,
        Displays = new[] { Display(60, 144) },
        Battery = new BatteryInfo { DesignedCapacityMwh = 50000, FullChargedCapacityMwh = 26000, CapacityState = SensorState.Available },
    };

    private static SettingsSnapshot TroubledSettings() => GoodSettings() with
    {
        ActivePlan = Balanced with { IsActive = true },
        GameModeValue = 0,
        OnAcPower = false,
        CpuLoadPercent = 97,
        TemperatureC = 92,
        TemperatureState = SensorState.Available,
        FastStartupValue = 1,
        PagefileUsedMb = 2600,
        PagefileAllocatedMb = 4096,
    };

    private static List<Recommendation> ShownOnTheDashboard(IEnumerable<Recommendation> items)
        => items.Where(r => r.Kind is not (RecommendationKind.Good or RecommendationKind.Navigate)
                            && !OptimizerService.PageOnlyIds.Contains(r.Id)).ToList();

    // ===================== settings links =====================

    [Fact]
    public async Task EveryAdvice_PassesItsSettingsLinkToTheDashboard()
    {
        var service = Create(TroubledHardware(), TroubledSettings());
        var (_, _, items) = await service.EvaluateAsync(false, CancellationToken.None);
        var shown = ShownOnTheDashboard(items);

        var issues = service.ToHealthIssues(items);

        Assert.Equal(shown.Count, issues.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            Assert.Equal(shown[i].Title, issues[i].Title);
            Assert.Equal(shown[i].SettingsUri, issues[i].SettingsUri); // unchanged: web pages stay web pages
        }
        // Not a vacuous pass: the links of a few kinds of advice are really there.
        Assert.Contains(issues, i => i.SettingsUri == "ms-settings:gaming-gamemode");
        Assert.Contains(issues, i => i.SettingsUri == "ms-settings:display-advanced");
        Assert.Contains(issues, i => i.SettingsUri == "powercfg.cpl");
        Assert.Contains(issues, i => i.SettingsUri is { } uri && uri.StartsWith("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void AdviceWithoutALink_HasNone()
    {
        var service = Create(GoodDesktop(), GoodSettings());
        var rec = new Recommendation { Id = "Tip", Title = "Совет", Explanation = "x", Kind = RecommendationKind.Info };
        Assert.Null(Assert.Single(service.ToHealthIssues(new[] { rec })).SettingsUri);
    }

    // ===================== GPU scheduling without administrator rights =====================

    private async Task<HealthIssue> HagsIssueAsync(bool elevated)
    {
        var s = GoodSettings() with { HagsRegistryMode = 1, HagsActive = false };
        var issues = await Create(GoodDesktop(), s, elevated).AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title.StartsWith("Включите планирование GPU", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hags_WithoutAdmin_HasAFixThatChangesNothingAndSaysWhy()
    {
        var hags = await HagsIssueAsync(elevated: false);

        Assert.NotNull(hags.FixAsync);
        Assert.True(hags.CanFix);
        Assert.False(hags.AutoFixSafe);
        Assert.DoesNotContain("Важно:", hags.Details); // the fix does not apply the tweak, so it announces no consequence

        var text = await hags.FixAsync!(CancellationToken.None);

        Assert.Equal(HagsAdminText, text);
        Assert.True(HealthScoring.LooksLikeAdminRequired(text)); // → «Перезапустить от имени администратора» on the row
        Assert.True(HealthScoring.LooksLikeFailure(text)); // → not marked fixed
        using var key = Registry.CurrentUser.OpenSubKey(_sandbox);
        Assert.Null(key); // the sandbox engine never wrote anything
    }

    [Fact]
    public async Task Hags_WithAdmin_KeepsTheRealFix()
    {
        var hags = await HagsIssueAsync(elevated: true);

        Assert.NotNull(hags.FixAsync);
        Assert.False(hags.AutoFixSafe);
        Assert.Contains("Важно: Изменение вступит в силу после перезагрузки компьютера.", hags.Details);
    }

    [Fact]
    public async Task AnyAdminOnlyTweak_WithoutAdmin_SaysSoInsteadOfHavingNoButton()
    {
        var rec = new Recommendation
        {
            Id = "Other", Title = "Другая настройка", Explanation = "x", Kind = RecommendationKind.AutoFix, FixId = TweakIds.GameMode,
            NeedsAdmin = true,
        };
        var issue = Assert.Single(Create(GoodDesktop(), GoodSettings()).ToHealthIssues(new[] { rec }));

        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.True(HealthScoring.LooksLikeAdminRequired(text));
        Assert.True(HealthScoring.LooksLikeFailure(text));
    }

    // ===================== «Перезагрузить» =====================

    private async Task<HealthIssue> RebootIssueAsync(bool pendingReboot)
    {
        var s = pendingReboot
            ? GoodSettings() with { PendingReboot = true, Uptime = TimeSpan.FromDays(2) }
            : GoodSettings() with { Uptime = TimeSpan.FromDays(9) };
        var issues = await Create(GoodDesktop(), s).AnalyzeAsync(CancellationToken.None);
        return Assert.Single(issues, i => i.Title.StartsWith(pendingReboot ? RebootPending : LongUptime, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RebootAdvice_GetsARestartButton_ThatFixAllNeverPresses(bool pendingReboot)
    {
        var reboot = await RebootIssueAsync(pendingReboot);

        Assert.NotNull(reboot.FixAsync);
        Assert.True(reboot.CanFix);
        Assert.Equal("Перезагрузить", reboot.FixLabel);
        Assert.False(reboot.InFixAll);
        Assert.False(reboot.OnlyOpensTool); // after «Да» the computer restarts: the row is done, not "a tool was opened"
        Assert.False(reboot.AutoFixSafe);
        if (pendingReboot)
            Assert.Equal("ms-settings:windowsupdate", reboot.SettingsUri);
    }

    [Fact]
    public async Task RebootFix_DoesNothingWhenTheUserSaysNo()
    {
        var reboot = await RebootIssueAsync(pendingReboot: true);
        using var fake = new SystemRestartFake(answer: false);

        var text = await reboot.FixAsync!(CancellationToken.None);

        Assert.Equal("Перезагрузка отменена — ничего не изменено.", text);
        Assert.Equal(0, fake.StartCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RebootFix_RestartsOnceWhenTheUserSaysYes(bool pendingReboot)
    {
        var reboot = await RebootIssueAsync(pendingReboot);
        using var fake = new SystemRestartFake(answer: true);

        var text = await reboot.FixAsync!(CancellationToken.None);

        Assert.Equal("Компьютер перезагружается…", text);
        Assert.Equal(1, fake.StartCalls);
        Assert.Equal("Перезагрузить компьютер", Assert.Single(fake.Questions).Title);
    }

    [Fact]
    public async Task OnlyTheTwoRestartTips_AmongManualAdvice_GetAFix()
    {
        var manual = 0;
        foreach (var s in new[] { TroubledSettings() with { PendingReboot = true }, TroubledSettings() with { Uptime = TimeSpan.FromDays(12) } })
        {
            var service = Create(TroubledHardware(), s);
            var (_, _, items) = await service.EvaluateAsync(false, CancellationToken.None);
            var shown = ShownOnTheDashboard(items);
            var issues = service.ToHealthIssues(items);

            for (var i = 0; i < shown.Count; i++)
            {
                if (shown[i].Kind is not (RecommendationKind.Manual or RecommendationKind.Info))
                    continue;
                manual++;
                var restartTip = shown[i].Id is "PendingReboot" or "Uptime";
                Assert.Equal(restartTip, shown[i].OffersRestart);
                Assert.True(restartTip == (issues[i].FixAsync is not null), $"«{issues[i].Title}»: a fix only for the restart tips");
            }
        }
        Assert.True(manual >= 10, $"only {manual} manual tips were checked");
    }
}
