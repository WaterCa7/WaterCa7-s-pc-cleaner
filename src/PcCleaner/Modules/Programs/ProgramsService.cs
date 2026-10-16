using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

public sealed class ProgramsService : IHealthContributor
{
    public string ModuleName => "Программы";

    public Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<HealthIssue>>(Array.Empty<HealthIssue>());
}
