using System.IO;
using System.Text.RegularExpressions;

namespace PcCleaner.Core;

/// <summary>
/// Splits Windows command lines like the ones found in Run keys, scheduled tasks, services and uninstall strings.
/// </summary>
public static class CommandLine
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".vbs", ".js", ".ps1", ".lnk" };

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "rundll32", "regsvr32.exe", "regsvr32",
    };

    /// <summary>
    /// Returns (executable, arguments). Handles quotes, unquoted paths with spaces, environment variables,
    /// 8.3 names and missing ".exe". Executable is null if nothing could be parsed.
    /// </summary>
    public static (string? Executable, string Arguments) Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (null, "");
        var cmd = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        // \??\C:\... and \SystemRoot\... prefixes used by services/drivers.
        if (cmd.StartsWith(@"\??\"))
            cmd = cmd[4..];
        if (cmd.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), cmd[12..]);
        if (cmd.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), cmd);

        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 1)
                return (Normalize(cmd[1..end]), cmd[(end + 1)..].Trim());
            return (Normalize(cmd.Trim('"')), "");
        }

        // Unquoted: grow token by token until an existing file (or an executable extension) is found.
        var parts = cmd.Split(' ');
        for (var i = 1; i <= parts.Length; i++)
        {
            var candidate = string.Join(' ', parts.Take(i));
            var args = string.Join(' ', parts.Skip(i)).Trim();
            if (File.Exists(candidate))
                return (Normalize(candidate), args);
            if (!Path.HasExtension(candidate) && File.Exists(candidate + ".exe"))
                return (Normalize(candidate + ".exe"), args);
            if (ExecutableExtensions.Any(ext => candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                return (Normalize(candidate), args);
        }

        var first = parts[0];
        return (Normalize(first), string.Join(' ', parts.Skip(1)).Trim());
    }

    /// <summary>
    /// The file that actually matters for a command: for "rundll32.exe C:\x\evil.dll,Entry" returns the dll.
    /// </summary>
    public static string? TargetFile(string? commandLine)
    {
        var (exe, args) = Split(commandLine);
        if (exe is null)
            return null;
        if (Hosts.Contains(Path.GetFileName(exe)) && args.Length > 0)
        {
            // Skip any switches (/s, /d, /i:..., -n) before the library path.
            var m = Regex.Match(args, "^\\s*(?:[/-][\\w:]*\\s+)*\"?([^\",]+?\\.(?:dll|ocx|cpl|ax))\"?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var dll = Normalize(m.Groups[1].Value);
                if (!Path.IsPathRooted(dll))
                {
                    var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", dll);
                    if (File.Exists(wow))
                        return wow;
                }
                return dll;
            }
        }
        return exe;
    }

    private static string Normalize(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
            return path;
        if (!Path.IsPathRooted(path))
        {
            // Bare name like "explorer.exe": try PATH-like system folders.
            foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                var full = Path.Combine(dir, path);
                if (File.Exists(full))
                    return full;
                if (!Path.HasExtension(full) && File.Exists(full + ".exe"))
                    return full + ".exe";
            }
            return path;
        }
        try
        {
            // Expands 8.3 short names (C:\PROGRA~1\...) when the file exists.
            if (File.Exists(path))
                return new FileInfo(path).FullName;
        }
        catch
        {
            // keep as-is
        }
        return path;
    }
}
