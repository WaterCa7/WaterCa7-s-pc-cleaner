using System.Text.Json;
using System.Text.Json.Serialization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

/// <summary>
/// What the hero card shows after a restart: the last smart-scan score, time and counters.
/// Stored as JSON in AppSettings.ModuleData["Dashboard"]["LastScan"]. Issues themselves are not persisted.
/// </summary>
public sealed record ScanSummary
{
    internal const string ModuleKey = "Dashboard";
    internal const string ValueKey = "LastScan";

    public int Score { get; init; }

    public DateTime Time { get; init; }

    /// <summary>All remaining findings, tips included.</summary>
    public int IssueCount { get; init; }

    /// <summary>Remaining Info-level findings ("советы") — they do not lower the score and are not called problems.</summary>
    public int TipCount { get; init; }

    /// <summary>Remaining findings that lower the score.</summary>
    [JsonIgnore]
    public int ProblemCount => Math.Max(0, IssueCount - TipCount);

    /// <summary>Critical + High issues.</summary>
    public int ImportantCount { get; init; }

    public int FixableCount { get; init; }

    public long ReclaimableBytes { get; init; }

    /// <summary>Modules that failed or timed out (the score may be incomplete).</summary>
    public int FailedModules { get; init; }

    public static ScanSummary? Load(AppSettings settings)
    {
        var json = settings.GetModuleValue(ModuleKey, ValueKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var summary = JsonSerializer.Deserialize<ScanSummary>(json);
            if (summary is null || summary.Time == default)
                return null;
            return summary with { Score = Math.Clamp(summary.Score, 0, 100) };
        }
        catch (JsonException ex)
        {
            Log.Warn($"Главная: не удалось прочитать результат прошлой проверки: {ex.Message}");
            return null;
        }
    }

    public void Save(AppSettings settings)
        => settings.SetModuleValue(ModuleKey, ValueKey, JsonSerializer.Serialize(this));
}
