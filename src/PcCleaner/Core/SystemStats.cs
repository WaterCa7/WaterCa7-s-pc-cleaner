using System.Runtime.InteropServices;

namespace PcCleaner.Core;

public sealed record MemoryStatus(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent)
{
    public ulong UsedBytes => TotalBytes - AvailableBytes;
}

/// <summary>Cheap live system metrics (no WMI, no performance counters).</summary>
public static class SystemStats
{
    private static readonly object CpuLock = new();
    private static ulong _lastIdle, _lastKernel, _lastUser;
    private static bool _hasSample;

    /// <summary>
    /// Total CPU usage in percent since the previous call (first call returns a short 250 ms sample).
    /// Call it periodically (e.g. once per 1-2 s) from a timer.
    /// </summary>
    public static double CpuUsagePercent()
    {
        lock (CpuLock)
        {
            if (!_hasSample)
            {
                Sample(out _lastIdle, out _lastKernel, out _lastUser);
                _hasSample = true;
                Thread.Sleep(250);
            }
            Sample(out var idle, out var kernel, out var user);
            var idleDelta = idle - _lastIdle;
            var totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            if (totalDelta == 0)
                return 0;
            return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }
    }

    public static MemoryStatus Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status)
            ? new MemoryStatus(status.ullTotalPhys, status.ullAvailPhys, status.dwMemoryLoad)
            : new MemoryStatus(0, 0, 0);
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static void Sample(out ulong idle, out ulong kernel, out ulong user)
    {
        GetSystemTimes(out var i, out var k, out var u);
        idle = i;
        kernel = k;
        user = u;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong lpIdleTime, out ulong lpKernelTime, out ulong lpUserTime);
}
