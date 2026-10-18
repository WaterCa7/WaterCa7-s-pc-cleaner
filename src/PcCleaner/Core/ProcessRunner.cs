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

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>
    /// ANSI code page of the system ("language for non-Unicode programs", 1251 on Russian Windows), for files written by
    /// non-Unicode programs. Not CultureInfo.CurrentCulture's: the start sets the culture to the UI language.
    /// Latin-1 if the code page is not available.
    /// </summary>
    public static Encoding AnsiEncoding
    {
        get
        {
            try
            {
                return Encoding.GetEncoding((int)GetACP());
            }
            catch
            {
                return Encoding.Latin1;
            }
        }
    }

    /// <summary>
    /// "reg.exe" -> "C:\Windows\System32\reg.exe". CreateProcess looks in the application's own folder first,
    /// so a bare name would let a planted reg.exe next to our exe (e.g. in Downloads) run with our — possibly
    /// administrator — rights. Names that are not found in System32 are returned unchanged.
    /// </summary>
    public static string ResolveSystemTool(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) ||
            fileName.Contains('\\') || fileName.Contains('/'))
            return fileName;
        var name = Path.HasExtension(fileName) ? fileName : fileName + ".exe";
        var system = Path.Combine(Environment.SystemDirectory, name);
        if (File.Exists(system))
            return system;
        var wbem = Path.Combine(Environment.SystemDirectory, "wbem", name);
        return File.Exists(wbem) ? wbem : fileName;
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo(ResolveSystemTool(fileName), arguments)
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
    /// The script is written to a temporary .ps1 and run with -File: an "-EncodedCommand" launch looks exactly like
    /// malware to antivirus software (and to our own Processes page).
    /// Not elevated, the .ps1 lives in the app's data folder. Elevated, it is created in %WINDIR%\Temp with an
    /// admin-only DACL (<see cref="SecureTemp"/>), so a non-elevated process cannot swap it before PowerShell reads it,
    /// and modules are loaded only from the machine-wide module folders (not from the user-writable Documents).
    /// Falls back to -EncodedCommand only if a group policy forbids running script files.
    /// </summary>
    public static async Task<ProcessResult> PowerShellAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var elevated = Admin.IsElevated;
        var full = BuildPowerShellScript(script, elevated);
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var utf8 = new UTF8Encoding(false);

        string? file = null;
        try
        {
            // BOM so that Windows PowerShell 5.1 reads non-ASCII (Cyrillic) text correctly.
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(full)).ToArray();
            if (elevated)
            {
                file = SecureTemp.Create(SecureTemp.ElevatedDir, "WaterCa7sPcCleaner-ps", ".ps1", bytes);
            }
            else
            {
                var dir = AppPaths.EnsureDir(Path.Combine(AppPaths.DataDir, "Scripts"));
                file = Path.Combine(dir, $"ps-{Guid.NewGuid():N}.ps1");
                await File.WriteAllBytesAsync(file, bytes, ct);
            }
            var result = await RunAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"",
                timeout, ct, utf8);
            if (!IsScriptExecutionBlocked(result))
                return result;
            Log.Warn("Запуск .ps1 запрещён политикой — используется резервный способ запуска PowerShell");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось создать временный скрипт PowerShell: {ErrorText.For(ex)}");
        }
        finally
        {
            SecureTemp.TryDelete(file);
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return await RunAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeout, ct, utf8);
    }

    /// <summary>The script with the common prologue (pure, unit-tested).</summary>
    internal static string BuildPowerShellScript(string script, bool elevated)
        => "$ProgressPreference='SilentlyContinue'\r\n" +
           "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\r\n" +
           "$OutputEncoding=[System.Text.Encoding]::UTF8\r\n" +
           // Windows PowerShell always puts Documents\WindowsPowerShell\Modules (user-writable) first in the module
           // path; an elevated script must not auto-load a module planted there.
           (elevated
               ? "$machineModules=[Environment]::GetEnvironmentVariable('PSModulePath','Machine'); if ($machineModules) { $env:PSModulePath=$machineModules }\r\n"
               : "") +
           script;

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
            Log.Warn($"Не удалось открыть {target}: {ErrorText.For(ex)}");
            Dialogs.Error("Не удалось открыть", $"{target}\n\n{ErrorText.Sentence(ex)}");
        }
    }

    /// <summary>
    /// "C:\Windows\explorer.exe". CreateProcess looks in the application's own folder first, so a bare "explorer.exe"
    /// would let a planted explorer.exe next to our exe run with our (possibly administrator) rights.
    /// </summary>
    internal static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(ExplorerPath, $"/select,\"{path}\"")?.Dispose();
            else if (Directory.Exists(path))
                Process.Start(ExplorerPath, $"\"{path}\"")?.Dispose();
            else
                Dialogs.Info("Файл не найден", path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось открыть проводник для {path}: {ErrorText.For(ex)}");
        }
    }
}
