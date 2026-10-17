using PcCleaner.Core.Updates;
using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Disk;
using PcCleaner.Modules.Network;
using PcCleaner.Modules.Optimizer;
using PcCleaner.Modules.Processes;
using PcCleaner.Modules.Programs;
using PcCleaner.Modules.Security;
using PcCleaner.Modules.Startup;

namespace PcCleaner.Core;

/// <summary>Navigation between pages (implemented by MainViewModel).</summary>
public interface INavigator
{
    /// <summary>Opens the page with the given PageViewModel.Key, e.g. "Startup".</summary>
    void NavigateTo(string pageKey);

    /// <summary>
    /// All pages of the shell. <see cref="Admin.RestartElevated"/> uses it to ask before a restart interrupts work
    /// that is still running on any page.
    /// </summary>
    IEnumerable<PageViewModel> Pages => Enumerable.Empty<PageViewModel>();
}

/// <summary>
/// Composition root: one instance of every module service for the whole app.
/// </summary>
public static class AppServices
{
    private static AppSettings? _settings;

    public static AppSettings Settings
    {
        get => _settings ??= AppSettings.Load();
        set => _settings = value;
    }

    public static INavigator? Navigator { get; set; }

    public static CleanerService Cleaner { get; } = new();
    public static ProcessesService Processes { get; } = new();
    public static StartupService Startup { get; } = new();
    public static ProgramsService Programs { get; } = new();
    public static DiskService Disk { get; } = new();
    public static OptimizerService Optimizer { get; } = new();
    public static SecurityService Security { get; } = new();
    public static NetworkService Network { get; } = new();

    private static readonly Lazy<UpdateService> LazyUpdates =
        new(() => new UpdateService(UpdateEnvironment.ForCurrentProcess(), () => Settings));

    /// <summary>Created on first use, after App has set AppPaths.DataDir.</summary>
    public static UpdateService Updates => LazyUpdates.Value;

    /// <summary>All module analyses, used by the dashboard smart-scan.</summary>
    public static IReadOnlyList<IHealthContributor> HealthContributors => new IHealthContributor[]
    {
        Cleaner, Startup, Processes, Programs, Disk, Optimizer, Network, Security,
    };
}
