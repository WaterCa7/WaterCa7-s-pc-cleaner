using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

/// <summary>
/// PID → process name, executable path and friendly name. Cached for a short time (PIDs are reused by Windows),
/// works without admin rights (PROCESS_QUERY_LIMITED_INFORMATION), handles processes that already exited.
/// </summary>
public sealed class ProcessIdentities
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<int, (ProcessIdentity Identity, DateTime Time)> _cache = new();
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Windows' own service hosts. Without admin rights the path of some of them cannot be read, so the name is used
    /// (only for classifying their ports as "Windows"; the Processes page checks signatures and locations).
    /// </summary>
    private static readonly Dictionary<string, string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost"] = "Хост-процесс служб Windows",
        ["services"] = "Диспетчер служб Windows",
        ["lsass"] = "Служба проверки подлинности Windows",
        ["wininit"] = "Запуск Windows",
        ["spoolsv"] = "Диспетчер печати",
        ["dashost"] = "Узел связи с устройствами",
        ["csrss"] = "Среда выполнения Windows",
        ["winlogon"] = "Вход в Windows",
        ["lsaiso"] = "Изолированная проверка подлинности",
        ["searchindexer"] = "Индексатор поиска Windows",
        ["msmpeng"] = "Защитник Windows",
    };

    public ProcessIdentity Get(int pid)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(pid, out var cached) && now - cached.Time < Lifetime && !cached.Identity.Exited)
            return cached.Identity;
        var identity = Resolve(pid);
        _cache[pid] = (identity, now);
        return identity;
    }

    public Dictionary<int, ProcessIdentity> GetAll(IEnumerable<int> pids)
    {
        var map = new Dictionary<int, ProcessIdentity>();
        foreach (var pid in pids.Distinct())
            map[pid] = Get(pid);
        // Forget processes that are long gone so the cache never grows without bound.
        if (_cache.Count > 2000)
        {
            foreach (var key in _cache.Where(kv => DateTime.UtcNow - kv.Value.Time > Lifetime).Select(kv => kv.Key).ToList())
                _cache.TryRemove(key, out _);
        }
        return map;
    }

    private static ProcessIdentity Resolve(int pid)
    {
        switch (pid)
        {
            case 0:
                return new ProcessIdentity(0, "Система", null, "Система (без владельца)", true);
            case 4:
                return new ProcessIdentity(4, "System", null, "Ядро Windows (System)", true);
        }

        string? name = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (ArgumentException)
        {
            // exited
        }
        catch (InvalidOperationException)
        {
            // exited while reading
        }

        var path = QueryPath(pid);
        if (name is null && path is null)
            return new ProcessIdentity(pid, $"PID {pid}", null, "Процесс уже завершился", false, Exited: true);

        var exe = path is not null ? Path.GetFileName(path) : name + ".exe";
        var stem = Path.GetFileNameWithoutExtension(exe);
        var known = SystemProcesses.TryGetValue(stem, out var friendly);
        var display = Describe(path) ?? (known ? friendly! : stem);
        var isWindows = path is not null ? Suspicion.IsUnder(path, WindowsDir) : known;
        return new ProcessIdentity(pid, exe, path, display, isWindows);
    }

    private static string? Describe(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? QueryPath(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            return null;
        var size = 1024;
        var sb = new StringBuilder(size);
        if (!QueryFullProcessImageName(handle, 0, sb, ref size))
            return null;
        var path = sb.ToString(0, size);
        return Path.IsPathRooted(path) ? path : null;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder exeName, ref int size);
}
