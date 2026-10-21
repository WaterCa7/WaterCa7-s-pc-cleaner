using PcCleaner.Core;
using PcCleaner.Core.Localization;

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
        > 0 => "+" + Change.ToString(T.Culture),
        < 0 => "−" + (-Change).ToString(T.Culture),
        _ => T._("без изменений"),
    };

    public string Text => T.F("Было {0} → стало {1} ({2})", Previous, Current, ChangeText);

    /// <summary>Tooltip / report line explaining what is compared.</summary>
    public string Explanation(DateTime now) => PreviousTime is { } t
        ? T.F("Сравнение с прошлой проверкой ({0})", HealthScoring.RelativeTime(t, now))
        : T._("Сравнение с прошлой проверкой");

    /// <summary>Null when there is nothing to compare (first scan, or the current score is unknown).</summary>
    public static ScoreDelta? Create(int? previous, int? current, DateTime? previousTime = null)
        => previous is null || current is null
            ? null
            : new ScoreDelta(Math.Clamp(previous.Value, 0, 100), Math.Clamp(current.Value, 0, 100), previousTime);
}

/// <summary>
/// Reclaimable space split honestly: what the fix buttons on the dashboard free right away (issues with a FixAsync that
/// does the freeing: the Recycle Bin, old downloads moved to it) and what the user still frees themselves (Windows.old:
/// its button only opens «Очистка диска»; findings without a fix).
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
            // A fix that only opens a tool (Windows.old -> Disk Cleanup) frees nothing by itself: the user does that part.
            if (issue.FixAsync is not null && !issue.OnlyOpensTool)
                auto = Add(auto, issue.ReclaimableBytes);
            else
                manual = Add(manual, issue.ReclaimableBytes);
        }
        return new ReclaimableSplit(auto, manual);
    }

    /// <summary>"Можно освободить 2,1 ГБ" or null.</summary>
    public string? AutoText => AutoBytes > 0 ? T.F("Можно освободить {0}", Format.Bytes(AutoBytes)) : null;

    // Not "после проверки вручную": everywhere else in the app "проверка" means the scan, while this space is freed
    // only if the user decides to delete it and does it themselves (Windows.old in «Очистка диска», ...): "если решите удалить сами".

    /// <summary>"Ещё до 41 ГБ — если решите удалить сами" (or a standalone wording when nothing is freed automatically).</summary>
    public string? ManualText => ManualBytes <= 0
        ? null
        : AutoBytes > 0
            ? T.F("Ещё до {0} — если решите удалить сами", Format.Bytes(ManualBytes))
            : T.F("До {0} можно освободить, если решите удалить сами", Format.Bytes(ManualBytes));

    /// <summary>One line for reports: "Можно освободить 2,1 ГБ (ещё до 41 ГБ — если решите удалить сами)".</summary>
    public string? SummaryText
    {
        get
        {
            if (AutoBytes > 0 && ManualBytes > 0)
                return T.F("Можно освободить {0} (ещё до {1} — если решите удалить сами)", Format.Bytes(AutoBytes), Format.Bytes(ManualBytes));
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
        var na = T._("нет данных");
        var rows = new List<(string, string)>
        {
            (T._("Имя компьютера"), Clean(ComputerName) ?? na),
            ("Windows", Clean(WindowsName) ?? na),
        };
        if (Clean(WindowsVersion) is { } version)
            rows.Add((T._("Версия"), version));

        var cpu = Clean(CpuName) ?? na;
        if (LogicalProcessors > 0)
            cpu += $" ({Format.Count(LogicalProcessors, "логический процессор", "логических процессора", "логических процессоров")})";
        rows.Add((T._("Процессор"), cpu));
        rows.Add((T._("Оперативная память"), RamBytes > 0 ? Format.Bytes(RamBytes) : na));

        var gpus = Gpus.Select(Clean).OfType<string>().ToList();
        rows.Add((gpus.Count > 1 ? T._("Видеокарты") : T._("Видеокарта"), gpus.Count == 0 ? na : string.Join("; ", gpus)));

        var driveLabel = string.IsNullOrWhiteSpace(SystemDrive) ? T._("Системный диск") : T.F("Системный диск ({0})", SystemDrive.Trim());
        rows.Add((driveLabel, SystemDriveTotal > 0
            ? T.F("свободно {0} из {1} ({2})", Format.Bytes(Math.Max(0, SystemDriveFree)), Format.Bytes(SystemDriveTotal),
                Format.Percent(Math.Round(100.0 * Math.Clamp(SystemDriveFree, 0, SystemDriveTotal) / SystemDriveTotal, 1)))
            : na));
        rows.Add((T._("Без перезагрузки"), Uptime is { } u ? Format.Duration(u) : na));
        rows.Add((T._("Права программы"), IsAdmin ? T._("администратор — доступны все проверки") : T._("обычный пользователь — часть проверок ограничена")));
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

    /// <summary>Modules that failed or timed out: the score only counts the others (verdict "Проверка неполная").</summary>
    public int FailedModules { get; init; }

    public bool IsIncomplete => FailedModules > 0;

    public string AppName { get; init; } = AppInfo.Name;

    public string AppVersion { get; init; } = AppInfo.Version;

    public string RepoUrl { get; init; } = AppInfo.RepoUrl;

    public IEnumerable<ReportIssue> AllIssues => Groups.SelectMany(g => g.Issues);

    public int FixedCount => AllIssues.Count(i => i.IsFixed);

    /// <summary>Remaining findings that lower the score.</summary>
    public int RemainingProblems => AllIssues.Count(i => !i.IsFixed && i.Severity > IssueSeverity.Info);

    public int RemainingTips => AllIssues.Count(i => !i.IsFixed && i.Severity <= IssueSeverity.Info);

    public string Verdict => HealthScoring.Verdict(Score, FailedModules);

    public string VerdictDescription => HealthScoring.VerdictDescription(Score, RemainingProblems, FixedCount > 0, FailedModules);

    /// <summary>"Найдено: 2 проблемы и 1 совет" over every finding of the scan (fixed ones included).</summary>
    public string FindingsText
    {
        get
        {
            var all = AllIssues.ToList();
            var problems = all.Count(i => i.Severity > IssueSeverity.Info);
            return all.Count == 0 ? T._("Проблем не найдено") : T.F("Найдено: {0}", HealthScoring.FindingsText(problems, all.Count - problems));
        }
    }

    /// <summary>Groups issues by severity exactly like the dashboard list (most severe first).</summary>
    public static IReadOnlyList<ReportGroup> GroupIssues(IEnumerable<HealthIssue> issues)
        => HealthScoring.Group(issues.Where(i => i is not null))
            .Select(g => new ReportGroup(g.Severity, g.Issues.Select(ReportIssue.From).ToList()))
            .ToList();
}
