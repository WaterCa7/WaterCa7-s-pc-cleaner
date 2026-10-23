using System.Threading;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// The setup holds <see cref="MutexName"/> (SetupMutex in installer/WaterCa7sPcCleaner.iss) while it runs. A start in that time
/// (a shortcut click during an update, the setup's own <c>--updated-from</c> start and the launch at the end of a manual install,
/// which both come while the setup still holds it for a moment) waits until the setup has exited and then starts normally:
/// the single-copy place sorts out any second copy. Not used by the setup's own commands or by screenshot runs.
/// </summary>
public static class SetupInProgress
{
    public const string MutexName = "WaterCa7sPcCleaner.Setup";

    /// <summary>How long a start waits at most; an update normally takes well under a minute.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Waits while the setup runs. True = no setup runs (any more); false = it still ran after <see cref="MaxWait"/>.</summary>
    public static bool WaitUntilDone()
        => WaitUntilDone(() => MutexExists(MutexName), MaxWait, PollInterval, Thread.Sleep);

    internal static bool WaitUntilDone(Func<bool> setupRuns, TimeSpan maxWait, TimeSpan pollInterval, Action<TimeSpan> delay)
    {
        if (!setupRuns())
            return true;
        Log.Info(T._("Идёт установка программы — ждём, пока она закончится"));
        var waited = TimeSpan.Zero;
        while (waited < maxWait)
        {
            delay(pollInterval);
            waited += pollInterval;
            if (!setupRuns())
                return true;
        }
        Log.Warn(T._("Установка программы не закончилась за 3 минуты — программа открывается без ожидания"));
        return false;
    }

    /// <summary>
    /// Whether a mutex of this name exists in this session (<c>Local\</c>, where the setup creates it). The setup runs as
    /// administrator, so a copy with limited rights may not open it: that answer also means it exists.
    /// </summary>
    internal static bool MutexExists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting($@"Local\{name}", out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
