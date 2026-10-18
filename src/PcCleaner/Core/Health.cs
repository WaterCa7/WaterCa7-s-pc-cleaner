namespace PcCleaner.Core;

public enum IssueSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>
/// A finding produced by a module analysis. Shown on the dashboard after a smart scan.
/// </summary>
public sealed class HealthIssue : ObservableObject
{
    private bool _isFixed;
    private string? _fixResult;

    /// <summary>Module display name, e.g. "Очистка".</summary>
    public required string Module { get; init; }

    public required string Title { get; init; }

    public string Details { get; init; } = "";

    public IssueSeverity Severity { get; init; } = IssueSeverity.Info;

    /// <summary>What the user gains, e.g. "Освободит 2,3 ГБ".</summary>
    public string? Gain { get; init; }

    /// <summary>Bytes that fixing this would free (used for the dashboard total). 0 if not applicable.</summary>
    public long ReclaimableBytes { get; init; }

    /// <summary>Optional fix. Returns a human-readable result message.</summary>
    public Func<CancellationToken, Task<string>>? FixAsync { get; init; }

    /// <summary>True if the fix is low-risk: the row shows the «Безопасно исправить» badge. «Исправить всё» is gated by InFixAll.</summary>
    public bool AutoFixSafe { get; init; }

    /// <summary>Text of the fix button: "Исправить", or what exactly happens ("Перезагрузить", "Удалить программу").</summary>
    public string FixLabel { get; init; } = "Исправить";

    /// <summary>False for fixes that open their own windows, restart the PC or remove programs: they are not run by "Исправить всё".</summary>
    public bool InFixAll { get; init; } = true;

    /// <summary>Windows settings page or web page that helps with the issue ("Открыть настройки" / "Открыть страницу").</summary>
    public string? SettingsUri { get; init; }

    /// <summary>Page key (PageViewModel.Key) to open for details, e.g. "Startup".</summary>
    public string? TargetPageKey { get; init; }

    public bool CanFix => FixAsync is not null && !IsFixed;

    public bool IsFixed
    {
        get => _isFixed;
        set
        {
            if (Set(ref _isFixed, value))
                OnPropertyChanged(nameof(CanFix));
        }
    }

    public string? FixResult
    {
        get => _fixResult;
        set => Set(ref _fixResult, value);
    }
}

/// <summary>
/// Implemented by every module service so the dashboard can run a combined read-only analysis.
/// </summary>
public interface IHealthContributor
{
    string ModuleName { get; }

    /// <summary>Read-only analysis. MUST NOT modify the system.</summary>
    Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct);
}
