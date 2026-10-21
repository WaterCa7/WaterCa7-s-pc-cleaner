using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

internal static class ProcessMath
{
    /// <summary>
    /// CPU usage of one process between two samples, as a share of the whole machine (like Task Manager):
    /// CPU time spent / wall time elapsed / number of logical processors.
    /// </summary>
    public static double CpuPercent(long previousCpu100ns, long currentCpu100ns, double elapsedSeconds, int logicalCpus)
    {
        if (elapsedSeconds <= 0 || logicalCpus <= 0)
            return 0;
        var delta = currentCpu100ns - previousCpu100ns;
        if (delta <= 0)
            return 0;
        var percent = delta / 1e7 / elapsedSeconds / logicalCpus * 100.0;
        return Math.Clamp(percent, 0, 100);
    }

    public static double CpuPercent(TimeSpan previousCpu, TimeSpan currentCpu, TimeSpan elapsed, int logicalCpus)
        => CpuPercent(previousCpu.Ticks, currentCpu.Ticks, elapsed.TotalSeconds, logicalCpus);

    public static DateTime? FromFileTime(long fileTime)
    {
        if (fileTime <= 0)
            return null;
        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>"0%" for idle processes, otherwise "12,3%".</summary>
    public static string CpuText(double percent) => percent < 0.05 ? "0%" : Format.Percent(percent);
}

/// <summary>
/// Turns cumulative CPU times into percentages between consecutive snapshots. One sampler per consumer
/// (the live page and the dashboard analysis keep separate baselines).
/// </summary>
internal sealed class CpuSampler
{
    /// <summary>A baseline older than this is useless for "current" CPU usage.</summary>
    private static readonly double MaxBaselineAgeSeconds = 10;

    private Dictionary<ProcessKey, long> _previous = new();
    private long _previousTimestamp;

    public bool HasFreshBaseline(long nowTimestamp)
        => _previousTimestamp != 0 && (nowTimestamp - _previousTimestamp) / (double)Stopwatch.Frequency < MaxBaselineAgeSeconds;

    public void Reset()
    {
        _previous = new Dictionary<ProcessKey, long>();
        _previousTimestamp = 0;
    }

    /// <summary>Returns CPU % for each raw process (0 for processes without a previous sample) and stores the new baseline.</summary>
    public double[] Sample(IReadOnlyList<RawProcess> raw, long timestamp, int logicalCpus)
    {
        var result = new double[raw.Count];
        var elapsed = _previousTimestamp == 0 ? 0 : (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
        var next = new Dictionary<ProcessKey, long>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            next[r.Key] = r.CpuTime;
            if (elapsed > 0 && _previous.TryGetValue(r.Key, out var previous))
                result[i] = ProcessMath.CpuPercent(previous, r.CpuTime, elapsed, logicalCpus);
        }
        _previous = next;
        _previousTimestamp = timestamp;
        return result;
    }
}

internal enum KillVerdict
{
    Allowed,
    /// <summary>Allowed, but the confirmation must carry an extra warning (explorer.exe).</summary>
    Warn,
    Blocked,
}

/// <summary>Never lets the user terminate processes Windows cannot live without (or the cleaner itself).</summary>
internal static class ProcessGuard
{
    public static string CriticalText =>
        T._("Это важный системный процесс Windows. Завершать его нельзя: компьютер может зависнуть, перезагрузиться или остаться без защиты.");

    public static string SelfText => T._("Это сам WaterCa7s PC Cleaner. Чтобы его закрыть, просто закройте окно программы.");

    public static string ExplorerText =>
        T._("Это Проводник Windows: рабочий стол и панель задач. Если его завершить, они на несколько секунд пропадут и запустятся снова, а открытые окна папок закроются.");

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "System Idle Process", "Registry", "Memory Compression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost",
        "MsMpEng", "NisSrv", "SecurityHealthService", "MsSense",
    };

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Folders the real critical binaries live in directly. Deliberately not "anything under C:\Windows": C:\Windows\Temp,
    /// System32\Tasks and a few others are writable by ordinary users, and a fake svchost.exe there must stay killable.
    /// </summary>
    private static readonly string[] SystemBinaryDirs =
    {
        Environment.SystemDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Path.Combine(WindowsDir, "SysArm32"),
    };

    /// <summary>Admin-only locations of Microsoft Defender (MsMpEng, NisSrv, MsSense).</summary>
    private static readonly string[] SystemSoftwareRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender Advanced Threat Protection"),
    };

    public static string BaseName(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public static bool IsCriticalName(string name) => CriticalNames.Contains(BaseName(name));

    /// <summary>
    /// Decides whether a process may be terminated. A critical name only protects the real Windows binary:
    /// a fake "svchost.exe" in AppData is exactly what the user should be able to kill.
    /// </summary>
    public static (KillVerdict Verdict, string? Reason) Check(int pid, string name, string? path, int? selfPid = null)
    {
        if (pid == (selfPid ?? Environment.ProcessId))
            return (KillVerdict.Blocked, SelfText);
        if (pid is 0 or 4)
            return (KillVerdict.Blocked, CriticalText);

        var baseName = BaseName(name);
        if (CriticalNames.Contains(baseName) && IsSystemLocation(path))
            return (KillVerdict.Blocked, CriticalText);

        if (string.Equals(baseName, "explorer", StringComparison.OrdinalIgnoreCase) &&
            (path is null || Suspicion.IsUnder(path, WindowsDir)))
            return (KillVerdict.Warn, ExplorerText);

        return (KillVerdict.Allowed, null);
    }

    /// <summary>Unknown path (access denied) counts as system: we can't prove it is a fake.</summary>
    internal static bool IsSystemLocation(string? path)
    {
        if (path is null)
            return true;
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return true; // unparsable: be conservative
        }
        if (dir is not null &&
            SystemBinaryDirs.Any(d => d.Length > 0 && string.Equals(dir.TrimEnd('\\'), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return true;
        return SystemSoftwareRoots.Any(r => r.Length > 0 && Suspicion.IsUnder(path, r));
    }

    public static KillVerdict Worst(IEnumerable<KillVerdict> verdicts)
    {
        var worst = KillVerdict.Allowed;
        foreach (var v in verdicts)
            if (v > worst)
                worst = v;
        return worst;
    }
}

/// <summary>Friendly descriptions (in the language of the UI) for Windows processes whose file can't be read without admin rights.</summary>
internal static class KnownWindowsProcesses
{
    private static readonly Dictionary<string, Func<string>> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = () => T._("Ядро Windows"),
        ["Registry"] = () => T._("Реестр Windows"),
        ["Memory Compression"] = () => T._("Сжатие памяти Windows"),
        ["Secure System"] = () => T._("Защищённое ядро Windows"),
        ["smss"] = () => T._("Диспетчер сеансов Windows"),
        ["csrss"] = () => T._("Процесс исполнения клиент-сервер"),
        ["wininit"] = () => T._("Запуск Windows"),
        ["winlogon"] = () => T._("Вход в Windows"),
        ["services"] = () => T._("Диспетчер служб Windows"),
        ["lsass"] = () => T._("Локальная система безопасности (LSA)"),
        ["LsaIso"] = () => T._("Изолированная LSA (Credential Guard)"),
        ["svchost"] = () => T._("Хост-процесс для служб Windows"),
        ["dwm"] = () => T._("Диспетчер окон рабочего стола"),
        ["fontdrvhost"] = () => T._("Драйвер шрифтов Windows"),
        ["conhost"] = () => T._("Узел окна консоли"),
        ["MsMpEng"] = () => T._("Антивирус Microsoft Defender"),
        ["NisSrv"] = () => T._("Проверка сети Microsoft Defender"),
        ["MpDefenderCoreService"] = () => T._("Ядро Microsoft Defender"),
        ["SecurityHealthService"] = () => T._("Служба «Безопасность Windows»"),
        ["MsSense"] = () => T._("Microsoft Defender для конечных точек"),
        ["SearchIndexer"] = () => T._("Индексатор поиска Windows"),
        ["SearchProtocolHost"] = () => T._("Узел протокола поиска Windows"),
        ["SearchFilterHost"] = () => T._("Узел фильтров поиска Windows"),
        ["spoolsv"] = () => T._("Диспетчер печати"),
        ["WmiPrvSE"] = () => T._("Узел поставщика WMI"),
        ["WUDFHost"] = () => T._("Узел драйверов пользовательского режима"),
        ["dasHost"] = () => T._("Узел связи с устройствами"),
        ["vmmem"] = () => T._("Память виртуальных машин (WSL, Hyper-V)"),
        ["vmwp"] = () => T._("Рабочий процесс виртуальной машины"),
        ["vmcompute"] = () => T._("Служба вычислений Hyper-V"),
        ["AggregatorHost"] = () => T._("Узел сбора данных Windows"),
        ["audiodg"] = () => T._("Изоляция графов аудиоустройств Windows"),
        ["taskhostw"] = () => T._("Хост-процесс для задач Windows"),
        ["RuntimeBroker"] = () => T._("Брокер среды выполнения"),
        ["sihost"] = () => T._("Узел инфраструктуры оболочки"),
        ["ctfmon"] = () => T._("Загрузчик ввода текста (CTF)"),
        ["smartscreen"] = () => T._("Фильтр SmartScreen"),
        ["MoUsoCoreWorker"] = () => T._("Центр обновления Windows"),
        ["TrustedInstaller"] = () => T._("Установщик модулей Windows"),
        ["TiWorker"] = () => T._("Рабочий процесс установщика модулей Windows"),
    };

    /// <summary>Pseudo processes that never have a file.</summary>
    private static readonly HashSet<string> PseudoNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "Idle",
    };

    public static string? Describe(string name) => Descriptions.TryGetValue(ProcessGuard.BaseName(name), out var d) ? d() : null;

    public static bool IsKnown(string name) => Descriptions.ContainsKey(ProcessGuard.BaseName(name));

    /// <summary>Kernel pseudo processes ("System", "Registry"...) — no file to inspect, nothing to hint about.</summary>
    public static bool IsPseudo(string name) => PseudoNames.Contains(name);
}

/// <summary>Groups processes of the same program (same executable path) into one row.</summary>
internal static class ProcessGrouping
{
    public static string KeyFor(string name, string? path)
        => path is null ? "name:" + name.ToLowerInvariant() : "path:" + path.ToLowerInvariant();

    /// <summary>
    /// Group key for every item. Processes whose path is unknown (no access) join the group of the same file name,
    /// when exactly one such group exists (e.g. inaccessible svchost.exe instances join C:\Windows\System32\svchost.exe).
    /// </summary>
    public static Dictionary<T, string> AssignKeys<T>(IEnumerable<T> items, Func<T, string> name, Func<T, string?> path)
        where T : notnull
    {
        var list = items.ToList();
        var pathKeysByFileName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            var p = path(item);
            var fileName = PathUtil.FileNameOrNull(p);
            if (p is null || fileName is null)
                continue;
            if (!pathKeysByFileName.TryGetValue(fileName, out var keys))
                pathKeysByFileName[fileName] = keys = new HashSet<string>();
            keys.Add(KeyFor(name(item), p));
        }

        var result = new Dictionary<T, string>(list.Count);
        foreach (var item in list)
        {
            var p = path(item);
            if (p is not null)
                result[item] = KeyFor(name(item), p);
            else if (pathKeysByFileName.TryGetValue(name(item), out var keys) && keys.Count == 1)
                result[item] = keys.First();
            else
                result[item] = KeyFor(name(item), null);
        }
        return result;
    }

    /// <summary>Totals for a group: CPU and memory are summed, like Task Manager's "Apps" view.</summary>
    public static (double Cpu, long Memory, int Count) Aggregate(IEnumerable<ProcessRow> rows)
    {
        double cpu = 0;
        long memory = 0;
        var count = 0;
        foreach (var r in rows)
        {
            cpu += r.Cpu;
            memory += r.Memory;
            count++;
        }
        return (Math.Min(cpu, 100), memory, count);
    }

    /// <summary>Worst suspicion of the instances: highest score, union of reasons (command lines differ per instance).</summary>
    public static SuspicionResult? Combine(IEnumerable<SuspicionResult?> results)
    {
        var list = results.Where(r => r is not null).Cast<SuspicionResult>().ToList();
        if (list.Count == 0)
            return null;
        var maxScore = list.Max(r => r.Score);
        if (maxScore == 0)
            return SuspicionResult.Clean;
        var reasons = list.SelectMany(r => r.Reasons).Distinct().ToList();
        return new SuspicionResult(maxScore, reasons, list.Aggregate(SuspicionFlags.None, (all, r) => all | r.Flags));
    }
}

/// <summary>Search box + "Только подозрительные" + "Скрыть системные процессы Microsoft".</summary>
internal sealed class ProcessFilter
{
    private readonly string[] _tokens;

    public ProcessFilter(string? search, bool suspiciousOnly, bool hideMicrosoft)
    {
        _tokens = (search ?? "").ToLowerInvariant()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        SuspiciousOnly = suspiciousOnly;
        HideMicrosoft = hideMicrosoft;
    }

    public bool SuspiciousOnly { get; }

    public bool HideMicrosoft { get; }

    public bool HasSearch => _tokens.Length > 0;

    public bool IsEmpty => !HasSearch && !SuspiciousOnly && !HideMicrosoft;

    public bool MatchesText(string searchText) => _tokens.All(searchText.Contains);

    /// <summary>Flat list / a single item check.</summary>
    public bool Matches(ProcessItem item)
        => (!SuspiciousOnly || item.IsSuspicious) &&
           (!HideMicrosoft || !IsHiddenAsMicrosoft(item)) &&
           (!HasSearch || MatchesText(item.SearchText)); // the search text is only built when there is a query

    /// <summary>
    /// Windows components are hidden only while nothing is wrong with them: PowerShell in C:\Windows running a hidden
    /// encoded script is exactly what the user must still see.
    /// </summary>
    private static bool IsHiddenAsMicrosoft(ProcessItem item) => item.IsMicrosoftSystem && !item.IsSuspicious;

    /// <summary>Should an instance be listed under its (expanded) group?</summary>
    public bool ShowChild(ProcessGroup group, ProcessRow child)
    {
        if (SuspiciousOnly && !child.IsSuspicious)
            return false;
        if (HideMicrosoft && IsHiddenAsMicrosoft(child))
            return false;
        return !HasSearch || MatchesText(child.SearchText) || MatchesText(group.OwnSearchText);
    }
}

internal enum ProcessSortColumn
{
    Name,
    Cpu,
    Memory,
    Publisher,
    Status,
}

internal sealed class ProcessComparer : IComparer<ProcessItem>
{
    private readonly ProcessSortColumn _column;
    private readonly bool _descending;

    public ProcessComparer(ProcessSortColumn column, bool descending)
    {
        _column = column;
        _descending = descending;
    }

    public int Compare(ProcessItem? a, ProcessItem? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;

        var r = _column switch
        {
            // Rounded so that 0,31% vs 0,29% jitter does not reshuffle the list every refresh.
            ProcessSortColumn.Cpu => Math.Round(a.Cpu, 1).CompareTo(Math.Round(b.Cpu, 1)),
            ProcessSortColumn.Memory => a.Memory.CompareTo(b.Memory),
            ProcessSortColumn.Publisher => CompareText(a.PublisherText, b.PublisherText),
            ProcessSortColumn.Status => a.StatusRank.CompareTo(b.StatusRank),
            _ => CompareText(a.PrimaryText, b.PrimaryText),
        };
        if (_descending)
            r = -r;
        if (r != 0)
            return r;

        // Stable tie-breakers: heavier first, then by name, then by PID.
        r = b.Memory.CompareTo(a.Memory);
        if (r != 0 && _column != ProcessSortColumn.Name)
            return r;
        r = CompareText(a.PrimaryText, b.PrimaryText);
        return r != 0 ? r : a.SortPid.CompareTo(b.SortPid);
    }

    private static int CompareText(string? a, string? b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// Applies a new ordered list to an ObservableCollection with as few changes as possible, reusing the same objects,
/// so the grid keeps its selection and scroll position during live refreshes.
/// </summary>
internal static class CollectionSync
{
    public static void SyncTo<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var desiredIndex = new Dictionary<T, int>(desired.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < desired.Count; i++)
            desiredIndex.TryAdd(desired[i], i);

        // 1. Remove what is no longer wanted.
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desiredIndex.ContainsKey(target[i]))
                target.RemoveAt(i);

        // 2. Items forming the longest run already in the right relative order stay where they are.
        var sequence = new int[target.Count];
        for (var i = 0; i < target.Count; i++)
            sequence[i] = desiredIndex[target[i]];
        var keep = LongestIncreasingSubsequence(sequence);

        // 3. Park the others at the end...
        var toMove = new List<T>();
        for (var i = 0; i < target.Count; i++)
            if (!keep[i])
                toMove.Add(target[i]);
        foreach (var item in toMove)
        {
            var j = IndexOf(target, item, 0);
            if (j != target.Count - 1)
                target.Move(j, target.Count - 1);
        }

        // 4. ...and drop each one (or a new item) into its place.
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
                continue;
            var j = IndexOf(target, item, i);
            if (j < 0)
                target.Insert(i, item);
            else
                target.Move(j, i);
        }

        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static int IndexOf<T>(IList<T> list, T item, int start) where T : class
    {
        for (var i = start; i < list.Count; i++)
            if (ReferenceEquals(list[i], item))
                return i;
        return -1;
    }

    /// <summary>Marks the elements of one longest strictly increasing subsequence (patience sorting, O(n log n)).</summary>
    internal static bool[] LongestIncreasingSubsequence(IReadOnlyList<int> sequence)
    {
        var n = sequence.Count;
        var keep = new bool[n];
        if (n == 0)
            return keep;
        var tailIndices = new int[n];
        var previous = new int[n];
        var length = 0;
        for (var i = 0; i < n; i++)
        {
            int lo = 0, hi = length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (sequence[tailIndices[mid]] < sequence[i])
                    lo = mid + 1;
                else
                    hi = mid;
            }
            previous[i] = lo > 0 ? tailIndices[lo - 1] : -1;
            tailIndices[lo] = i;
            if (lo == length)
                length++;
        }
        for (var k = tailIndices[length - 1]; k >= 0; k = previous[k])
            keep[k] = true;
        return keep;
    }
}

/// <summary>Matches snapshot entries to existing row objects so rows are updated in place instead of recreated.</summary>
internal static class SnapshotDiff
{
    public static (List<TRow> Added, List<TRow> Removed) Apply<TRow>(
        Dictionary<ProcessKey, TRow> rows,
        IReadOnlyList<ProcessSample> samples,
        Func<ProcessSample, TRow> create,
        Action<TRow, ProcessSample> update)
    {
        var added = new List<TRow>();
        var seen = new HashSet<ProcessKey>();
        foreach (var sample in samples)
        {
            if (!seen.Add(sample.Key))
                continue;
            if (!rows.TryGetValue(sample.Key, out var row))
            {
                row = create(sample);
                rows[sample.Key] = row;
                added.Add(row);
            }
            update(row, sample);
        }

        var removed = new List<TRow>();
        foreach (var key in rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            removed.Add(rows[key]);
            rows.Remove(key);
        }
        return (added, removed);
    }
}

/// <summary>Parent/child relations for "Завершить дерево процессов".</summary>
internal static class ProcessTree
{
    /// <summary>
    /// Roots followed by all their descendants (breadth-first). A child counts only if it was started after its parent,
    /// because Windows reuses PIDs and a stale parent PID would otherwise drag unrelated processes into the tree.
    /// </summary>
    public static List<T> Collect<T>(IReadOnlyCollection<T> all, IEnumerable<T> roots, Func<T, int> pid, Func<T, int> parentPid,
        Func<T, long> createTime) where T : notnull
    {
        var byParent = all.GroupBy(parentPid).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<T>();
        var seen = new HashSet<T>();
        var queue = new Queue<T>();
        foreach (var root in roots)
            if (seen.Add(root))
            {
                result.Add(root);
                queue.Enqueue(root);
            }

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!byParent.TryGetValue(pid(parent), out var children))
                continue;
            foreach (var child in children)
            {
                if (pid(child) == pid(parent) || createTime(child) < createTime(parent) || !seen.Add(child))
                    continue;
                result.Add(child);
                queue.Enqueue(child);
            }
        }
        return result;
    }
}

/// <summary>How a process relates to the cleaner itself.</summary>
public enum ProcessOwnRole
{
    None,
    /// <summary>WaterCa7s PC Cleaner itself.</summary>
    Self,
    /// <summary>A helper the cleaner started for its checks (powershell.exe, reg.exe, netsh.exe...) or one of its children.</summary>
    Helper,
}

/// <summary>
/// The cleaner runs powershell.exe, reg.exe, netsh.exe and other tools for its checks. Those helpers (and the cleaner
/// itself) must never be reported as suspicious or as resource hogs: they are our own work, not the user's problem.
/// </summary>
internal static class OwnProcesses
{
    public static string HelperLabel => T._("служебный процесс WaterCa7s PC Cleaner");

    /// <summary>
    /// Console tools the cleaner runs in the background for its checks and fixes (ProcessRunner, RegistryBackup,
    /// RestorePoint, Defender scans...), plus the console host Windows attaches to them.
    /// </summary>
    private static readonly HashSet<string> HelperTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell.exe", "pwsh.exe", "cmd.exe", "conhost.exe", "reg.exe", "netsh.exe", "ipconfig.exe", "sc.exe",
        "fsutil.exe", "vssadmin.exe", "powercfg.exe", "dism.exe", "defrag.exe", "MpCmdRun.exe", "tpmtool.exe", "schtasks.exe",
        "wevtutil.exe", "w32tm.exe", "bcdedit.exe", "whoami.exe", "systeminfo.exe", "driverquery.exe", "wmic.exe", "ping.exe",
        "tracert.exe", "pathping.exe", "nslookup.exe", "netstat.exe", "getmac.exe", "arp.exe", "route.exe", "net.exe",
        "net1.exe", "chkdsk.exe", "manage-bde.exe", "winsat.exe", "tasklist.exe", "chcp.com",
    };

    /// <summary>Where those tools live: Windows itself, and Microsoft Defender's folders (MpCmdRun.exe).</summary>
    private static readonly string[] HelperFolders =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender"),
    };

    /// <summary>
    /// A Windows console tool of the kind the cleaner runs for its checks. Everything else it may start is the user's
    /// own business and is judged like any other program: the browser opened by "VirusTotal", Explorer, Notepad, and
    /// above all a program's uninstaller (and whatever that uninstaller starts) from the "Программы" page.
    /// A copy of a tool outside the Windows folders is not a helper either. Unknown path (the helper already exited or
    /// Windows denied access) falls back to the name: the process was started by the cleaner itself.
    /// </summary>
    internal static bool IsHelperTool(string name, string? path)
    {
        var fileName = PathUtil.FileNameOrNull(path) ?? name;
        if (!HelperTools.Contains(fileName))
            return false;
        return path is null || HelperFolders.Any(f => f.Length > 0 && Suspicion.IsUnder(path, f));
    }

    /// <summary>
    /// The cleaner and the console tools it started for its checks, directly or through another such tool
    /// (powershell.exe → conhost.exe). The chain stops at anything that is not a helper tool, so a program launched
    /// from the cleaner (an uninstaller, a browser) and its children stay under scrutiny. A child counts only if it
    /// started after its parent, so a stale parent PID reused by Windows never drags in unrelated programs. If the
    /// cleaner itself is not in the list, its direct helper children are the roots.
    /// </summary>
    public static HashSet<T> Find<T>(IReadOnlyCollection<T> all, int selfPid, Func<T, int> pid, Func<T, int> parentPid,
        Func<T, long> createTime, Func<T, string> name, Func<T, string?> path) where T : notnull
    {
        var own = new HashSet<T>();
        var queue = new Queue<T>();
        foreach (var self in all.Where(p => pid(p) == selfPid))
        {
            if (own.Add(self))
                queue.Enqueue(self);
        }
        if (own.Count == 0)
        {
            foreach (var child in all.Where(p => parentPid(p) == selfPid && pid(p) != selfPid && IsHelperTool(name(p), path(p))))
            {
                if (own.Add(child))
                    queue.Enqueue(child);
            }
        }
        if (own.Count == 0)
            return own;

        var byParent = all.GroupBy(parentPid).ToDictionary(g => g.Key, g => g.ToList());
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!byParent.TryGetValue(pid(parent), out var children))
                continue;
            foreach (var child in children)
            {
                if (pid(child) == pid(parent) || createTime(child) < createTime(parent) || !IsHelperTool(name(child), path(child)))
                    continue;
                if (own.Add(child))
                    queue.Enqueue(child);
            }
        }
        return own;
    }

    public static ProcessOwnRole RoleOf<T>(T item, int selfPid, Func<T, int> pid, HashSet<T> own) where T : notnull
        => pid(item) == selfPid ? ProcessOwnRole.Self : own.Contains(item) ? ProcessOwnRole.Helper : ProcessOwnRole.None;
}

/// <summary>
/// Hidden crypto miners keep the processor fully loaded all the time. Heavy load alone means nothing (games, video
/// editing, archivers, compilers), so it only counts for a file that is already unusual: not validly signed, started from
/// a risky folder, or a known miner / remote-access tool.
/// </summary>
internal static class MinerHeuristic
{
    /// <summary>Share of the whole processor (all cores), like the "ЦП" column.</summary>
    public const double SustainedCpuPercent = 80;

    /// <summary>Consecutive measurements at or above <see cref="SustainedCpuPercent"/> before the load counts as sustained.</summary>
    public const int RequiredSamples = 2;

    public const int Score = 35;

    public static string Reason => T._("Очень высокая загрузка процессора — возможен скрытый майнер");

    public static bool IsCandidate(string? path, SignatureInfo? signature)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false; // no access to the file: nothing to back the suspicion up
        if (signature is { Status: SignatureStatus.Unsigned or SignatureStatus.Untrusted or SignatureStatus.Invalid })
            return true;
        if (KnownPups.MatchRiskyTool(PathUtil.FileNameOrNull(path)) is not null)
            return true;
        // Windows unpacks its own Microsoft-signed tools into Temp (DismHost.exe and friends) and they can be busy for a while.
        return signature?.IsMicrosoft != true && Suspicion.DescribeRiskyLocation(path) is not null;
    }

    /// <summary>Adds the miner reason when the load was sustained and the file is a candidate.</summary>
    public static SuspicionResult Apply(SuspicionResult result, bool sustainedHighCpu, string? path, SignatureInfo? signature)
    {
        if (!sustainedHighCpu || !IsCandidate(path, signature) || result.Flags.HasFlag(SuspicionFlags.SustainedCpu))
            return result;
        return result with
        {
            Score = Math.Clamp(result.Score + Score, 0, 100),
            Reasons = result.Reasons.Append(Reason).ToList(),
            Flags = result.Flags | SuspicionFlags.SustainedCpu,
        };
    }
}

/// <summary>
/// Remembers which processes kept the processor at or above <see cref="MinerHeuristic.SustainedCpuPercent"/> for
/// <see cref="MinerHeuristic.RequiredSamples"/> consecutive measurements. Once reached, the mark stays for the life of
/// the process: a miner that pauses when someone looks (many do) must not lose its warning a refresh later.
/// </summary>
internal sealed class CpuStreaks
{
    private Dictionary<ProcessKey, int> _streaks = new();
    private HashSet<ProcessKey> _sustained = new();

    /// <summary>Breaks every running streak (e.g. after a long pause between measurements). Sustained marks are kept.</summary>
    public void BreakStreaks() => _streaks = new Dictionary<ProcessKey, int>();

    /// <summary>Feeds one measurement of all running processes; processes that are gone are forgotten.</summary>
    public void Update(IEnumerable<(ProcessKey Key, double Cpu)> measurement)
    {
        var streaks = new Dictionary<ProcessKey, int>();
        var alive = new HashSet<ProcessKey>();
        foreach (var (key, cpu) in measurement)
        {
            alive.Add(key);
            if (cpu < MinerHeuristic.SustainedCpuPercent)
                continue;
            var n = (_streaks.TryGetValue(key, out var previous) ? previous : 0) + 1;
            streaks[key] = n;
            if (n >= MinerHeuristic.RequiredSamples)
                _sustained.Add(key);
        }
        _streaks = streaks;
        _sustained.IntersectWith(alive);
    }

    public bool IsSustained(ProcessKey key) => _sustained.Contains(key);
}

/// <summary>When a file may be moved to quarantine from the Processes page.</summary>
internal static class QuarantineRules
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Null if allowed, otherwise a short explanation for the tooltip.</summary>
    public static string? BlockReason(string? path, SignatureInfo? signature, SuspicionResult? suspicion, KillVerdict verdict,
        bool trusted, string? selfPath = null, bool isOwnHelper = false)
    {
        if (path is null)
            return T._("Нет доступа к файлу процесса.");
        if (Suspicion.IsUnder(path, WindowsDir))
            return T._("Файлы из папки Windows нельзя помещать в карантин.");
        if (verdict == KillVerdict.Blocked)
            return T._("Это защищённый процесс — его нельзя трогать.");
        if (string.Equals(path, selfPath ?? Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return ProcessGuard.SelfText;
        if (isOwnHelper)
            return T._("Это служебный процесс WaterCa7s PC Cleaner — он завершится сам, когда проверка закончится.");
        if (signature is null)
            return T._("Подождите: ещё проверяется цифровая подпись.");
        if (signature.IsMicrosoft)
            return T._("Файл подписан Microsoft — это часть Windows или программа Microsoft.");
        if (trusted)
            return T._("Вы отметили эту программу как доверенную.");
        if (suspicion is null || suspicion.Level < SuspicionLevel.Medium)
            return T._("Карантин доступен только для подозрительных файлов (оценка «Подозрительно» или «Высокий риск»).");
        return null;
    }
}
