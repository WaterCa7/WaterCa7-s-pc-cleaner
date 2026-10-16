using System.IO;
using System.Text.RegularExpressions;

namespace PcCleaner.Core;

public enum SuspicionLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public sealed record SuspicionResult(int Score, IReadOnlyList<string> Reasons)
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
        SuspicionLevel.High => "Высокий риск",
        SuspicionLevel.Medium => "Подозрительно",
        SuspicionLevel.Low => "Внимание",
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
        @"(\\|^)(wscript|cscript|mshta|powershell|pwsh)(\.exe)?\b|(\\|^)cmd(\.exe)?\s+/c\b|regsvr32(\.exe)?\s+/s\s+/i:http|rundll32(\.exe)?\s+javascript:",
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
        var score = 0;

        if (!string.IsNullOrWhiteSpace(path) && AppServices.Settings.IsTrusted(path))
            return SuspicionResult.Clean;

        var fileName = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path);
        var dir = string.IsNullOrWhiteSpace(path) ? "" : (Path.GetDirectoryName(path) ?? "");

        // 1. Imitation of a system binary outside of the Windows folder.
        if (fileName.Length > 0 && SystemBinaryNames.Contains(fileName) && !IsUnder(path!, Win))
        {
            score += 70;
            reasons.Add($"Имя системного файла «{fileName}», но расположен не в папке Windows");
        }

        // 2. Location.
        if (path is not null && path.Length > 0)
        {
            var location = DescribeRiskyLocation(path);
            if (location is not null)
            {
                score += 30;
                reasons.Add(location);
            }
        }

        // 3. Signature.
        if (signature is not null)
        {
            switch (signature.Status)
            {
                case SignatureStatus.Invalid:
                    score += 45;
                    reasons.Add("Цифровая подпись повреждена или отозвана");
                    break;
                case SignatureStatus.Untrusted:
                    score += 20;
                    reasons.Add("Подпись недоверенным сертификатом");
                    break;
                case SignatureStatus.Unsigned:
                    score += 20;
                    reasons.Add("Нет цифровой подписи");
                    break;
            }
        }

        // 4. File name tricks.
        if (fileName.Length > 0)
        {
            if (DoubleExtension.IsMatch(fileName))
            {
                score += 50;
                reasons.Add("Двойное расширение (маскировка под документ)");
            }
            else if (LooksRandom(Path.GetFileNameWithoutExtension(fileName)))
            {
                score += 15;
                reasons.Add("Случайное на вид имя файла");
            }

            try
            {
                if (File.Exists(path) && File.GetAttributes(path!).HasFlag(FileAttributes.Hidden))
                {
                    score += 15;
                    reasons.Add("Файл скрыт");
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
                reasons.Add("Скрытый или закодированный запуск PowerShell");
            }
            else if (ScriptLauncher.IsMatch(commandLine))
            {
                score += 20;
                reasons.Add("Запуск через скриптовый интерпретатор");
            }
            if (UrlInCommand.IsMatch(commandLine))
            {
                score += 20;
                reasons.Add("Командная строка содержит интернет-адрес");
            }
        }

        // 6. Known potentially unwanted programs.
        var pup = KnownPups.Match(fileName, null);
        if (pup is not null)
        {
            score += 35;
            reasons.Add(pup);
        }

        // Trust: a valid signature from Microsoft or a known vendor reduces the score,
        // but never hides masquerading or scripted launches (the interpreter itself is always signed).
        if (signature is { IsValid: true })
        {
            var hardSignals = reasons.Any(r => r.StartsWith("Имя системного файла") || r.StartsWith("Скрытый") ||
                                               r.StartsWith("Запуск через скриптовый") || r.StartsWith("Командная строка"));
            if (!hardSignals)
            {
                if (signature.IsMicrosoft)
                    return SuspicionResult.Clean;
                score -= 25;
            }
        }

        score = Math.Clamp(score, 0, 100);
        return score == 0 ? SuspicionResult.Clean : new SuspicionResult(score, reasons);
    }

    /// <summary>Analyzes an installed program by its display name/publisher (for the Programs module).</summary>
    public static SuspicionResult AnalyzeProgram(string displayName, string? publisher, string? installLocation)
    {
        var reasons = new List<string>();
        var score = 0;
        if (AppServices.Settings.IsTrusted(displayName))
            return SuspicionResult.Clean;

        var pup = KnownPups.Match(displayName, publisher);
        if (pup is not null)
        {
            score += 50;
            reasons.Add(pup);
        }
        if (string.IsNullOrWhiteSpace(publisher))
        {
            score += 10;
            reasons.Add("Издатель не указан");
        }
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var location = DescribeRiskyLocation(installLocation);
            if (location is not null)
            {
                score += 20;
                reasons.Add(location.Replace("Запускается", "Установлена"));
            }
        }
        score = Math.Clamp(score, 0, 100);
        return score == 0 ? SuspicionResult.Clean : new SuspicionResult(score, reasons);
    }

    /// <summary>Returns a reason string if the path is in a place where legitimate software rarely lives.</summary>
    public static string? DescribeRiskyLocation(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        var temp = Path.GetTempPath();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";

        if (IsUnder(full, temp) || IsUnder(full, Path.Combine(localAppData, "Temp")) || IsUnder(full, Path.Combine(Win, "Temp")))
            return "Запускается из временной папки (Temp)";
        if (IsUnder(full, Path.Combine(profile, "Downloads")))
            return "Запускается из папки «Загрузки»";
        if (full.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
            return "Запускается из корзины";
        if (IsUnder(full, publicDir))
            return "Запускается из общей папки пользователей (Public)";

        var dir = Path.GetDirectoryName(full) ?? "";
        if (string.Equals(dir.TrimEnd('\\'), roaming.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dir.TrimEnd('\\'), localAppData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dir.TrimEnd('\\'), programData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "Файл лежит прямо в корне AppData/ProgramData";
        return null;
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
