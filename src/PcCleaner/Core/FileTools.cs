using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PcCleaner.Core;

public static class FileTools
{
    public static async Task<string?> Sha256Async(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the VirusTotal report for the file's SHA-256 in the browser (the file itself is not uploaded).</summary>
    public static async Task OpenVirusTotalAsync(string path, CancellationToken ct = default)
    {
        var hash = await Sha256Async(path, ct);
        if (hash is null)
        {
            Dialogs.Error("VirusTotal", "Не удалось прочитать файл для вычисления хеша.");
            return;
        }
        ProcessRunner.ShellOpen($"https://www.virustotal.com/gui/file/{hash.ToLowerInvariant()}");
    }

    /// <summary>Opens a web search for the text (e.g. a process or program name).</summary>
    public static void SearchWeb(string query)
        => ProcessRunner.ShellOpen("https://www.google.com/search?q=" + Uri.EscapeDataString(query));

    /// <summary>Scans one file with Microsoft Defender (MpCmdRun). Returns a human-readable result.</summary>
    public static async Task<string> ScanWithDefenderAsync(string path, CancellationToken ct = default)
    {
        var mpCmd = FindMpCmdRun();
        if (mpCmd is null)
            return "Microsoft Defender не найден на этом компьютере.";
        var result = await ProcessRunner.RunAsync(mpCmd, $"-Scan -ScanType 3 -File \"{path}\"",
            TimeSpan.FromMinutes(10), ct);
        Log.Info($"Проверка Defender: {path} -> код {result.ExitCode}");
        return result.ExitCode switch
        {
            0 => "Угроз не найдено.",
            2 => "Defender обнаружил угрозу! Откройте «Безопасность Windows» → «Журнал защиты».",
            _ => $"Проверка завершилась с кодом {result.ExitCode}. {result.StdOut.Trim()}",
        };
    }

    public static string? FindMpCmdRun()
    {
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender\Platform");
        try
        {
            if (Directory.Exists(platform))
            {
                var latest = Directory.GetDirectories(platform)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "MpCmdRun.exe"))
                    .FirstOrDefault(File.Exists);
                if (latest is not null)
                    return latest;
            }
        }
        catch
        {
            // fall through
        }
        var classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        return File.Exists(classic) ? classic : null;
    }

    /// <summary>
    /// Moves a file or folder to the Recycle Bin (recoverable). Returns false if it failed or was cancelled.
    /// Caller must have confirmed with the user.
    /// </summary>
    public static bool SendToRecycleBin(string path)
    {
        // FOF_WANTNUKEWARNING: if the item cannot go to the Recycle Bin (too big, no bin on that drive),
        // Windows asks instead of silently deleting it for good.
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        var rc = SHFileOperation(ref op);
        var ok = rc == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
        if (ok)
            Log.Action($"Перемещено в корзину: {path}");
        else
            Log.Warn($"Не удалось переместить в корзину: {path} (код {rc})");
        return ok;
    }

    /// <summary>True if the file or directory is a junction/symlink (must never be followed when deleting or sizing).</summary>
    public static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true; // be conservative
        }
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
