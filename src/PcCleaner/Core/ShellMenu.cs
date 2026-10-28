using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>The sidebar of the program (spec 2026-10-27, part A): which pages are the tabs of which section.</summary>
public static class ShellMenu
{
    private sealed record Entry(string Key, Func<string>? Title, string? IconGlyph, string[] PageKeys, SectionPlace Place);

    private static readonly Entry[] Entries =
    {
        new("Dashboard", null, null, new[] { "Dashboard" }, SectionPlace.Top),
        new("Cleaner", null, null, new[] { "Cleaner" }, SectionPlace.Top),
        new("Computer", () => T._("Компьютер"), "", new[] { "Hardware", "Optimizer" }, SectionPlace.Top),
        new("System", () => T._("Система"), "", new[] { "Processes", "Startup", "Programs", "Disk" }, SectionPlace.Top),
        // The plain «Защита» is "Security" elsewhere in the UI; the menu says "Protection" (Security is one of its tabs).
        new("Protection", () => T.Ctx("menu", "Защита"), null, new[] { "Security", "Network" }, SectionPlace.Top),
        new("Profile", null, null, new[] { "Profile" }, SectionPlace.Bottom),
        new("Settings", null, null, new[] { "Settings" }, SectionPlace.Bottom),
    };

    /// <summary>
    /// The sections over <paramref name="pages"/>, in menu order. A section gets the pages it names that exist (tests pass a few
    /// pages) and is left out when there are none; a page no section names gets a section of its own at the end of the main
    /// list, so a new page is never lost.
    /// </summary>
    public static List<SectionViewModel> Build(IReadOnlyList<PageViewModel> pages)
    {
        PageViewModel? Find(string key) => pages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

        var named = new List<(Entry Entry, List<PageViewModel> Tabs)>();
        foreach (var entry in Entries)
        {
            var tabs = entry.PageKeys.Select(Find).OfType<PageViewModel>().ToList();
            if (tabs.Count > 0)
                named.Add((entry, tabs));
        }
        var placed = named.SelectMany(n => n.Tabs).ToHashSet();
        var sections = named.Select(n => new SectionViewModel(n.Entry.Key, n.Entry.Title, n.Entry.IconGlyph, n.Tabs, n.Entry.Place)).ToList();
        var firstBottom = sections.FindIndex(s => s.Place == SectionPlace.Bottom);
        var at = firstBottom < 0 ? sections.Count : firstBottom;
        foreach (var page in pages.Where(p => !placed.Contains(p)))
            sections.Insert(at++, new SectionViewModel(page.Key, null, null, new[] { page }, SectionPlace.Top));
        return sections;
    }
}
