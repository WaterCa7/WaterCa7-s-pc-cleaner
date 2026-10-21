using System.IO;
using System.Text.RegularExpressions;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

public enum SuspicionLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// What a <see cref="SuspicionResult"/> found, as codes: the same in every UI language. <see cref="SuspicionResult.Reasons"/> is
/// text for the user (Russian or English); anything that has to tell the findings apart reads these flags, never the words.
/// </summary>
[Flags]
public enum SuspicionFlags
{
    None = 0,
    SystemNameOutsideWindows = 1 << 0,
    RiskyLocation = 1 << 1,
    InvalidSignature = 1 << 2,
    UntrustedSignature = 1 << 3,
    Unsigned = 1 << 4,
    DoubleExtension = 1 << 5,
    RandomName = 1 << 6,
    HiddenFile = 1 << 7,
    HiddenPowerShell = 1 << 8,
    ScriptLauncher = 1 << 9,
    UrlInCommand = 1 << 10,
    KnownPup = 1 << 11,
    LookalikeName = 1 << 12,
    RiskyTool = 1 << 13,
    NoPublisher = 1 << 14,
    SustainedCpu = 1 << 15,
}

/// <summary>Where a path lives when legitimate software rarely lives there (<see cref="Suspicion.ClassifyLocation"/>).</summary>
public enum RiskyLocation
{
    None = 0,
    Temp,
    Downloads,
    RecycleBin,
    Public,
    AppDataRoot,
}

/// <param name="Score">0–100.</param>
/// <param name="Reasons">Why, for the user, in the UI language of the moment the result was made.</param>
/// <param name="Flags">Why, as codes (language independent). Results built from a score and texts only have none.</param>
public sealed record SuspicionResult(int Score, IReadOnlyList<string> Reasons, SuspicionFlags Flags = SuspicionFlags.None)
{
    public static readonly SuspicionResult Clean = new(0, Array.Empty<string>());

    public SuspicionLevel Level => Score switch
    {
        >= 60 => SuspicionLevel.High,
        >= 35 => SuspicionLevel.Medium,
        >= 15 => SuspicionLevel.Low,
        _ => SuspicionLevel.None,
    };

    public bool IsSuspicious => Level >= SuspicionLevel.Medium;

    public string LevelText => Level switch
    {
        SuspicionLevel.High => T._("Высокий риск"),
        SuspicionLevel.Medium => T._("Подозрительно"),
        SuspicionLevel.Low => T._("Внимание"),
        _ => "OK",
    };

    public string ReasonsText => Reasons.Count == 0 ? "" : string.Join("; ", Reasons);
}

/// <summary>
/// Heuristics shared by the Processes, Startup, Programs and Security modules.
/// It never decides on its own to remove anything: it only explains why an item looks unusual.
/// </summary>
public static class Suspicion
{
    /// <summary>Names of core Windows binaries that malware likes to imitate, with where they really live.</summary>
    private static readonly HashSet<string> SystemBinaryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "csrss.exe", "lsass.exe", "winlogon.exe", "services.exe", "smss.exe", "wininit.exe",
        "explorer.exe", "dwm.exe", "taskhostw.exe", "conhost.exe", "spoolsv.exe", "ctfmon.exe", "sihost.exe",
        "runtimebroker.exe", "dllhost.exe", "rundll32.exe", "regsvr32.exe", "taskmgr.exe", "lsm.exe",
        "audiodg.exe", "searchindexer.exe", "fontdrvhost.exe", "wmiprvse.exe", "msmpeng.exe", "smartscreen.exe",
    };

    private static readonly Regex DoubleExtension = new(@"\.(pdf|doc|docx|xls|xlsx|jpg|jpeg|png|txt|mp3|mp4|zip|rar)\.(exe|scr|com|bat|cmd|vbs|js|pif)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptLauncher = new(
        @"(\\|^)(wscript|cscript|mshta|powershell|pwsh)(\.exe)?\b|(\\|^)cmd(\.exe)?\s+/c\b|regsvr32(\.exe)?\s+.*(/i:\s*""?https?:|scrobj\.dll)|rundll32(\.exe)?\s+javascript:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HiddenPowerShell = new(
        @"-(w|win|window|windowstyle)\s+h(idden)?\b|-e(nc|ncodedcommand)?\s+[A-Za-z0-9+/=]{20,}|frombase64string|downloadstring|invoke-expression|\biex\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlInCommand = new(@"https?://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Analyzes an executable (process image, startup target, uninstaller...).
    /// </summary>
    /// <param name="path">Full path to the executable, may be null/missing.</param>
    /// <param name="signature">Pass the result of <see cref="FileSignature.Get"/> (null = not checked).</param>
    /// <param name="commandLine">Full command line if known (startup entries, scheduled tasks).</param>
    public static SuspicionResult Analyze(string? path, SignatureInfo? signature = null, string? commandLine = null)
    {
        var reasons = new List<string>();
        var flags = SuspicionFlags.None;
        var score = 0;

        if (!string.IsNullOrWhiteSpace(path) && AppServices.Settings.IsTrusted(path))
            return SuspicionResult.Clean;

        var fileName = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path);
        var dir = string.IsNullOrWhiteSpace(path) ? "" : (Path.GetDirectoryName(path) ?? "");

        // 1. Imitation of a system binary outside of the Windows folder.
        // Genuine Microsoft binaries also live outside Windows (Defender's MsMpEng.exe in ProgramData\Microsoft\Windows Defender\Platform),
        // so a valid Microsoft signature clears this rule — but only in Microsoft's own install locations: a signed svchost.exe
        // copied to Temp and started from there is a classic trick.
        if (fileName.Length > 0 && SystemBinaryNames.Contains(fileName) && !IsUnder(path!, Win) &&
            !((signature?.IsMicrosoft ?? false) && IsMicrosoftInstallLocation(path!)))
        {
            score += 70;
            reasons.Add(T.F("Имя системного файла «{0}», но расположен не в папке Windows", fileName));
            flags |= SuspicionFlags.SystemNameOutsideWindows;
        }

        // 2. Location.
        if (path is not null && path.Length > 0)
        {
            var location = ClassifyLocation(path);
            if (location != RiskyLocation.None)
            {
                score += 30;
                reasons.Add(LocationText(location, isInstallFolder: false));
                flags |= SuspicionFlags.RiskyLocation;
            }
        }

        // 3. Signature.
        if (signature is not null)
        {
            switch (signature.Status)
            {
                case SignatureStatus.Invalid:
                    score += 45;
                    reasons.Add(T._("Цифровая подпись повреждена или отозвана"));
                    flags |= SuspicionFlags.InvalidSignature;
                    break;
                case SignatureStatus.Untrusted:
                    score += 20;
                    reasons.Add(T._("Подпись недоверенным сертификатом"));
                    flags |= SuspicionFlags.UntrustedSignature;
                    break;
                case SignatureStatus.Unsigned:
                    score += 20;
                    reasons.Add(T._("Нет цифровой подписи"));
                    flags |= SuspicionFlags.Unsigned;
                    break;
            }
        }

        // 4. File name tricks.
        if (fileName.Length > 0)
        {
            if (DoubleExtension.IsMatch(fileName))
            {
                score += 50;
                reasons.Add(T._("Двойное расширение (маскировка под документ)"));
                flags |= SuspicionFlags.DoubleExtension;
            }
            else if (LooksRandom(Path.GetFileNameWithoutExtension(fileName)))
            {
                score += 15;
                reasons.Add(T._("Случайное на вид имя файла"));
                flags |= SuspicionFlags.RandomName;
            }

            try
            {
                if (File.Exists(path) && File.GetAttributes(path!).HasFlag(FileAttributes.Hidden))
                {
                    score += 15;
                    reasons.Add(T._("Файл скрыт"));
                    flags |= SuspicionFlags.HiddenFile;
                }
            }
            catch
            {
                // ignore access errors
            }
        }

        // 5. Command line.
        if (!string.IsNullOrWhiteSpace(commandLine))
        {
            if (HiddenPowerShell.IsMatch(commandLine))
            {
                score += 45;
                reasons.Add(T._("Скрытый или закодированный запуск PowerShell"));
                flags |= SuspicionFlags.HiddenPowerShell;
            }
            else if (ScriptLauncher.IsMatch(commandLine))
            {
                score += 20;
                reasons.Add(T._("Запуск через скриптовый интерпретатор"));
                flags |= SuspicionFlags.ScriptLauncher;
            }
            if (UrlInCommand.IsMatch(commandLine))
            {
                score += 20;
                reasons.Add(T._("Командная строка содержит интернет-адрес"));
                flags |= SuspicionFlags.UrlInCommand;
            }
        }

        // 6. Known potentially unwanted programs.
        var pup = KnownPups.Match(fileName, null);
        if (pup is not null)
        {
            score += 35;
            reasons.Add(pup);
            flags |= SuspicionFlags.KnownPup;
        }

        // 7. Look-alike of a system binary name (svch0st.exe, scvhost.exe, lsasss.exe).
        var lookalike = LookalikeOf(fileName);
        var isLookalike = lookalike is not null && !(signature?.IsMicrosoft ?? false);
        if (isLookalike)
        {
            score += 55;
            reasons.Add(T.F("Имя похоже на системный процесс «{0}» — возможная подделка", lookalike));
            flags |= SuspicionFlags.LookalikeName;
        }

        // 8. Miners, remote-access and password tools: legit for some people, alarming if you didn't install them.
        var tool = KnownPups.MatchRiskyTool(fileName);
        if (tool is not null)
        {
            score += tool.Value.Score;
            reasons.Add(tool.Value.Reason);
            flags |= SuspicionFlags.RiskyTool;
        }

        // Trust: a valid signature from Microsoft or a known vendor reduces the score,
        // but never hides masquerading, risky tools or scripted launches (the interpreter itself is always signed).
        if (signature is { IsValid: true })
        {
            // A URL in the command line is normal for signed browsers and updaters; it only matters for script hosts.
            var hardSignals = isLookalike || tool is not null ||
                              flags.HasFlag(SuspicionFlags.SystemNameOutsideWindows) || flags.HasFlag(SuspicionFlags.HiddenPowerShell) ||
                              flags.HasFlag(SuspicionFlags.ScriptLauncher);
            if (!hardSignals)
            {
                if (signature.IsMicrosoft)
                    return SuspicionResult.Clean;
                score -= 25;
            }
        }

        score = Math.Clamp(score, 0, 100);
        return score == 0 ? SuspicionResult.Clean : new SuspicionResult(score, reasons, flags);
    }

    /// <summary>Analyzes an installed program by its display name/publisher (for the Programs module).</summary>
    public static SuspicionResult AnalyzeProgram(string displayName, string? publisher, string? installLocation)
    {
        var reasons = new List<string>();
        var flags = SuspicionFlags.None;
        var score = 0;
        if (AppServices.Settings.IsTrusted(displayName))
            return SuspicionResult.Clean;

        var pup = KnownPups.Match(displayName, publisher);
        if (pup is not null)
        {
            score += 50;
            reasons.Add(pup);
            flags |= SuspicionFlags.KnownPup;
        }
        if (string.IsNullOrWhiteSpace(publisher))
        {
            score += 10;
            reasons.Add(T._("Издатель не указан"));
            flags |= SuspicionFlags.NoPublisher;
        }
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var location = ClassifyLocation(installLocation, isInstallFolder: true);
            if (location != RiskyLocation.None)
            {
                score += 20;
                reasons.Add(LocationText(location, isInstallFolder: true));
                flags |= SuspicionFlags.RiskyLocation;
            }
        }
        score = Math.Clamp(score, 0, 100);
        return score == 0 ? SuspicionResult.Clean : new SuspicionResult(score, reasons, flags);
    }

    /// <summary>
    /// Returns a reason string if the path is in a place where legitimate software rarely lives.
    /// For install folders (isInstallFolder) the "directly in AppData" rule is skipped: per-user apps such as
    /// Discord legitimately install into %LOCALAPPDATA%\Name.
    /// </summary>
    public static string? DescribeRiskyLocation(string path, bool isInstallFolder = false)
    {
        var location = ClassifyLocation(path, isInstallFolder);
        return location == RiskyLocation.None ? null : LocationText(location, isInstallFolder);
    }

    /// <summary>The words of <see cref="DescribeRiskyLocation"/> for a kind of place: "runs from" for a file, "installed in" for an install folder.</summary>
    private static string LocationText(RiskyLocation location, bool isInstallFolder) => location switch
    {
        RiskyLocation.Temp => isInstallFolder ? T._("Установлена во временную папку (Temp)") : T._("Запускается из временной папки (Temp)"),
        RiskyLocation.Downloads => isInstallFolder ? T._("Установлена в папку «Загрузки»") : T._("Запускается из папки «Загрузки»"),
        RiskyLocation.RecycleBin => isInstallFolder ? T._("Находится в корзине") : T._("Запускается из корзины"),
        RiskyLocation.Public => isInstallFolder
            ? T._("Установлена в общую папку пользователей (Public)")
            : T._("Запускается из общей папки пользователей (Public)"),
        RiskyLocation.AppDataRoot => T._("Файл лежит прямо в корне AppData/ProgramData"),
        _ => "",
    };

    /// <summary>
    /// What kind of unusual place the path is in (the code behind <see cref="DescribeRiskyLocation"/>): callers that need to tell
    /// the places apart use this, never the words of the description. For install folders the "directly in AppData" rule is skipped.
    /// </summary>
    public static RiskyLocation ClassifyLocation(string path, bool isInstallFolder = false)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return RiskyLocation.None;
        }

        var temp = Path.GetTempPath();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";

        if (IsUnder(full, temp) || IsUnder(full, Path.Combine(localAppData, "Temp")) || IsUnder(full, Path.Combine(Win, "Temp")))
            return RiskyLocation.Temp;
        if (IsUnder(full, Path.Combine(profile, "Downloads")))
            return RiskyLocation.Downloads;
        if (full.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
            return RiskyLocation.RecycleBin;
        if (IsUnder(full, publicDir))
            return RiskyLocation.Public;

        if (isInstallFolder)
            return RiskyLocation.None;
        var dir = Path.GetDirectoryName(full) ?? "";
        if (string.Equals(dir.TrimEnd('\\'), roaming.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dir.TrimEnd('\\'), localAppData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dir.TrimEnd('\\'), programData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return RiskyLocation.AppDataRoot;
        return RiskyLocation.None;
    }

    private static readonly string[] LookalikeTargets = { "svchost", "csrss", "lsass", "winlogon", "explorer", "services", "smss", "wininit", "taskhostw" };

    /// <summary>Legit names that happen to be close to a system name.</summary>
    private static readonly HashSet<string> LookalikeExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "iexplore", "explore", "explorer++", "service", "servicehost", "lsasrv", "taskhost", "taskhostex",
    };

    /// <summary>Returns the system name that fileName imitates (edit distance 1–2), or null.</summary>
    public static string? LookalikeOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (stem.Length < 5 || LookalikeExceptions.Contains(stem) || SystemBinaryNames.Contains(fileName))
            return null;
        foreach (var target in LookalikeTargets)
        {
            if (stem == target)
                return null;
            var d = Levenshtein(stem, target);
            if (d is >= 1 and <= 2 && Math.Abs(stem.Length - target.Length) <= 2)
                return target + ".exe";
        }
        return null;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Windows, Program Files (both) and the Microsoft folders in ProgramData / LocalAppData.</summary>
    public static bool IsMicrosoftInstallLocation(string path)
    {
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return IsUnder(path, Win) ||
               IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
               IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) ||
               IsUnder(path, Path.Combine(pd, "Microsoft")) ||
               IsUnder(path, Path.Combine(local, "Microsoft"));
    }

    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
            return false;
        var f = folder.TrimEnd('\\') + "\\";
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Heuristic for names like "a8f3k2x9q1", "x7Gh2Kq", "3f9a0c1b2d4e".</summary>
    public static bool LooksRandom(string name)
    {
        if (name.Length < 8)
            return false;
        if (Regex.IsMatch(name, "^[0-9a-fA-F]{12,}$"))
            return true;
        // GUID-like names are common for legit installers/updaters, don't flag them alone.
        if (Regex.IsMatch(name, @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-"))
            return false;
        var letters = name.Count(char.IsLetter);
        var digits = name.Count(char.IsDigit);
        if (letters == 0)
            return false;
        var vowels = name.Count(c => "aeiouyAEIOUY".Contains(c));
        var vowelRatio = (double)vowels / letters;
        var transitions = 0;
        for (var i = 1; i < name.Length; i++)
            if (char.IsDigit(name[i]) != char.IsDigit(name[i - 1]))
                transitions++;
        return (vowelRatio < 0.15 && letters >= 6) || (digits >= 3 && transitions >= 4);
    }
}
