using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

/// <summary>
/// Resolves "what file actually runs" for startup commands, on top of <see cref="CommandLine"/>:
/// rundll32/regsvr32 with switches ("rundll32.exe /d acproxy.dll,Entry"), bare DLL names,
/// COM handlers (InprocServer32 / LocalServer32 / AppID → service) and svchost-hosted services.
/// </summary>
internal static class StartupTargets
{
    private static readonly HashSet<string> DllHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "rundll32", "regsvr32.exe", "regsvr32",
    };

    /// <summary>
    /// Interpreters and generic hosts: what they run is decided by the arguments, so trusting the file itself would silence
    /// warnings for every script, DLL or command launched through it (in this and other modules).
    /// </summary>
    private static readonly HashSet<string> GenericHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "powershell_ise.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "regsvr32.exe", "svchost.exe", "dllhost.exe", "conhost.exe", "msiexec.exe", "explorer.exe",
        "schtasks.exe", "sc.exe", "reg.exe", "regedit.exe", "wmic.exe", "forfiles.exe", "certutil.exe", "bitsadmin.exe",
        "msbuild.exe", "installutil.exe", "regasm.exe", "regsvcs.exe", "cmstp.exe", "hh.exe", "wsl.exe", "bash.exe",
        "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "node.exe", "autohotkey.exe",
    };

    /// <summary>Environment variables whose value differs from one user account to another.</summary>
    private static readonly Regex UserVariable = new(
        @"%(APPDATA|LOCALAPPDATA|USERPROFILE|HOMEPATH|HOMESHARE|TEMP|TMP|USERNAME|ONEDRIVE|ONEDRIVECONSUMER|ONEDRIVECOMMERCIAL)%",
        RegexOptions.IgnoreCase);

    private static readonly Regex SidSuffix = new(@"^(?<base>.*?)[\s\-_]*S-1-5-21(-\d+){3,}$", RegexOptions.IgnoreCase);
    private static readonly Regex GuidSuffix = new(@"^(?<base>.+?)\s*\{?[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}?$", RegexOptions.IgnoreCase);

    /// <summary>The file that matters for a command line (the DLL for rundll32/regsvr32).</summary>
    public static string? Resolve(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var (exe, args) = CommandLine.Split(command);
        if (exe is null)
            return null;
        if (DllHosts.Contains(Path.GetFileName(exe)))
            return HostedDll(args) ?? exe;
        return exe;
    }

    /// <summary>"/s /d "C:\x\a.dll",Entry" → C:\x\a.dll; bare names are looked up in System32/SysWOW64/Windows.</summary>
    internal static string? HostedDll(string? args)
    {
        var rest = (args ?? "").Trim();
        // Skip switches: /s /d /n /u /i:cmdline -x
        while (rest.Length > 0 && (rest[0] == '/' || rest[0] == '-'))
        {
            var space = rest.IndexOf(' ');
            if (space < 0)
                return null;
            rest = rest[(space + 1)..].TrimStart();
        }
        if (rest.Length == 0 || rest.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return null;

        string candidate;
        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            candidate = end > 0 ? rest[1..end] : rest.Trim('"');
        }
        else
        {
            var comma = rest.IndexOf(',');
            candidate = comma >= 0 ? rest[..comma] : rest.Split(' ')[0];
        }
        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (candidate.Length == 0 || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;
        return FindFile(candidate);
    }

    /// <summary>Absolute paths are returned as-is (normalized); bare names are searched in the system folders.</summary>
    internal static string FindFile(string path)
    {
        try
        {
            if (Path.IsPathFullyQualified(path))
                return Path.GetFullPath(path);
            foreach (var dir in new[]
                     {
                         Environment.SystemDirectory,
                         Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     })
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                var full = Path.Combine(dir, path);
                if (File.Exists(full))
                    return full;
                if (!Path.HasExtension(full) && File.Exists(full + ".dll"))
                    return full + ".dll";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // keep as-is
        }
        return path;
    }

    /// <summary>Server file of a COM class (used by scheduled tasks with COM-handler actions).</summary>
    public static string? ComServer(string classId)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            using var clsid = root.OpenSubKey($@"CLSID\{classId}");
            if (clsid is null)
                return null;
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                using var key = clsid.OpenSubKey(server);
                if (key?.GetValue("") is string value && !string.IsNullOrWhiteSpace(value))
                    return ComServerFile(server, value);
            }
            // Out-of-process servers hosted by a service: CLSID → AppID → LocalService.
            if (clsid.GetValue("AppID") is string appId)
            {
                using var app = root.OpenSubKey($@"AppID\{appId}");
                if (app?.GetValue("LocalService") is string serviceName && !string.IsNullOrWhiteSpace(serviceName))
                    return ServiceTarget(serviceName).Target;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // unknown server
        }
        return null;
    }

    /// <summary>
    /// File of a COM server registration. InprocServer32 holds a DLL path (may contain unquoted spaces);
    /// LocalServer32 holds a command line that often carries switches ("C:\x\server.exe -Embedding").
    /// </summary>
    internal static string? ComServerFile(string serverKind, string value)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value.Length == 0)
            return null;
        return value.StartsWith('"') || serverKind.Equals("LocalServer32", StringComparison.OrdinalIgnoreCase)
            ? Resolve(value)
            : FindFile(value.Trim('"'));
    }

    /// <summary>True for interpreters/hosts (powershell.exe, cmd.exe, rundll32.exe…) that run whatever the arguments say.</summary>
    public static bool IsGenericHost(string? path)
        => !string.IsNullOrWhiteSpace(path) && GenericHosts.Contains(Path.GetFileName(path.Trim().Trim('"')));

    /// <summary>True if the (unexpanded) path uses variables such as %LOCALAPPDATA% that differ between user accounts.</summary>
    public static bool HasUserVariable(string? rawPath)
        => !string.IsNullOrEmpty(rawPath) && UserVariable.IsMatch(rawPath);

    /// <summary>ImagePath and the file that really runs for a service (ServiceDll for svchost-hosted ones).</summary>
    public static (string ImagePath, string? Target) ServiceTarget(string serviceName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key is null ? ("", null) : ServiceTarget(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return ("", null);
        }
    }

    public static (string ImagePath, string? Target) ServiceTarget(RegistryKey serviceKey)
    {
        var imagePath = (serviceKey.GetValue("ImagePath") as string ?? "").Trim();
        if (imagePath.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            using var parameters = serviceKey.OpenSubKey("Parameters");
            var dll = parameters?.GetValue("ServiceDll") as string ?? serviceKey.GetValue("ServiceDll") as string;
            if (!string.IsNullOrWhiteSpace(dll))
                return (imagePath, FindFile(Environment.ExpandEnvironmentVariables(dll.Trim().Trim('"'))));
        }
        return (imagePath, Resolve(imagePath));
    }

    /// <summary>
    /// Friendlier task names: "OneDrive Startup Task-S-1-5-21-…" → "OneDrive Startup Task",
    /// a task named only by a SID → its folder name, "Updater{GUID}" → "Updater".
    /// </summary>
    public static string FriendlyTaskName(string name, string? taskPath)
    {
        var sid = SidSuffix.Match(name);
        if (sid.Success)
        {
            var baseName = sid.Groups["base"].Value.Trim(' ', '-', '_');
            if (baseName.Length > 0)
                return baseName;
            var folder = FolderName(taskPath);
            return string.IsNullOrEmpty(folder) ? name : folder;
        }
        var guid = GuidSuffix.Match(name);
        if (guid.Success && guid.Groups["base"].Value.Trim().Length >= 3)
            return guid.Groups["base"].Value.Trim(' ', '-', '_');
        return name;
    }

    private static string? FolderName(string? taskPath)
    {
        if (string.IsNullOrEmpty(taskPath))
            return null;
        var parts = taskPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : null;
    }
}
