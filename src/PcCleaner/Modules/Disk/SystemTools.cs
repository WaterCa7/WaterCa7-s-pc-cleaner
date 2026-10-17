using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Disk;

/// <summary>Paths of the Windows console tools used by the module.</summary>
internal static class SystemTools
{
    public static string Exe(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>
    /// Starts a GUI tool (Disk Cleanup, System Protection...). Never throws. A cancelled UAC prompt (the user said "No")
    /// is <see cref="ToolLaunchResult.Cancelled"/>, not a failure.
    /// </summary>
    public static ToolLaunchResult Launch(string exe, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true })?.Dispose();
            return ToolLaunchResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info($"{Path.GetFileName(exe)}: запуск отменён пользователем");
            return ToolLaunchResult.Cancelled;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Не удалось запустить {Path.GetFileName(exe)}: {ex.Message}");
            return ToolLaunchResult.Failed;
        }
    }

    private const int ErrorCancelled = 1223;

    /// <summary>
    /// True if a window of a process with this name (without ".exe") is open. Windows itself runs "cleanmgr /autoclean"
    /// hidden (the SilentCleanup maintenance task): such a windowless instance must not make the button say
    /// "уже открыто". Read-only, never throws.
    /// </summary>
    public static bool IsRunning(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
        var anyWindow = false;
        foreach (var p in processes)
        {
            anyWindow |= HasWindow(p);
            p.Dispose();
        }
        return anyWindow;
    }

    /// <summary>Whether the process shows a visible top-level window (false if it already exited).</summary>
    internal static bool HasWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs shell work (SHFileOperation may show a confirmation) on a dedicated STA thread, off the UI thread.
    /// </summary>
    public static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WaterCa7s disk shell worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Opens a shell location (folder, shell: URI). Never throws.</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Не удалось открыть {target}: {ex.Message}");
        }
    }

    /// <summary>
    /// "C:\Windows\explorer.exe". CreateProcess looks in the application's own folder first, so a bare "explorer.exe"
    /// would let a planted explorer.exe next to our exe (e.g. in Downloads) run with our — possibly administrator —
    /// rights. Same path as <see cref="ProcessRunner.ShowInExplorer"/> uses.
    /// </summary>
    internal static string ExplorerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Explorer arguments that select a file or open a folder; null if the path no longer exists.</summary>
    internal static string? ExplorerArguments(string path)
    {
        if (File.Exists(path))
            return $"/select,\"{path}\"";
        if (Directory.Exists(path))
            return $"\"{path}\"";
        return null;
    }

    /// <summary>Selects a file (or opens a folder) in Explorer. Never throws.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            var arguments = ExplorerArguments(path);
            if (arguments is not null)
                Process.Start(ExplorerPath, arguments)?.Dispose();
            else
                Dialogs.Info("Не найдено", $"Файл или папка больше не существует:\n{path}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Не удалось открыть проводник для {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a console tool and reports every output line (lines are split on CR too, so progress updates of
    /// defrag.exe /U arrive one by one). Cancellation kills the tool and throws.
    /// </summary>
    public static async Task<ProcessResult> RunStreamingAsync(string exe, string arguments, Action<string> onLine,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ProcessRunner.OemEncoding,
            StandardErrorEncoding = ProcessRunner.OemEncoding,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            var buffer = new char[2048];
            var line = new StringBuilder();
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var ch = buffer[i];
                    if (ch is '\r' or '\n')
                    {
                        FlushLine(line, output, onLine);
                        continue;
                    }
                    line.Append(ch);
                }
            }
            FlushLine(line, output, onLine);
        }, CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // already exited
            }
            // Let the readers finish on the closed pipes (and observe their faults: an unobserved faulted task
            // would be reported as an application error later).
            var readers = Task.WhenAll(reader, errors);
            _ = readers.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw;
            string partial;
            lock (output)
                partial = output.ToString();
            return new ProcessResult(-1, partial, "", TimedOut: true);
        }

        await reader.ConfigureAwait(false);
        var stderr = await errors.ConfigureAwait(false);
        string text;
        lock (output)
            text = output.ToString();
        return new ProcessResult(process.ExitCode, text, stderr, TimedOut: false);
    }

    private static void FlushLine(StringBuilder line, StringBuilder output, Action<string> onLine)
    {
        if (line.Length == 0)
            return;
        var text = line.ToString().TrimEnd();
        line.Clear();
        if (text.Length == 0)
            return;
        lock (output)
            output.AppendLine(text);
        try
        {
            onLine(text);
        }
        catch (Exception ex)
        {
            Log.Warn($"Ошибка обработки вывода: {ex.Message}");
        }
    }

    /// <summary>Size and item count of the Recycle Bin on all drives (read-only).</summary>
    public static (long Bytes, long Items) QueryRecycleBin()
    {
        try
        {
            if (IntPtr.Size == 8)
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(null, ref info) == 0)
                    return (info.i64Size, info.i64NumItems);
            }
            else
            {
                var info = new SHQUERYRBINFO32 { cbSize = Marshal.SizeOf<SHQUERYRBINFO32>() };
                if (SHQueryRecycleBin32(null, ref info) == 0)
                    return (info.i64Size, info.i64NumItems);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // ignore
        }
        return (0, 0);
    }

    // shellapi.h packs this structure to 1 byte on 32-bit Windows only.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SHQUERYRBINFO32
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin32(string? pszRootPath, ref SHQUERYRBINFO32 pSHQueryRBInfo);
}

/// <summary>
/// Resolves the real location of an existing file or folder (through junctions, symbolic links, SUBST drives and
/// 8.3 short names) with GetFinalPathNameByHandle. Read-only.
/// </summary>
internal static class FinalPaths
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>The final path ("C:\Windows\..."), or null if the path does not exist or cannot be opened.</summary>
    public static string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        // Access 0: only metadata is queried, so files locked by other programs can still be resolved.
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                return null;
            if (length < buffer.Length)
                return Normalize(new string(buffer, 0, (int)length));
            buffer = new char[length + 1];
        }
    }

    /// <summary>"\\?\C:\x" -> "C:\x", "\\?\UNC\srv\share\x" -> "\\srv\share\x".</summary>
    internal static string Normalize(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + finalPath[8..];
        if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return finalPath[4..];
        return finalPath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, [Out] char[] lpszFilePath,
        uint cchFilePath, uint dwFlags);
}

/// <summary>Why a file was not moved to the Recycle Bin.</summary>
internal enum RecycleFailure
{
    None,
    /// <summary>Removable or network drive: Windows has no Recycle Bin there, so the file is not touched.</summary>
    NoRecycleBin,
    /// <summary>The file does not fit into the Recycle Bin, Windows offered to delete it permanently and the user said no.</summary>
    DeclinedPermanentDelete,
    /// <summary>Another program has the file open.</summary>
    InUse,
    /// <summary>No permission to delete the file.</summary>
    AccessDenied,
    /// <summary>The file is gone (deleted or moved meanwhile).</summary>
    Missing,
    /// <summary>Anything else (the shell error code is kept).</summary>
    Failed,
}

/// <summary>Result of one attempt to move a file to the Recycle Bin, with a plain-language reason on failure.</summary>
internal readonly record struct RecycleOutcome(RecycleFailure Failure, int Code = 0)
{
    public static RecycleOutcome Ok => new(RecycleFailure.None);

    public bool Success => Failure == RecycleFailure.None;

    /// <summary>Why the file was skipped (shown after "имя файла — "), empty on success.</summary>
    public string Reason => Failure switch
    {
        RecycleFailure.None => "",
        RecycleFailure.NoRecycleBin => "у этого диска нет корзины (флешка или сетевой диск), поэтому файл не тронут",
        RecycleFailure.DeclinedPermanentDelete =>
            "в корзину файл не помещается, а удалить его навсегда вы отказались — файл не тронут",
        RecycleFailure.InUse => "файл открыт в другой программе — закройте её и повторите",
        RecycleFailure.AccessDenied => "нет прав на удаление этого файла",
        RecycleFailure.Missing => "файл уже удалён или перемещён",
        _ => Code == 0
            ? "Windows не смогла переместить файл в корзину"
            : $"Windows не смогла переместить файл в корзину (код {Code})",
    };
}

/// <summary>
/// Moves files to the Recycle Bin. Unlike a plain FO_DELETE with FOF_NOCONFIRMATION it sets FOF_WANTNUKEWARNING,
/// so Windows asks before permanently deleting a file that is too big for the Recycle Bin. A failure is reported with
/// its real reason (no Recycle Bin on that drive, permanent deletion declined, file busy, access denied).
/// </summary>
internal static class SafeRecycler
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    // Win32 errors and the legacy DE_* codes SHFileOperation may return.
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorCancelled = 1223;
    private const int DeOpCancelled = 0x75;
    private const int DeAccessDeniedSrc = 0x78;
    private const int DeInvalidFiles = 0x7C;

    public static RecycleOutcome Recycle(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                Log.Warn($"Не перемещено в корзину (у этого диска нет корзины): {path}");
                return new RecycleOutcome(RecycleFailure.NoRecycleBin);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Log.Warn($"Не перемещено в корзину: {path}: {ex.Message}");
            return new RecycleOutcome(RecycleFailure.NoRecycleBin);
        }

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofNoErrorUi | FofSilent,
        };
        var rc = SHFileOperation(ref op);
        var outcome = Classify(rc, op.fAnyOperationsAborted, File.Exists(path), () => ProbeDelete(path));
        if (outcome.Success)
            Log.Action($"Перемещено в корзину: {path}");
        else
            Log.Warn($"Не удалось переместить в корзину: {path} — {outcome.Reason} (код {rc})");
        return outcome;
    }

    /// <summary>
    /// Turns the result of SHFileOperation into a reason. <paramref name="probe"/> is asked only when the shell code says
    /// nothing specific or the operation was aborted: it opens the file for deletion (without deleting anything) and returns
    /// the Win32 error, 0 if it opened. Measured on Windows 11: a busy file gives 32, a protected one 0x78 (DE_ACCESSDENIEDSRC).
    /// </summary>
    internal static RecycleOutcome Classify(int rc, bool aborted, bool stillExists, Func<int> probe)
    {
        if (rc == 0 && !aborted && !stillExists)
            return RecycleOutcome.Ok;
        if (!stillExists)
            return new RecycleOutcome(RecycleFailure.Missing, rc);
        // With FOF_NOCONFIRMATION and FOF_NOERRORUI the only question Windows can ask is "delete permanently?". But with
        // FOF_NOERRORUI Windows may also abort silently on an error (DE_OPCANCELLED covers both), so a file that cannot be
        // opened for deletion is reported as busy / protected rather than as a refused prompt.
        if (aborted || rc is ErrorCancelled or DeOpCancelled)
        {
            return probe() switch
            {
                ErrorSharingViolation or ErrorLockViolation => new RecycleOutcome(RecycleFailure.InUse, rc),
                ErrorAccessDenied => new RecycleOutcome(RecycleFailure.AccessDenied, rc),
                _ => new RecycleOutcome(RecycleFailure.DeclinedPermanentDelete, rc),
            };
        }
        switch (rc)
        {
            case ErrorSharingViolation or ErrorLockViolation:
                return new RecycleOutcome(RecycleFailure.InUse, rc);
            case ErrorAccessDenied or DeAccessDeniedSrc:
                return new RecycleOutcome(RecycleFailure.AccessDenied, rc);
            case ErrorFileNotFound or ErrorPathNotFound or DeInvalidFiles:
                return new RecycleOutcome(RecycleFailure.Missing, rc);
        }
        return probe() switch
        {
            ErrorSharingViolation or ErrorLockViolation => new RecycleOutcome(RecycleFailure.InUse, rc),
            ErrorAccessDenied => new RecycleOutcome(RecycleFailure.AccessDenied, rc),
            ErrorFileNotFound or ErrorPathNotFound => new RecycleOutcome(RecycleFailure.Missing, rc),
            _ => new RecycleOutcome(RecycleFailure.Failed, rc),
        };
    }

    /// <summary>Opens the file with DELETE access and closes it again (nothing is deleted). Returns the Win32 error or 0.</summary>
    internal static int ProbeDelete(string path)
    {
        using var handle = CreateFileW(path, DeleteAccess, FileShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
