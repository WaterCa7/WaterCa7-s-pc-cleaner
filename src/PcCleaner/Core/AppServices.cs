using PcCleaner.Modules.Cleaner;
using PcCleaner.Modules.Disk;
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

    /// <summary>All module analyses, used by the dashboard smart-scan.</summary>
    public static IReadOnlyList<IHealthContributor> HealthContributors => new IHealthContributor[]
    {
        Cleaner, Startup, Processes, Programs, Disk, Optimizer, Security,
    };
}
