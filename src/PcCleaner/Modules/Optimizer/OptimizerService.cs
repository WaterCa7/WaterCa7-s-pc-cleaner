using PcCleaner.Core;
using PcCleaner.Core.Localization;

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
        BatteryReport = new BatteryReportGenerator();
        // The CPU is sampled only after our own hardware scan (WMI) has finished, so it is not counted as load.
        var settingsProbe = new SettingsProbe(Power, WaitForHardwareScanAsync);
        _hardwareProbe = HardwareProbe.CollectAsync;
        _settingsProbe = settingsProbe.CollectAsync;
        _isElevated = () => Admin.IsElevated;
    }

    /// <summary>For tests: synthetic snapshots and sandboxed tweak engines.</summary>
    internal OptimizerService(Func<Task<HardwareSnapshot>> hardwareProbe, Func<CancellationToken, Task<SettingsSnapshot>> settingsProbe,
        RegistryTweakEngine registry, PowerPlanSwitcher power, Func<bool>? isElevated = null, BatteryReportGenerator? batteryReport = null)
    {
        _hardwareProbe = hardwareProbe;
        _settingsProbe = settingsProbe;
        Registry = registry;
        Power = power;
        BatteryReport = batteryReport ?? new BatteryReportGenerator();
        _isElevated = isElevated ?? (() => Admin.IsElevated);
    }

    public string ModuleName => T._("Ускорение");

    internal RegistryTweakEngine Registry { get; }

    internal PowerPlanSwitcher Power { get; }

    internal BatteryReportGenerator BatteryReport { get; }

    /// <summary>Creates the Windows battery report (a read-only HTML report in Documents). No confirmation needed.</summary>
    public Task<BatteryReportResult> CreateBatteryReportAsync(CancellationToken ct = default) => BatteryReport.CreateAsync(ct);

    /// <summary>Completes when the hardware scan that is running now (if any) has finished; never throws.</summary>
    private async Task WaitForHardwareScanAsync()
    {
        Task? running;
        lock (_lock)
            running = _hardwareTask;
        if (running is null)
            return;
        try
        {
            await running.ConfigureAwait(false);
        }
        catch
        {
            // the caller only needs the scan to be over
        }
    }

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
                Title = T.F("Изменено программой: {0}", TweakTitle(fixId)),
                CurrentValue = T._("Можно вернуть прежнее значение"),
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
        ? T._("План электропитания")
        : RegistryTweaks.Get(fixId)?.Title ?? fixId;

    /// <summary>Lines describing what "Вернуть" will restore.</summary>
    public IReadOnlyList<string> DescribeRevert(string fixId)
    {
        if (fixId == TweakIds.PowerPlan)
        {
            var name = Power.PreviousName ?? Power.PreviousGuid;
            return name is null ? Array.Empty<string>() : new[] { T.F("• Будет снова включён план электропитания «{0}».", name) };
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
            return new TweakResult(false, T._("Эту рекомендацию нельзя применить автоматически."));

        if (rec.FixId == TweakIds.PowerPlan)
        {
            if (string.IsNullOrEmpty(rec.FixArgument))
                return new TweakResult(false, T._("Не указан план электропитания. Ничего не изменено."));
            var plans = await Power.ListAsync(ct).ConfigureAwait(false);
            var target = plans.FirstOrDefault(p => string.Equals(p.Guid, rec.FixArgument, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new TweakResult(false, T._("Нужный план электропитания больше не найден в системе. Ничего не изменено."));
            return await Power.ApplyAsync(target.Guid, PowerPlans.DisplayName(target), ct).ConfigureAwait(false);
        }

        var tweak = RegistryTweaks.Get(rec.FixId);
        if (tweak is null)
            return new TweakResult(false, T._("Неизвестная настройка. Ничего не изменено."));
        // Checked even for a sandboxed engine, so tests can verify this guard without a real-registry engine.
        if (tweak.NeedsAdmin && !_isElevated())
            return new TweakResult(false, T.F("Для изменения «{0}» нужны права администратора. Ничего не изменено — перезапустите программу от имени администратора.", tweak.Title));
        if (createRestorePoint && tweak.NeedsAdmin && Registry.SandboxSubKey is null)
            await RestorePoint.TryCreateAsync(T.F("Ускорение: {0}", tweak.Title), ct).ConfigureAwait(false);
        return await Registry.ApplyAsync(tweak, ct).ConfigureAwait(false);
    }

    /// <summary>Restores the stored previous value exactly (deletes values that did not exist). No confirmation here.</summary>
    public async Task<TweakResult> RevertAsync(string fixId, CancellationToken ct = default)
    {
        if (fixId == TweakIds.PowerPlan)
            return await Power.RevertAsync(ct).ConfigureAwait(false);
        var tweak = RegistryTweaks.Get(fixId);
        if (tweak is null)
            return new TweakResult(false, T._("Неизвестная настройка."));
        if (tweak.NeedsAdmin && !_isElevated())
            return new TweakResult(false, T.F("Чтобы вернуть «{0}», нужны права администратора. Ничего не изменено.", tweak.Title));
        return await Registry.RevertAsync(tweak, ct).ConfigureAwait(false);
    }

    /// <summary>Read-only analysis for the dashboard.</summary>
    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var (_, _, items) = await EvaluateAsync(false, ct).ConfigureAwait(false);
        return ToHealthIssues(items);
    }

    /// <summary>
    /// Recommendations shown only on this page, never on the dashboard: another module reports the same symptom there.
    /// High RAM load belongs to "Процессы", which reads the same memory counter and also names the biggest consumers —
    /// reporting it from both modules would penalise one cause twice in the health score.
    /// </summary>
    internal static readonly IReadOnlySet<string> PageOnlyIds = new HashSet<string>(StringComparer.Ordinal) { "RamLoad" };

    /// <summary>
    /// Low-risk one-click fixes: their dashboard row gets the «Безопасно исправить» badge (AutoFixSafe). The badge only
    /// informs; «Исправить всё» is gated by InFixAll, not by this. Turning off Xbox Game Bar recording is NOT among them:
    /// it takes away clip and screenshot capture, so it is not marked low-risk, and «Исправить всё» leaves it out too.
    /// </summary>
    internal static bool IsLowRiskFix(string? fixId) => fixId is TweakIds.GameMode or TweakIds.PowerPlan;

    /// <summary>Text of the dashboard button of the two "restart Windows" tips.</summary>
    internal static string RestartFixLabel => T._("Перезагрузить");

    /// <summary>Shown in the details of the restart tips, i.e. in the dashboard's question before the restart.</summary>
    internal static string RestartWarning => T._("Сохраните работу во всех программах: компьютер перезагрузится через несколько секунд.");

    internal IReadOnlyList<HealthIssue> ToHealthIssues(IEnumerable<Recommendation> items)
    {
        var issues = new List<HealthIssue>();
        foreach (var rec in items)
        {
            if (rec.Kind is RecommendationKind.Good or RecommendationKind.Navigate)
                continue;
            if (PageOnlyIds.Contains(rec.Id))
                continue;

            Func<CancellationToken, Task<string>>? fix = null;
            var fixLabel = HealthIssue.DefaultFixLabel;
            // Turning off Xbox Game Bar recording takes clip and screenshot capture away: the user decides on it per item,
            // «Исправить всё» does not press its button (nor the one that only says administrator rights are needed).
            var inFixAll = rec.FixId != TweakIds.GameDvr;
            var appliesTweak = rec.Kind == RecommendationKind.AutoFix && (!rec.NeedsAdmin || _isElevated());
            var consequence = appliesTweak ? rec.AfterApplyNote : null;
            if (appliesTweak)
            {
                var captured = rec;
                fix = async ct =>
                {
                    var result = await ApplyAsync(captured, createRestorePoint: true, ct).ConfigureAwait(false);
                    return DashboardResultText(result, captured.AfterApplyNote);
                };
            }
            else if (rec.Kind == RecommendationKind.AutoFix)
            {
                // The tweak needs administrator rights and the program has none. The row keeps its button: it changes
                // nothing and says why, so the dashboard can offer «Перезапустить от имени администратора».
                var text = AdminRequiredText(rec);
                fix = _ => Task.FromResult(text);
            }
            else if (rec.OffersRestart)
            {
                // Manual advice is never fixed by the program, except "restart Windows". A restart loses unsaved work in
                // other programs, so «Исправить всё» does not press it. The dashboard's own «Исправить проблему» question
                // (it shows the warning below) is the only one: SystemRestart does not ask a second time.
                fix = SystemRestart.RestartWithoutAskingAsync;
                fixLabel = RestartFixLabel;
                inFixAll = false;
                consequence = RestartWarning;
            }

            var details = rec.Explanation;
            if (!string.IsNullOrWhiteSpace(rec.CurrentValue))
                details += "\n" + T.F("Сейчас: {0}.", rec.CurrentValue);
            // The consequence of the fix must be visible BEFORE it runs (the dashboard's confirmation shows these details).
            if (!string.IsNullOrWhiteSpace(consequence))
                details += "\n" + T.F("Важно: {0}", consequence);

            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = rec.Title,
                Details = details,
                Severity = rec.Kind == RecommendationKind.Info ? IssueSeverity.Info : rec.Severity,
                Gain = rec.Gain,
                FixAsync = fix,
                FixLabel = fixLabel,
                InFixAll = inFixAll,
                Consequence = string.IsNullOrWhiteSpace(consequence) ? null : consequence,
                // Only reversible tweaks that take nothing away from the user get the «Безопасно исправить» badge.
                AutoFixSafe = appliesTweak && rec.IsSafe && !rec.NeedsAdmin && IsLowRiskFix(rec.FixId),
                SettingsUri = rec.SettingsUri,
                TargetPageKey = "Optimizer",
            });
        }
        return issues;
    }

    /// <summary>What the dashboard row says when a tweak needs administrator rights and the program has none.</summary>
    private static string AdminRequiredText(Recommendation rec)
        => rec.FixId == TweakIds.Hags
            ? T._("Нужны права администратора: планирование GPU включается только с ними.")
            : T._("Нужны права администратора: без них эту настройку не изменить.");

    /// <summary>Prefix of a failed fix's text: the dashboard reads a result that starts with it as "не исправлено".</summary>
    internal static string FailurePrefix => T.F("Не удалось исправить: {0}", "");

    /// <summary>
    /// The text the dashboard gets for a fix. HealthIssue.FixAsync has no success flag, so a failed tweak
    /// (<see cref="TweakResult.Success"/> = false) must say so in words the dashboard recognises: such a text starts with
    /// «Не удалось». A success keeps its own text, plus what the user has to do afterwards (e.g. restart Windows).
    /// </summary>
    internal static string DashboardResultText(TweakResult result, string? afterApplyNote)
    {
        var message = string.IsNullOrWhiteSpace(result.Message) ? "" : result.Message.Trim();
        if (!result.Success)
        {
            // The producer says that its message already opens with the failure sentence (OpensWithFailure); a Russian result made
            // without the flag still counts by its first words (the rule of the Russian dashboard).
            // l10n:data-begin — the Russian opening of a failure text, matched and never shown
            if (result.OpensWithFailure || message.StartsWith("Не удалось", StringComparison.OrdinalIgnoreCase))
                return message;
            // l10n:data-end
            return T.F("Не удалось исправить: {0}", message.Length == 0 ? T._("причина неизвестна.") : LowerFirst(message));
        }
        var note = result.Changed && !string.IsNullOrEmpty(afterApplyNote) ? " " + afterApplyNote : "";
        return message + note;
    }

    /// <summary>"Для изменения…" → "для изменения…" (an abbreviation such as "BIOS…" or a quote «…» stays as is).</summary>
    private static string LowerFirst(string text)
        => !T.IsEnglish && text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1])
            ? char.ToLower(text[0], Format.Ru) + text[1..]
            : text;
}
