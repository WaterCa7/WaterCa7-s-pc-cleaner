using System.IO;

namespace PcCleaner.Modules.Processes;

/// <summary>Identity of a running process. PIDs are reused by Windows, so the creation time is part of the key.</summary>
internal readonly record struct ProcessKey(int Pid, long CreateTime);

/// <summary>One process as reported by the OS in a single snapshot (times in 100 ns units, FILETIME for CreateTime).</summary>
internal readonly record struct RawProcess(
    int Pid,
    int ParentPid,
    string Name,
    long CreateTime,
    long CpuTime,
    long PrivateWorkingSet,
    long WorkingSet,
    int SessionId)
{
    public ProcessKey Key => new(Pid, CreateTime);

    /// <summary>"Память" column: private working set (like Task Manager), or the full working set if unknown.</summary>
    public long MemoryBytes => PrivateWorkingSet > 0 ? PrivateWorkingSet : WorkingSet;
}

/// <summary>Version resource of an executable (unverified, the signature is what can be trusted).</summary>
internal sealed record FileMeta(string? Description, string? Company, string? Product);

/// <summary>Row of Win32_Process used for the command line / parent / path fallback.</summary>
internal sealed record WmiProcessRow(int Pid, int ParentPid, string? CommandLine, string? ExecutablePath, DateTime? CreationDate);

/// <summary>Row of Win32_Service: which services live inside which process.</summary>
internal sealed record WmiServiceRow(int Pid, string Name, string? DisplayName, string? PathName);

/// <summary>
/// Facts about a process that do not change while it runs. Cached per <see cref="ProcessKey"/> so refreshes stay cheap.
/// Filled in steps: path + version info right away, command line and services later (WMI, in the background).
/// </summary>
internal sealed class ProcessStaticInfo
{
    private volatile string? _path;
    private volatile string? _commandLine;
    private volatile FileMeta? _meta;
    private volatile IReadOnlyList<string> _services = Array.Empty<string>();
    private volatile bool _detailsLoaded;

    public ProcessStaticInfo(ProcessKey key, string name)
    {
        Key = key;
        Name = name;
    }

    public ProcessKey Key { get; }

    /// <summary>Image file name reported by the OS, e.g. "chrome.exe" or "System".</summary>
    public string Name { get; }

    /// <summary>Full path of the executable; null when Windows denies access (other users / protected processes).</summary>
    public string? Path
    {
        get => _path;
        set => _path = value;
    }

    /// <summary>True when the path was taken from the service configuration rather than from the process itself.</summary>
    public bool PathFromService { get; set; }

    public FileMeta? Meta
    {
        get => _meta;
        set => _meta = value;
    }

    public string? CommandLine
    {
        get => _commandLine;
        set => _commandLine = value;
    }

    /// <summary>Display names of the Windows services hosted by this process (svchost and friends).</summary>
    public IReadOnlyList<string> Services
    {
        get => _services;
        set => _services = value;
    }

    /// <summary>Parent PID from WMI (used only when the process list itself did not provide one).</summary>
    public int ParentPid { get; set; }

    /// <summary>The WMI pass (command line, services) has been done for this process.</summary>
    public bool DetailsLoaded
    {
        get => _detailsLoaded;
        set => _detailsLoaded = value;
    }

    public string FileName => _path is { } p ? System.IO.Path.GetFileName(p) : Name;
}

/// <summary>One process in a snapshot: raw counters + cached static info + CPU usage since the previous snapshot.</summary>
internal sealed record ProcessSample(RawProcess Raw, ProcessStaticInfo Info, double CpuPercent)
{
    public ProcessKey Key => Raw.Key;

    public int Pid => Raw.Pid;

    public long MemoryBytes => Raw.MemoryBytes;

    public DateTime? StartTime => ProcessMath.FromFileTime(Raw.CreateTime);
}

/// <summary>Source of process lists (the real OS or a fake one in tests).</summary>
internal interface IProcessSource
{
    /// <summary>All running processes. Must not throw.</summary>
    IReadOnlyList<RawProcess> Enumerate();

    /// <summary>Full image path, or null if access is denied or the PID now belongs to another process.</summary>
    string? QueryImagePath(int pid, long createTime);
}

/// <summary>Slow details (command lines, services) — WMI in the app, a fake in tests.</summary>
internal interface IProcessDetailsProvider
{
    /// <summary>Win32_Process rows for the given PIDs (null = all processes).</summary>
    IReadOnlyList<WmiProcessRow> QueryProcesses(IReadOnlyCollection<int>? pids);

    /// <summary>Win32_Service rows of the services hosted by the given PIDs (null = all running services).</summary>
    IReadOnlyList<WmiServiceRow> QueryServices(IReadOnlyCollection<int>? pids);
}

/// <summary>A process to terminate, identified precisely so a reused PID is never killed by mistake.</summary>
internal sealed record KillTarget(int Pid, long CreateTime, string Name, string? Path);

/// <summary>Outcome of a kill request.</summary>
internal sealed class KillReport
{
    public List<KillTarget> Killed { get; } = new();

    /// <summary>Processes that had already exited before we got to them (counted as done).</summary>
    public List<KillTarget> AlreadyExited { get; } = new();

    public List<(KillTarget Target, string Error)> Failed { get; } = new();

    public bool AccessDenied { get; set; }

    public int Done => Killed.Count + AlreadyExited.Count;
}

internal static class PathUtil
{
    public static string? FileNameOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
