using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

/// <summary>
/// Pure rules for Uninstall entries: filtering, date parsing, uninstall command building and flags.
/// No registry or UI access, so everything here is unit-tested.
/// </summary>
public static class ProgramRules
{
    public const int RecentDays = 14;

    public const long LargeProgramBytes = 1L << 30; // 1 GB

    private static readonly Regex GuidRegex = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);

    /// <summary>"/I{GUID}", "/X {GUID}", "/uninstall {GUID}", "/package {GUID}", "/fa{GUID}" (repair flags p,o,e,d,c,a,u,m,s,v).</summary>
    private static readonly Regex MsiSwitchWithGuid = new(
        @"(?:^|\s)[/-](?:i|x|uninstall|package|f[poecdaumsv]*)\s*""?(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Any other msiexec action (install, repair, admin, advertise, patch...): a quiet command with one is not run.</summary>
    private static readonly Regex MsiActionSwitch = new(
        @"(?:^|\s)[/-](?:i|x|a|p|j[mu]?|f[poecdaumsv]*|package|uninstall|update)(?=\s|""|\{|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] SkippedReleaseTypes =
    {
        "Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack",
    };

    private static readonly Regex RuntimeName = new(
        @"^Microsoft Visual C\+\+ .*(Redistributable|Runtime)|" +
        @"^Microsoft \.NET|^Microsoft ASP\.NET Core|^Microsoft Windows Desktop Runtime|\.NET Framework|^\.NET |" +
        @"DirectX|^Microsoft Edge WebView2|^Microsoft XNA Framework|^Microsoft Visual Studio 20\d\d Tools for Office Runtime|" +
        @"^Windows App Runtime|^Microsoft Windows App Runtime|^Windows Desktop Runtime",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DriverName = new(
        @"^(Пакет драйверов Windows|Windows Driver Package)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] InstallDateFormats =
    {
        "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy.MM.dd",
    };

    /// <summary>
    /// True if the entry should be shown: has a name, is not a hidden system component and not a patch/update.
    /// </summary>
    public static bool ShouldInclude(UninstallEntryValues v)
    {
        if (string.IsNullOrWhiteSpace(v.DisplayName))
            return false;
        if (v.SystemComponent)
            return false;
        if (!string.IsNullOrWhiteSpace(v.ParentKeyName))
            return false;
        if (!string.IsNullOrWhiteSpace(v.ReleaseType) &&
            SkippedReleaseTypes.Any(t => string.Equals(t, v.ReleaseType.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>Parses InstallDate (normally yyyyMMdd). Returns null for empty or invalid values.</summary>
    public static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var s = value.Trim();
        if (DateTime.TryParseExact(s, InstallDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) &&
            d.Year is >= 1990 and <= 2100)
            return d.Date;
        return null;
    }

    /// <summary>EstimatedSize is in KB (REG_DWORD). Returns bytes, 0 if unknown.</summary>
    public static long EstimatedSizeToBytes(long? kb)
        => kb is > 0 and < int.MaxValue ? kb.Value * 1024L : 0;

    public static bool IsGuid(string? s) => s is not null && s.Length == 38 && GuidRegex.IsMatch(s);

    /// <summary>
    /// "msiexec ...", "MsiExec.exe ...", "\"C:\Windows\System32\msiexec.exe\" ...". Checked before CommandLine.Split, which
    /// would read "msiexec /x C:\Setup\app.msi" as one file name (it stops at the first token with an executable extension).
    /// </summary>
    private static readonly Regex MsiexecHead = new(
        @"^\s*(?:""(?:[^""]*\\)?msiexec(?:\.exe)?""|(?:[^\s""]*\\)?msiexec(?:\.exe)?)(?=\s|$)(?<args>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>True for "MsiExec.exe /X{...}" and similar.</summary>
    public static bool IsMsiexecCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;
        if (MsiexecHead.IsMatch(commandLine))
            return true;
        var (exe, _) = CommandLine.Split(commandLine);
        if (exe is null)
            return false;
        var name = Path.GetFileName(exe);
        return string.Equals(name, "msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "msiexec", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts {GUID} from "MsiExec.exe /I{GUID}" / "/X {GUID}" / "/uninstall {GUID}". Upper-case with braces.</summary>
    public static string? ExtractProductCode(string? commandLine)
    {
        if (!IsMsiexecCommand(commandLine))
            return null;
        var m = MsiSwitchWithGuid.Match(commandLine!);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static string MsiExecPath => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    /// <summary>
    /// Builds the standard (interactive) uninstall launch. MSI packages always become "msiexec.exe /x {GUID}"
    /// (never the repair/modify "/I"), other programs run their own UninstallString.
    /// </summary>
    public static UninstallLaunch? BuildLaunch(string? uninstallString, bool windowsInstaller, string? keyName)
    {
        var isMsiCommand = IsMsiexecCommand(uninstallString);
        var productCode = ExtractProductCode(uninstallString);
        if (productCode is null && (windowsInstaller || isMsiCommand) && IsGuid(keyName))
            productCode = keyName!.ToUpperInvariant();

        if ((isMsiCommand || windowsInstaller) && productCode is not null)
            return new UninstallLaunch(MsiExecPath, $"/x {productCode}", Environment.SystemDirectory, true, productCode);

        return BuildRawLaunch(uninstallString);
    }

    /// <summary>
    /// Launch for QuietUninstallString as written by the installer. For msiexec it is always "msiexec.exe /x {GUID}" plus the
    /// installer's other switches (/qn, /norestart...): an install/repair ("/I", "/fa") never runs, and a command without a
    /// product code or with a second msiexec action is not offered at all.
    /// </summary>
    public static UninstallLaunch? BuildQuietLaunch(string? quietUninstallString)
    {
        if (string.IsNullOrWhiteSpace(quietUninstallString))
            return null;
        var launch = BuildRawLaunch(quietUninstallString);
        if (launch is null)
            return null;
        if (IsMsiexecCommand(quietUninstallString))
        {
            var code = ExtractProductCode(quietUninstallString);
            if (code is null)
                return null;
            var rest = MsiSwitchWithGuid.Replace(launch.Arguments, " ", 1).Trim();
            if (MsiActionSwitch.IsMatch(rest))
                return null;
            var args = rest.Length == 0 ? $"/x {code}" : $"/x {code} {rest}";
            return new UninstallLaunch(MsiExecPath, args, Environment.SystemDirectory, true, code);
        }
        return launch;
    }

    /// <summary>
    /// WiX Burn bundles keep their setup in "Package Cache" (BundleCachePath). When the uninstaller named in the
    /// registry is gone, "&lt;cached setup&gt; /uninstall" is the bundle's own uninstall (the command Burn registers by default).
    /// Returns <paramref name="launch"/> unchanged in every other case.
    /// </summary>
    public static UninstallLaunch? WithBundleFallback(UninstallLaunch? launch, string? bundleCachePath, bool quiet,
        Func<string, bool>? fileExists = null)
    {
        if (launch is { IsMsi: true })
            return launch;
        fileExists ??= File.Exists;
        var cache = NormalizeFolder(bundleCachePath);
        if (cache is null || !IsLocalRootedPath(cache) || !cache.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return launch;
        if (launch is not null && (!IsLocalRootedPath(launch.FileName) || fileExists(launch.FileName)))
            return launch; // the registered uninstaller is there (or cannot be checked): use it
        if (!fileExists(cache))
            return launch;
        return new UninstallLaunch(cache, quiet ? "/uninstall /quiet" : "/uninstall", SafeDirectory(cache), false, null);
    }

    /// <summary>
    /// The program's main .exe for a VirusTotal lookup: DisplayIcon if it is an existing .exe, else the uninstaller.
    /// Network paths and Windows' own tools (rundll32, msiexec, powershell) are skipped.
    /// </summary>
    public static string? FindMainExecutable(string? displayIcon, UninstallLaunch? launch, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        bool Usable(string? path)
            => path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsLocalRootedPath(path) &&
               !IsUnderWindows(path) && fileExists(path);

        var icon = ParseIconPath(displayIcon).Path;
        if (Usable(icon))
            return icon;
        var exe = launch is { IsMsi: false } ? launch.FileName : null;
        return Usable(exe) ? exe : null;
    }

    private static UninstallLaunch? BuildRawLaunch(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var msi = MsiexecHead.Match(commandLine);
        if (msi.Success)
            return new UninstallLaunch(MsiExecPath, msi.Groups["args"].Value.Trim(), Environment.SystemDirectory, true,
                ExtractProductCode(commandLine));
        var (exe, args) = CommandLine.Split(commandLine);
        if (string.IsNullOrWhiteSpace(exe))
            return null;
        string? dir = null;
        try
        {
            if (Path.IsPathRooted(exe))
                dir = Path.GetDirectoryName(exe);
        }
        catch (ArgumentException)
        {
            // invalid characters: leave working directory unset
        }
        var isMsi = string.Equals(Path.GetFileName(exe), "msiexec.exe", StringComparison.OrdinalIgnoreCase);
        return new UninstallLaunch(exe, args, dir, isMsi, isMsi ? ExtractProductCode(commandLine) : null);
    }

    /// <summary>
    /// "Битая запись": the uninstaller file (for rundll32-style commands, the DLL) is missing AND the install folder
    /// is empty or missing. MSI entries are never considered broken here. Missing drives (unplugged disks) are not broken.
    /// Conservative exceptions (the program may still be installed, so the entry must not be offered for removal):
    /// Microsoft runtimes, WiX Burn bundles and "Package Cache" setups (their MSI parts can outlive the cached setup),
    /// and entries whose own program .exe (DisplayIcon) still exists.
    /// </summary>
    public static bool IsBroken(UninstallEntryValues v, Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null)
    {
        fileExists ??= File.Exists;
        dirExists ??= Directory.Exists;
        if (string.IsNullOrWhiteSpace(v.UninstallString))
            return false;
        if (IsMsiexecCommand(v.UninstallString))
            return false;
        if (v.WindowsInstaller && IsGuid(v.KeyName))
            return false;
        if (IsRuntime(v.DisplayName, v.Publisher))
            return false;
        if (!string.IsNullOrWhiteSpace(v.BundleCachePath))
            return false;

        string? target;
        try
        {
            target = CommandLine.TargetFile(v.UninstallString);
        }
        catch (Exception)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(target) || !IsLocalRootedPath(target))
            return false;

        var root = SafeRoot(target);
        if (root is null || !dirExists(root))
            return false; // the disk is not connected right now - do not call it broken

        if (fileExists(target))
            return false;
        if (target.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return false; // a cleaned-up setup cache does not mean the program itself is gone

        var location = NormalizeFolder(v.InstallLocation);
        if (location is not null && (IsNetworkPath(location) || dirExists(location)))
            return false; // network folders are not checked (could hang) - never call them broken

        // The program's own .exe (DisplayIcon) is still there: only the uninstaller is missing.
        var icon = ParseIconPath(v.DisplayIcon).Path;
        if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsLocalRootedPath(icon) &&
            !IsUnderWindows(icon) && fileExists(icon))
            return false;
        return true;
    }

    private static bool IsUnderWindows(string path)
        => Suspicion.IsUnder(path.Replace('/', '\\'), Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>UNC paths (\\server\share) are never probed: an offline server can block for a long time.</summary>
    public static bool IsNetworkPath(string? path)
        => path is not null && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>Recently installed (or updated): within <see cref="RecentDays"/> days, not in the future.</summary>
    public static bool IsRecent(DateTime? installDate, DateTime now)
        => installDate is { } d && d.Date >= now.Date.AddDays(-RecentDays) && d.Date <= now.Date.AddDays(1);

    /// <summary>Microsoft runtimes other programs depend on (Visual C++, .NET, DirectX, WebView2...).</summary>
    public static bool IsRuntime(string? displayName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(displayName) || !RuntimeName.IsMatch(displayName))
            return false;
        return string.IsNullOrWhiteSpace(publisher) ||
               publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
               publisher.Contains("Майкрософт", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plain-language note when a program lives where normal programs do not (Temp, Downloads, Recycle Bin, Public).
    /// Folders directly in AppData are normal for per-user apps and are not reported.
    /// </summary>
    public static string? DescribeInstallLocationRisk(string? installLocation) => ClassifyInstallLocation(installLocation) switch
    {
        LocationRisk.Temp => "Программа установлена во временную папку (Temp) — обычные программы так не делают.",
        LocationRisk.Downloads => "Программа установлена прямо в папку «Загрузки» — обычно программы ставятся в Program Files.",
        LocationRisk.RecycleBin => "Папка программы находится в корзине.",
        LocationRisk.Public => "Программа установлена в общую папку пользователей (Public) — это необычно.",
        _ => null,
    };

    /// <summary>
    /// PUP heuristics for an installed program: the Core checks (known PUPs, missing publisher) plus the install folder.
    /// Unlike <see cref="Suspicion.AnalyzeProgram"/>, a folder directly in AppData is normal for programs (Discord, Telegram,
    /// GitHub Desktop install per user there), and the reasons describe an install folder rather than a running file.
    /// </summary>
    public static SuspicionResult AnalyzeProgram(string displayName, string? publisher, string? installLocation)
    {
        if (AppServices.Settings.IsTrusted(displayName))
            return SuspicionResult.Clean;
        var core = Suspicion.AnalyzeProgram(displayName, publisher, installLocation: null);
        var location = ClassifyInstallLocation(installLocation) switch
        {
            LocationRisk.Temp => "Установлена во временную папку (Temp)",
            LocationRisk.Downloads => "Установлена прямо в папку «Загрузки»",
            LocationRisk.RecycleBin => "Папка программы находится в корзине",
            LocationRisk.Public => "Установлена в общую папку пользователей (Public)",
            _ => null,
        };
        if (location is null)
            return core;
        return new SuspicionResult(Math.Clamp(core.Score + 20, 0, 100), core.Reasons.Append(location).ToList());
    }

    private enum LocationRisk
    {
        None,
        Temp,
        Downloads,
        RecycleBin,
        Public,
    }

    private static LocationRisk ClassifyInstallLocation(string? installLocation)
    {
        var folder = NormalizeFolder(installLocation);
        if (folder is null || IsNetworkPath(folder))
            return LocationRisk.None;
        string? reason;
        try
        {
            // DescribeRiskyLocation looks at the parent folder of a file, so describe a file inside the install folder.
            reason = Suspicion.DescribeRiskyLocation(Path.Combine(folder, "app.exe"));
        }
        catch (ArgumentException)
        {
            return LocationRisk.None;
        }
        if (reason is null)
            return LocationRisk.None;
        if (reason.Contains("Temp", StringComparison.OrdinalIgnoreCase))
            return LocationRisk.Temp;
        if (reason.Contains("Загрузки", StringComparison.OrdinalIgnoreCase))
            return LocationRisk.Downloads;
        if (reason.Contains("корзин", StringComparison.OrdinalIgnoreCase))
            return LocationRisk.RecycleBin;
        if (reason.Contains("Public", StringComparison.OrdinalIgnoreCase))
            return LocationRisk.Public;
        return LocationRisk.None; // "directly in AppData" is normal for per-user programs
    }

    public static bool IsDriverPackage(string? displayName)
        => !string.IsNullOrWhiteSpace(displayName) && DriverName.IsMatch(displayName);

    /// <summary>Trims quotes/spaces, expands variables. Returns null if empty.</summary>
    public static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        var s = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"').Trim());
        if (s.Length == 0)
            return null;
        if (s.Length > 3)
            s = s.TrimEnd('\\', '/');
        return s;
    }

    /// <summary>Parses DisplayIcon like "\"C:\x\app.exe\",0" or "C:\x\app.ico". Returns (path, index).</summary>
    public static (string? Path, int Index) ParseIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return (null, 0);
        var s = displayIcon.Trim();
        var index = 0;
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
        {
            index = idx;
            s = s[..comma];
        }
        s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"').Trim());
        return (s.Length == 0 ? null : s, index);
    }

    /// <summary>
    /// Folders that must never be measured as "the program's size" (a broken InstallLocation like C:\ or Program Files
    /// would otherwise count the whole disk).
    /// </summary>
    public static bool IsTooBroadToMeasure(string folder)
    {
        string full;
        try
        {
            full = Path.GetFullPath(folder).TrimEnd('\\');
        }
        catch (Exception)
        {
            return true;
        }
        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var broad = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("PUBLIC") ?? "",
            Path.GetTempPath(),
        };
        foreach (var b in broad)
        {
            if (string.IsNullOrWhiteSpace(b))
                continue;
            var bt = b.TrimEnd('\\');
            // Equal to a broad folder, or an ancestor of one (e.g. C:\Users).
            if (string.Equals(full, bt, StringComparison.OrdinalIgnoreCase) || Suspicion.IsUnder(bt, full))
                return true;
        }
        return false;
    }

    /// <summary>Dedup key: same name + version from different registry views is one program.</summary>
    public static string DedupKey(string displayName, string? version)
        => displayName.Trim().ToUpperInvariant() + "|" + (version ?? "").Trim().ToUpperInvariant();

    private static bool IsLocalRootedPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
