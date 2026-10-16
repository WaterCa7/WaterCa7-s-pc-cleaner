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
    /// The script is written to a temporary .ps1 in the app's data folder and run with -File: an "-EncodedCommand"
    /// launch looks exactly like malware to antivirus software (and to our own Processes page).
    /// Falls back to -EncodedCommand only if a group policy forbids running script files.
    /// </summary>
    public static async Task<ProcessResult> PowerShellAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var full = "$ProgressPreference='SilentlyContinue'\r\n" +
                   "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\r\n" +
                   "$OutputEncoding=[System.Text.Encoding]::UTF8\r\n" + script;
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var utf8 = new UTF8Encoding(false);

        string? file = null;
        try
        {
            var dir = AppPaths.EnsureDir(Path.Combine(AppPaths.DataDir, "Scripts"));
            file = Path.Combine(dir, $"ps-{Guid.NewGuid():N}.ps1");
            // BOM so that Windows PowerShell 5.1 reads non-ASCII (Cyrillic) text correctly.
            await File.WriteAllTextAsync(file, full, new UTF8Encoding(true), ct);
            var result = await RunAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"",
                timeout, ct, utf8);
            if (!IsScriptExecutionBlocked(result))
                return result;
            Log.Warn("Запуск .ps1 запрещён политикой — используется резервный способ запуска PowerShell");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось создать временный скрипт PowerShell: {ex.Message}");
        }
        finally
        {
            if (file is not null)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // best effort
                }
            }
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return await RunAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeout, ct, utf8);
    }

    private static bool IsScriptExecutionBlocked(ProcessResult result)
        => !result.Success &&
           (result.StdErr.Contains("PSSecurityException", StringComparison.OrdinalIgnoreCase) ||
            result.StdErr.Contains("about_Execution_Policies", StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens a file/URL/folder with the shell (Explorer, default browser).</summary>
    public static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info($"Открытие отменено пользователем: {target}");
        }
        catch (Exception ex)
        {
            // Environment problems (no default browser, missing file) are not app bugs.
            Log.Warn($"Не удалось открыть {target}: {ex.Message}");
            Dialogs.Error("Не удалось открыть", $"{target}\n\n{ex.Message}");
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
            Log.Warn($"Не удалось открыть проводник для {path}: {ex.Message}");
        }
    }
}
