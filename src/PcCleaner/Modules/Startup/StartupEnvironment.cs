using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

/// <summary>
/// Where the startup service reads and writes. The default points at the real system;
/// tests point the registry roots at HKCU\Software\WaterCa7sPcCleanerTests\Startup\... and the folders at temp dirs,
/// so destructive logic can be verified without touching the real startup configuration.
/// </summary>
internal sealed class StartupEnvironment
{
    /// <summary>Hive of the per-user keys (HKCU).</summary>
    public RegistryHive UserHive { get; init; } = RegistryHive.CurrentUser;

    /// <summary>Per-user "...\CurrentVersion" key containing Run, RunOnce and Explorer\StartupApproved.</summary>
    public string UserSoftware { get; init; } = @"Software\Microsoft\Windows\CurrentVersion";

    /// <summary>Hive of the machine-wide keys (HKLM).</summary>
    public RegistryHive MachineHive { get; init; } = RegistryHive.LocalMachine;

    /// <summary>Machine "...\CurrentVersion" key (64-bit view).</summary>
    public string MachineSoftware { get; init; } = @"SOFTWARE\Microsoft\Windows\CurrentVersion";

    /// <summary>Machine "...\CurrentVersion" key of 32-bit programs (WOW6432Node).</summary>
    public string MachineSoftware32 { get; init; } = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion";

    public string? UserStartupFolder { get; init; }

    public string? CommonStartupFolder { get; init; }

    public bool IncludeTasks { get; init; } = true;

    public bool IncludeServices { get; init; } = true;

    /// <summary>Whether machine-wide changes are allowed (admin rights).</summary>
    public Func<bool> IsElevated { get; init; } = () => Admin.IsElevated;

    /// <summary>Moves a Startup-folder file to the Recycle Bin.</summary>
    public Func<string, bool> RecycleFile { get; init; } = FileTools.SendToRecycleBin;

    /// <summary>Exports "HKCU\..." to a .reg file; returns the file path or null on failure.</summary>
    public Func<string, string, CancellationToken, Task<string?>> BackupKey { get; init; } =
        (key, reason, ct) => RegistryBackup.ExportAsync(key, reason, ct);

    public string UserRunKey => UserSoftware + @"\Run";
    public string UserRunOnceKey => UserSoftware + @"\RunOnce";
    public string MachineRunKey => MachineSoftware + @"\Run";
    public string MachineRunOnceKey => MachineSoftware + @"\RunOnce";
    public string MachineRun32Key => MachineSoftware32 + @"\Run";
    public string MachineRunOnce32Key => MachineSoftware32 + @"\RunOnce";

    public string UserApprovedRun => UserSoftware + @"\Explorer\StartupApproved\Run";
    public string UserApprovedFolder => UserSoftware + @"\Explorer\StartupApproved\StartupFolder";
    public string MachineApprovedRun => MachineSoftware + @"\Explorer\StartupApproved\Run";
    public string MachineApprovedRun32 => MachineSoftware + @"\Explorer\StartupApproved\Run32";
    public string MachineApprovedFolder => MachineSoftware + @"\Explorer\StartupApproved\StartupFolder";

    public static StartupEnvironment CreateDefault() => new()
    {
        UserStartupFolder = SafeFolder(Environment.SpecialFolder.Startup),
        CommonStartupFolder = SafeFolder(Environment.SpecialFolder.CommonStartup),
    };

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => hive.ToString(),
    };

    private static string? SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
