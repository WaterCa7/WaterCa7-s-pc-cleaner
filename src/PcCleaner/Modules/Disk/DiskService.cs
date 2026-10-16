using PcCleaner.Core;

namespace PcCleaner.Modules.Disk;

public sealed class DiskService : IHealthContributor
{
    public string ModuleName => "Диски";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
