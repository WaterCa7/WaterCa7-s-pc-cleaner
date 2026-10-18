using PcCleaner.Core;

namespace PcCleaner.Tests.Dashboard;

/// <summary>Test double for a module analysis.</summary>
internal sealed class FakeContributor : IHealthContributor
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<HealthIssue>>> _analyze;

    public FakeContributor(string name, Func<CancellationToken, Task<IReadOnlyList<HealthIssue>>> analyze)
    {
        ModuleName = name;
        _analyze = analyze;
    }

    public FakeContributor(string name, params HealthIssue[] issues)
        : this(name, _ => Task.FromResult<IReadOnlyList<HealthIssue>>(issues))
    {
    }

    public string ModuleName { get; }

    public int Calls;

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return _analyze(ct);
    }

    public static HealthIssue Issue(string module, IssueSeverity severity, string title = "Проблема",
        long reclaimable = 0, Func<CancellationToken, Task<string>>? fix = null, bool autoFixSafe = false, bool inFixAll = true)
        => new()
        {
            Module = module,
            Title = title,
            Details = "Описание проблемы",
            Severity = severity,
            ReclaimableBytes = reclaimable,
            FixAsync = fix,
            AutoFixSafe = autoFixSafe,
            InFixAll = inFixAll,
            TargetPageKey = "Cleaner",
        };
}
