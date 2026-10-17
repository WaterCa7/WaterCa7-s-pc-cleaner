using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

public enum BackupKind
{
    Registry,
    Hosts,
    ScheduledTask,
    Other,
}

/// <summary>One file in the backups folder, prepared for display.</summary>
public sealed class BackupEntry
{
    public required string FullPath { get; init; }

    public string FileName => Path.GetFileName(FullPath);

    public required DateTime Time { get; init; }

    public required BackupKind Kind { get; init; }

    /// <summary>Registry key in short form ("HKCU\Software\..."), registry backups only.</summary>
    public string? KeyPath { get; init; }

    /// <summary>Human-readable name, e.g. "Автозагрузка программ (Run) — текущий пользователь".</summary>
    public required string Title { get; init; }

    /// <summary>Second line: key path or file name.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Text of the side "*.txt" description file, if any.</summary>
    public string? Description { get; init; }

    /// <summary>Path of the side description file (deleted together with the backup).</summary>
    public string? DescriptionPath { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Importing this backup writes outside HKCU and needs administrator rights.</summary>
    public bool NeedsAdmin { get; init; }

    public bool CanRestore => Kind == BackupKind.Registry;

    /// <summary>
    /// Only registry exports can be deleted from the page: other modules may need their own backups
    /// (hosts, scheduled tasks) to undo changes.
    /// </summary>
    public bool CanDelete => Kind == BackupKind.Registry;

    public string KindText => Kind switch
    {
        BackupKind.Registry => NeedsAdmin ? "Реестр (система)" : "Реестр",
        BackupKind.Hosts => "Файл hosts",
        BackupKind.ScheduledTask => "Задание",
        _ => "Файл",
    };

    public string TimeText => Format.DateTime(Time);

    public string SizeText => Format.Bytes(SizeBytes);

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string ToolTipText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Title);
            if (KeyPath is not null)
                sb.Append('\n').Append(KeyPath);
            if (HasDescription)
                sb.Append("\n\n").Append(Description);
            sb.Append("\n\nФайл: ").Append(FileName);
            if (NeedsAdmin)
                sb.Append("\nДля восстановления нужны права администратора.");
            return sb.ToString();
        }
    }
}

/// <summary>Lists, describes and deletes files in %LOCALAPPDATA%\WaterCa7sPcCleaner\Backups.</summary>
internal static class BackupCatalog
{
    private static readonly Regex StampRx = new(@"^(?<stamp>\d{8}-\d{6}(?:-\d{3})?)(?:-[0-9a-fA-F]{4})?_(?<rest>.+)$", RegexOptions.CultureInvariant);

    private static readonly (string Long, string Short)[] Hives =
    {
        ("HKEY_LOCAL_MACHINE", "HKLM"),
        ("HKEY_CURRENT_USER", "HKCU"),
        ("HKEY_CLASSES_ROOT", "HKCR"),
        ("HKEY_USERS", "HKU"),
        ("HKEY_CURRENT_CONFIG", "HKCC"),
    };

    /// <summary>All backups in the app's folder, newest first (registry part comes from RegistryBackup.ListBackups).</summary>
    public static List<BackupEntry> List()
    {
        var dir = AppPaths.BackupsDir;
        try
        {
            var regFiles = RegistryBackup.ListBackups().ToList();
            return Build(dir, regFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Не удалось прочитать папку резервных копий: {ex.Message}");
            return new List<BackupEntry>();
        }
    }

    /// <summary>Same as <see cref="List"/> for an arbitrary folder (tests).</summary>
    public static List<BackupEntry> ListIn(string dir)
    {
        var info = new DirectoryInfo(dir);
        return info.Exists ? Build(dir, info.EnumerateFiles("*.reg").ToList()) : new List<BackupEntry>();
    }

    private static List<BackupEntry> Build(string dir, List<FileInfo> regFiles)
    {
        var result = new List<BackupEntry>();
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
            return result;

        var all = info.EnumerateFiles().Where(f => !FileTools.IsReparsePoint(f)).ToList();
        var names = new HashSet<string>(all.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var reg in regFiles.Where(f => f.Exists && !FileTools.IsReparsePoint(f)))
            result.Add(DescribeRegistry(reg));

        foreach (var file in all)
        {
            var ext = file.Extension.ToLowerInvariant();
            if (ext == ".reg" || ext == ".tmp")
                continue;
            // "X.reg.txt" / "X.xml.txt" are descriptions of X, not separate backups.
            if (ext == ".txt" && names.Contains(Path.GetFileNameWithoutExtension(file.Name))
                              && Path.HasExtension(Path.GetFileNameWithoutExtension(file.Name)))
                continue;
            result.Add(DescribeOther(file));
        }

        return result.OrderByDescending(e => e.Time).ToList();
    }

    private static BackupEntry DescribeRegistry(FileInfo file)
    {
        var key = ReadKeyFromRegFile(file.FullName) ?? KeyFromFileName(file.Name);
        var shortKey = ShortHive(key);
        var (description, descriptionPath) = ReadDescription(file.FullName);
        return new BackupEntry
        {
            FullPath = file.FullName,
            Time = TimeFromFileName(file.Name) ?? SafeTime(file),
            Kind = BackupKind.Registry,
            KeyPath = shortKey,
            Title = FriendlyKeyName(shortKey),
            Subtitle = shortKey,
            Description = description,
            DescriptionPath = descriptionPath,
            SizeBytes = SafeLength(file),
            NeedsAdmin = NeedsAdmin(shortKey),
        };
    }

    private static BackupEntry DescribeOther(FileInfo file)
    {
        var ext = file.Extension.ToLowerInvariant();
        var bare = Path.GetFileNameWithoutExtension(file.Name);
        var m = StampRx.Match(bare);
        var rest = m.Success ? m.Groups["rest"].Value : bare;
        var (description, descriptionPath) = ReadDescription(file.FullName);

        BackupKind kind;
        string title;
        if (ext == ".hosts" || bare.Contains("hosts", StringComparison.OrdinalIgnoreCase))
        {
            kind = BackupKind.Hosts;
            title = "Копия файла hosts";
        }
        else if (ext == ".xml" && ReadTaskUri(file.FullName) is { } uri)
        {
            kind = BackupKind.ScheduledTask;
            title = $"Задание планировщика «{uri.TrimStart('\\')}»";
        }
        else if (ext == ".xml")
        {
            kind = BackupKind.ScheduledTask;
            title = $"Задание планировщика «{rest.Replace('_', ' ').Trim()}»";
        }
        else
        {
            kind = BackupKind.Other;
            title = rest.Replace('_', ' ').Trim();
            if (title.Length == 0)
                title = file.Name;
        }

        return new BackupEntry
        {
            FullPath = file.FullName,
            Time = TimeFromFileName(file.Name) ?? SafeTime(file),
            Kind = kind,
            Title = title,
            Subtitle = file.Name,
            Description = description,
            DescriptionPath = descriptionPath,
            SizeBytes = SafeLength(file),
        };
    }

    /// <summary>Reads the first "[KEY]" line of a .reg export (UTF-16 or ANSI/UTF-8).</summary>
    public static string? ReadKeyFromRegFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            for (var i = 0; i < 400; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                    break;
                line = line.Trim().Trim('\0');
                if (line.Length > 2 && line[0] == '[' && line[^1] == ']' && line[1] != '-')
                    return line[1..^1];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать резервную копию {Path.GetFileName(path)}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Parses a .reg file to find out exactly what an import would touch (every key and value).
    /// RegistryBackup exports always hold one key with its subkeys; anything else means the file was edited by hand.
    /// </summary>
    public static RegFileInfo InspectRegFile(string path) => ReadForRestore(path).Info;

    /// <summary>
    /// Reads the file once and parses those bytes. The restore imports exactly these bytes (from a private locked copy),
    /// so what was checked and shown to the user is what Windows writes, even if the file changes in the meantime.
    /// </summary>
    public static (byte[]? Bytes, RegFileInfo Info) ReadForRestore(string path)
    {
        var bytes = RegFileParser.ReadBytes(path, out var tooBig);
        if (bytes is null)
        {
            return (null, new RegFileInfo(null, false, false)
            {
                Problem = tooBig ? "Файл копии слишком большой для копии одного раздела реестра." : null,
            });
        }
        return (bytes, RegFileParser.Parse(bytes));
    }

    /// <summary>
    /// Modules (ModuleData keys) whose saved undo data mentions this backup file — deleting it would break their "undo".
    /// </summary>
    public static List<string> ModulesReferencing(AppSettings settings, string backupPath)
    {
        var name = Path.GetFileName(backupPath);
        if (string.IsNullOrEmpty(name))
            return new List<string>();
        try
        {
            return (settings.ModuleData ?? new Dictionary<string, Dictionary<string, string>>())
                .ToList()
                .Where(m => m.Value is not null && m.Value.Values.ToList()
                    .Any(v => v is not null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .Select(m => m.Key)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // Another module is changing its data right now; the warning is best-effort.
            return new List<string>();
        }
    }

    /// <summary>Sidebar name of a module key ("Optimizer" → "Ускорение").</summary>
    public static string ModuleDisplayName(string key) => key switch
    {
        "Dashboard" => "Главная",
        "Cleaner" => "Очистка",
        "Processes" => "Процессы",
        "Startup" => "Автозагрузка",
        "Programs" => "Программы",
        "Disk" => "Диски",
        "Optimizer" => "Ускорение",
        "Security" => "Безопасность",
        "Settings" => "Настройки",
        _ => key,
    };

    /// <summary>
    /// Restores a readable key from RegistryBackup's file name:
    /// "20240131-101500-123_HKCU_Software_Microsoft_Windows_CurrentVersion_Run.reg" → "HKCU\Software\Microsoft\Windows\CurrentVersion\Run".
    /// Spaces and underscores in key names cannot be told apart, so the .reg content is preferred when available.
    /// </summary>
    public static string KeyFromFileName(string fileName)
    {
        var name = fileName.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        var m = StampRx.Match(name);
        var rest = m.Success ? m.Groups["rest"].Value : name;

        foreach (var (longName, shortName) in Hives)
        {
            foreach (var prefix in new[] { longName, shortName })
            {
                if (string.Equals(rest, prefix, StringComparison.OrdinalIgnoreCase))
                    return shortName;
                if (rest.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                    return shortName + "\\" + rest[(prefix.Length + 1)..].Replace('_', '\\');
            }
        }
        // The hive was cut off (RegistryBackup keeps only the last 80 characters of long names).
        return "…\\" + rest.Replace('_', '\\');
    }

    /// <summary>"HKEY_CURRENT_USER\Software" → "HKCU\Software".</summary>
    public static string ShortHive(string key)
    {
        foreach (var (longName, shortName) in Hives)
        {
            if (string.Equals(key, longName, StringComparison.OrdinalIgnoreCase))
                return shortName;
            if (key.StartsWith(longName + "\\", StringComparison.OrdinalIgnoreCase))
                return shortName + key[longName.Length..];
        }
        return key;
    }

    /// <summary>Everything except the current user's hive needs admin rights to import.</summary>
    public static bool NeedsAdmin(string key)
    {
        var k = ShortHive(key);
        return !(string.Equals(k, "HKCU", StringComparison.OrdinalIgnoreCase)
                 || k.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Plain-language name of well-known keys; otherwise "Раздел «LastSegment»".</summary>
    public static string FriendlyKeyName(string key)
    {
        var k = ShortHive(key);
        var scope = k.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? " — текущий пользователь"
            : k.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? " — все пользователи"
            : "";

        if (Has(k, @"\Explorer\StartupApproved\"))
            return "Включение и отключение автозагрузки" + scope;
        if (EndsOrHas(k, @"\CurrentVersion\RunOnce"))
            return "Однократный автозапуск (RunOnce)" + scope;
        if (EndsOrHas(k, @"\CurrentVersion\Run"))
            return "Автозагрузка программ (Run)" + scope;
        if (After(k, @"\CurrentVersion\Uninstall\") is { } program)
            return $"Запись об установленной программе «{program}»";
        if (After(k, @"\CurrentControlSet\Services\") is { } service)
            return $"Параметры службы «{service}»";
        if (Has(k, @"\Image File Execution Options"))
            return "Параметры запуска программ (IFEO)";
        if (Has(k, @"\Winlogon"))
            return "Параметры входа в Windows (Winlogon)";
        if (Has(k, @"\Multimedia\SystemProfile"))
            return "Приоритеты мультимедиа и игр";
        if (Has(k, @"\GameBar") || Has(k, @"\GameConfigStore") || Has(k, @"\GameDVR"))
            return "Игровые настройки Windows";
        if (Has(k, @"\Control Panel\Desktop"))
            return "Параметры рабочего стола" + scope;
        if (Has(k, @"\Control Panel\Mouse"))
            return "Параметры мыши";
        if (Has(k, @"\Explorer\Advanced"))
            return "Параметры Проводника";
        if (Has(k, @"\Explorer\Serialize"))
            return "Параметры запуска Проводника";
        if (Has(k, @"\Schedule\TaskCache"))
            return "Планировщик заданий";
        if (Has(k, @"\Control\Power") || Has(k, @"\PowerSettings"))
            return "Параметры электропитания";
        if (Has(k, @"\Windows Defender"))
            return "Microsoft Defender";
        if (Has(k, @"\Policies\"))
            return "Групповые политики" + scope;

        var last = k.TrimEnd('\\');
        var idx = last.LastIndexOf('\\');
        var segment = idx >= 0 ? last[(idx + 1)..] : last;
        return segment.Length == 0 || segment == "…" ? "Раздел реестра" : $"Раздел «{segment}»";
    }

    public static DateTime? TimeFromFileName(string fileName)
    {
        var m = StampRx.Match(Path.GetFileNameWithoutExtension(fileName));
        if (!m.Success)
            return null;
        var stamp = m.Groups["stamp"].Value;
        var format = stamp.Length > 15 ? "yyyyMMdd-HHmmss-fff" : "yyyyMMdd-HHmmss";
        return DateTime.TryParseExact(stamp, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
            ? dt
            : null;
    }

    /// <summary>
    /// Deletes a registry backup (.reg + its .txt description). Refuses anything outside <paramref name="backupsDir"/>,
    /// other file types and junctions/symlinks.
    /// </summary>
    public static (bool Ok, string Message) DeleteBackup(string regFile, string backupsDir)
    {
        var name = Path.GetFileName(regFile);
        // Backups live directly in the folder; anything in a subfolder (possibly behind a junction) is refused.
        if (!PathGuard.IsDirectChild(regFile, backupsDir))
        {
            Log.Warn($"Удаление отменено: {regFile} находится вне папки резервных копий");
            return (false, "Файл находится вне папки резервных копий — удаление отменено.");
        }
        if (!regFile.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            return (false, "Из программы можно удалять только резервные копии реестра (.reg).");

        var files = new List<string> { Path.GetFullPath(regFile) };
        var side = Path.GetFullPath(regFile) + ".txt";
        if (File.Exists(side) && PathGuard.IsDirectChild(side, backupsDir))
            files.Add(side);

        try
        {
            foreach (var f in files)
            {
                var info = new FileInfo(f);
                if (!info.Exists)
                    continue;
                if (FileTools.IsReparsePoint(info))
                    return (false, "Это ссылка на другой файл, а не резервная копия — удаление отменено.");
            }
            foreach (var f in files.Where(File.Exists))
                File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось удалить резервную копию {name}: {ex.Message}");
            return (false, $"Не удалось удалить файл: {ex.Message}");
        }

        Log.Action($"Удалена резервная копия реестра: {name}");
        return (true, $"Резервная копия «{name}» удалена.");
    }

    private static (string? Text, string? Path) ReadDescription(string file)
    {
        var side = file + ".txt";
        try
        {
            if (!File.Exists(side))
                return (null, null);
            var text = File.ReadAllText(side).Trim();
            if (text.Length > 400)
                text = text[..400] + "…";
            return (text.Length == 0 ? null : text, side);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, side);
        }
    }

    private static string? ReadTaskUri(string xmlFile)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(xmlFile, settings);
            var sawTask = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (!sawTask)
                {
                    if (reader.LocalName != "Task")
                        return null;
                    sawTask = true;
                    continue;
                }
                if (reader.LocalName == "URI")
                    return reader.ReadElementContentAsString().Trim();
                if (reader.LocalName is "Triggers" or "Actions" or "Settings")
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // Not a task export or unreadable — shown as a plain file.
        }
        return null;
    }

    private static bool Has(string key, string part) => key.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static bool EndsOrHas(string key, string part)
        => key.EndsWith(part, StringComparison.OrdinalIgnoreCase) || Has(key, part + "\\");

    private static string? After(string key, string part)
    {
        var idx = key.IndexOf(part, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var tail = key[(idx + part.Length)..];
        var cut = tail.IndexOf('\\');
        var segment = cut >= 0 ? tail[..cut] : tail;
        return segment.Length == 0 ? null : segment;
    }

    private static DateTime SafeTime(FileInfo f)
    {
        try
        {
            return f.LastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static long SafeLength(FileInfo f)
    {
        try
        {
            return f.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
