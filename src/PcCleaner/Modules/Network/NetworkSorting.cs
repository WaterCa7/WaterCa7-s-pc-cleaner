namespace PcCleaner.Modules.Network;

/// <summary>Column keys of the connections list (DataGridColumn.SortMemberPath).</summary>
public static class ConnectionSortKeys
{
    public const string Count = "Count";
    public const string Address = "Address";
    public const string Host = "Host";
    public const string Service = "Service";
    public const string State = "State";
}

/// <summary>
/// Sorting of the connections list: programs stay together (suspicious ones first), connections are sorted inside
/// their program.
/// </summary>
public static class NetworkSorting
{
    public static IEnumerable<(ConnectionGroupItem Group, List<ConnectionItem> Children)> SortGroups(
        IEnumerable<(ConnectionGroupItem Group, List<ConnectionItem> Children)> groups, string key, bool descending)
    {
        var suspiciousFirst = groups.OrderByDescending(g => g.Group.Suspicious);
        return key switch
        {
            ConnectionSortKeys.Address or ConnectionSortKeys.Host => descending
                ? suspiciousFirst.ThenByDescending(g => g.Group.ProcessDisplay, StringComparer.CurrentCultureIgnoreCase)
                : suspiciousFirst.ThenBy(g => g.Group.ProcessDisplay, StringComparer.CurrentCultureIgnoreCase),
            _ => descending
                ? suspiciousFirst.ThenByDescending(g => g.Children.Sum(c => c.Count)).ThenBy(g => g.Group.ProcessDisplay)
                : suspiciousFirst.ThenBy(g => g.Children.Sum(c => c.Count)).ThenBy(g => g.Group.ProcessDisplay),
        };
    }

    public static IEnumerable<ConnectionItem> SortChildren(IEnumerable<ConnectionItem> children, string key, bool descending)
    {
        var suspiciousFirst = children.OrderByDescending(c => c.IsSuspicious);
        IOrderedEnumerable<ConnectionItem> sorted = key switch
        {
            ConnectionSortKeys.Host => Order(suspiciousFirst, c => c.HostName, descending),
            ConnectionSortKeys.Service => Order(suspiciousFirst, c => c.Service, descending),
            ConnectionSortKeys.State => Order(suspiciousFirst, c => c.StateText, descending),
            ConnectionSortKeys.Address => Order(suspiciousFirst, c => c.RemoteSortKey, descending),
            _ => descending ? suspiciousFirst.ThenByDescending(c => c.Count) : suspiciousFirst.ThenBy(c => c.Count),
        };
        return sorted.ThenBy(c => c.RemoteSortKey, StringComparer.Ordinal);
    }

    private static IOrderedEnumerable<ConnectionItem> Order(IOrderedEnumerable<ConnectionItem> source, Func<ConnectionItem, string> key,
        bool descending)
        => descending
            ? source.ThenByDescending(key, StringComparer.CurrentCultureIgnoreCase)
            : source.ThenBy(key, StringComparer.CurrentCultureIgnoreCase);
}
