using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

public sealed class ProcessesService : IHealthContributor
{
    public string ModuleName => "Процессы";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
