using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

public sealed class NetworkService : IHealthContributor
{
    public string ModuleName => "Сеть";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
