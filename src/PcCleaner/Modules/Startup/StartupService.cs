using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

public sealed class StartupService : IHealthContributor
{
    public string ModuleName => "Автозагрузка";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
