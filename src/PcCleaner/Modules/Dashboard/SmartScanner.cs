using System.ComponentModel;
using System.IO;
using System.Security;
using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

public enum ModuleScanState
{
    Waiting,
    Running,
    Done,
    Failed,
    TimedOut,
    Cancelled,
}

/// <summary>Result of one module analysis inside the smart scan.</summary>
public sealed record ModuleScanResult(
    string ModuleName,
    ModuleScanState State,
    IReadOnlyList<HealthIssue> Issues,
    string? Error,
    TimeSpan Elapsed);

/// <summary>
/// Runs the read-only analyses of all modules for the smart scan. Every module is isolated: an exception or a hang
/// in one module never breaks the others (exception → "ошибка проверки", hang → per-module timeout).
/// </summary>
public static class SmartScanner
{
    public static readonly TimeSpan DefaultModuleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs one contributor on a worker thread with a per-module timeout (linked to <paramref name="ct"/>).
    /// Never throws: overall cancellation is reported as <see cref="ModuleScanState.Cancelled"/>.
    /// </summary>
    public static async Task<ModuleScanResult> RunModuleAsync(IHealthContributor contributor, TimeSpan timeout, CancellationToken ct)
    {
        var name = SafeName(contributor);
        var started = DateTime.UtcNow;
        TimeSpan Elapsed() => DateTime.UtcNow - started;

        if (ct.IsCancellationRequested)
            return new ModuleScanResult(name, ModuleScanState.Cancelled, Array.Empty<HealthIssue>(), null, TimeSpan.Zero);

        using var moduleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        moduleCts.CancelAfter(timeout);

        // Task.Run: a contributor that does blocking work before its first await must not stall the UI thread
        // or the other modules.
        var analysis = Task.Run(() => contributor.AnalyzeAsync(moduleCts.Token), CancellationToken.None);
        try
        {
            // WaitAsync stops waiting even if the contributor ignores its token.
            var issues = await analysis.WaitAsync(moduleCts.Token).ConfigureAwait(false);
            var clean = (issues ?? Array.Empty<HealthIssue>()).Where(i => i is not null).ToList();
            return new ModuleScanResult(name, ModuleScanState.Done, clean, null, Elapsed());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Observe(analysis);
            return new ModuleScanResult(name, ModuleScanState.Cancelled, Array.Empty<HealthIssue>(), null, Elapsed());
        }
        catch (OperationCanceledException) when (moduleCts.IsCancellationRequested)
        {
            Observe(analysis);
            Log.Warn($"Главная: модуль «{name}» не уложился в {timeout.TotalSeconds:0} с — проверка пропущена");
            return new ModuleScanResult(name, ModuleScanState.TimedOut, Array.Empty<HealthIssue>(),
                $"Проверка не уложилась в {timeout.TotalSeconds:0} с", Elapsed());
        }
        catch (Exception ex)
        {
            var kind = ex is UnauthorizedAccessException or SecurityException or Win32Exception or IOException
                ? "нет доступа или файл занят"
                : ex.GetType().Name;
            Log.Warn($"Главная: ошибка проверки модуля «{name}» ({kind}): {ex.Message}");
            return new ModuleScanResult(name, ModuleScanState.Failed, Array.Empty<HealthIssue>(), ErrorText.Sentence(ex), Elapsed());
        }
    }

    private static string SafeName(IHealthContributor contributor)
    {
        try
        {
            var n = contributor.ModuleName;
            return string.IsNullOrWhiteSpace(n) ? contributor.GetType().Name : n;
        }
        catch
        {
            return contributor.GetType().Name;
        }
    }

    /// <summary>Abandoned analyses may fault later; observe them so they never become "unobserved task" errors.</summary>
    private static void Observe(Task task)
        => task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
