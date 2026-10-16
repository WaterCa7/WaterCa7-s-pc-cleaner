namespace PcCleaner.Modules.Startup;

/// <summary>Filter state of the Startup page.</summary>
public sealed record StartupFilterOptions
{
    public StartupSourceFilter Source { get; init; } = StartupSourceFilter.All;

    public StartupStatusFilter Status { get; init; } = StartupStatusFilter.All;

    public string? Search { get; init; }

    public bool ShowWindowsTasks { get; init; }

    public bool ShowMicrosoftServices { get; init; }
}

/// <summary>Pure filtering rules (unit-tested).</summary>
public static class StartupFilter
{
    /// <summary>Built-in Windows tasks and Microsoft services are hidden unless the user asks for them.</summary>
    public static bool IsVisible(StartupItem item, StartupFilterOptions options)
    {
        if (item.IsWindowsTask && !options.ShowWindowsTasks)
            return false;
        if (item.IsMicrosoftService && !options.ShowMicrosoftServices)
            return false;
        return true;
    }

    public static bool MatchesSource(StartupItem item, StartupSourceFilter source)
        => source == StartupSourceFilter.All || item.SourceGroup == source;

    public static bool MatchesStatus(StartupItem item, StartupStatusFilter status) => status switch
    {
        StartupStatusFilter.Enabled => item.IsEnabled,
        StartupStatusFilter.Disabled => !item.IsEnabled,
        StartupStatusFilter.Suspicious => item.IsSuspicious,
        StartupStatusFilter.Broken => item.IsBroken,
        _ => true,
    };

    public static bool MatchesSearch(StartupItem item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            Contains(item.Name, term) || Contains(item.Publisher, term) || Contains(item.Command, term) ||
            Contains(item.TargetFile, term) || Contains(item.SourceText, term) || Contains(item.FileDescription, term) ||
            Contains(item.ServiceName, term) || Contains(item.TaskPath, term));
    }

    public static bool Matches(StartupItem item, StartupFilterOptions options)
        => IsVisible(item, options) && MatchesSource(item, options.Source) && MatchesStatus(item, options.Status) &&
           MatchesSearch(item, options.Search);

    public static IEnumerable<StartupItem> Apply(IEnumerable<StartupItem> items, StartupFilterOptions options)
        => items.Where(i => Matches(i, options));

    private static bool Contains(string? text, string term)
        => !string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
