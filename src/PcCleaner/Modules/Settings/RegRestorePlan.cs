using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

/// <summary>A value as it is in the registry now (Data has the same shape as <see cref="RegFileValue.Data"/>).</summary>
internal sealed record CurrentRegValue(int Type, object? Data);

/// <summary>State of one key right now. Not readable = unknown, so every value in it is shown as a change.</summary>
internal sealed record CurrentRegKey(bool Exists, bool Readable, IReadOnlyDictionary<string, CurrentRegValue> Values)
{
    private static readonly IReadOnlyDictionary<string, CurrentRegValue> NoValues =
        new Dictionary<string, CurrentRegValue>(StringComparer.OrdinalIgnoreCase);

    public static CurrentRegKey Missing { get; } = new(false, true, NoValues);

    public static CurrentRegKey Unreadable { get; } = new(true, false, NoValues);
}

/// <summary>A value the import would add or change. <see cref="Current"/> is null when the value does not exist now.</summary>
internal sealed record RegValueChange(RegFileValue Value, CurrentRegValue? Current, bool CurrentKnown, bool IsLaunch);

/// <summary>
/// What restoring a registry backup would actually change: the values that differ from the current registry
/// (values that start programs listed separately) and the keys that would be created. Built before the confirmation,
/// so the user sees the real data instead of only a key name.
/// </summary>
internal sealed class RegRestorePlan
{
    public const int MaxShownChanges = 6;
    public const int MaxShownLaunch = 8;
    public const int MaxShownKeys = 4;

    private static readonly Regex ExecutableRx = new(
        @"\.(?:exe|dll|sys|scr|cpl|ocx|bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|hta|msi|jar|lnk|pif)(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Run, RunOnce, RunOnceEx, RunServices(Once) and the policy Run list: every value is a command.</summary>
    private static readonly Regex RunKeyRx = new(
        @"\\(?:CurrentVersion|Policies\\Explorer)\\Run(?:Once|OnceEx|Services|ServicesOnce)?(?:\\|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Key part → value names that start a program (null = every value of that key).</summary>
    private static readonly (string KeyPart, string[]? Names)[] LaunchRules =
    {
        (@"\Session Manager\AppCertDlls", null),
        (@"\Services\", new[] { "ImagePath", "ServiceDll", "FailureCommand" }),
        (@"\Winlogon", new[] { "Shell", "Userinit", "Taskman", "AppSetup", "GinaDLL", "VMApplet", "System", "DllName" }),
        (@"\Windows NT\CurrentVersion\Windows", new[] { "AppInit_DLLs", "Load", "Run" }),
        (@"\Image File Execution Options\", new[] { "Debugger", "VerifierDlls" }),
        (@"\SilentProcessExit\", new[] { "MonitorProcess" }),
        (@"\Active Setup\Installed Components\", new[] { "StubPath" }),
        (@"\Session Manager", new[] { "BootExecute", "SetupExecute", "Execute" }),
        (@"\Control\Lsa", new[] { "Authentication Packages", "Notification Packages", "Security Packages" }),
        (@"\Command Processor", new[] { "AutoRun" }),
        (@"\Print\Monitors\", new[] { "Driver" }),
        (@"\CurrentVersion\Uninstall\", new[] { "UninstallString", "QuietUninstallString", "ModifyPath" }),
        (@"\Explorer\Shell Folders", new[] { "Startup", "Common Startup" }),
        (@"\Explorer\User Shell Folders", new[] { "Startup", "Common Startup" }),
    };

    /// <summary>Default values of these keys are command lines or DLLs (shell verbs, COM servers).</summary>
    private static readonly string[] CommandKeyEnds =
    {
        @"\command", @"\InprocServer32", @"\LocalServer32", @"\InprocHandler32", @"\LocalServer",
    };

    private RegRestorePlan(string rootKey, List<RegValueChange> changes, int unchanged, List<string> newKeys)
    {
        RootKey = rootKey;
        Changes = changes;
        Unchanged = unchanged;
        NewKeys = newKeys;
    }

    public string RootKey { get; }

    /// <summary>Values that would be added or changed (or whose current state cannot be read), in file order.</summary>
    public IReadOnlyList<RegValueChange> Changes { get; }

    /// <summary>Values already equal to the backup (writing them changes nothing).</summary>
    public int Unchanged { get; }

    /// <summary>Subkeys of the root that do not exist now and would be created.</summary>
    public IReadOnlyList<string> NewKeys { get; }

    public IEnumerable<RegValueChange> LaunchChanges => Changes.Where(c => c.IsLaunch);

    public IEnumerable<RegValueChange> OtherChanges => Changes.Where(c => !c.IsLaunch);

    public bool ChangesNothing => Changes.Count == 0 && NewKeys.Count == 0;

    /// <summary>True when the confirmation cannot list everything (the full list then goes to the log).</summary>
    public bool IsTruncated =>
        LaunchChanges.Count() > MaxShownLaunch || OtherChanges.Count() > MaxShownChanges || NewKeys.Count > MaxShownKeys;

    /// <summary>Compares every value of a parsed, single-root backup with the registry as <paramref name="readKey"/> reports it.</summary>
    public static RegRestorePlan Build(RegFileInfo reg, Func<string, CurrentRegKey> readKey)
    {
        var root = reg.RootKey ?? throw new ArgumentException("The backup has no root key.", nameof(reg));
        var cache = new Dictionary<string, CurrentRegKey>(StringComparer.OrdinalIgnoreCase);
        CurrentRegKey Read(string key)
        {
            if (!cache.TryGetValue(key, out var state))
                cache[key] = state = readKey(key);
            return state;
        }

        var changes = new List<RegValueChange>();
        var unchanged = 0;
        foreach (var value in reg.Values)
        {
            var state = Read(value.Key);
            var launch = IsLaunchValue(value);
            if (!state.Readable)
                changes.Add(new RegValueChange(value, null, false, launch));
            else if (!state.Exists || !state.Values.TryGetValue(value.Name, out var current))
                changes.Add(new RegValueChange(value, null, true, launch));
            else if (SameValue(value, current))
                unchanged++;
            else
                changes.Add(new RegValueChange(value, current, true, launch));
        }

        var newKeys = reg.Keys
            .Where(k => !string.Equals(k, root, StringComparison.OrdinalIgnoreCase))
            .Where(k => Read(k) is { Readable: true, Exists: false })
            .ToList();
        return new RegRestorePlan(root, changes, unchanged, newKeys);
    }

    /// <summary>Whether a value starts a program: autostart and service commands, debuggers, COM servers, scripts…</summary>
    public static bool IsLaunchValue(RegFileValue value)
    {
        var key = value.Key;
        if (RunKeyRx.IsMatch(key))
            return true;
        foreach (var (part, names) in LaunchRules)
        {
            if (!key.Contains(part, StringComparison.OrdinalIgnoreCase))
                continue;
            if (names is null || names.Any(n => string.Equals(n, value.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        if (value.Name.Length == 0 && CommandKeyEnds.Any(e => key.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return true;
        return value.Data switch
        {
            string s => ExecutableRx.IsMatch(s),
            string[] list => list.Any(s => ExecutableRx.IsMatch(s)),
            _ => false,
        };
    }

    public static bool SameValue(RegFileValue value, CurrentRegValue current)
    {
        if (value.Type != current.Type)
            return false;
        return (value.Data, current.Data) switch
        {
            (string a, string b) => string.Equals(a.TrimEnd('\0'), b.TrimEnd('\0'), StringComparison.Ordinal),
            (string[] a, string[] b) => RegFileParser.TrimMulti(a).SequenceEqual(RegFileParser.TrimMulti(b), StringComparer.Ordinal),
            (uint a, uint b) => a == b,
            (ulong a, ulong b) => a == b,
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            _ => false,
        };
    }

    // ===================== text for the confirmation =====================

    /// <summary>"What will change" block: other changes, new subkeys and the number of values that stay as they are.</summary>
    public string DescribeChanges()
    {
        if (ChangesNothing)
            return "Все значения в копии совпадают с текущими — восстановление ничего не изменит.";

        var sb = new StringBuilder();
        var other = OtherChanges.ToList();
        if (other.Count > 0)
        {
            sb.Append(LaunchChanges.Any() ? "Другие изменения в реестре:\n" : "Что изменится в реестре:\n");
            sb.Append(SettingsViewModel.BulletList(other.Select(c => ChangeLine(c, 90)), MaxShownChanges));
        }
        if (NewKeys.Count > 0)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append("Будут созданы разделы:\n");
            sb.Append(SettingsViewModel.BulletList(NewKeys.Select(k => Visible(Relative(k), 80)), MaxShownKeys));
        }
        if (Unchanged > 0)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append("Без изменений (уже совпадают с копией): ")
              .Append(Format.Count(Unchanged, "значение", "значения", "значений"))
              .Append('.');
        }
        return sb.ToString();
    }

    /// <summary>Warning with every value that starts a program (null if there are none).</summary>
    public string? DescribeLaunchValues()
    {
        var launch = LaunchChanges.ToList();
        if (launch.Count == 0)
            return null;
        return "Внимание: эти значения запускают программы. Восстанавливайте копию, только если узнаёте их — " +
               "папку с копиями могут изменить и другие программы, запущенные от вашего имени:\n" +
               SettingsViewModel.BulletList(launch.Select(c => ChangeLine(c, 200)), MaxShownLaunch);
    }

    /// <summary>Every change in one line (written to the log when the confirmation had to shorten the lists).</summary>
    public string DescribeAll()
    {
        var parts = Changes.Select(c => (c.IsLaunch ? "[запуск] " : "") + ChangeLine(c, 300))
            .Concat(NewKeys.Select(k => "новый раздел " + Visible(Relative(k), 200)))
            .Take(300)
            .ToList();
        return string.Join("; ", parts);
    }

    private string ChangeLine(RegValueChange change, int max)
    {
        var value = change.Value;
        var name = value.Name.Length == 0 ? "(по умолчанию)" : Visible(value.Name, 60);
        var rel = Relative(value.Key);
        var label = rel.Length == 0 ? name : $"{Visible(rel, 60)}\\{name}";
        var data = FormatData(value.Data, max);
        if (!change.CurrentKnown)
            return $"{label} = {data}";
        if (change.Current is null)
            return $"{label} = {data} (сейчас нет)";
        return $"{label}: {FormatData(change.Current.Data, max)} → {data}";
    }

    /// <summary>Key relative to the restored root ("" for the root itself).</summary>
    private string Relative(string key)
        => key.Length > RootKey.Length && key.StartsWith(RootKey + "\\", StringComparison.OrdinalIgnoreCase)
            ? key[(RootKey.Length + 1)..]
            : string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase) ? "" : key;

    internal static string FormatData(object? data, int max) => data switch
    {
        null => "(нет)",
        string { Length: 0 } => "(пустая строка)",
        string s => Visible(s, max),
        string[] { Length: 0 } => "(пустой список)",
        string[] list => Visible(string.Join("; ", list), max),
        uint n => n <= 9 ? n.ToString(CultureInfo.InvariantCulture) : $"{n.ToString(CultureInfo.InvariantCulture)} (0x{n:X})",
        ulong n => n <= 9 ? n.ToString(CultureInfo.InvariantCulture) : $"{n.ToString(CultureInfo.InvariantCulture)} (0x{n:X})",
        byte[] { Length: 0 } => "(пусто)",
        byte[] bytes => $"двоичные данные, {Format.Count(bytes.Length, "байт", "байта", "байт")}",
        _ => Visible(data.ToString() ?? "", max),
    };

    /// <summary>
    /// Text from the file made safe for a message box: line breaks, control and invisible formatting characters
    /// (e.g. right-to-left override) are shown as escapes, so data cannot fake extra lines or hide its real end;
    /// long text is shortened in the middle, keeping both the start and the end.
    /// </summary>
    internal static string Visible(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max + 16));
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            var category = char.GetUnicodeCategory(c);
            if (c == '\r')
                sb.Append("\\r");
            else if (c == '\n')
                sb.Append("\\n");
            else if (c == '\t')
                sb.Append("\\t");
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                     or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                     or UnicodeCategory.OtherNotAssigned)
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        var s = sb.ToString();
        if (max < 10 || s.Length <= max)
            return s;
        var head = (max - 1) / 2;
        var tail = max - 1 - head;
        return s[..head] + "…" + s[^tail..];
    }

    /// <summary>One line of free text (e.g. the side description file) for a message: whitespace collapsed, escapes as in <see cref="Visible"/>.</summary>
    internal static string OneLine(string text, int max)
        => Visible(Regex.Replace(text.Trim(), @"[\r\n\t]+", " "), max);

    // ===================== reading the current registry =====================

    /// <summary>Reads one key (64-bit view, like the export and the existence check). Never writes anything.</summary>
    public static CurrentRegKey ReadCurrent(string shortKey)
    {
        var idx = shortKey.IndexOf('\\');
        var hiveName = idx < 0 ? shortKey : shortKey[..idx];
        var sub = idx < 0 ? "" : shortKey[(idx + 1)..];
        RegistryHive? hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => null,
        };
        if (hive is null)
            return CurrentRegKey.Unreadable;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = sub.Length == 0 ? root : root.OpenSubKey(sub, writable: false);
            if (key is null)
                return CurrentRegKey.Missing;
            var values = new Dictionary<string, CurrentRegValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                values[name] = new CurrentRegValue(TypeOf(kind), Normalize(data));
            }
            return new CurrentRegKey(true, true, values);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return CurrentRegKey.Unreadable;
        }
    }

    private static int TypeOf(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => 1,
        RegistryValueKind.ExpandString => 2,
        RegistryValueKind.Binary => 3,
        RegistryValueKind.DWord => 4,
        RegistryValueKind.MultiString => 7,
        RegistryValueKind.QWord => 11,
        RegistryValueKind.None => 0,
        _ => -1, // unknown type: never equal, so it is always shown
    };

    private static object? Normalize(object? data) => data switch
    {
        int i => unchecked((uint)i),
        long l => unchecked((ulong)l),
        _ => data,
    };
}

/// <summary>
/// A private copy of a backup, held open so no other process can change, rename or replace it until the import is done.
/// The import reads exactly the bytes that were checked and shown to the user, even if the original in the
/// (user-writable) backups folder is rewritten while the confirmation is open.
/// </summary>
internal sealed class LockedRegCopy : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _dir;

    private LockedRegCopy(string path, string dir, FileStream lockStream)
    {
        FilePath = path;
        _dir = dir;
        _lock = lockStream;
    }

    /// <summary>Path to pass to reg.exe (same file name as the backup, so the log names the same file).</summary>
    public string FilePath { get; }

    /// <summary>Creates the copy in a new random folder under <paramref name="parentDir"/> (default: %TEMP%). Null + logged warning on failure.</summary>
    public static LockedRegCopy? Create(byte[] bytes, string fileName, string? parentDir = null)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            name = "backup.reg";
        var dir = Path.Combine(parentDir ?? Path.GetTempPath(), "WaterCa7sPcCleaner-restore-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, name);
        FileStream? lockStream = null;
        var created = false;
        try
        {
            // A fresh random folder: nothing else can have prepared a file or a link there.
            if (Directory.Exists(dir) || File.Exists(dir))
                throw new IOException("временная папка уже существует");
            var info = Directory.CreateDirectory(dir);
            if (FileTools.IsReparsePoint(info))
                throw new IOException("временная папка оказалась ссылкой");
            using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                writer.Write(bytes);
                writer.Flush(flushToDisk: true);
            }

            // Read-only handle that lets others only read: nobody can write, rename or delete the file while it is open
            // (reg.exe opens it for reading). Then check that nothing changed between writing and locking.
            lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (lockStream.Length != bytes.Length)
                throw new IOException("копия изменилась при записи");
            var check = new byte[bytes.Length];
            lockStream.ReadExactly(check);
            if (!check.AsSpan().SequenceEqual(bytes))
                throw new IOException("копия изменилась при записи");
            lockStream.Position = 0;
            return new LockedRegCopy(path, dir, lockStream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lockStream?.Dispose();
            // Only what this call created is removed (never a file or folder that was there before).
            if (created)
                Cleanup(path, dir);
            Log.Warn($"Не удалось подготовить резервную копию {name} к восстановлению: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        Cleanup(FilePath, _dir);
    }

    private static void Cleanup(string path, string dir)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file that stays behind is harmless; Windows cleans %TEMP% itself.
        }
    }
}
