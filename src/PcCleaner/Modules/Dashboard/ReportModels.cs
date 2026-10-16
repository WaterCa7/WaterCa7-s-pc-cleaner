using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

public enum ScoreTrend
{
    Same,
    Better,
    Worse,
}

/// <summary>Score change between the previous and the latest smart scan: "Было 52 → стало 78 (+26)".</summary>
public sealed record ScoreDelta(int Previous, int Current, DateTime? PreviousTime)
{
    public int Change => Current - Previous;

    public ScoreTrend Trend => Change switch
    {
        > 0 => ScoreTrend.Better,
        < 0 => ScoreTrend.Worse,
        _ => ScoreTrend.Same,
    };

    /// <summary>"+26", "−12" (typographic minus) or "без изменений".</summary>
    public string ChangeText => Change switch
    {
        > 0 => "+" + Change.ToString(Format.Ru),
        < 0 => "−" + (-Change).ToString(Format.Ru),
        _ => "без изменений",
    };

    public string Text => $"Было {Previous} → стало {Current} ({ChangeText})";

    /// <summary>Tooltip / report line explaining what is compared.</summary>
    public string Explanation(DateTime now) => PreviousTime is { } t
        ? $"Сравнение с прошлой проверкой ({HealthScoring.RelativeTime(t, now)})"
        : "Сравнение с прошлой проверкой";

    /// <summary>Null when there is nothing to compare (first scan, or the current score is unknown).</summary>
    public static ScoreDelta? Create(int? previous, int? current, DateTime? previousTime = null)
        => previous is null || current is null
            ? null
            : new ScoreDelta(Math.Clamp(previous.Value, 0, 100), Math.Clamp(current.Value, 0, 100), previousTime);
}

/// <summary>
/// Reclaimable space split honestly: what the fixes on the dashboard free right away (issues with a FixAsync)
/// and what only the user can free after reviewing it (old downloads, Windows.old, ...).
/// </summary>
public readonly record struct ReclaimableSplit(long AutoBytes, long ManualBytes)
{
    public long TotalBytes => Add(AutoBytes, ManualBytes);

    public bool IsEmpty => AutoBytes <= 0 && ManualBytes <= 0;

    /// <summary>Remaining (not fixed) issues only; negative sizes are ignored; sums saturate instead of overflowing.</summary>
    public static ReclaimableSplit From(IEnumerable<HealthIssue> issues)
    {
        long auto = 0, manual = 0;
        foreach (var issue in issues)
        {
            if (issue is null || issue.IsFixed || issue.ReclaimableBytes <= 0)
                continue;
            if (issue.FixAsync is not null)
                auto = Add(auto, issue.ReclaimableBytes);
            else
                manual = Add(manual, issue.ReclaimableBytes);
        }
        return new ReclaimableSplit(auto, manual);
    }

    /// <summary>"Можно освободить 2,1 ГБ" or null.</summary>
    public string? AutoText => AutoBytes > 0 ? "Можно освободить " + Format.Bytes(AutoBytes) : null;

    /// <summary>"Ещё до 41 ГБ — после проверки вручную" (or a standalone wording when nothing is freed automatically).</summary>
    public string? ManualText => ManualBytes <= 0
        ? null
        : AutoBytes > 0
            ? $"Ещё до {Format.Bytes(ManualBytes)} — после проверки вручную"
            : $"До {Format.Bytes(ManualBytes)} можно освободить после проверки вручную";

    /// <summary>One line for reports: "Можно освободить 2,1 ГБ (ещё до 41 ГБ — после проверки вручную)".</summary>
    public string? SummaryText
    {
        get
        {
            if (AutoBytes > 0 && ManualBytes > 0)
                return $"Можно освободить {Format.Bytes(AutoBytes)} (ещё до {Format.Bytes(ManualBytes)} — после проверки вручную)";
            return AutoText ?? ManualText;
        }
    }

    private static long Add(long a, long b)
    {
        a = Math.Max(0, a);
        b = Math.Max(0, b);
        return b > long.MaxValue - a ? long.MaxValue : a + b;
    }
}

/// <summary>Facts about the computer for the report.</summary>
public sealed record ReportPcInfo
{
    public string ComputerName { get; init; } = "—";

    public string WindowsName { get; init; } = "Windows";

    public string WindowsVersion { get; init; } = "";

    public string CpuName { get; init; } = "";

    public int LogicalProcessors { get; init; }

    public ulong RamBytes { get; init; }

    public IReadOnlyList<string> Gpus { get; init; } = Array.Empty<string>();

    /// <summary>"C:".</summary>
    public string? SystemDrive { get; init; }

    public long SystemDriveFree { get; init; }

    public long SystemDriveTotal { get; init; }

    public TimeSpan? Uptime { get; init; }

    public bool IsAdmin { get; init; }

    /// <summary>Label/value pairs in display order (shared by the HTML and the text report).</summary>
    public IReadOnlyList<(string Label, string Value)> Rows()
    {
        const string na = "нет данных";
        var rows = new List<(string, string)>
        {
            ("Имя компьютера", Clean(ComputerName) ?? na),
            ("Windows", Clean(WindowsName) ?? na),
        };
        if (Clean(WindowsVersion) is { } version)
            rows.Add(("Версия", version));

        var cpu = Clean(CpuName) ?? na;
        if (LogicalProcessors > 0)
            cpu += $" ({Format.Count(LogicalProcessors, "логический процессор", "логических процессора", "логических процессоров")})";
        rows.Add(("Процессор", cpu));
        rows.Add(("Оперативная память", RamBytes > 0 ? Format.Bytes(RamBytes) : na));

        var gpus = Gpus.Select(Clean).OfType<string>().ToList();
        rows.Add((gpus.Count > 1 ? "Видеокарты" : "Видеокарта", gpus.Count == 0 ? na : string.Join("; ", gpus)));

        var driveLabel = string.IsNullOrWhiteSpace(SystemDrive) ? "Системный диск" : $"Системный диск ({SystemDrive.Trim()})";
        rows.Add((driveLabel, SystemDriveTotal > 0
            ? $"свободно {Format.Bytes(Math.Max(0, SystemDriveFree))} из {Format.Bytes(SystemDriveTotal)} " +
              $"({Format.Percent(Math.Round(100.0 * Math.Clamp(SystemDriveFree, 0, SystemDriveTotal) / SystemDriveTotal, 1))})"
            : na));
        rows.Add(("Без перезагрузки", Uptime is { } u ? Format.Duration(u) : na));
        rows.Add(("Права программы", IsAdmin ? "администратор — доступны все проверки" : "обычный пользователь — часть проверок ограничена"));
        return rows;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Result of one module in the report.</summary>
public sealed record ReportModule(string Name, ModuleScanState State, string StatusText, string? Error)
{
    public static ReportModule From(ModuleScanRow row) => new(row.ModuleName, row.State, row.StatusText, row.Error);
}

/// <summary>One finding in the report (with what happened to it during this session).</summary>
public sealed record ReportIssue(
    IssueSeverity Severity,
    string Module,
    string Title,
    string Details,
    string? Gain,
    bool IsFixed,
    string? FixResult)
{
    /// <summary>A fix was tried in this session but did not succeed.</summary>
    public bool FixFailed => !IsFixed && !string.IsNullOrWhiteSpace(FixResult);

    public static ReportIssue From(HealthIssue issue) => new(
        issue.Severity,
        issue.Module ?? "",
        issue.Title ?? "",
        issue.Details ?? "",
        HealthScoring.GainText(issue),
        issue.IsFixed,
        string.IsNullOrWhiteSpace(issue.FixResult) ? null : issue.FixResult.Trim());
}

/// <summary>Findings of one severity, in display order.</summary>
public sealed record ReportGroup(IssueSeverity Severity, IReadOnlyList<ReportIssue> Issues)
{
    public string Title => HealthScoring.GroupTitle(Severity);

    public string Hint => HealthScoring.GroupHint(Severity);
}

/// <summary>Everything a saved / copied report contains. Built by the dashboard, rendered by <see cref="ReportBuilder"/>.</summary>
public sealed record DashboardReport
{
    public DateTime CreatedAt { get; init; }

    public DateTime ScanTime { get; init; }

    public int? Score { get; init; }

    public ScoreDelta? Delta { get; init; }

    public ReclaimableSplit Reclaimable { get; init; }

    public ReportPcInfo Pc { get; init; } = new();

    public IReadOnlyList<ReportModule> Modules { get; init; } = Array.Empty<ReportModule>();

    public IReadOnlyList<ReportGroup> Groups { get; init; } = Array.Empty<ReportGroup>();

    /// <summary>"Не удалось проверить: …" — the score may be incomplete.</summary>
    public string? Warning { get; init; }

    public string AppName { get; init; } = AppInfo.Name;

    public string AppVersion { get; init; } = AppInfo.Version;

    public string RepoUrl { get; init; } = AppInfo.RepoUrl;

    public IEnumerable<ReportIssue> AllIssues => Groups.SelectMany(g => g.Issues);

    public int FixedCount => AllIssues.Count(i => i.IsFixed);

    /// <summary>Remaining findings that lower the score.</summary>
    public int RemainingProblems => AllIssues.Count(i => !i.IsFixed && i.Severity > IssueSeverity.Info);

    public int RemainingTips => AllIssues.Count(i => !i.IsFixed && i.Severity <= IssueSeverity.Info);

    public string Verdict => HealthScoring.Verdict(Score);

    public string VerdictDescription => HealthScoring.VerdictDescription(Score, RemainingProblems, FixedCount > 0);

    /// <summary>"Найдено: 2 проблемы и 1 совет" over every finding of the scan (fixed ones included).</summary>
    public string FindingsText
    {
        get
        {
            var all = AllIssues.ToList();
            var problems = all.Count(i => i.Severity > IssueSeverity.Info);
            return all.Count == 0 ? "Проблем не найдено" : "Найдено: " + HealthScoring.FindingsText(problems, all.Count - problems);
        }
    }

    /// <summary>Groups issues by severity exactly like the dashboard list (most severe first).</summary>
    public static IReadOnlyList<ReportGroup> GroupIssues(IEnumerable<HealthIssue> issues)
        => HealthScoring.Group(issues.Where(i => i is not null))
            .Select(g => new ReportGroup(g.Severity, g.Issues.Select(ReportIssue.From).ToList()))
            .ToList();
}
