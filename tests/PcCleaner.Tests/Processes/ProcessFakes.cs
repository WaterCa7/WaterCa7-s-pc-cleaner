using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;

namespace PcCleaner.Tests.Processes;

/// <summary>In-memory process list for tests: nothing touches the real system.</summary>
internal sealed class FakeProcessSource : IProcessSource
{
    private readonly object _lock = new();
    private readonly List<RawProcess> _processes = new();

    public Dictionary<int, string?> Paths { get; } = new();

    /// <summary>CPU time (100 ns units) added to a process on every Enumerate — a program that keeps working.</summary>
    public Dictionary<int, long> CpuPerEnumerate { get; } = new();

    public IReadOnlyList<RawProcess> Enumerate()
    {
        lock (_lock)
        {
            foreach (var (pid, cpu) in CpuPerEnumerate)
            {
                var i = _processes.FindIndex(p => p.Pid == pid);
                if (i >= 0)
                    _processes[i] = _processes[i] with { CpuTime = _processes[i].CpuTime + cpu };
            }
            return _processes.ToList();
        }
    }

    public string? QueryImagePath(int pid, long createTime) => Paths.TryGetValue(pid, out var p) ? p : null;

    public RawProcess Add(int pid, string name, string? path, long memory = 10_000_000, int parent = 0, long cpu = 0,
        long createTime = 0)
    {
        var raw = new RawProcess(pid, parent, name, createTime == 0 ? 133_000_000_000_000_000L + pid : createTime, cpu, memory,
            memory, 1);
        lock (_lock)
            _processes.Add(raw);
        Paths[pid] = path;
        return raw;
    }

    public void Remove(int pid)
    {
        lock (_lock)
            _processes.RemoveAll(p => p.Pid == pid);
    }

    /// <summary>Simulates CPU work: adds CPU time (100 ns units) to a process.</summary>
    public void AddCpu(int pid, long cpu100ns)
    {
        lock (_lock)
        {
            var i = _processes.FindIndex(p => p.Pid == pid);
            _processes[i] = _processes[i] with { CpuTime = _processes[i].CpuTime + cpu100ns };
        }
    }

    public void SetMemory(int pid, long memory)
    {
        lock (_lock)
        {
            var i = _processes.FindIndex(p => p.Pid == pid);
            _processes[i] = _processes[i] with { PrivateWorkingSet = memory, WorkingSet = memory };
        }
    }
}

internal sealed class FakeDetails : IProcessDetailsProvider
{
    public List<WmiProcessRow> Processes { get; } = new();

    public List<WmiServiceRow> Services { get; } = new();

    public int ProcessQueries { get; private set; }

    public IReadOnlyList<WmiProcessRow> QueryProcesses(IReadOnlyCollection<int>? pids)
    {
        ProcessQueries++;
        return Processes.Where(p => pids is null || pids.Contains(p.Pid)).ToList();
    }

    /// <summary>PID filter of every service query (null = all services were asked for).</summary>
    public List<IReadOnlyCollection<int>?> ServiceQueries { get; } = new();

    public IReadOnlyList<WmiServiceRow> QueryServices(IReadOnlyCollection<int>? pids)
    {
        ServiceQueries.Add(pids?.ToList());
        return Services.Where(s => pids is null || pids.Contains(s.Pid)).ToList();
    }
}

internal sealed class FakeClock
{
    public long Now { get; set; } = 1_000_000_000;

    public void Advance(TimeSpan span) => Now += (long)(span.TotalSeconds * Stopwatch.Frequency);
}

internal static class ProcessTestData
{
    public static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static readonly string SvchostPath = Path.Combine(WindowsDir, "System32", "svchost.exe");

    public static readonly string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    public static readonly string EvilPath = Path.Combine(Path.GetTempPath(), "evilupdater.exe");

    public static SignatureInfo SignatureFor(string path)
    {
        if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase))
            return new SignatureInfo(SignatureStatus.Valid, "Microsoft Windows", IsCatalogSigned: true);
        if (path.Contains("evil", StringComparison.OrdinalIgnoreCase) || path.Contains("unsigned", StringComparison.OrdinalIgnoreCase))
            return new SignatureInfo(SignatureStatus.Unsigned, null);
        if (path.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            return new SignatureInfo(SignatureStatus.Valid, "Google LLC");
        return new SignatureInfo(SignatureStatus.Valid, "Test Vendor");
    }

    public static FileMeta? MetaFor(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name switch
        {
            "chrome.exe" => new FileMeta("Google Chrome", "Google LLC", "Google Chrome"),
            "svchost.exe" => new FileMeta("Хост-процесс для служб Windows", "Microsoft Corporation", "Windows"),
            _ => new FileMeta(null, null, null),
        };
    }

    public static (ProcessesService Service, FakeProcessSource Source, FakeDetails Details, FakeClock Clock) CreateService(
        int? selfPid = null, Func<MemoryStatus>? memory = null, TimeSpan? analysisInterval = null, Func<long>? clock = null)
    {
        var source = new FakeProcessSource();
        var details = new FakeDetails();
        var fakeClock = new FakeClock();
        var service = new ProcessesService(source, details, SignatureFor, MetaFor, clock ?? (() => fakeClock.Now), logicalCpus: 4,
            selfPid: selfPid, memory: memory, analysisInterval: analysisInterval);
        return (service, source, details, fakeClock);
    }

    /// <summary>A typical machine: Chrome with 3 processes, 2 svchost (one inaccessible), one unsigned exe in Temp.</summary>
    public static void AddTypicalProcesses(FakeProcessSource source)
    {
        source.Add(4, "System", null, memory: 100_000);
        source.Add(100, "chrome.exe", ChromePath, memory: 300_000_000);
        source.Add(101, "chrome.exe", ChromePath, memory: 200_000_000, parent: 100);
        source.Add(102, "chrome.exe", ChromePath, memory: 100_000_000, parent: 100);
        source.Add(200, "svchost.exe", SvchostPath, memory: 20_000_000);
        source.Add(201, "svchost.exe", null, memory: 30_000_000);
        source.Add(300, "evilupdater.exe", EvilPath, memory: 50_000_000);
    }

    public static ProcessRow Row(int pid, string name, string? path, SignatureInfo? signature = null, SuspicionResult? suspicion = null,
        string? description = null, string? company = null)
    {
        var row = new ProcessRow(new ProcessStaticInfo(new ProcessKey(pid, 1000 + pid), name) { Path = path })
        {
            Path = path,
            Description = description,
            Company = company,
            Signature = signature,
            Suspicion = suspicion ?? SuspicionResult.Clean,
            AccessDenied = path is null && !KnownWindowsProcesses.IsPseudo(name),
        };
        return row;
    }
}
