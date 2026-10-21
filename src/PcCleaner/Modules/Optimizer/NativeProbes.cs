using System.Runtime.InteropServices;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>Raw display info from EnumDisplayDevices / EnumDisplaySettings (read-only).</summary>
internal sealed record RawDisplay(
    string DeviceName,
    string AdapterName,
    string? MonitorDeviceId,
    string? MonitorDescription,
    int Width,
    int Height,
    int CurrentHz,
    int MaxHzAtCurrentResolution,
    bool IsPrimary);

/// <summary>Read-only Win32 / D3DKMT queries. Nothing here changes the system.</summary>
internal static class NativeProbes
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    private const int DM_INTERLACED = 0x2;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

    /// <summary>Displays attached to the desktop with their current and maximum refresh rate at the current resolution.</summary>
    public static List<RawDisplay> GetDisplays()
    {
        var list = new List<RawDisplay>();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var adapter = NewDisplayDevice();
                if (!EnumDisplayDevices(null, i, ref adapter, 0))
                    break;
                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 ||
                    (adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                    continue;

                var current = NewDevMode();
                if (!EnumDisplaySettingsEx(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref current, 0))
                    continue;
                var width = current.dmPelsWidth;
                var height = current.dmPelsHeight;
                var hz = current.dmDisplayFrequency;
                var max = hz;
                for (var mode = 0; mode < 5000; mode++)
                {
                    var dm = NewDevMode();
                    if (!EnumDisplaySettingsEx(adapter.DeviceName, mode, ref dm, 0))
                        break;
                    // Interlaced TV modes (1080i) are not a real refresh-rate upgrade.
                    if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && dm.dmDisplayFrequency > max &&
                        (dm.dmDisplayFlags & DM_INTERLACED) == 0)
                        max = dm.dmDisplayFrequency;
                }

                var monitor = NewDisplayDevice();
                string? monitorId = null;
                string? monitorDesc = null;
                if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    monitorId = monitor.DeviceID;
                    monitorDesc = monitor.DeviceString;
                }

                list.Add(new RawDisplay(adapter.DeviceName, adapter.DeviceString, monitorId, monitorDesc, width, height, hz, max,
                    (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            Log.Warn(T.F("Ускорение: не удалось получить список мониторов: {0}", ErrorText.For(ex)), ex);
        }
        return list;
    }

    /// <summary>
    /// Asks the graphics drivers whether hardware-accelerated GPU scheduling is supported and active.
    /// Returns null when the query is not available (old Windows / drivers).
    /// </summary>
    public static (bool Supported, bool Enabled)? QueryHagsCaps()
    {
        try
        {
            var enumArgs = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0)
                return null;
            var count = (int)Math.Min(enumArgs.NumAdapters, 64u);
            var size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            var buffer = Marshal.AllocHGlobal(size * count);
            try
            {
                enumArgs.NumAdapters = (uint)count;
                enumArgs.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref enumArgs) != 0)
                    return null;

                bool any = false, supported = false, enabled = false;
                for (var i = 0; i < enumArgs.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * size);
                    var data = Marshal.AllocHGlobal(sizeof(uint));
                    try
                    {
                        Marshal.WriteInt32(data, 0);
                        var query = new D3DKMT_QUERYADAPTERINFO
                        {
                            hAdapter = info.hAdapter,
                            Type = KMTQAITYPE_WDDM_2_7_CAPS,
                            pPrivateDriverData = data,
                            PrivateDriverDataSize = sizeof(uint),
                        };
                        if (D3DKMTQueryAdapterInfo(ref query) == 0)
                        {
                            any = true;
                            var caps = (uint)Marshal.ReadInt32(data);
                            if ((caps & 0x1) != 0)
                            {
                                supported = true;
                                if ((caps & 0x2) != 0)
                                    enabled = true;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(data);
                        var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                }
                return any ? (supported, enabled) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            Log.Info(T.F("Ускорение: запрос возможностей видеодрайвера недоступен ({0})", ex.GetType().Name));
            return null;
        }
    }

    /// <summary>true = on AC power, false = on battery, null = unknown.</summary>
    public static bool? IsOnAcPower() => GetPowerStatus().OnAc;

    /// <summary>AC line, battery charge and charging flag from GetSystemPowerStatus (cheap, no WMI).</summary>
    public static PowerStatus GetPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return new PowerStatus(null, null, BatteryChargeState.Unknown);
            bool? onAc = status.ACLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
            return new PowerStatus(onAc, ChargePercent(status.BatteryFlag, status.BatteryLifePercent),
                ChargeState(onAc, status.BatteryFlag, status.BatteryLifePercent));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new PowerStatus(null, null, BatteryChargeState.Unknown);
        }
    }

    private const byte BatteryFlagCharging = 8;
    private const byte BatteryFlagNoBattery = 128;
    private const byte BatteryFlagUnknown = 255;

    /// <summary>BatteryLifePercent is 255 when unknown; no battery -> null.</summary>
    internal static int? ChargePercent(byte batteryFlag, byte lifePercent)
        => batteryFlag is BatteryFlagUnknown || (batteryFlag & BatteryFlagNoBattery) != 0 || lifePercent > 100 ? null : lifePercent;

    internal static BatteryChargeState ChargeState(bool? onAc, byte batteryFlag, byte lifePercent)
    {
        if (batteryFlag == BatteryFlagUnknown || (batteryFlag & BatteryFlagNoBattery) != 0)
            return BatteryChargeState.Unknown;
        if ((batteryFlag & BatteryFlagCharging) != 0)
            return BatteryChargeState.Charging;
        return onAc switch
        {
            false => BatteryChargeState.Discharging,
            true when lifePercent is >= 99 and <= 100 => BatteryChargeState.Full,
            true => BatteryChargeState.PluggedIn,
            _ => BatteryChargeState.Unknown,
        };
    }

    /// <summary>GetFirmwareType: 1 = legacy BIOS, 2 = UEFI; null when the call is not available or fails.</summary>
    public static int? GetFirmwareTypeValue()
    {
        try
        {
            return GetFirmwareType(out var type) ? type : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>How often the processes started by this app are re-read during a CPU sample.</summary>
    private static readonly TimeSpan OwnTreePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// CPU load over <paramref name="duration"/>, without the CPU time this app and the processes it started
    /// (PowerShell, powercfg, fsutil… and their console hosts) used meanwhile — so our own scans, e.g. the other modules
    /// of the dashboard smart scan, do not look like a busy PC. Kept separate from SystemStats on purpose:
    /// SystemStats.CpuUsagePercent measures "since the previous call" for live widgets, and a 2-second sample from here
    /// would shorten their interval. Null if the counters are not available.
    /// </summary>
    public static async Task<double?> SampleCpuLoadAsync(TimeSpan duration, CancellationToken ct)
    {
        try
        {
            if (!GetSystemTimes(out var idle1, out var kernel1, out var user1))
                return null;
            var own = new OwnCpuTracker();
            own.Poll();
            var started = System.Diagnostics.Stopwatch.StartNew();
            // Polled during the sample: a helper process that exits before the end still has its CPU time counted as ours.
            while (started.Elapsed < duration)
            {
                var left = duration - started.Elapsed;
                await Task.Delay(left < OwnTreePollInterval ? left : OwnTreePollInterval, ct).ConfigureAwait(false);
                own.Poll();
            }
            if (!GetSystemTimes(out var idle2, out var kernel2, out var user2))
                return null;
            return HardwareText.CpuLoadPercent(idle2 - idle1, kernel2 - kernel1, user2 - user1, own.Used);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// CPU time (100 ns units) of this process and every process it started, keyed by (PID, creation time) so a reused
    /// PID is never mixed up. A child's creation time must not be earlier than its parent's: Windows keeps a stale parent
    /// PID after the parent exits, and an unrelated process could otherwise look like ours.
    /// </summary>
    internal static Dictionary<(uint Pid, long Created), long> ProcessTreeCpu(uint rootPid,
        IReadOnlyList<(uint Pid, uint Parent)> processes, Func<uint, (long Created, long Cpu)?> times)
    {
        var result = new Dictionary<(uint Pid, long Created), long>();
        if (times(rootPid) is not { } root)
            return result;
        result[(rootPid, root.Created)] = root.Cpu;
        var seen = new HashSet<uint> { rootPid };
        var queue = new Queue<(uint Pid, long Created)>();
        queue.Enqueue((rootPid, root.Created));
        while (queue.Count > 0)
        {
            var (parentPid, parentCreated) = queue.Dequeue();
            foreach (var (pid, parent) in processes)
            {
                if (parent != parentPid || pid == 0 || seen.Contains(pid))
                    continue;
                if (times(pid) is not { } child || child.Created < parentCreated)
                    continue;
                seen.Add(pid);
                result[(pid, child.Created)] = child.Cpu;
                queue.Enqueue((pid, child.Created));
            }
        }
        return result;
    }

    /// <summary>
    /// CPU time our process tree used between the first and the last <see cref="Poll"/>. Processes that appeared later
    /// count from their creation; processes that already ran before the first poll but were missed then are ignored
    /// (never subtract more than we know is ours).
    /// </summary>
    internal sealed class OwnCpuTracker
    {
        private readonly Func<Dictionary<(uint Pid, long Created), long>> _read;
        private Dictionary<(uint Pid, long Created), long>? _start;
        private long _startTime;
        private readonly Dictionary<(uint Pid, long Created), long> _latest = new();

        public OwnCpuTracker()
            : this(ReadOwnTree) { }

        /// <param name="read">Current CPU time per process of our tree (tests pass a fake).</param>
        internal OwnCpuTracker(Func<Dictionary<(uint Pid, long Created), long>> read) => _read = read;

        public void Poll()
        {
            var now = DateTime.UtcNow.ToFileTimeUtc();
            var current = _read();
            if (_start is null)
            {
                _start = current;
                _startTime = now;
            }
            foreach (var (key, cpu) in current)
                _latest[key] = cpu;
        }

        /// <summary>100 ns units.</summary>
        public long Used
        {
            get
            {
                if (_start is null)
                    return 0;
                long used = 0;
                foreach (var (key, cpu) in _latest)
                {
                    if (_start.TryGetValue(key, out var before))
                        used += Math.Max(0, cpu - before);
                    else if (key.Created >= _startTime)
                        used += Math.Max(0, cpu);
                }
                return used;
            }
        }
    }

    /// <summary>This process and its descendants (Toolhelp snapshot); only this process if the snapshot is not available.</summary>
    private static Dictionary<(uint Pid, long Created), long> ReadOwnTree()
    {
        var self = (uint)Environment.ProcessId;
        var processes = new List<(uint Pid, uint Parent)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        processes.Add((entry.th32ProcessID, entry.th32ParentProcessID));
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        return ProcessTreeCpu(self, processes, ProcessTimes);
    }

    /// <summary>Creation time and total CPU time (kernel + user) of a process, or null when it cannot be opened.</summary>
    private static (long Created, long Cpu)? ProcessTimes(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            return GetProcessTimes(handle, out var created, out _, out var kernel, out var user)
                ? (created, kernel + user)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static DISPLAY_DEVICE NewDisplayDevice() => new() { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>FIRMWARE_TYPE: 0 unknown, 1 BIOS, 2 UEFI (Windows 8+).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    /// <summary>FILETIME values read as 64-bit integers (100 ns units).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Live power state from GetSystemPowerStatus.</summary>
internal sealed record PowerStatus(bool? OnAc, int? BatteryChargePercent, BatteryChargeState BatteryState);
