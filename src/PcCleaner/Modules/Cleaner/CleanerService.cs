using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

public sealed class CleanerService : IHealthContributor
{
    public string ModuleName => "Очистка";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
