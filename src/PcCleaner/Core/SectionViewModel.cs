using System.Collections.ObjectModel;

namespace PcCleaner.Core;

/// <summary>Where a section sits in the sidebar: the main list or the block at the bottom (Профиль, Настройки).</summary>
public enum SectionPlace
{
    Top,
    Bottom,
}

/// <summary>
/// A sidebar item: one or more pages shown as tabs in a strip at the top of the page. The pages are the unchanged
/// <see cref="PageViewModel"/>s; a section with one visible tab shows no strip. The simple mode hides tabs, not sections:
/// a section without visible tabs is not in the sidebar (<see cref="ShellMenu"/>, <see cref="MainViewModel"/>).
/// </summary>
public sealed class SectionViewModel : ObservableObject
{
    private readonly Func<string>? _title;
    private readonly string? _iconGlyph;
    private PageViewModel? _lastTab;

    /// <param name="title">The section's own title (read on every get, so it follows the UI language); null — the title of its first page.</param>
    /// <param name="iconGlyph">A Segoe MDL2 glyph; null — the glyph of its first page.</param>
    public SectionViewModel(string key, Func<string>? title, string? iconGlyph, IReadOnlyList<PageViewModel> tabs, SectionPlace place)
    {
        if (tabs.Count == 0)
            throw new ArgumentException("A section needs at least one page.", nameof(tabs));
        Key = key;
        _title = title;
        _iconGlyph = iconGlyph;
        Tabs = tabs;
        Place = place;
    }

    public string Key { get; }

    public string Title => _title?.Invoke() ?? Tabs[0].Title;

    public string IconGlyph => _iconGlyph ?? Tabs[0].IconGlyph;

    public SectionPlace Place { get; }

    /// <summary>All pages of the section, in tab order.</summary>
    public IReadOnlyList<PageViewModel> Tabs { get; }

    /// <summary>The tabs of the current mode, in the order of <see cref="Tabs"/>.</summary>
    public ObservableCollection<PageViewModel> VisibleTabs { get; } = new();

    /// <summary>The strip is shown only when there is a choice.</summary>
    public bool ShowTabs => VisibleTabs.Count > 1;

    public bool HasVisibleTabs => VisibleTabs.Count > 0;

    /// <summary>The tab the section opens on: the last one opened while it is still visible, else the first visible one.</summary>
    public PageViewModel? EntryTab => _lastTab is not null && VisibleTabs.Contains(_lastTab) ? _lastTab : VisibleTabs.FirstOrDefault();

    public bool Contains(PageViewModel page) => Tabs.Contains(page);

    /// <summary>Notes the tab that was opened (a page of another section is ignored).</summary>
    internal void Remember(PageViewModel page)
    {
        if (Tabs.Contains(page))
            _lastTab = page;
    }

    /// <summary>Makes <see cref="VisibleTabs"/> the tabs <paramref name="isVisible"/> lets through, removing and inserting only what differs.</summary>
    internal void SyncVisibleTabs(Func<PageViewModel, bool> isVisible)
    {
        var wanted = Tabs.Where(isVisible).ToList();
        foreach (var page in VisibleTabs.Where(p => !wanted.Contains(p)).ToList())
            VisibleTabs.Remove(page);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= VisibleTabs.Count || !ReferenceEquals(VisibleTabs[i], wanted[i]))
                VisibleTabs.Insert(i, wanted[i]);
        }
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(HasVisibleTabs));
    }
}
