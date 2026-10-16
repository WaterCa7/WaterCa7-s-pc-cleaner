using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PcCleaner.Modules.Programs;

/// <summary>
/// Waits for an uninstaller and the processes it spawns (many uninstallers copy themselves to %TEMP% and exit at once).
/// Only observes processes, never kills them.
/// </summary>
internal static class ProcessTree
{
    /// <summary>Long-lived apps an uninstaller may open (a feedback page in the browser): never wait for them.</summary>
    private static readonly HashSet<string> IgnoredChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "browser.exe", "brave.exe", "iexplore.exe", "vivaldi.exe",
        "explorer.exe", "conhost.exe", "werfault.exe", "notepad.exe", "steam.exe", "steamwebhelper.exe",
    };

    public sealed record Entry(int Pid, int ParentPid, string ExeName);

    /// <summary>
    /// Waits until the root process and every descendant started after <paramref name="launchTime"/> have exited.
    /// Cancellation stops waiting (the processes keep running).
    /// </summary>
    public static async Task WaitForTreeAsync(Process root, DateTime launchTime, Action<string>? onWaitingFor, CancellationToken ct)
    {
        var tracked = new HashSet<int> { root.Id };
        var everSeen = new HashSet<int> { root.Id };
        string? lastReported = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Snapshot();
            var alive = snapshot.ToDictionary(e => e.Pid);

            // New descendants: parent is (or was) one of ours and the process started after the launch.
            foreach (var e in snapshot)
            {
                if (everSeen.Contains(e.Pid) || !everSeen.Contains(e.ParentPid) || IgnoredChildren.Contains(e.ExeName))
                    continue;
                if (!StartedAfter(e.Pid, launchTime))
                    continue;
                everSeen.Add(e.Pid);
                tracked.Add(e.Pid);
            }

            tracked.RemoveWhere(pid => pid == root.Id ? HasExited(root) : !alive.ContainsKey(pid));
            if (tracked.Count == 0)
                return;

            var name = tracked.Select(pid => alive.TryGetValue(pid, out var e) ? e.ExeName : null).FirstOrDefault(n => n is not null);
            if (name is not null && name != lastReported)
            {
                lastReported = name;
                onWaitingFor?.Invoke(name);
            }
            await Task.Delay(700, ct);
        }
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private static bool StartedAfter(int pid, DateTime launchTime)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime >= launchTime.AddSeconds(-2);
        }
        catch (ArgumentException)
        {
            return false; // already gone
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return true; // cannot read (elevated): assume it is ours, the user can stop waiting with "Отмена"
        }
    }

    public static List<Entry> Snapshot()
    {
        var list = new List<Entry>();
        var handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return list;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(handle, ref entry))
                return list;
            do
            {
                list.Add(new Entry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, Path.GetFileName(entry.szExeFile ?? "")));
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            }
            while (Process32NextW(handle, ref entry));
        }
        finally
        {
            CloseHandle(handle);
        }
        return list;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
