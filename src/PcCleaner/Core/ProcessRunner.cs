using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PcCleaner.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs console tools (reg.exe, powercfg, dism, schtasks, ...) hidden, with timeout and correct encoding.
/// </summary>
public static class ProcessRunner
{
    static ProcessRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>Console code page used by classic tools (866 on Russian Windows).</summary>
    public static Encoding OemEncoding
    {
        get
        {
            try
            {
                return Encoding.GetEncoding((int)GetOEMCP());
            }
            catch
            {
                return Encoding.UTF8;
            }
        }
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? OemEncoding,
            StandardErrorEncoding = outputEncoding ?? OemEncoding,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited
            }
            if (ct.IsCancellationRequested)
                throw;
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        // Make sure async output handlers have flushed.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    /// <summary>
    /// Runs a PowerShell script (Windows PowerShell 5.1) and returns UTF-8 decoded output.
    /// The script is passed via -EncodedCommand, so no quoting problems.
    /// </summary>
    public static Task<ProcessResult> PowerShellAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var full = "$ProgressPreference='SilentlyContinue';" +
                   "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
                   "$OutputEncoding=[System.Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        return RunAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeout, ct, new UTF8Encoding(false));
    }

    /// <summary>Opens a file/URL/folder with the shell (Explorer, default browser).</summary>
    public static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {target}", ex);
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
            else
                Dialogs.Info("Файл не найден", path);
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть проводник для {path}", ex);
        }
    }
}
