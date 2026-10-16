using System.IO;

namespace PcCleaner.Core;

public static class AppPaths
{
    /// <summary>Overridable root (tests point it to a temp folder).</summary>
    public static string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterCa7sPcCleaner");

    public static string LogsDir => Path.Combine(DataDir, "Logs");

    /// <summary>Registry exports (.reg) made before any registry change.</summary>
    public static string BackupsDir => Path.Combine(DataDir, "Backups");

    /// <summary>Files moved out of the way instead of being deleted.</summary>
    public static string QuarantineDir => Path.Combine(DataDir, "Quarantine");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
