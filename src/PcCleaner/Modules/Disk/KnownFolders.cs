using System.IO;
using System.Runtime.InteropServices;

namespace PcCleaner.Modules.Disk;

/// <summary>User folders that may be redirected (e.g. Downloads moved to another drive).</summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static readonly Lazy<string?> DownloadsPath = new(() =>
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrWhiteSpace(path))
                        return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // fall back below
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    });

    public static string? Downloads => DownloadsPath.Value;

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags,
        IntPtr hToken, out IntPtr ppszPath);
}
