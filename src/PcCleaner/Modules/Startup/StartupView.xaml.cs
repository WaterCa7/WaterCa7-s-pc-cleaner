using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcCleaner.Modules.Startup;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    // Page widths from which an optional column fits next to the details panel. The list gets the page width minus the
    // gap and the details panel (12 + 250..300), 10 for the card's border and padding and 10 for the scroll bar; its
    // columns need 36 (checkbox) + 66 (toggle) + 140 (name) + 124 (risk), plus 122 (state), 104 (source) and 110 (command)
    // when shown. The DataGrid squeezes the fixed columns evenly when they do not fit (the checkbox column is protected by
    // its MinWidth), so each threshold lets the toggle, state, source and risk columns lose up to ~10 px each, no more.
    private const double StateColumnFrom = 750;
    private const double SourceColumnFrom = 864;
    private const double CommandColumnFrom = 994;

    /// <summary>
    /// Keeps the page readable from the minimum window size (~724 px of content) up to wide screens:
    /// the details panel narrows and less important columns are hidden (their data stays in the details panel).
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
            return;
        var wide = width >= 960;
        var medium = width >= 840;
        DetailsColumn.Width = new GridLength(wide ? 300 : medium ? 280 : 250);
        var changed = SetVisible(CommandColumn, width >= CommandColumnFrom);
        changed |= SetVisible(SourceColumn, width >= SourceColumnFrom);
        changed |= SetVisible(StateColumn, width >= StateColumnFrom);
        if (changed)
            RedistributeStarColumns(ItemsGrid);
        RefreshText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;

        // Card icons give way to the captions on narrow screens.
        var iconStyle = TryFindResource("StatIcon");
        foreach (var border in FindChildren<Border>(Cards))
        {
            if (ReferenceEquals(border.Style, iconStyle))
                border.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static bool SetVisible(DataGridColumn column, bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (column.Visibility == v)
            return false;
        column.Visibility = v;
        return true;
    }

    /// <summary>DataGrid does not give the space of hidden columns back to star columns; re-assigning widths forces it.</summary>
    private static void RedistributeStarColumns(DataGrid grid)
        => grid.Dispatcher.BeginInvoke(() =>
        {
            foreach (var column in grid.Columns)
            {
                if (!column.Width.IsStar)
                    continue;
                var width = column.Width;
                column.Width = new DataGridLength(column.MinWidth);
                column.Width = width;
            }
        }, DispatcherPriority.Loaded);

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindChildren<T>(child))
                yield return nested;
        }
    }
}
