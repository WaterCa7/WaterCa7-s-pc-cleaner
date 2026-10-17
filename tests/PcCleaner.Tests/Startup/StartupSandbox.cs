using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Startup;

namespace PcCleaner.Tests.Startup;

/// <summary>
/// Isolated playground for destructive startup logic: registry roots live under
/// HKCU\Software\WaterCa7sPcCleanerTests\Startup\&lt;guid&gt; and Startup folders/targets in a temp dir.
/// The real Run keys, Startup folders, tasks and services are never touched. Everything is removed on Dispose
/// (the shared parent keys at exit, see <see cref="TestEnvironment.RegistryRoot"/>).
/// </summary>
internal sealed class StartupSandbox : IDisposable
{
    public const string TestsRoot = TestEnvironment.RegistryRoot;
    public const string ModuleRoot = TestsRoot + @"\Startup";

    public StartupSandbox(bool elevated = true)
    {
        TestEnvironment.Init();
        Id = Guid.NewGuid().ToString("N")[..12];
        RegistryRoot = $@"{ModuleRoot}\{Id}";
        Dir = TestEnvironment.NewTempDir("startup");
        UserFolder = Directory.CreateDirectory(Path.Combine(Dir, "UserStartup")).FullName;
        CommonFolder = Directory.CreateDirectory(Path.Combine(Dir, "CommonStartup")).FullName;
        RecycledDir = Directory.CreateDirectory(Path.Combine(Dir, "Recycled")).FullName;
        Elevated = elevated;

        Environment = new StartupEnvironment
        {
            UserHive = RegistryHive.CurrentUser,
            UserSoftware = RegistryRoot + @"\HKCU",
            MachineHive = RegistryHive.CurrentUser,
            MachineSoftware = RegistryRoot + @"\HKLM",
            MachineSoftware32 = RegistryRoot + @"\HKLM32",
            UserStartupFolder = UserFolder,
            CommonStartupFolder = CommonFolder,
            IncludeTasks = false,
            IncludeServices = false,
            IsElevated = () => Elevated,
            RecycleFile = path =>
            {
                File.Move(path, Path.Combine(RecycledDir, Path.GetFileName(path)));
                return true;
            },
            // A private trusted list: tests of other modules use the shared AppServices.Settings at the same time.
            Settings = () => Settings,
        };
        Service = new StartupService(Environment);
    }

    /// <summary>Settings (trusted list) seen by this sandbox's service. Saves go to the temp test data folder.</summary>
    public AppSettings Settings { get; } = new();

    public string Id { get; }

    public string RegistryRoot { get; }

    public string Dir { get; }

    public string UserFolder { get; }

    public string CommonFolder { get; }

    public string RecycledDir { get; }

    public bool Elevated { get; set; }

    public StartupEnvironment Environment { get; }

    public StartupService Service { get; }

    /// <summary>Creates an (empty) file in the sandbox dir and returns its full path.</summary>
    public string CreateFile(string name, string content = "test")
    {
        var path = Path.Combine(Dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string MissingFile(string name) => Path.Combine(Dir, "gone", name);

    public void SetValue(string relativeKey, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{RegistryRoot}\{relativeKey}", writable: true);
        key.SetValue(name, value, kind);
    }

    public object? GetValue(string relativeKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{RegistryRoot}\{relativeKey}");
        return key?.GetValue(name);
    }

    public bool KeyExists(string relativeKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{RegistryRoot}\{relativeKey}");
        return key is not null;
    }

    public static IEnumerable<FileInfo> Backups(string pattern = "*")
    {
        var dir = new DirectoryInfo(AppPaths.BackupsDir);
        return dir.Exists ? dir.EnumerateFiles(pattern) : Enumerable.Empty<FileInfo>();
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryRoot, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // best effort
        }
        // The shared parents are removed at exit by TestEnvironment.
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (Exception)
        {
            // temp folder, best effort
        }
        // .reg exports made by this sandbox carry its id in the file name.
        foreach (var backup in Backups($"*{Id}*"))
        {
            try
            {
                backup.Delete();
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}
