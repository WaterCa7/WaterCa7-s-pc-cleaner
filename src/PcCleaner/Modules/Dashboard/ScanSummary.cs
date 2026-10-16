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

    /// <summary>Everything the remaining findings could free (fixable from the dashboard + manual review).</summary>
    public long ReclaimableBytes { get; init; }

    /// <summary>
    /// Part of <see cref="ReclaimableBytes"/> that only the user can free after reviewing it (findings without a fix,
    /// e.g. old downloads or Windows.old). 0 in summaries saved by older versions.
    /// </summary>
    public long ManualReclaimableBytes { get; init; }

    /// <summary>Modules that failed or timed out (the score may be incomplete).</summary>
    public int FailedModules { get; init; }

    /// <summary>
    /// Score the scan itself measured. <see cref="Score"/> is re-estimated after fixes made in that session; the next
    /// scan compares with this measured value, so "Проверить снова" right after fixing shows the real improvement
    /// (not "Было 100 → стало 94" because a fix freed slightly less than estimated). Null in older summaries.
    /// </summary>
    public int? MeasuredScore { get; init; }

    /// <summary>What the next scan is compared with: the measured score, or <see cref="Score"/> for older summaries.</summary>
    [JsonIgnore]
    public int BaselineScore => MeasuredScore ?? Score;

    /// <summary>Score of the scan before this one (for "Было 52 → стало 78"); null on the first scan.</summary>
    public int? PreviousScore { get; init; }

    public DateTime? PreviousTime { get; init; }

    /// <summary>Reclaimable space split into "fixable from the dashboard" and "after manual review".</summary>
    [JsonIgnore]
    public ReclaimableSplit Reclaimable
    {
        get
        {
            var total = Math.Max(0, ReclaimableBytes);
            var manual = Math.Clamp(ManualReclaimableBytes, 0, total);
            return new ReclaimableSplit(total - manual, manual);
        }
    }

    /// <summary>Change against the previous scan; null on the first scan.</summary>
    [JsonIgnore]
    public ScoreDelta? Delta => ScoreDelta.Create(PreviousScore, Score, PreviousTime);

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
            return summary with
            {
                Score = Math.Clamp(summary.Score, 0, 100),
                MeasuredScore = summary.MeasuredScore is { } m ? Math.Clamp(m, 0, 100) : null,
                PreviousScore = summary.PreviousScore is { } p ? Math.Clamp(p, 0, 100) : null,
            };
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
