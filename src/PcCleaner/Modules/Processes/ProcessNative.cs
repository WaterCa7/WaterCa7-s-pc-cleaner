using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

/// <summary>
/// Reads the process list with one NtQuerySystemInformation(SystemProcessInformation) call — the same source Task Manager
/// uses. Unlike Process.TotalProcessorTime it needs no process handles, so CPU time, memory, start time and the parent
/// PID are available for every process even without administrator rights. Falls back to System.Diagnostics.Process.
/// </summary>
internal sealed class NativeProcessSource : IProcessSource
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly HashSet<string> PseudoProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System",
    };

    private readonly object _lock = new();
    private byte[] _buffer = new byte[1 << 20];
    private bool _nativeFailed;

    public IReadOnlyList<RawProcess> Enumerate()
    {
        if (!_nativeFailed)
        {
            try
            {
                var list = EnumerateNative();
                if (list is not null)
                    return list;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn(T.F("Процессы: NtQuerySystemInformation недоступен ({0}), используется запасной способ", ex.Message));
            }
            _nativeFailed = true;
        }
        return EnumerateManaged();
    }

    public string? QueryImagePath(int pid, long createTime)
    {
        if (pid is 0 or 4)
            return null;
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            return null;

        // Make sure the PID still belongs to the process from the snapshot.
        if (createTime != 0 && GetProcessTimes(handle, out var created, out _, out _, out _) && created != createTime)
            return null;

        foreach (var capacity in new[] { 1024, 32768 })
        {
            var sb = new StringBuilder(capacity);
            var size = sb.Capacity;
            if (QueryFullProcessImageName(handle, 0, sb, ref size))
            {
                var path = sb.ToString(0, size);
                return Path.IsPathRooted(path) ? path : null;
            }
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                break;
        }
        return null;
    }

    private unsafe List<RawProcess>? EnumerateNative()
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                int status, needed;
                fixed (byte* p = _buffer)
                {
                    status = NtQuerySystemInformation(SystemProcessInformation, (IntPtr)p, _buffer.Length, out needed);
                    if (status == 0)
                        return Parse((IntPtr)p, _buffer.Length);
                }
                if (status != StatusInfoLengthMismatch)
                    return null;
                // The list grows between calls; leave some headroom.
                _buffer = new byte[Math.Max(needed, _buffer.Length) + (256 << 10)];
            }
            return null;
        }
    }

    private static List<RawProcess> Parse(IntPtr buffer, int length)
    {
        var list = new List<RawProcess>(512);
        var structSize = Marshal.SizeOf<SYSTEM_PROCESS_INFORMATION>();
        long offset = 0;
        while (offset + structSize <= length)
        {
            var spi = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(buffer + (int)offset);
            var pid = (int)spi.UniqueProcessId.ToInt64();
            string name;
            if (spi.ImageName.Buffer != IntPtr.Zero && spi.ImageName.Length > 0)
                name = Marshal.PtrToStringUni(spi.ImageName.Buffer, spi.ImageName.Length / 2);
            else
                name = pid == 0 ? "Idle" : "System";

            list.Add(new RawProcess(
                pid,
                (int)spi.InheritedFromUniqueProcessId.ToInt64(),
                name,
                spi.CreateTime,
                spi.KernelTime + spi.UserTime,
                spi.WorkingSetPrivateSize,
                (long)spi.WorkingSetSize.ToUInt64(),
                (int)spi.SessionId));

            if (spi.NextEntryOffset == 0)
                break;
            offset += spi.NextEntryOffset;
        }
        return list;
    }

    /// <summary>Fallback: slower and blind to protected processes, but always available.</summary>
    private static List<RawProcess> EnumerateManaged()
    {
        var list = new List<RawProcess>(512);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warn(T.F("Процессы: не удалось получить список процессов: {0}", ex.Message));
            return list;
        }
        foreach (var p in processes)
        {
            using (p)
            {
                try
                {
                    long created = 0, cpu = 0;
                    try
                    {
                        created = p.StartTime.ToFileTime();
                    }
                    catch (Exception)
                    {
                        // access denied or exited
                    }
                    try
                    {
                        cpu = p.TotalProcessorTime.Ticks;
                    }
                    catch (Exception)
                    {
                        // access denied or exited
                    }
                    var name = PseudoProcesses.Contains(p.ProcessName) ? p.ProcessName : p.ProcessName + ".exe";
                    list.Add(new RawProcess(p.Id, 0, name, created, cpu, 0, p.WorkingSet64, p.SessionId));
                }
                catch (InvalidOperationException)
                {
                    // exited while enumerating
                }
            }
        }
        return list;
    }

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);
}

/// <summary>
/// Read-only check whether the current user may move a file (NTFS grants DELETE access at open time if the file or its
/// folder allows it). Used before quarantine so that a process is never terminated when its file then can't be moved
/// (e.g. Program Files without administrator rights). Opens a handle and closes it — nothing is changed.
/// </summary>
internal static class FileMoveProbe
{
    private const uint Delete = 0x00010000;
    private const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const int ErrorAccessDenied = 5;

    /// <summary>False only when Windows explicitly denies the right to move/delete the file.</summary>
    public static bool CanMove(string path)
    {
        try
        {
            using var handle = CreateFile(path, Delete, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
                return true;
            // Sharing violations, missing files etc. are reported by the move itself.
            return Marshal.GetLastWin32Error() != ErrorAccessDenied;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}

/// <summary>Command lines, parent PIDs and hosted services via WMI. Never throws (Wmi.Query logs a warning instead).</summary>
internal sealed class WmiDetailsProvider : IProcessDetailsProvider
{
    /// <summary>Above this many PIDs a single full query is cheaper than a long OR filter.</summary>
    internal const int MaxFilteredPids = 40;

    /// <summary>"ProcessId = 1 OR ProcessId = 2", or null for no filter (no PIDs given, or too many of them).</summary>
    internal static string? PidFilter(IReadOnlyCollection<int>? pids)
        => pids is { Count: > 0 and <= MaxFilteredPids }
            ? string.Join(" OR ", pids.Select(p => "ProcessId = " + p.ToString(CultureInfo.InvariantCulture)))
            : null;

    public IReadOnlyList<WmiProcessRow> QueryProcesses(IReadOnlyCollection<int>? pids)
    {
        var query = "SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath, CreationDate FROM Win32_Process";
        if (PidFilter(pids) is { } filter)
            query += " WHERE " + filter;
        var rows = Wmi.Query(query, timeout: TimeSpan.FromSeconds(30));
        var result = new List<WmiProcessRow>(rows.Count);
        foreach (var row in rows)
        {
            var pid = row.Long("ProcessId");
            if (pid is null)
                continue;
            result.Add(new WmiProcessRow((int)pid.Value, (int)(row.Long("ParentProcessId") ?? 0), row.Str("CommandLine"),
                row.Str("ExecutablePath"), row.Date("CreationDate")));
        }
        return result;
    }

    /// <summary>
    /// Only the services of the given processes: the live list asks about the few processes that have just appeared,
    /// so it need not marshal every service of the system on each of those refreshes.
    /// </summary>
    public IReadOnlyList<WmiServiceRow> QueryServices(IReadOnlyCollection<int>? pids)
    {
        var filter = PidFilter(pids) is { } byPid ? "(" + byPid + ")" : "ProcessId <> 0";
        var rows = Wmi.Query("SELECT Name, DisplayName, PathName, ProcessId FROM Win32_Service WHERE " + filter,
            timeout: TimeSpan.FromSeconds(30));
        var result = new List<WmiServiceRow>(rows.Count);
        foreach (var row in rows)
        {
            var pid = row.Long("ProcessId");
            var name = row.Str("Name");
            if (pid is null or 0 || string.IsNullOrEmpty(name))
                continue;
            result.Add(new WmiServiceRow((int)pid.Value, name, row.Str("DisplayName"), row.Str("PathName")));
        }
        return result;
    }
}
