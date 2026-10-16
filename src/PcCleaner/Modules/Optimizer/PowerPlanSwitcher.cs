using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>
/// Switches the active power plan with "powercfg /setactive" and remembers the previous plan in
/// AppSettings.ModuleData["Optimizer"]["PowerPlan.Previous"] so it can be restored.
/// Switching plans does not need administrator rights.
/// </summary>
public sealed class PowerPlanSwitcher
{
    public const string PreviousKey = "PowerPlan.Previous";
    public const string PreviousNameKey = "PowerPlan.PreviousName";

    private readonly Func<string, CancellationToken, Task<ProcessResult>> _powercfg;
    private readonly AppSettings? _settings;

    public PowerPlanSwitcher()
        : this(null, null) { }

    /// <param name="powercfg">Runs powercfg.exe with the given arguments (tests pass a fake).</param>
    /// <param name="settings">Storage for the previous plan (defaults to AppServices.Settings).</param>
    internal PowerPlanSwitcher(Func<string, CancellationToken, Task<ProcessResult>>? powercfg, AppSettings? settings)
    {
        _powercfg = powercfg ?? ((args, ct) => ProcessRunner.RunAsync("powercfg.exe", args, TimeSpan.FromSeconds(20), ct));
        _settings = settings;
    }

    private AppSettings Settings => _settings ?? AppServices.Settings;

    public bool HasStoredState => PowerPlans.IsGuid(Settings.GetModuleValue(RegistryTweakEngine.ModuleKey, PreviousKey));

    public string? PreviousGuid => Settings.GetModuleValue(RegistryTweakEngine.ModuleKey, PreviousKey);

    public string? PreviousName => Settings.GetModuleValue(RegistryTweakEngine.ModuleKey, PreviousNameKey);

    /// <summary>Reads the active plan (read-only).</summary>
    public async Task<PowerPlan?> GetActiveAsync(CancellationToken ct = default)
    {
        var result = await _powercfg("/getactivescheme", ct);
        return result.Success ? PowerPlans.ParseActive(result.StdOut) : null;
    }

    /// <summary>Lists available plans (read-only).</summary>
    public async Task<List<PowerPlan>> ListAsync(CancellationToken ct = default)
    {
        var result = await _powercfg("/list", ct);
        return result.Success ? PowerPlans.ParseList(result.StdOut) : new List<PowerPlan>();
    }

    public async Task<TweakResult> ApplyAsync(string targetGuid, string? targetName, CancellationToken ct = default)
    {
        if (!PowerPlans.IsGuid(targetGuid))
            return new TweakResult(false, "Некорректный идентификатор плана электропитания. Ничего не изменено.");
        targetGuid = targetGuid.Trim().ToLowerInvariant();

        var current = await GetActiveAsync(ct);
        if (current is null)
            return new TweakResult(false, "Не удалось узнать текущий план электропитания. Ничего не изменено.");
        var name = string.IsNullOrWhiteSpace(targetName) ? targetGuid : targetName;
        if (current.Guid == targetGuid)
            return new TweakResult(true, $"План «{name}» уже активен.");

        var storedNow = false;
        if (!HasStoredState)
        {
            Store(current.Guid, PowerPlans.DisplayName(current));
            storedNow = true;
        }

        ct.ThrowIfCancellationRequested();
        var result = await _powercfg($"/setactive {targetGuid}", ct);
        if (!result.Success)
        {
            if (storedNow)
                Store(null, null);
            var reason = FirstLine(result.StdErr, result.StdOut);
            Log.Warn($"Ускорение: не удалось включить план «{name}»: {reason}");
            return new TweakResult(false, $"Не удалось включить план «{name}»: {reason}");
        }

        Log.Action($"Ускорение: включён план электропитания «{name}» ({targetGuid}), прежний — «{PowerPlans.DisplayName(current)}» ({current.Guid})");
        return new TweakResult(true, $"Включён план электропитания «{name}».", Changed: true);
    }

    public async Task<TweakResult> RevertAsync(CancellationToken ct = default)
    {
        var guid = PreviousGuid;
        var name = PreviousName ?? guid;
        if (!PowerPlans.IsGuid(guid))
            return new TweakResult(false, "Прежний план электропитания не сохранён — возвращать нечего.");

        var result = await _powercfg($"/setactive {guid}", ct);
        if (!result.Success)
        {
            var reason = FirstLine(result.StdErr, result.StdOut);
            Log.Warn($"Ускорение: не удалось вернуть план «{name}»: {reason}");
            return new TweakResult(false, $"Не удалось вернуть план «{name}» (возможно, он был удалён): {reason}");
        }

        Store(null, null);
        Log.Action($"Ускорение: возвращён прежний план электропитания «{name}» ({guid})");
        return new TweakResult(true, $"Возвращён план электропитания «{name}».", Changed: true);
    }

    private void Store(string? guid, string? name)
    {
        var settings = Settings;
        lock (settings)
        {
            if (!settings.ModuleData.TryGetValue(RegistryTweakEngine.ModuleKey, out var d))
                settings.ModuleData[RegistryTweakEngine.ModuleKey] = d = new Dictionary<string, string>();
            if (guid is null)
            {
                d.Remove(PreviousKey);
                d.Remove(PreviousNameKey);
            }
            else
            {
                d[PreviousKey] = guid;
                d[PreviousNameKey] = name ?? guid;
            }
        }
        settings.Save();
    }

    private static string FirstLine(params string[] texts)
    {
        foreach (var t in texts)
        {
            var line = t.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(line))
                return line;
        }
        return "неизвестная ошибка";
    }
}
