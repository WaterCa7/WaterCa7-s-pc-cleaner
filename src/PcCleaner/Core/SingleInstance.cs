using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;

namespace PcCleaner.Core;

/// <summary>
/// One copy of the program per Windows session. A second start (a double click, a second click while the first copy is still
/// asking for the rights) does not open a second window: it asks the running copy to show its window and exits.
/// A restart (<see cref="WaitsForPrevious"/>) starts the next copy before this one has finished closing, so that copy waits
/// for the place instead of giving up. The mutex and the event are open to the user's own account, so a copy with limited
/// rights still reaches one that runs as administrator, and the other way round.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// Added by the restarts that start the next copy before this one exits («Перезапустить сейчас», «Запустить как админ»):
    /// that copy waits for this one to close. Belongs to one start, like <see cref="StartupElevation.ElevatedRestartArg"/>.
    /// </summary>
    public const string RestartArg = "--restarted";

    internal const string ProgramName = "WaterCa7sPcCleaner";

    /// <summary>How long a restarted copy waits for the previous one to close.</summary>
    internal static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(15);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly int _ownerThread;
    private RegisteredWaitHandle? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _ownerThread = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// True for the starts that come from a restart of this program: the restart with rights at startup, the updater and
    /// <see cref="RestartArg"/>. Their previous copy is still closing.
    /// </summary>
    public static bool WaitsForPrevious(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a, StartupElevation.ElevatedRestartArg, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, UpdateService.UpdatedFromArg, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, RestartArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>The place of the program for a start with these arguments; null = another copy runs, it was asked to show itself.</summary>
    public static SingleInstance? Acquire(IReadOnlyList<string> args)
        => Acquire(ProgramName, WaitsForPrevious(args) ? RestartWait : TimeSpan.Zero);

    /// <summary>
    /// Takes the place called <paramref name="name"/>, waiting up to <paramref name="waitForPrevious"/> for a copy that holds it.
    /// Null when another copy keeps it: that copy has been asked to show its window. Must be disposed on the same thread.
    /// </summary>
    internal static SingleInstance? Acquire(string name, TimeSpan waitForPrevious)
    {
        Mutex mutex;
        try
        {
            mutex = MutexAcl.Create(false, $@"Local\{name}.Instance", out _, MutexSecurityForUser());
        }
        catch (UnauthorizedAccessException ex)
        {
            // Only a copy of another account could have made it so: the place is taken, nothing can be asked of it.
            Log.Warn(T._("Программа уже запущена, но её окно недоступно"), ex);
            return null;
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(waitForPrevious);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous copy ended without closing (a crash, a killed process): the place is free
        }

        if (!owned)
        {
            mutex.Dispose();
            AskTheRunningCopyToShowItself(name);
            return null;
        }

        try
        {
            var activate = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ActivateName(name), out _, EventSecurityForUser());
            return new SingleInstance(mutex, activate);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // The program still runs alone; only a second start will not bring its window forward.
            Log.Warn(T.F("Не удалось подготовить показ окна при повторном запуске: {0}", ErrorText.For(ex)), ex);
            return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset));
        }
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a pool thread) every time a second start asks this copy to show itself.</summary>
    public void ListenForActivation(Action onActivate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _listener?.Unregister(null);
        _listener = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Frees the place. Called on the thread that acquired it (a Windows mutex belongs to its thread).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _listener?.Unregister(null);
        _activate.Dispose();
        if (Environment.CurrentManagedThreadId == _ownerThread)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not held any more
            }
        }
        _mutex.Dispose(); // from another thread the place frees itself when that thread or the process ends
    }

    private static void AskTheRunningCopyToShowItself(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(ActivateName(name), out var activate))
            return; // the running copy is still starting (or asking for the rights): its window appears by itself
        using (activate)
        {
            // This start was a click of the user, so it may hand the right to come to the front over to the running copy.
            AllowSetForegroundWindow(AsfwAny);
            activate.Set();
        }
    }

    private static string ActivateName(string name) => $@"Local\{name}.Activate";

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("No user SID");
    }

    private static MutexSecurity MutexSecurityForUser()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(CurrentUser(), MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static EventWaitHandleSecurity EventSecurityForUser()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
