using System.Runtime.InteropServices;

namespace PcCleaner.Modules.Cleaner;

/// <summary>Recycle Bin access behind an interface so tests never touch the real one.</summary>
internal interface IRecycleBin
{
    /// <summary>Size and item count of all Recycle Bins, or null if it cannot be queried.</summary>
    (long Bytes, long Items)? Query();

    /// <summary>Empties all Recycle Bins without any Windows UI. Returns false with an error text on failure.</summary>
    bool Empty(out string? error);
}

internal sealed class ShellRecycleBin : IRecycleBin
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    public (long Bytes, long Items)? Query()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var hr = SHQueryRecycleBinW(null, ref info);
            return hr == 0 ? (info.i64Size, info.i64NumItems) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    public bool Empty(out string? error)
    {
        error = null;
        try
        {
            var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (hr == 0 || hr == E_UNEXPECTED) // E_UNEXPECTED = the bin was already empty
                return true;
            error = $"код 0x{hr:X8}";
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            error = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
