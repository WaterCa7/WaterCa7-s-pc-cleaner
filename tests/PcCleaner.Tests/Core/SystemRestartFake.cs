using PcCleaner.Core;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The tests that replace the static <see cref="SystemRestart"/> seams. The collection never runs in parallel with other
/// tests, so a replaced seam cannot leak into a test that expects the defaults (which must never restart Windows).
/// </summary>
[CollectionDefinition("SystemRestart", DisableParallelization = true)]
public sealed class SystemRestartCollection
{
}

/// <summary>
/// Answers the restart question and counts the restarts instead of starting one; <see cref="Dispose"/> puts the previous
/// seams back. Use it only in classes of the "SystemRestart" collection.
/// </summary>
internal sealed class SystemRestartFake : IDisposable
{
    private readonly Func<string, string, bool> _previousConfirm = SystemRestart.Confirm;
    private readonly Func<bool> _previousStart = SystemRestart.Start;

    public SystemRestartFake(bool answer, bool startSucceeds = true, Action? onConfirm = null)
    {
        SystemRestart.Confirm = (title, text) =>
        {
            Questions.Add((title, text));
            onConfirm?.Invoke();
            return answer;
        };
        SystemRestart.Start = () =>
        {
            StartCalls++;
            return startSucceeds;
        };
    }

    /// <summary>Every question that was asked (title, text).</summary>
    public List<(string Title, string Text)> Questions { get; } = new();

    /// <summary>How many times Windows would have been restarted.</summary>
    public int StartCalls { get; private set; }

    public void Dispose()
    {
        SystemRestart.Confirm = _previousConfirm;
        SystemRestart.Start = _previousStart;
    }
}
