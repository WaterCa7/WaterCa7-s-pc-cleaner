using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Tests;

/// <summary>
/// Points app data to a temp folder and makes all dialogs non-interactive (every confirmation = "No").
/// Call TestEnvironment.Init() at the start of tests that touch AppServices/Log/Dialogs.
/// </summary>
public static class TestEnvironment
{
    private static readonly object Lock = new();
    private static bool _initialized;

    public static string DataDir { get; } = Path.Combine(Path.GetTempPath(), "WaterCa7sPcCleaner.Tests", Guid.NewGuid().ToString("N"));

    public static void Init()
    {
        lock (Lock)
        {
            if (_initialized)
                return;
            AppPaths.DataDir = DataDir;
            Dialogs.UseSilentMode();
            AppServices.Settings = new AppSettings();
            _initialized = true;
        }
    }

    /// <summary>Creates a fresh empty folder under the temp test root.</summary>
    public static string NewTempDir(string name = "t")
    {
        var dir = Path.Combine(DataDir, "work", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
