using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

public sealed class SecurityService : IHealthContributor
{
    public string ModuleName => "Безопасность";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
