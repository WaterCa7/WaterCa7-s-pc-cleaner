using System.Runtime.InteropServices;
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
            Log.Warn($"Ускорение: не удалось получить список мониторов: {ex.Message}");
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
            Log.Info($"Ускорение: запрос возможностей видеодрайвера недоступен ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>true = on AC power, false = on battery, null = unknown.</summary>
    public static bool? IsOnAcPower()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return null;
            return status.ACLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
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
}
