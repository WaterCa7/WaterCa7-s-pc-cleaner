using PcCleaner.Core.Account;

namespace PcCleaner.Tests.Account;

/// <summary>The account store in memory.</summary>
internal sealed class MemoryAccountStore : IAccountStore
{
    public AccountData Data { get; set; } = new();

    public int Saves { get; private set; }

    public AccountData Load() => Data;

    public void Save(AccountData data)
    {
        Data = data;
        Saves++;
    }
}

internal sealed class FixedHwHash : IHwHash
{
    public string Value { get; init; } = TestLicense.Hw;
}

/// <summary>An IProgress that reports at once, on the caller's thread (Progress&lt;T&gt; posts later).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>A store whose Save fails (a full disk, a broken DPAPI profile, a locked file) while <see cref="Fail"/> is set.</summary>
internal sealed class ThrowingAccountStore(Func<Exception> error) : IAccountStore
{
    public AccountData Data { get; set; } = new();

    public bool Fail { get; set; } = true;

    public int Attempts { get; private set; }

    public AccountData Load() => Data;

    public void Save(AccountData data)
    {
        Attempts++;
        if (Fail)
            throw error();
        Data = data;
    }
}

/// <summary>An hw_hash source that fails every time (WMI broken).</summary>
internal sealed class ThrowingHwHash : IHwHash
{
    public string Value => throw new InvalidOperationException("WMI is broken");
}

/// <summary>What the real <c>Task.Delay</c> does with a negative duration: throws at once (it would fault the refresh loop).</summary>
internal static class DelayGuard
{
    public static void NotNegative(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A delay is never negative.");
    }
}

/// <summary>
/// The delay of a service under test, completed by the test: every call is recorded and waits until <see cref="Complete"/> (or its
/// token is cancelled). Nothing waits for real time; <see cref="RequestedAsync"/> only guards a hang.
/// </summary>
internal sealed class ManualDelays
{
    private readonly object _gate = new();
    private readonly List<(TimeSpan Delay, TaskCompletionSource Done)> _calls = new();

    public Func<TimeSpan, CancellationToken, Task> Delay => (d, ct) =>
    {
        // Like Task.Delay: a negative duration throws at once (the real delay would fault the refresh loop).
        DelayGuard.NotNegative(d);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => done.TrySetCanceled(ct));
        lock (_gate)
            _calls.Add((d, done));
        return done.Task;
    };

    public int Count
    {
        get
        {
            lock (_gate)
                return _calls.Count;
        }
    }

    /// <summary>The delay of call <paramref name="index"/> (0-based) once the service asked for it.</summary>
    public async Task<TimeSpan> RequestedAsync(int index)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (_gate)
            {
                if (_calls.Count > index)
                    return _calls[index].Delay;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"delay #{index} was never asked for");
            await Task.Delay(1);
        }
    }

    public void Complete(int index)
    {
        lock (_gate)
            _calls[index].Done.TrySetResult();
    }
}
