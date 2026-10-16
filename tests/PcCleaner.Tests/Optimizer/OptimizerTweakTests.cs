using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Optimizer;
using Xunit;
using static PcCleaner.Tests.Optimizer.OptimizerTestData;

namespace PcCleaner.Tests.Optimizer;

/// <summary>
/// Apply/revert against the sandbox key HKCU\Software\WaterCa7sPcCleanerTests\Optimizer\&lt;test id&gt;.
/// Real settings are never touched: the engine redirects every value into the sandbox.
/// </summary>
[Collection("OptimizerState")]
public sealed class RegistryTweakTests : IDisposable
{
    private const string SandboxRoot = @"Software\WaterCa7sPcCleanerTests\Optimizer";
    private readonly string _sandbox;
    private readonly AppSettings _settings = new();
    private readonly RegistryTweakEngine _engine;

    public RegistryTweakTests()
    {
        TestEnvironment.Init();
        _sandbox = $@"{SandboxRoot}\{Guid.NewGuid():N}";
        _engine = new RegistryTweakEngine(_sandbox, _settings, null); // real RegistryBackup.ExportAsync into the temp data dir
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);
        DeleteIfEmpty(SandboxRoot);
        DeleteIfEmpty(@"Software\WaterCa7sPcCleanerTests");
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                return;
        }
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private string SandboxPath(RegistryValueSpec spec) => $@"{_sandbox}\{spec.HiveName}\{spec.SubKey}";

    private object? ReadSandbox(RegistryValueSpec spec)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SandboxPath(spec));
        return key?.GetValue(spec.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private RegistryValueKind KindInSandbox(RegistryValueSpec spec)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SandboxPath(spec));
        return key!.GetValueKind(spec.ValueName);
    }

    private void WriteSandbox(RegistryValueSpec spec, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SandboxPath(spec), writable: true);
        key.SetValue(spec.ValueName, value, kind);
    }

    [Fact]
    public async Task Apply_WritesValue_RevertOfPreviouslyMissingValue_DeletesIt()
    {
        var tweak = RegistryTweaks.GameMode;
        var spec = tweak.Values[0];
        Assert.Null(ReadSandbox(spec));

        var applied = await _engine.ApplyAsync(tweak);
        Assert.True(applied.Success, applied.Message);
        Assert.True(applied.Changed);
        Assert.Equal(1, ReadSandbox(spec));
        Assert.True(_engine.HasStoredState(tweak.Id));
        Assert.Equal("missing", _settings.GetModuleValue("Optimizer", RegistryTweakEngine.ValueKey(tweak.Id, spec)));

        var reverted = await _engine.RevertAsync(tweak);
        Assert.True(reverted.Success, reverted.Message);
        Assert.Null(ReadSandbox(spec)); // the value did not exist before -> deleted again
        Assert.False(_engine.HasStoredState(tweak.Id));
    }

    [Fact]
    public async Task Revert_RestoresExactPreviousValues_AndBacksUpTheKeyFirst()
    {
        var tweak = RegistryTweaks.GameDvr;
        WriteSandbox(tweak.Values[0], 1, RegistryValueKind.DWord); // GameDVR_Enabled = 1
        WriteSandbox(tweak.Values[1], 1, RegistryValueKind.DWord); // AppCaptureEnabled = 1
        // HistoricalCaptureEnabled is missing

        var backupsBefore = Directory.Exists(AppPaths.BackupsDir) ? Directory.GetFiles(AppPaths.BackupsDir, "*.reg").Length : 0;
        var applied = await _engine.ApplyAsync(tweak);
        Assert.True(applied.Success, applied.Message);
        Assert.All(tweak.Values, v => Assert.Equal(0, ReadSandbox(v)));
        Assert.True(Directory.GetFiles(AppPaths.BackupsDir, "*.reg").Length >= backupsBefore + 2, "both existing keys must be exported");

        var reverted = await _engine.RevertAsync(tweak);
        Assert.True(reverted.Success, reverted.Message);
        Assert.Equal(1, ReadSandbox(tweak.Values[0]));
        Assert.Equal(1, ReadSandbox(tweak.Values[1]));
        Assert.Equal(RegistryValueKind.DWord, KindInSandbox(tweak.Values[0]));
        Assert.Null(ReadSandbox(tweak.Values[2]));
    }

    [Fact]
    public async Task SecondApply_KeepsTheOriginalPreviousValue()
    {
        var tweak = RegistryTweaks.Transparency;
        var spec = tweak.Values[0];
        WriteSandbox(spec, 1, RegistryValueKind.DWord);

        Assert.True((await _engine.ApplyAsync(tweak)).Success);
        WriteSandbox(spec, 1, RegistryValueKind.DWord); // something (e.g. Windows) turned it back on
        Assert.True((await _engine.ApplyAsync(tweak)).Success);
        Assert.Equal(0, ReadSandbox(spec));

        Assert.True((await _engine.RevertAsync(tweak)).Success);
        Assert.Equal(1, ReadSandbox(spec));
    }

    [Fact]
    public async Task StringValues_AreRestoredWithTheirKind()
    {
        var tweak = RegistryTweaks.VisualEffects;
        var minAnimate = tweak.Values.Single(v => v.ValueName == "MinAnimate");
        WriteSandbox(minAnimate, "1", RegistryValueKind.String);

        Assert.True((await _engine.ApplyAsync(tweak)).Success);
        Assert.Equal("0", ReadSandbox(minAnimate));
        Assert.Equal(2, ReadSandbox(tweak.Values.Single(v => v.ValueName == "VisualFXSetting")));

        Assert.True((await _engine.RevertAsync(tweak)).Success);
        Assert.Equal("1", ReadSandbox(minAnimate));
        Assert.Equal(RegistryValueKind.String, KindInSandbox(minAnimate));
        Assert.Null(ReadSandbox(tweak.Values.Single(v => v.ValueName == "VisualFXSetting")));
    }

    [Fact]
    public async Task FailedBackup_AbortsWithoutChanges()
    {
        var tweak = RegistryTweaks.GameMode;
        var spec = tweak.Values[0];
        WriteSandbox(spec, 0, RegistryValueKind.DWord);
        var engine = new RegistryTweakEngine(_sandbox, _settings, (_, _) => Task.FromResult<string?>(null));

        var result = await engine.ApplyAsync(tweak);
        Assert.False(result.Success);
        Assert.Equal(0, ReadSandbox(spec));
        Assert.False(engine.HasStoredState(tweak.Id));
    }

    [Fact]
    public async Task AlreadyApplied_ChangesNothing()
    {
        var tweak = RegistryTweaks.GameMode;
        WriteSandbox(tweak.Values[0], 1, RegistryValueKind.DWord);
        var result = await _engine.ApplyAsync(tweak);
        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.False(_engine.HasStoredState(tweak.Id));
    }

    [Fact]
    public async Task MachineWideTweak_IsRedirectedIntoTheSandbox()
    {
        var tweak = RegistryTweaks.Hags;
        Assert.True(tweak.NeedsAdmin);
        var result = await _engine.ApplyAsync(tweak);
        Assert.True(result.Success, result.Message);
        Assert.Equal(2, ReadSandbox(tweak.Values[0])); // HKCU\...\Optimizer\<id>\HKLM\SYSTEM\...\GraphicsDrivers
        Assert.True((await _engine.RevertAsync(tweak)).Success);
        Assert.Null(ReadSandbox(tweak.Values[0]));
    }

    [Fact]
    public async Task RevertWithoutStoredState_DoesNothing()
    {
        var result = await _engine.RevertAsync(RegistryTweaks.GameDvr);
        Assert.False(result.Success);
        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.MultiString)]
    public void EncodedValues_RoundTrip(RegistryValueKind kind)
    {
        object value = kind switch
        {
            RegistryValueKind.DWord => -5,
            RegistryValueKind.QWord => 1L << 40,
            RegistryValueKind.ExpandString => @"%SystemRoot%\x:y",
            RegistryValueKind.Binary => new byte[] { 0x90, 0x12, 0x03, 0x80 },
            RegistryValueKind.MultiString => new[] { "a", "b:c" },
            _ => "текст: с двоеточием",
        };
        var encoded = RegistryTweakEngine.Encode((value, kind));
        Assert.True(RegistryTweakEngine.TryDecode(encoded, out var decoded, out var decodedKind));
        Assert.Equal(kind, decodedKind);
        Assert.Equal(value, decoded);
        Assert.Equal("missing", RegistryTweakEngine.Encode((null, RegistryValueKind.Unknown)));
    }
}

/// <summary>Power plan switching with a fake powercfg — the real active plan is never changed.</summary>
[Collection("OptimizerState")]
public sealed class PowerPlanSwitcherTests
{
    private readonly AppSettings _settings = new();
    private readonly List<string> _calls = new();
    private string _active = PowerPlans.Balanced;
    private bool _failSetActive;

    public PowerPlanSwitcherTests() => TestEnvironment.Init();

    private PowerPlanSwitcher Create() => new((args, _) =>
    {
        _calls.Add(args);
        if (args == "/getactivescheme")
            return Task.FromResult(new ProcessResult(0, $"GUID схемы питания: {_active}  (План)\r\n", "", false));
        if (args == "/list")
            return Task.FromResult(new ProcessResult(0,
                $"GUID схемы питания: {PowerPlans.Balanced}  (Сбалансированная)\r\nGUID схемы питания: {PowerPlans.HighPerformance}  (Высокая производительность)\r\n",
                "", false));
        if (args.StartsWith("/setactive ", StringComparison.Ordinal))
        {
            if (_failSetActive)
                return Task.FromResult(new ProcessResult(1, "", "Недопустимые параметры", false));
            _active = args["/setactive ".Length..];
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }
        return Task.FromResult(new ProcessResult(1, "", "unexpected", false));
    }, _settings);

    [Fact]
    public async Task Apply_StoresPreviousPlan_RevertRestoresIt()
    {
        var switcher = Create();
        var result = await switcher.ApplyAsync(PowerPlans.HighPerformance, "Высокая производительность");
        Assert.True(result.Success, result.Message);
        Assert.Equal(PowerPlans.HighPerformance, _active);
        Assert.Equal(PowerPlans.Balanced, _settings.GetModuleValue("Optimizer", "PowerPlan.Previous"));
        Assert.True(switcher.HasStoredState);

        var reverted = await switcher.RevertAsync();
        Assert.True(reverted.Success, reverted.Message);
        Assert.Equal(PowerPlans.Balanced, _active);
        Assert.Null(_settings.GetModuleValue("Optimizer", "PowerPlan.Previous"));
        Assert.False(switcher.HasStoredState);
    }

    [Fact]
    public async Task SecondApply_DoesNotOverwriteTheOriginalPlan()
    {
        var switcher = Create();
        await switcher.ApplyAsync(PowerPlans.HighPerformance, "Высокая производительность");
        await switcher.ApplyAsync(PowerPlans.PowerSaver, "Экономия энергии");
        Assert.Equal(PowerPlans.Balanced, _settings.GetModuleValue("Optimizer", "PowerPlan.Previous"));
    }

    [Fact]
    public async Task AlreadyActivePlan_IsNotSwitched()
    {
        var switcher = Create();
        var result = await switcher.ApplyAsync(PowerPlans.Balanced, "Сбалансированная");
        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.DoesNotContain(_calls, c => c.StartsWith("/setactive", StringComparison.Ordinal));
        Assert.False(switcher.HasStoredState);
    }

    [Fact]
    public async Task InvalidGuid_IsRejectedBeforeRunningAnything()
    {
        var switcher = Create();
        var result = await switcher.ApplyAsync("381b4222 & shutdown /s", "x");
        Assert.False(result.Success);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task FailedSwitch_ForgetsThePreviousPlan()
    {
        _failSetActive = true;
        var switcher = Create();
        var result = await switcher.ApplyAsync(PowerPlans.HighPerformance, "Высокая производительность");
        Assert.False(result.Success);
        Assert.Contains("Недопустимые параметры", result.Message);
        Assert.False(switcher.HasStoredState);
        Assert.Equal(PowerPlans.Balanced, _active);
    }
}

/// <summary>Service-level behaviour with synthetic snapshots and sandboxed tweaks.</summary>
[Collection("OptimizerState")]
public sealed class OptimizerServiceTests : IDisposable
{
    private readonly string _sandbox = $@"Software\WaterCa7sPcCleanerTests\Optimizer\{Guid.NewGuid():N}";
    private readonly AppSettings _settings = new();
    private string _active = PowerPlans.Balanced;

    public OptimizerServiceTests() => TestEnvironment.Init();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\WaterCa7sPcCleanerTests\Optimizer"))
        {
            if (key is { SubKeyCount: 0, ValueCount: 0 })
            {
                key.Close();
                Registry.CurrentUser.DeleteSubKey(@"Software\WaterCa7sPcCleanerTests\Optimizer", false);
            }
        }
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\WaterCa7sPcCleanerTests"))
        {
            if (key is { SubKeyCount: 0, ValueCount: 0 })
            {
                key.Close();
                Registry.CurrentUser.DeleteSubKey(@"Software\WaterCa7sPcCleanerTests", false);
            }
        }
    }

    private OptimizerService Create(HardwareSnapshot hw, SettingsSnapshot s, bool elevated = false)
    {
        var power = new PowerPlanSwitcher((args, _) =>
        {
            if (args == "/getactivescheme")
                return Task.FromResult(new ProcessResult(0, $"Power Scheme GUID: {_active}  (Plan)", "", false));
            if (args == "/list")
                return Task.FromResult(new ProcessResult(0,
                    $"Power Scheme GUID: {PowerPlans.Balanced}  (Balanced)\nPower Scheme GUID: {PowerPlans.HighPerformance}  (High performance)", "", false));
            if (args.StartsWith("/setactive ", StringComparison.Ordinal))
            {
                _active = args["/setactive ".Length..];
                return Task.FromResult(new ProcessResult(0, "", "", false));
            }
            return Task.FromResult(new ProcessResult(1, "", "", false));
        }, _settings);
        return new OptimizerService(() => Task.FromResult(hw), _ => Task.FromResult(s),
            new RegistryTweakEngine(_sandbox, _settings, null), power, () => elevated);
    }

    [Fact]
    public async Task Analyze_MapsRecommendationsToHealthIssues()
    {
        var s = GoodSettings() with
        {
            ActivePlan = Balanced with { IsActive = true }, GameModeValue = 0, HagsRegistryMode = 1, HagsActive = false,
        };
        var hw = GoodDesktop() with { BiosDate = Now.AddYears(-5), Displays = new[] { Display(60, 144) } };
        var service = Create(hw, s, elevated: false);

        var issues = await service.AnalyzeAsync(CancellationToken.None);

        Assert.All(issues, i =>
        {
            Assert.Equal("Optimizer", i.TargetPageKey);
            Assert.Equal("Ускорение", i.Module);
            Assert.False(string.IsNullOrWhiteSpace(i.Details));
        });
        Assert.DoesNotContain(issues, i => i.Title.StartsWith("Проверьте", StringComparison.Ordinal)); // page links are not issues

        var gameMode = issues.Single(i => i.Title.Contains("игровой режим", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gameMode.FixAsync);
        Assert.True(gameMode.AutoFixSafe);
        Assert.Equal(IssueSeverity.Medium, gameMode.Severity);

        var plan = issues.Single(i => i.Title.Contains("электропитания", StringComparison.Ordinal));
        Assert.True(plan.AutoFixSafe);

        var hags = issues.Single(i => i.Title.Contains("GPU", StringComparison.Ordinal));
        Assert.Null(hags.FixAsync); // needs admin, app not elevated

        var refresh = issues.Single(i => i.Title.Contains("Гц", StringComparison.Ordinal));
        Assert.Null(refresh.FixAsync);
        Assert.Equal(IssueSeverity.High, refresh.Severity);

        var bios = issues.Single(i => i.Title.Contains("BIOS", StringComparison.Ordinal));
        Assert.Equal(IssueSeverity.Info, bios.Severity);
    }

    [Fact]
    public async Task DashboardFix_AppliesTheTweak_AndPageCanRevertIt()
    {
        var s = GoodSettings() with { GameModeValue = 0 };
        var service = Create(GoodDesktop(), s);
        var issue = (await service.AnalyzeAsync(CancellationToken.None)).Single(i => i.FixAsync is not null);

        var message = await issue.FixAsync!(CancellationToken.None);
        Assert.Contains("готово", message, StringComparison.OrdinalIgnoreCase);
        using (var key = Registry.CurrentUser.OpenSubKey($@"{_sandbox}\HKCU\Software\Microsoft\GameBar"))
            Assert.Equal(1, key?.GetValue("AutoGameModeEnabled"));
        Assert.True(service.CanRevert(TweakIds.GameMode));
        Assert.Contains(TweakIds.GameMode, service.RevertableFixIds());

        var reverted = await service.RevertAsync(TweakIds.GameMode);
        Assert.True(reverted.Success, reverted.Message);
        Assert.False(service.CanRevert(TweakIds.GameMode));
    }

    [Fact]
    public async Task AdminTweak_WithoutElevation_ChangesNothing()
    {
        // The engine is sandboxed: even if the guard regressed, nothing outside the test key could be touched.
        var service = new OptimizerService(() => Task.FromResult(GoodDesktop()), _ => Task.FromResult(GoodSettings()),
            new RegistryTweakEngine(_sandbox, _settings, null),
            new PowerPlanSwitcher((_, _) => throw new InvalidOperationException("must not run"), _settings),
            () => false);
        var rec = new Recommendation
        {
            Id = "Hags", Title = "HAGS", Kind = RecommendationKind.AutoFix, FixId = TweakIds.Hags, NeedsAdmin = true,
        };
        var result = await service.ApplyAsync(rec, createRestorePoint: true);
        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Contains("администратора", result.Message);
        using (var key = Registry.CurrentUser.OpenSubKey($@"{_sandbox}\HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
            Assert.Null(key?.GetValue("HwSchMode"));
        Assert.False(service.CanRevert(TweakIds.Hags));

        var issues = service.ToHealthIssues(new[] { rec });
        Assert.Null(Assert.Single(issues).FixAsync); // the dashboard gets no fix button without elevation
    }

    [Fact]
    public async Task PowercfgLaunchFailure_IsAFailedResult_NotACrash()
    {
        var switcher = new PowerPlanSwitcher((_, _) => Task.FromResult(new ProcessResult(-1, "", "Доступ запрещён", false)), _settings);
        Assert.Null(await switcher.GetActiveAsync());
        Assert.Empty(await switcher.ListAsync());
        var result = await switcher.ApplyAsync(PowerPlans.HighPerformance, "Высокая производительность");
        Assert.False(result.Success);
        Assert.False(switcher.HasStoredState);
    }

    [Fact]
    public async Task PowerPlanFix_SwitchesThroughPowercfg_AndReverts()
    {
        var s = GoodSettings() with { ActivePlan = Balanced with { IsActive = true } };
        var service = Create(GoodDesktop(), s);
        var (_, _, items) = await service.EvaluateAsync(false, CancellationToken.None);
        var rec = items.Single(r => r.Id == "PowerPlan");

        var result = await service.ApplyAsync(rec, createRestorePoint: false);
        Assert.True(result.Success, result.Message);
        Assert.Equal(PowerPlans.HighPerformance, _active);
        Assert.True(service.CanRevert(TweakIds.PowerPlan));
        Assert.Contains(service.DescribeRevert(TweakIds.PowerPlan), l => l.Contains("Plan", StringComparison.Ordinal));

        Assert.True((await service.RevertAsync(TweakIds.PowerPlan)).Success);
        Assert.Equal(PowerPlans.Balanced, _active);
    }

    [Fact]
    public async Task ChangedTweak_StaysRevertable_EvenWhenNoLongerRecommended()
    {
        var service = Create(GoodDesktop(), GoodSettings());
        Assert.True((await service.Registry.ApplyAsync(RegistryTweaks.VisualEffects)).Success);

        var (_, _, items) = await service.EvaluateAsync(false, CancellationToken.None);
        var entry = items.Single(r => r.FixId == TweakIds.VisualEffects);
        Assert.Equal(RecommendationKind.Good, entry.Kind);
        Assert.True(entry.CanRevert);

        Assert.True((await service.RevertAsync(TweakIds.VisualEffects)).Success);
    }

    [Fact]
    public async Task HardwareSnapshot_IsCollectedOnceAndCached()
    {
        var calls = 0;
        var service = new OptimizerService(() =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(GoodDesktop());
            }, _ => Task.FromResult(GoodSettings()), new RegistryTweakEngine(_sandbox, _settings, null),
            new PowerPlanSwitcher((_, _) => Task.FromResult(new ProcessResult(1, "", "", false)), _settings));
        await Task.WhenAll(service.GetHardwareAsync(), service.GetHardwareAsync(), service.GetHardwareAsync());
        Assert.Equal(1, calls);
        Assert.NotNull(service.CachedHardware);
        await service.GetHardwareAsync(refresh: true);
        Assert.Equal(2, calls);
    }
}
