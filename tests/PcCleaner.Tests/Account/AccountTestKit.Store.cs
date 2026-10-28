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
