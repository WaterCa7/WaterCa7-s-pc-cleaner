namespace PcCleaner.Core;

/// <summary>Text for the one confirmation a bulk action shows before it touches the checked items.</summary>
public static class BulkText
{
    /// <summary>"• a\n• b\n…и ещё N": the first <paramref name="max"/> lines as bullets, the rest only counted.</summary>
    public static string BulletList(IEnumerable<string> lines, int max = 14)
    {
        var all = lines.ToList();
        var shown = all.Take(max).Select(l => "• " + l).ToList();
        if (all.Count > max)
            shown.Add($"…и ещё {all.Count - max}");
        return string.Join("\n", shown);
    }
}
