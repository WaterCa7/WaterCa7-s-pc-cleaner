using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PcCleaner.Modules.Cleaner;

/// <summary>
/// The well-known folders the cleaner works with. Production uses the real system folders;
/// tests point every folder into a temp directory so nothing real is ever touched.
/// </summary>
public sealed class CleanerEnvironment
{
    public required string Temp { get; init; }

    public required string LocalAppData { get; init; }

    public required string RoamingAppData { get; init; }

    public required string ProgramData { get; init; }

    public required string WindowsDir { get; init; }

    public required string UserProfile { get; init; }

    /// <summary>Returns the names (no .exe, case-insensitive) of running processes.</summary>
    public Func<IReadOnlySet<string>> RunningProcessNames { get; init; } = GetRunningProcessNames;

    /// <summary>
    /// Opens the Windows "Очистка диска" wizard (cleanmgr.exe) for a drive letter such as "C". Returns false if it could not
    /// be started. Tests replace it, so they never open the real wizard.
    /// </summary>
    public Func<string, bool> StartDiskCleanup { get; init; } = StartDiskCleanupProcess;

    /// <summary>
    /// True while a "Очистка диска" window is open (a second one must not be started). A hidden cleanmgr started by
    /// Windows' own maintenance does not count.
    /// </summary>
    public Func<bool> IsDiskCleanupRunning { get; init; } = () => IsProcessRunning("cleanmgr");

    public string SystemDrive => Path.GetPathRoot(WindowsDir) ?? @"C:\";

    public string DeliveryOptimizationCache => Path.Combine(WindowsDir,
        @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");

    public static CleanerEnvironment FromSystem()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
            windows = @"C:\Windows";
        return new CleanerEnvironment
        {
            Temp = SafePathTools.Normalize(Path.GetTempPath()) ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            WindowsDir = windows,
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
    }

    /// <summary>
    /// A fake environment where every well-known folder lives under <paramref name="root"/>. The Disk Cleanup wizard is
    /// never really started: <paramref name="startDiskCleanup"/> (default: report a failure) is called instead.
    /// </summary>
    internal static CleanerEnvironment ForTests(string root, IReadOnlySet<string>? running = null,
        Func<string, bool>? startDiskCleanup = null, Func<bool>? isDiskCleanupRunning = null)
    {
        var profile = Path.Combine(root, "Users", "Test");
        var local = Path.Combine(profile, "AppData", "Local");
        return new CleanerEnvironment
        {
            Temp = Path.Combine(local, "Temp"),
            LocalAppData = local,
            RoamingAppData = Path.Combine(profile, "AppData", "Roaming"),
            ProgramData = Path.Combine(root, "ProgramData"),
            WindowsDir = Path.Combine(root, "Windows"),
            UserProfile = profile,
            RunningProcessNames = () => running ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StartDiskCleanup = startDiskCleanup ?? (_ => false),
            IsDiskCleanupRunning = isDiskCleanupRunning ?? (() => false),
        };
    }

    private static bool StartDiskCleanupProcess(string driveLetter)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), $"/d {driveLetter}")
            {
                UseShellExecute = true,
            })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Core.Log.Warn($"Не удалось открыть мастер очистки диска: {ex.Message}");
            return false;
        }
    }

    internal static bool IsProcessRunning(string name)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        // Only an instance with a window counts: Windows runs "cleanmgr /autoclean" hidden (the SilentCleanup task),
        // and then the button must still open the wizard instead of saying it is already open.
        var anyWindow = false;
        foreach (var p in processes)
        {
            try
            {
                anyWindow |= p.MainWindowHandle != IntPtr.Zero;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // exited meanwhile
            }
            p.Dispose();
        }
        return anyWindow;
    }

    public static IReadOnlySet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return names;
        }
        foreach (var p in processes)
        {
            try
            {
                names.Add(p.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // exited meanwhile
            }
            finally
            {
                p.Dispose();
            }
        }
        return names;
    }
}

/// <summary>Path helpers shared by the guard, the walker and the engine.</summary>
internal static class SafePathTools
{
    /// <summary>
    /// Full, long-name, trailing-separator-free form of a local path; null for relative, UNC or device paths.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
            return null; // UNC or \\?\ device paths are never cleaning roots
        if (!Path.IsPathFullyQualified(p))
            return null;
        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        p = ToLongPath(p);
        var root = Path.GetPathRoot(p);
        if (root is not null && p.Length > root.Length)
            p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="baseDir"/> or lies inside it.</summary>
    public static bool IsSameOrUnder(string path, string baseDir)
        => string.Equals(path, baseDir, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(path, baseDir);

    /// <summary>True if <paramref name="path"/> lies inside <paramref name="baseDir"/> (not equal).</summary>
    public static bool IsStrictlyUnder(string path, string baseDir)
    {
        if (path.Length <= baseDir.Length)
            return false;
        var prefix = baseDir.EndsWith('\\') ? baseDir : baseDir + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string normalized)
    {
        var root = Path.GetPathRoot(normalized);
        return root is not null && string.Equals(root.TrimEnd('\\'), normalized.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Expands 8.3 short names (C:\Users\WATERC~1) so prefix comparisons are reliable. When the path itself does not
    /// exist (yet), its longest existing parent is expanded and the rest is kept as written.
    /// </summary>
    private static string ToLongPath(string path)
    {
        if (!path.Contains('~'))
            return path;
        var probe = path;
        var suffix = "";
        while (true)
        {
            var expanded = GetLongPath(probe);
            if (expanded is not null)
                return suffix.Length == 0 ? expanded : expanded.TrimEnd('\\') + suffix;
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent.Length >= probe.Length)
                return path;
            suffix = probe[parent.TrimEnd('\\').Length..] + suffix;
            probe = parent;
        }
    }

    private static string? GetLongPath(string path)
    {
        try
        {
            var buffer = new char[1024];
            var len = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            if (len > 0 && len < buffer.Length)
                return new string(buffer, 0, (int)len);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // keep the original
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);
}
