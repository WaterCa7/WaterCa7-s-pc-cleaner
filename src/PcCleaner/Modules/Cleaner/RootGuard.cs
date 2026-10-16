using System.IO;
using System.Runtime.InteropServices;

namespace PcCleaner.Modules.Cleaner;

/// <summary>
/// Last line of defence: refuses any cleaning root that is a drive root, a system or personal folder,
/// a parent of such a folder, or anything outside the expected base folders (AppData, ProgramData, Windows, Temp).
/// </summary>
internal sealed class RootGuard
{
    /// <summary>Single files may only be deleted directly if they have one of these extensions.</summary>
    private static readonly string[] SingleFileExtensions = { ".dmp" };

    private static readonly Lazy<SystemFolders> System = new(SystemFolders.Load);

    private readonly List<string> _protectedFolders = new();
    private readonly List<string> _protectedTrees = new();
    private readonly List<(string Path, bool AllowEqual)> _allowedBases = new();

    /// <param name="protectedFolders">May never be a root and may never be inside a root.</param>
    /// <param name="protectedTrees">Neither these folders nor anything inside them may be a root.</param>
    /// <param name="allowedBases">A root must lie inside one of these (or equal it when AllowEqual).</param>
    public RootGuard(IEnumerable<string?> protectedFolders, IEnumerable<string?> protectedTrees,
        IEnumerable<(string? Path, bool AllowEqual)> allowedBases)
    {
        AddNormalized(_protectedFolders, System.Value.ProtectedFolders.Concat(protectedFolders));
        AddNormalized(_protectedTrees, System.Value.ProtectedTrees.Concat(protectedTrees));
        foreach (var (path, allowEqual) in allowedBases)
        {
            var n = SafePathTools.Normalize(path);
            if (n is not null && !SafePathTools.IsDriveRoot(n))
                _allowedBases.Add((n, allowEqual));
        }
    }

    /// <summary>Guard for a cleaner environment: roots must be inside its AppData/ProgramData/Windows/Temp.</summary>
    public static RootGuard ForEnvironment(CleanerEnvironment env)
    {
        var protectedFolders = new[]
        {
            env.WindowsDir, env.UserProfile, Path.GetDirectoryName(env.UserProfile), env.LocalAppData, env.RoamingAppData,
            env.ProgramData,
            Path.Combine(env.LocalAppData, "Microsoft"), Path.Combine(env.LocalAppData, "Microsoft", "Windows"),
            Path.Combine(env.LocalAppData, "Packages"), Path.Combine(env.RoamingAppData, "Microsoft"),
            Path.Combine(env.RoamingAppData, "Microsoft", "Windows"), Path.Combine(env.ProgramData, "Microsoft"),
            Path.Combine(env.ProgramData, "Microsoft", "Windows"), Path.Combine(env.WindowsDir, "ServiceProfiles"),
        };
        var protectedTrees = new[]
        {
            Path.Combine(env.WindowsDir, "System32"), Path.Combine(env.WindowsDir, "SysWOW64"),
            Path.Combine(env.WindowsDir, "WinSxS"), Path.Combine(env.WindowsDir, "Installer"),
            Path.Combine(env.WindowsDir, "servicing"), Path.Combine(env.WindowsDir, "assembly"),
            Path.Combine(env.WindowsDir, "Microsoft.NET"), Path.Combine(env.WindowsDir, "Boot"),
            Path.Combine(env.WindowsDir, "Fonts"), Path.Combine(env.WindowsDir, "INF"),
            Path.Combine(env.UserProfile, "Desktop"), Path.Combine(env.UserProfile, "Documents"),
            Path.Combine(env.UserProfile, "Downloads"), Path.Combine(env.UserProfile, "Pictures"),
            Path.Combine(env.UserProfile, "Videos"), Path.Combine(env.UserProfile, "Music"),
            Path.Combine(env.UserProfile, "OneDrive"),
        };
        var allowed = new (string?, bool)[]
        {
            (env.LocalAppData, false), (env.RoamingAppData, false), (env.ProgramData, false), (env.WindowsDir, false),
            (env.Temp, true),
        };
        return new RootGuard(protectedFolders, protectedTrees, allowed);
    }

    /// <summary>Guard that only allows roots inside the given test folders (system folders stay protected).</summary>
    internal static RootGuard ForTests(params string[] allowedBases)
        => new(Array.Empty<string>(), Array.Empty<string>(), allowedBases.Select(b => ((string?)b, true)));

    public bool IsSafeRoot(CleanRoot root, out string reason)
        => root.IsSingleFile ? IsSafeFile(root.Path, out reason) : IsSafeFolder(root.Path, out reason);

    public bool IsSafeFolder(string folder, out string reason)
    {
        var n = SafePathTools.Normalize(folder);
        if (n is null)
        {
            reason = "путь не является полным локальным путём";
            return false;
        }
        if (SafePathTools.IsDriveRoot(n))
        {
            reason = "это корень диска";
            return false;
        }
        foreach (var p in _protectedFolders)
        {
            if (string.Equals(n, p, StringComparison.OrdinalIgnoreCase))
            {
                reason = "это системная или личная папка";
                return false;
            }
            if (SafePathTools.IsStrictlyUnder(p, n))
            {
                reason = $"внутри находится защищённая папка {p}";
                return false;
            }
        }
        foreach (var t in _protectedTrees)
        {
            if (SafePathTools.IsSameOrUnder(n, t) || SafePathTools.IsStrictlyUnder(t, n))
            {
                reason = $"папка относится к защищённой {t}";
                return false;
            }
        }
        if (!IsInsideAllowedBase(n, allowEqual: true))
        {
            reason = "папка находится вне разрешённых для очистки мест";
            return false;
        }
        reason = "";
        return true;
    }

    public bool IsSafeFile(string file, out string reason)
    {
        var n = SafePathTools.Normalize(file);
        if (n is null || SafePathTools.IsDriveRoot(n))
        {
            reason = "путь не является полным локальным путём";
            return false;
        }
        if (!SingleFileExtensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase))
        {
            reason = "такой тип файла нельзя удалять напрямую";
            return false;
        }
        foreach (var t in _protectedTrees)
        {
            if (SafePathTools.IsSameOrUnder(n, t))
            {
                reason = $"файл находится в защищённой папке {t}";
                return false;
            }
        }
        if (!IsInsideAllowedBase(n, allowEqual: false))
        {
            reason = "файл находится вне разрешённых для очистки мест";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// True when an existing folder between the allowed base and the root is a junction/symlink, i.e. the root
    /// physically lies somewhere else (a cache moved to another drive, or a link into Documents). Such roots are
    /// skipped just like junctions inside a root. The root itself is checked by the walker / the deletion.
    /// </summary>
    public bool IsRedirected(string normalizedRoot, bool isSingleFile)
    {
        string? basePath = null;
        foreach (var (path, _) in _allowedBases)
        {
            if (SafePathTools.IsSameOrUnder(normalizedRoot, path) && (basePath is null || path.Length > basePath.Length))
                basePath = path;
        }
        if (basePath is null)
            return true;

        var current = Path.GetDirectoryName(normalizedRoot);
        while (current is not null && SafePathTools.IsStrictlyUnder(current, basePath))
        {
            if (IsReparsePoint(current))
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static bool IsReparsePoint(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.Security.SecurityException)
        {
            return true; // cannot tell: be conservative
        }
    }

    private bool IsInsideAllowedBase(string normalized, bool allowEqual)
    {
        foreach (var (path, baseAllowsEqual) in _allowedBases)
        {
            if (SafePathTools.IsStrictlyUnder(normalized, path))
                return true;
            if (allowEqual && baseAllowsEqual && string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void AddNormalized(List<string> target, IEnumerable<string?> paths)
    {
        foreach (var p in paths)
        {
            var n = SafePathTools.Normalize(p);
            if (n is not null && !target.Contains(n, StringComparer.OrdinalIgnoreCase))
                target.Add(n);
        }
    }

    /// <summary>The real system and personal folders of this PC (always protected, also in tests).</summary>
    private sealed class SystemFolders
    {
        public List<string?> ProtectedFolders { get; } = new();
        public List<string?> ProtectedTrees { get; } = new();

        public static SystemFolders Load()
        {
            var s = new SystemFolders();
            string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

            var windows = F(Environment.SpecialFolder.Windows);
            var profile = F(Environment.SpecialFolder.UserProfile);
            var local = F(Environment.SpecialFolder.LocalApplicationData);
            var roaming = F(Environment.SpecialFolder.ApplicationData);
            var programData = F(Environment.SpecialFolder.CommonApplicationData);

            s.ProtectedFolders.AddRange(new[]
            {
                windows, profile, string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile), local, roaming,
                programData, string.IsNullOrEmpty(local) ? null : Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
                Environment.GetEnvironmentVariable("PUBLIC"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Microsoft"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Microsoft", "Windows"),
                string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Packages"),
                string.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "Microsoft"),
                string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, "Microsoft"),
                string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, "Microsoft", "Windows"),
                string.IsNullOrEmpty(windows) ? null : Path.Combine(windows, "ServiceProfiles"),
            });

            s.ProtectedTrees.AddRange(new[]
            {
                Environment.SystemDirectory, F(Environment.SpecialFolder.SystemX86),
                F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                F(Environment.SpecialFolder.Desktop), F(Environment.SpecialFolder.DesktopDirectory),
                F(Environment.SpecialFolder.MyDocuments), F(Environment.SpecialFolder.MyPictures),
                F(Environment.SpecialFolder.MyVideos), F(Environment.SpecialFolder.MyMusic),
                F(Environment.SpecialFolder.Favorites), F(Environment.SpecialFolder.CommonDesktopDirectory),
                F(Environment.SpecialFolder.CommonDocuments), GetDownloadsFolder(profile),
                Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
            });
            if (!string.IsNullOrEmpty(windows))
            {
                foreach (var sub in new[] { "WinSxS", "Installer", "servicing", "assembly", "Microsoft.NET", "Boot", "Fonts", "INF",
                             "SystemApps", "SystemResources", "System" })
                    s.ProtectedTrees.Add(Path.Combine(windows, sub));
            }
            return s;
        }

        private static string? GetDownloadsFolder(string profile)
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
                {
                    try
                    {
                        return Marshal.PtrToStringUni(ptr);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(ptr);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
            {
                // fall back below
            }
            return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
    }
}
