using System.IO;
using Microsoft.Win32;
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

    /// <summary>
    /// HKCU parent of every registry sandbox (HKCU\Software\WaterCa7sPcCleanerTests\&lt;module&gt;\&lt;id&gt;). A sandbox deletes
    /// only its own &lt;id&gt; key: deleting an emptied shared parent while another test creates its key there makes that test
    /// fail ("недопустимая операция над параметром реестра, отмеченным для удаления"). The parents are removed at exit.
    /// </summary>
    public const string RegistryRoot = @"Software\WaterCa7sPcCleanerTests";

    public static void Init()
    {
        lock (Lock)
        {
            if (_initialized)
                return;
            AppPaths.DataDir = DataDir;
            Dialogs.UseSilentMode();
            SystemRestart.Start = NoRestart;
            AppServices.Settings = new AppSettings();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveEmptyRegistryParents();
            _initialized = true;
        }
    }

    /// <summary>
    /// What a test gets for "restart Windows" unless it replaces it with its own fake: nothing is restarted. A test that
    /// answers "Да" to every question (<see cref="Dialogs.ConfirmHandler"/>) would otherwise run the real shutdown.exe.
    /// </summary>
    public static bool NoRestart() => false;

    /// <summary>Removes the empty module keys and then the empty root; keys still holding data (another test run) stay.</summary>
    private static void RemoveEmptyRegistryParents()
    {
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(RegistryRoot, writable: true))
            {
                if (root is null)
                    return;
                foreach (var name in root.GetSubKeyNames())
                {
                    using (var module = root.OpenSubKey(name))
                    {
                        if (module is null || module.SubKeyCount > 0 || module.ValueCount > 0)
                            continue;
                    }
                    root.DeleteSubKey(name, throwOnMissingSubKey: false);
                }
                if (root.SubKeyCount > 0 || root.ValueCount > 0)
                    return;
            }
            Registry.CurrentUser.DeleteSubKey(RegistryRoot, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // best effort: another test run may be using the keys right now
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
