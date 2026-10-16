using System.ComponentModel;
using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>Result of "Отчёт о батарее".</summary>
public sealed record BatteryReportResult(bool Success, string Message, string? Path = null);

/// <summary>
/// "Отчёт о батарее": runs "powercfg /batteryreport /output &lt;Documents&gt;\battery-report.html". The report only reads
/// the battery history Windows already keeps — nothing in the system is changed (the previous report with the same name
/// is replaced, like powercfg itself does).
/// </summary>
public sealed class BatteryReportGenerator
{
    public const string FileName = "battery-report.html";

    private readonly Func<string, CancellationToken, Task<ProcessResult>> _powercfg;
    private readonly Func<string> _outputDir;

    public BatteryReportGenerator()
        : this(null, null) { }

    /// <param name="powercfg">Runs powercfg.exe with the given arguments (tests pass a fake).</param>
    /// <param name="outputDir">Folder for the report (defaults to the user's Documents).</param>
    internal BatteryReportGenerator(Func<string, CancellationToken, Task<ProcessResult>>? powercfg, Func<string>? outputDir)
    {
        _powercfg = powercfg ?? RunPowercfgAsync;
        _outputDir = outputDir ?? DefaultOutputDir;
    }

    /// <summary>Where the report will be saved.</summary>
    public string OutputPath => Path.Combine(_outputDir(), FileName);

    internal static string Arguments(string outputPath) => $"/batteryreport /output \"{outputPath}\"";

    public async Task<BatteryReportResult> CreateAsync(CancellationToken ct = default)
    {
        string path;
        try
        {
            var dir = _outputDir();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"Ускорение: нет доступа к папке для отчёта о батарее: {ex.Message}");
            return new BatteryReportResult(false, $"Не удалось подготовить папку для отчёта: {ex.Message}");
        }

        var before = LastWrite(path);
        var result = await _powercfg(Arguments(path), ct).ConfigureAwait(false);
        var after = LastWrite(path);
        if (result.Success && after is not null && after != before)
        {
            Log.Info($"Ускорение: создан отчёт о батарее — {path}");
            return new BatteryReportResult(true, $"Отчёт о батарее сохранён: {path}", path);
        }

        var reason = FirstLine(result.StdErr) ?? FirstLine(result.StdOut) ??
                     (result.TimedOut ? "powercfg не ответил вовремя" : $"код завершения {result.ExitCode}");
        Log.Warn($"Ускорение: не удалось создать отчёт о батарее: {reason}");
        return new BatteryReportResult(false, $"Windows не смогла создать отчёт о батарее: {reason}");
    }

    private static DateTime? LastWrite(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string DefaultOutputDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(docs) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : docs;
    }

    /// <summary>%SystemRoot%\System32\powercfg.exe by full path; a launch failure becomes a failed result.</summary>
    private static async Task<ProcessResult> RunPowercfgAsync(string args, CancellationToken ct)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
        try
        {
            return await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(60), ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessResult(-1, "", ex.Message, false);
        }
    }
}
