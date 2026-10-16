using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>
/// "Ускорение": hardware/settings snapshots (cached), recommendations and reversible tweaks.
/// Snapshots and analysis are read-only; only <see cref="ApplyAsync"/> / <see cref="RevertAsync"/> change the system,
/// and callers confirm with the user first.
/// </summary>
public sealed class OptimizerService : IHealthContributor
{
    private readonly Func<Task<HardwareSnapshot>> _hardwareProbe;
    private readonly Func<CancellationToken, Task<SettingsSnapshot>> _settingsProbe;
    private readonly Func<bool> _isElevated;
    private readonly object _lock = new();
    private Task<HardwareSnapshot>? _hardwareTask;

    public OptimizerService()
    {
        Registry = new RegistryTweakEngine();
        Power = new PowerPlanSwitcher();
        var settingsProbe = new SettingsProbe(Power);
        _hardwareProbe = HardwareProbe.CollectAsync;
        _settingsProbe = settingsProbe.CollectAsync;
        _isElevated = () => Admin.IsElevated;
    }

    /// <summary>For tests: synthetic snapshots and sandboxed tweak engines.</summary>
    internal OptimizerService(Func<Task<HardwareSnapshot>> hardwareProbe, Func<CancellationToken, Task<SettingsSnapshot>> settingsProbe,
        RegistryTweakEngine registry, PowerPlanSwitcher power, Func<bool>? isElevated = null)
    {
        _hardwareProbe = hardwareProbe;
        _settingsProbe = settingsProbe;
        Registry = registry;
        Power = power;
        _isElevated = isElevated ?? (() => Admin.IsElevated);
    }

    public string ModuleName => "Ускорение";

    internal RegistryTweakEngine Registry { get; }

    internal PowerPlanSwitcher Power { get; }

    /// <summary>Last collected hardware snapshot, if any.</summary>
    public HardwareSnapshot? CachedHardware
    {
        get
        {
            lock (_lock)
                return _hardwareTask is { IsCompletedSuccessfully: true } t ? t.Result : null;
        }
    }

    /// <summary>Hardware does not change while the app runs, so the snapshot is collected once and shared.</summary>
    public Task<HardwareSnapshot> GetHardwareAsync(bool refresh = false, CancellationToken ct = default)
    {
        Task<HardwareSnapshot> task;
        lock (_lock)
        {
            if (refresh || _hardwareTask is null || _hardwareTask.IsFaulted || _hardwareTask.IsCanceled)
                _hardwareTask = Task.Run(_hardwareProbe);
            task = _hardwareTask;
        }
        return task.WaitAsync(ct);
    }

    /// <summary>Current Windows settings (cheap: powercfg + registry + a couple of WMI queries).</summary>
    public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken ct = default) => Task.Run(() => _settingsProbe(ct), ct);

    /// <summary>Snapshots + engine. Read-only.</summary>
    public async Task<(HardwareSnapshot Hardware, SettingsSnapshot Settings, List<Recommendation> Items)> EvaluateAsync(
        bool refreshHardware, CancellationToken ct)
    {
        var hwTask = GetHardwareAsync(refreshHardware, ct);
        var settingsTask = GetSettingsAsync(ct);
        await Task.WhenAll(hwTask, settingsTask).ConfigureAwait(false);
        var items = RecommendationEngine.Evaluate(hwTask.Result, settingsTask.Result, DateTime.Now);
        AddRevertEntries(items);
        MarkRevertable(items);
        return (hwTask.Result, settingsTask.Result, items);
    }

    /// <summary>
    /// Every tweak the app changed earlier must stay revertable, even when the engine no longer mentions it
    /// (e.g. visual effects on a PC that is not weak): add a "changed by the app" entry for it.
    /// </summary>
    internal void AddRevertEntries(List<Recommendation> items)
    {
        foreach (var fixId in RevertableFixIds())
        {
            if (items.Any(r => r.FixId == fixId))
                continue;
            items.Add(new Recommendation
            {
                Id = "Changed:" + fixId,
                Title = $"Изменено программой: {TweakTitle(fixId)}",
                CurrentValue = "Можно вернуть прежнее значение",
                Kind = RecommendationKind.Good,
                Category = fixId is TweakIds.GameMode or TweakIds.GameDvr or TweakIds.Hags
                    ? RecommendationCategory.Games
                    : RecommendationCategory.Windows,
                FixId = fixId,
            });
        }
    }

    /// <summary>True if the app changed this tweak earlier and remembers what to restore.</summary>
    public bool CanRevert(string? fixId)
    {
        if (fixId is null)
            return false;
        if (fixId == TweakIds.PowerPlan)
            return Power.HasStoredState;
        return RegistryTweaks.Get(fixId) is not null && Registry.HasStoredState(fixId);
    }

    /// <summary>Tweak ids that can be reverted right now.</summary>
    public IReadOnlyList<string> RevertableFixIds() => TweakIds.All.Where(CanRevert).ToList();

    public void MarkRevertable(IEnumerable<Recommendation> items)
    {
        foreach (var item in items)
            item.CanRevert = CanRevert(item.FixId);
    }

    /// <summary>Human-readable title of a tweak (for the "changed by the app" list).</summary>
    public static string TweakTitle(string fixId) => fixId == TweakIds.PowerPlan
        ? "План электропитания"
        : RegistryTweaks.Get(fixId)?.Title ?? fixId;

    /// <summary>Lines describing what "Вернуть" will restore.</summary>
    public IReadOnlyList<string> DescribeRevert(string fixId)
    {
        if (fixId == TweakIds.PowerPlan)
        {
            var name = Power.PreviousName ?? Power.PreviousGuid;
            return name is null ? Array.Empty<string>() : new[] { $"• Будет снова включён план электропитания «{name}»." };
        }
        var tweak = RegistryTweaks.Get(fixId);
        return tweak is null ? Array.Empty<string>() : Registry.DescribeRevert(tweak);
    }

    /// <summary>
    /// Applies an auto-fix recommendation. Does NOT ask for confirmation (the caller already did).
    /// Admin-only tweaks change nothing without elevation and return an explanatory message.
    /// </summary>
    public async Task<TweakResult> ApplyAsync(Recommendation rec, bool createRestorePoint, CancellationToken ct = default)
    {
        if (rec.Kind != RecommendationKind.AutoFix || rec.FixId is null)
            return new TweakResult(false, "Эту рекомендацию нельзя применить автоматически.");

        if (rec.FixId == TweakIds.PowerPlan)
        {
            if (string.IsNullOrEmpty(rec.FixArgument))
                return new TweakResult(false, "Не указан план электропитания. Ничего не изменено.");
            var plans = await Power.ListAsync(ct).ConfigureAwait(false);
            var target = plans.FirstOrDefault(p => string.Equals(p.Guid, rec.FixArgument, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new TweakResult(false, "Нужный план электропитания больше не найден в системе. Ничего не изменено.");
            return await Power.ApplyAsync(target.Guid, PowerPlans.DisplayName(target), ct).ConfigureAwait(false);
        }

        var tweak = RegistryTweaks.Get(rec.FixId);
        if (tweak is null)
            return new TweakResult(false, "Неизвестная настройка. Ничего не изменено.");
        // Checked even for a sandboxed engine, so tests can verify this guard without a real-registry engine.
        if (tweak.NeedsAdmin && !_isElevated())
            return new TweakResult(false, $"Для изменения «{tweak.Title}» нужны права администратора. " +
                                          "Ничего не изменено — перезапустите программу от имени администратора.");
        if (createRestorePoint && tweak.NeedsAdmin && Registry.SandboxSubKey is null)
            await RestorePoint.TryCreateAsync($"Ускорение: {tweak.Title}", ct).ConfigureAwait(false);
        return await Registry.ApplyAsync(tweak, ct).ConfigureAwait(false);
    }

    /// <summary>Restores the stored previous value exactly (deletes values that did not exist). No confirmation here.</summary>
    public async Task<TweakResult> RevertAsync(string fixId, CancellationToken ct = default)
    {
        if (fixId == TweakIds.PowerPlan)
            return await Power.RevertAsync(ct).ConfigureAwait(false);
        var tweak = RegistryTweaks.Get(fixId);
        if (tweak is null)
            return new TweakResult(false, "Неизвестная настройка.");
        if (tweak.NeedsAdmin && !_isElevated())
            return new TweakResult(false, $"Чтобы вернуть «{tweak.Title}», нужны права администратора. Ничего не изменено.");
        return await Registry.RevertAsync(tweak, ct).ConfigureAwait(false);
    }

    /// <summary>Read-only analysis for the dashboard.</summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var (_, _, items) = await EvaluateAsync(false, ct).ConfigureAwait(false);
        return ToHealthIssues(items);
    }

    internal IReadOnlyList<HealthIssue> ToHealthIssues(IEnumerable<Recommendation> items)
    {
        var issues = new List<HealthIssue>();
        foreach (var rec in items)
        {
            if (rec.Kind is RecommendationKind.Good or RecommendationKind.Navigate)
                continue;

            Func<CancellationToken, Task<string>>? fix = null;
            if (rec.Kind == RecommendationKind.AutoFix && (!rec.NeedsAdmin || _isElevated()))
            {
                var captured = rec;
                fix = async ct =>
                {
                    var result = await ApplyAsync(captured, createRestorePoint: true, ct).ConfigureAwait(false);
                    var note = result.Success && result.Changed && !string.IsNullOrEmpty(captured.AfterApplyNote)
                        ? " " + captured.AfterApplyNote
                        : "";
                    return result.Message + note;
                };
            }

            var details = rec.Explanation;
            if (!string.IsNullOrWhiteSpace(rec.CurrentValue))
                details += $"\nСейчас: {rec.CurrentValue}.";

            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = rec.Title,
                Details = details,
                Severity = rec.Kind == RecommendationKind.Info ? IssueSeverity.Info : rec.Severity,
                Gain = rec.Gain,
                FixAsync = fix,
                // Only HKCU-only reversible tweaks and the desktop power plan are safe for "fix everything".
                AutoFixSafe = fix is not null && rec.IsSafe && !rec.NeedsAdmin &&
                              rec.FixId is TweakIds.GameMode or TweakIds.GameDvr or TweakIds.PowerPlan,
                TargetPageKey = "Optimizer",
            });
        }
        return issues;
    }
}
