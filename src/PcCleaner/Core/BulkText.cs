using PcCleaner.Core.Localization;

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
            shown.Add(T.F("…и ещё {0}", all.Count - max));
        return string.Join("\n", shown);
    }

    /// <summary>
    /// What a bulk action works on, for its question: "{header}:" and the bullets — or, when some checked rows are not in the list
    /// right now (search, filter, tab, collapsed program), "{header}." with those first under «Не видны в списке сейчас:», never
    /// folded into "…и ещё N" (they are acted on too), then the rows the list shows under «В списке:», folded after 14.
    /// The bulk actions work in this order too: hidden rows first.
    /// </summary>
    public static string TargetList(string header, IReadOnlyCollection<string> hiddenLines, IReadOnlyCollection<string> shownLines)
    {
        var nl = Environment.NewLine;
        if (hiddenLines.Count == 0)
            return header + ":" + nl + BulletList(shownLines);
        var text = header + "." + nl + nl + T._("Не видны в списке сейчас:") + nl + BulletList(hiddenLines, hiddenLines.Count);
        if (shownLines.Count > 0)
            text += nl + nl + T._("В списке:") + nl + BulletList(shownLines);
        return text;
    }
}
