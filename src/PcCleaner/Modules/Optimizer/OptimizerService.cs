using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

public sealed class OptimizerService : IHealthContributor
{
    public string ModuleName => "Ускорение";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
