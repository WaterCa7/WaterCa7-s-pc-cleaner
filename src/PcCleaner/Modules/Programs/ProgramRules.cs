using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

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

    // l10n:data-begin — the Russian name of a driver package in the Uninstall list, matched and never shown
    private static readonly Regex DriverName = new(
        @"^(Пакет драйверов Windows|Windows Driver Package)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // l10n:data-end

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
        Func<string, bool>? fileExists = null, Func<string, DriveType>? driveType = null)
    {
        if (launch is { IsMsi: true })
            return launch;
        fileExists ??= File.Exists;
        var cache = NormalizeFolder(bundleCachePath);
        if (cache is null || !IsLocalRootedPath(cache) || IsUnprobeablePath(cache, driveType) ||
            !cache.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return launch;
        if (launch is not null && (!IsLocalRootedPath(launch.FileName) || IsUnprobeablePath(launch.FileName, driveType) ||
                                   fileExists(launch.FileName)))
            return launch; // the registered uninstaller is there (or cannot be checked): use it
        if (!fileExists(cache))
            return launch;
        return new UninstallLaunch(cache, quiet ? "/uninstall /quiet" : "/uninstall", SafeDirectory(cache), false, null);
    }

    /// <summary>
    /// The program's main .exe for a VirusTotal lookup: DisplayIcon if it is an existing .exe, else the uninstaller.
    /// Network and removable drives (see <see cref="IsUnprobeablePath"/>) and Windows' own tools (rundll32, msiexec,
    /// powershell) are skipped.
    /// </summary>
    public static string? FindMainExecutable(string? displayIcon, UninstallLaunch? launch, Func<string, bool>? fileExists = null,
        Func<string, DriveType>? driveType = null)
    {
        fileExists ??= File.Exists;
        bool Usable(string? path)
            => path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsLocalRootedPath(path) &&
               !IsUnprobeablePath(path, driveType) && !IsUnderWindows(path) && fileExists(path);

        var icon = ParseIconPath(displayIcon).Path;
        if (Usable(icon))
            return icon;
        var exe = launch is { IsMsi: false } ? launch.FileName : null;
        return Usable(exe) ? exe : null;
    }

    /// <summary>
    /// Windows' own binaries (malware imitates them; Core's list) and script hosts or interpreters (trusting one would vouch
    /// for whatever it is told to run; the «Автозагрузка» page's list): "Доверять" never extends to such a file.
    /// </summary>
    private static readonly HashSet<string> NeverTrustedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "csrss.exe", "lsass.exe", "winlogon.exe", "services.exe", "smss.exe", "wininit.exe",
        "explorer.exe", "dwm.exe", "taskhostw.exe", "conhost.exe", "spoolsv.exe", "ctfmon.exe", "sihost.exe",
        "runtimebroker.exe", "dllhost.exe", "rundll32.exe", "regsvr32.exe", "taskmgr.exe", "lsm.exe",
        "audiodg.exe", "searchindexer.exe", "fontdrvhost.exe", "wmiprvse.exe", "msmpeng.exe", "smartscreen.exe",
        "powershell.exe", "pwsh.exe", "powershell_ise.exe", "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "msiexec.exe", "schtasks.exe", "sc.exe", "reg.exe", "regedit.exe", "wmic.exe", "forfiles.exe", "certutil.exe",
        "bitsadmin.exe", "msbuild.exe", "installutil.exe", "regasm.exe", "regsvcs.exe", "cmstp.exe", "hh.exe", "wsl.exe",
        "bash.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "node.exe", "autohotkey.exe",
    };

    /// <summary>
    /// The program file that "Доверять" trusts together with the program's name. The «Процессы» and «Автозагрузка» pages
    /// recognise programs by file path, so without it a program trusted here would still be flagged there.
    /// Only the program's own .exe (<paramref name="mainExecutable"/>, never its uninstaller) inside its own install folder
    /// qualifies, and only when nothing but its name could look suspicious: a file named like a Windows binary or a
    /// look-alike of one, a script host or interpreter, a miner or remote-access tool, a random or double-extension name,
    /// or a file in Temp, «Загрузки», the Recycle Bin or Public keeps every warning — the user trusted the program's name,
    /// not whatever runs from its folder.
    /// </summary>
    public static string? TrustFileFor(string? mainExecutable, string? installLocation, UninstallLaunch? launch)
    {
        if (string.IsNullOrWhiteSpace(mainExecutable) || string.IsNullOrWhiteSpace(installLocation) ||
            IsNetworkPath(mainExecutable) || IsNetworkPath(installLocation))
            return null;
        string exe, folder;
        try
        {
            exe = Path.GetFullPath(mainExecutable);
            folder = Path.GetFullPath(installLocation).TrimEnd('\\');
            if (launch is { IsMsi: false } && Path.IsPathFullyQualified(launch.FileName) &&
                string.Equals(Path.GetFullPath(launch.FileName), exe, StringComparison.OrdinalIgnoreCase))
                return null; // the uninstaller
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsUnderWindows(exe) ||
            IsTooBroadToMeasure(folder) || !Suspicion.IsUnder(exe, folder))
            return null;

        var name = Path.GetFileName(exe);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (NeverTrustedFileNames.Contains(name) || Suspicion.LookalikeOf(name) is not null ||
            KnownPups.MatchRiskyTool(name) is not null || Suspicion.LooksRandom(stem) || stem.Contains('.') ||
            Suspicion.DescribeRiskyLocation(exe) is not null)
            return null;
        return exe;
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
    /// and entries whose own program .exe (DisplayIcon) still exists. Nothing on a network or removable drive is checked
    /// (<see cref="IsUnprobeablePath"/>): it could hang, and another stick under the same letter would make a program
    /// look gone.
    /// </summary>
    public static bool IsBroken(UninstallEntryValues v, Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null,
        Func<string, DriveType>? driveType = null)
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
        if (IsUnprobeablePath(target, driveType))
            return false; // network or removable drive: not checked - never call it broken

        var root = SafeRoot(target);
        if (root is null || !dirExists(root))
            return false; // the disk is not connected right now - do not call it broken

        if (fileExists(target))
            return false;
        if (target.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return false; // a cleaned-up setup cache does not mean the program itself is gone

        var location = NormalizeFolder(v.InstallLocation);
        if (location is not null && (IsUnprobeablePath(location, driveType) || dirExists(location)))
            return false; // network folders are not checked (could hang) - never call them broken

        // The program's own .exe (DisplayIcon) is still there (or is on a drive that is not checked): only the uninstaller
        // is missing.
        var icon = ParseIconPath(v.DisplayIcon).Path;
        if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsUnderWindows(icon) &&
            (IsUnprobeablePath(icon, driveType) || (IsLocalRootedPath(icon) && fileExists(icon))))
            return false;
        return true;
    }

    private static bool IsUnderWindows(string path)
        => Suspicion.IsUnder(path.Replace('/', '\\'), Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>UNC paths (\\server\share) are never probed: an offline server can block for a long time.</summary>
    public static bool IsNetworkPath(string? path)
        => path is not null && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>
    /// True if the disk must not be touched for this path during a scan: UNC paths, and drive letters that are not local
    /// fixed disks — mapped network drives (an offline NAS or share blocks every check until the redirector times out),
    /// USB sticks and card readers, CD/DVD drives and letters with nothing behind them. Such paths are not checked at all
    /// and never judged "missing", like on the «Автозагрузка» page. Relative paths count as local.
    /// </summary>
    /// <param name="driveType">Drive type of "X:" (tests); the real one, cached briefly, by default.</param>
    public static bool IsUnprobeablePath(string? path, Func<string, DriveType>? driveType = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (IsNetworkPath(path))
            return true;
        if (path.Length < 2 || path[1] != ':')
            return false;
        return (driveType ?? CachedDriveType)(path[..2]) is not (DriveType.Fixed or DriveType.Ram);
    }

    /// <summary>Drive types by letter: a list of a few hundred programs asks about the same few letters over and over.</summary>
    private static readonly ConcurrentDictionary<char, (DriveType Type, long ValidUntil)> DriveTypes = new();

    private const int DriveTypeCacheMs = 30_000;

    /// <summary>
    /// GetDriveType for "X:" (answered locally: a mapped drive is reported as a network drive even while its server is
    /// offline). Cached for a short while, so a drive mapped or plugged in later is noticed by the next scan.
    /// </summary>
    private static DriveType CachedDriveType(string drive)
    {
        var letter = char.ToUpperInvariant(drive[0]);
        if (letter is < 'A' or > 'Z')
            return DriveType.Unknown;
        var now = Environment.TickCount64;
        if (DriveTypes.TryGetValue(letter, out var cached) && cached.ValidUntil > now)
            return cached.Type;
        DriveType type;
        try
        {
            type = new DriveInfo(letter + @":\").DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            type = DriveType.Unknown;
        }
        DriveTypes[letter] = (type, now + DriveTypeCacheMs);
        return type;
    }

    /// <summary>Recently installed (or updated): within <see cref="RecentDays"/> days, not in the future.</summary>
    public static bool IsRecent(DateTime? installDate, DateTime now)
        => installDate is { } d && d.Date >= now.Date.AddDays(-RecentDays) && d.Date <= now.Date.AddDays(1);

    /// <summary>Microsoft runtimes other programs depend on (Visual C++, .NET, DirectX, WebView2...).</summary>
    public static bool IsRuntime(string? displayName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(displayName) || !RuntimeName.IsMatch(displayName))
            return false;
        // l10n:data-begin — the Russian spelling of the publisher's name, matched and never shown
        return string.IsNullOrWhiteSpace(publisher) ||
               publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
               publisher.Contains("Майкрософт", StringComparison.OrdinalIgnoreCase);
        // l10n:data-end
    }

    /// <summary>
    /// Plain-language note when a program lives where normal programs do not (Temp, Downloads, Recycle Bin, Public).
    /// Folders directly in AppData are normal for per-user apps and are not reported. The classification is Core's
    /// (<see cref="Suspicion.ClassifyLocation"/> for an install folder, a code that is the same in every language); only the
    /// longer explanation is ours.
    /// </summary>
    public static string? DescribeInstallLocationRisk(string? installLocation) => InstallLocationKind(installLocation) switch
    {
        RiskyLocation.Temp => T._("Программа установлена во временную папку (Temp) — обычные программы так не делают."),
        RiskyLocation.Downloads => T._("Программа установлена прямо в папку «Загрузки» — обычно программы ставятся в Program Files."),
        RiskyLocation.RecycleBin => T._("Папка программы находится в корзине."),
        RiskyLocation.Public => T._("Программа установлена в общую папку пользователей (Public) — это необычно."),
        _ => null,
    };

    /// <summary>
    /// PUP heuristics for an installed program — Core's <see cref="Suspicion.AnalyzeProgram"/> (known PUPs, missing
    /// publisher, unusual install folder). Only the folder is prepared here: quotes and variables are removed, and network
    /// folders are not judged.
    /// </summary>
    public static SuspicionResult AnalyzeProgram(string displayName, string? publisher, string? installLocation)
        => Suspicion.AnalyzeProgram(displayName, publisher, AnalyzableFolder(installLocation));

    /// <summary>Core's kind of unusual install folder (temporary folder, Downloads, Recycle Bin, Public), or none.</summary>
    internal static RiskyLocation InstallLocationKind(string? installLocation)
    {
        var folder = AnalyzableFolder(installLocation);
        if (folder is null)
            return RiskyLocation.None;
        try
        {
            return Suspicion.ClassifyLocation(folder, isInstallFolder: true);
        }
        catch (ArgumentException)
        {
            return RiskyLocation.None;
        }
    }

    /// <summary>
    /// The install folder in the form Core expects: normalized, with a trailing separator so that a program installed
    /// right into a risky folder (e.g. %TEMP% itself) counts as being inside it. Null for empty and network folders.
    /// </summary>
    private static string? AnalyzableFolder(string? installLocation)
    {
        var folder = NormalizeFolder(installLocation);
        if (folder is null || IsNetworkPath(folder))
            return null;
        return folder.TrimEnd('\\', '/') + "\\";
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
