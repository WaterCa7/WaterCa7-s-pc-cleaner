using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Optimizer;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>Dashboard fix results of «Ускорение» and Russian wording of its texts.</summary>
[Collection("OptimizerState")]
public class OptimizerRound3Tests
{
    public OptimizerRound3Tests() => TestEnvironment.Init();

    [Theory]
    [InlineData("Эту рекомендацию нельзя применить автоматически.")]
    [InlineData("Неизвестная настройка. Ничего не изменено.")]
    [InlineData("Для изменения «Аппаратное ускорение GPU» нужны права администратора. Ничего не изменено — перезапустите программу от имени администратора.")]
    [InlineData("Прежний план электропитания не сохранён — возвращать нечего.")]
    [InlineData("Не удалось включить план «Высокая производительность»: неизвестная ошибка")]
    [InlineData("")]
    public void FailedTweak_IsAFailureForTheDashboard(string message)
    {
        var text = OptimizerService.DashboardResultText(new TweakResult(false, message), afterApplyNote: "Перезагрузите компьютер.");

        Assert.StartsWith("Не удалось", text);
        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.DoesNotContain("Перезагрузите", text); // the "after apply" note belongs to a success only
    }

    [Fact]
    public void FailedTweak_KeepsItsOwnExplanation()
    {
        var text = OptimizerService.DashboardResultText(
            new TweakResult(false, "Неизвестная настройка. Ничего не изменено."), null);
        Assert.Equal(OptimizerService.FailurePrefix + "неизвестная настройка. Ничего не изменено.", text);

        // Already says «Не удалось…»: no second prefix.
        var plan = OptimizerService.DashboardResultText(new TweakResult(false, "Не удалось включить план «X»: ошибка"), null);
        Assert.Equal("Не удалось включить план «X»: ошибка", plan);
    }

    [Fact]
    public void SuccessfulTweak_IsNotAFailure_AndAddsTheNoteOnlyWhenSomethingChanged()
    {
        var changed = OptimizerService.DashboardResultText(new TweakResult(true, "«Игровой режим»: готово.", Changed: true),
            "Перезагрузите компьютер.");
        Assert.Equal("«Игровой режим»: готово. Перезагрузите компьютер.", changed);
        Assert.False(HealthScoring.LooksLikeFailure(changed));

        var already = OptimizerService.DashboardResultText(
            new TweakResult(true, "«Игровой режим»: настройка уже применена, менять ничего не нужно."), "Перезагрузите компьютер.");
        Assert.Equal("«Игровой режим»: настройка уже применена, менять ничего не нужно.", already);
        Assert.False(HealthScoring.LooksLikeFailure(already));
    }

    [Fact]
    public async Task DashboardFix_ThatFails_IsReportedAsNotFixed()
    {
        // The recommended plan disappeared between the scan and the fix: powercfg lists no plans, nothing is switched.
        var settings = new AppSettings();
        var calls = new List<string>();
        var power = new PowerPlanSwitcher((args, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }, settings);
        var service = new OptimizerService(() => Task.FromResult(GoodDesktop()), _ => Task.FromResult(GoodSettings()),
            new RegistryTweakEngine(@"Software\WaterCa7sPcCleanerTests\Optimizer\never-written", settings, null), power, () => false);
        var rec = new Recommendation
        {
            Id = "PowerPlan",
            Title = "Включите план электропитания «Высокая производительность»",
            Kind = RecommendationKind.AutoFix,
            Severity = IssueSeverity.Medium,
            FixId = TweakIds.PowerPlan,
            FixArgument = PowerPlans.HighPerformance,
        };

        var issue = Assert.Single(service.ToHealthIssues(new[] { rec }));
        var text = await issue.FixAsync!(CancellationToken.None);

        Assert.StartsWith(OptimizerService.FailurePrefix, text);
        Assert.Contains("больше не найден", text);
        Assert.True(HealthScoring.LooksLikeFailure(text), text);
        Assert.DoesNotContain(calls, a => a.StartsWith("/setactive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(60, 144, "60 раз в секунду")]
    [InlineData(144, 165, "144 раза в секунду")]
    [InlineData(120, 165, "120 раз в секунду")]
    [InlineData(75, 144, "75 раз в секунду")]
    public void RefreshRateExplanation_UsesTheRightPlural(int current, int max, string expected)
    {
        var hw = GoodDesktop() with { Displays = new[] { Display(current, max) } };
        var rec = Evaluate(hw, GoodSettings()).Single(r => r.Id.StartsWith("Refresh:", StringComparison.Ordinal));
        Assert.Contains(expected, rec.Explanation);
    }
}
