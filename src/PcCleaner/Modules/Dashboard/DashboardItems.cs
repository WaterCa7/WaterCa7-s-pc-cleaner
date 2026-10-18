using System.Collections.ObjectModel;
using System.Windows.Input;
using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

/// <summary>Progress row of one module during the smart scan: "Очистка — готово: 2 проблемы".</summary>
public sealed class ModuleScanRow : ObservableObject
{
    private ModuleScanState _state = ModuleScanState.Waiting;
    private string _statusText = "ждёт очереди";
    private string? _error;
    private int _issueCount;

    public ModuleScanRow(string moduleName) => ModuleName = moduleName;

    public string ModuleName { get; }

    public ModuleScanState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
                OnPropertyChanged(nameof(DisplayStatus));
        }
    }

    /// <summary>Status as a standalone line under the module name: "Готово: 1 проблема".</summary>
    public string DisplayStatus => string.IsNullOrEmpty(StatusText)
        ? ""
        : char.ToUpper(StatusText[0], Format.Ru) + StatusText[1..];

    public int IssueCount
    {
        get => _issueCount;
        private set => Set(ref _issueCount, value);
    }

    /// <summary>Error text shown as a tooltip for failed/timed-out modules.</summary>
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public string ToolTipText => Error is null
        ? $"{ModuleName}: {StatusText}"
        : $"{ModuleName}: {StatusText}.\n{Error}\n\nОстальные разделы проверены как обычно. Откройте раздел «{ModuleName}», чтобы посмотреть подробности.";

    public void SetRunning()
    {
        State = ModuleScanState.Running;
        StatusText = "проверяется…";
        OnPropertyChanged(nameof(ToolTipText));
    }

    public void SetResult(ModuleScanResult result)
    {
        IssueCount = result.Issues.Count;
        Error = result.Error;
        State = result.State;
        var problems = result.Issues.Count(HealthScoring.IsProblem);
        StatusText = result.State switch
        {
            ModuleScanState.Done => "готово: " + HealthScoring.FindingsText(problems, result.Issues.Count - problems),
            ModuleScanState.Failed => "ошибка проверки",
            ModuleScanState.TimedOut => "превышено время ожидания",
            ModuleScanState.Cancelled => "отменено",
            ModuleScanState.Running => "проверяется…",
            _ => "ждёт очереди",
        };
        OnPropertyChanged(nameof(ToolTipText));
    }
}

/// <summary>One issue row in the results list (wraps the module's <see cref="HealthIssue"/>).</summary>
public sealed class IssueItem : ObservableObject
{
    private bool _isExpanded;
    private bool _isFixing;
    private bool _fixFailed;

    public IssueItem(HealthIssue issue)
    {
        Issue = issue;
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public HealthIssue Issue { get; }

    public IssueSeverity Severity => Issue.Severity;

    public string SeverityText => HealthScoring.GroupTitle(Issue.Severity) switch
    {
        "Советы" => "Совет",
        var s => s,
    };

    public bool HasDetails => !string.IsNullOrWhiteSpace(Issue.Details);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                OnPropertyChanged(nameof(ExpandText));
        }
    }

    public string ExpandText => IsExpanded ? "Свернуть" : "Показать полностью";

    public bool HasGain => !string.IsNullOrWhiteSpace(GainText);

    /// <summary>Module's gain text, or the reclaimable size when the module did not provide one.</summary>
    public string? GainText => HealthScoring.GainText(Issue);

    public bool HasTargetPage => !string.IsNullOrWhiteSpace(Issue.TargetPageKey)
                                 && !string.Equals(Issue.TargetPageKey, "Dashboard", StringComparison.OrdinalIgnoreCase);

    public bool IsFixing
    {
        get => _isFixing;
        set
        {
            if (Set(ref _isFixing, value))
                RelayCommand.Refresh();
        }
    }

    /// <summary>The last fix attempt failed or changed nothing (FixResult explains why).</summary>
    public bool FixFailed
    {
        get => _fixFailed;
        set
        {
            if (Set(ref _fixFailed, value))
                OnPropertyChanged(nameof(NeedsAdmin));
        }
    }

    /// <summary>The fix failed because administrator rights are required (show "Перезапустить от имени администратора").</summary>
    public bool NeedsAdmin => FixFailed && !Admin.IsElevated
                              && (AccessDenied || HealthScoring.LooksLikeAdminRequired(Issue.FixResult));

    /// <summary>The last fix attempt threw UnauthorizedAccessException/SecurityException.</summary>
    internal bool AccessDenied { get; set; }

    internal void RaiseNeedsAdmin() => OnPropertyChanged(nameof(NeedsAdmin));

    public string AutoFixHint => Issue.InFixAll
        ? "Входит в «Исправить всё»"
        : "Не входит в «Исправить всё»: это действие запускается только отдельно";

    /// <summary>The issue has a Windows settings page or a web page that helps with it.</summary>
    public bool HasSettings => !string.IsNullOrWhiteSpace(Issue.SettingsUri);

    /// <summary>Web pages are "Открыть страницу", Windows settings (ms-settings: and the like) are "Открыть настройки".</summary>
    public string SettingsLabel => Uri.TryCreate(Issue.SettingsUri, UriKind.Absolute, out var uri)
                                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        ? "Открыть страницу"
        : "Открыть настройки";

    public ICommand ToggleExpandCommand { get; }

    public ICommand? FixCommand { get; internal set; }

    public ICommand? OpenPageCommand { get; internal set; }

    public ICommand? OpenSettingsCommand { get; internal set; }

    public ICommand? CopyCommand { get; internal set; }

    public ICommand? SearchCommand { get; internal set; }

    public ICommand? RequestAdminCommand { get; internal set; }
}

/// <summary>Issues of one severity: "Критично · 2". Long groups show the first few items and "Показать ещё N".</summary>
public sealed class IssueGroup : ObservableObject
{
    /// <summary>How many rows a group shows before "Показать ещё".</summary>
    internal const int InitialVisible = 8;

    private readonly List<IssueItem> _all;

    public IssueGroup(IssueSeverity severity, IEnumerable<IssueItem> items)
    {
        Severity = severity;
        _all = items.ToList();
        Items = new ObservableCollection<IssueItem>(_all.Take(InitialVisible));
        ShowAllCommand = new RelayCommand(ShowAll);
    }

    public IssueSeverity Severity { get; }

    public string Title => HealthScoring.GroupTitle(Severity);

    public string Hint => HealthScoring.GroupHint(Severity);

    /// <summary>Every issue of the group (including the ones not shown yet).</summary>
    public IReadOnlyList<IssueItem> AllItems => _all;

    /// <summary>Rows currently shown.</summary>
    public ObservableCollection<IssueItem> Items { get; }

    public int HiddenCount => _all.Count - Items.Count;

    public bool HasHidden => HiddenCount > 0;

    public string ShowAllText => $"Показать ещё {HiddenCount}";

    public ICommand ShowAllCommand { get; }

    public int RemainingCount => _all.Count(i => !i.Issue.IsFixed);

    public string CountText => RemainingCount == _all.Count
        ? _all.Count.ToString(Format.Ru)
        : $"{RemainingCount} из {_all.Count}";

    public void ShowAll()
    {
        foreach (var item in _all.Skip(Items.Count).ToList())
            Items.Add(item);
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(ShowAllText));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>Severity counter chip in the results header.</summary>
public sealed record SeverityChip(IssueSeverity Severity, string Title, int Count);

/// <summary>Quick link tile to another page.</summary>
public sealed record QuickLink(string Key, string Title, string Description, string Glyph)
{
    public string ToolTip => $"Открыть раздел «{Title}»";
}

/// <summary>Result of a smart scan (returned for tests and logging).</summary>
public sealed record ScanOutcome(bool Cancelled, int? Score, IReadOnlyList<ModuleScanResult> Modules)
{
    public IEnumerable<HealthIssue> Issues => Modules.Where(m => m.State == ModuleScanState.Done).SelectMany(m => m.Issues);
}

/// <summary>
/// Result of "Исправить всё". <see cref="FreedBytes"/> = the scan's estimate of the fully completed fixes.
/// <see cref="Interrupted"/> = "Отмена" stopped a fix while it was running (it is counted neither fixed nor failed,
/// but it may have changed part of what it promised). <see cref="NeedAdmin"/> = how many of the failed fixes need
/// administrator rights.
/// </summary>
public sealed record FixBatchResult(int Total, int Fixed, int Failed, bool Cancelled, long FreedBytes, bool Interrupted = false,
    int NeedAdmin = 0);
