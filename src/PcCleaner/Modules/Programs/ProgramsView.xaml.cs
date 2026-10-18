using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcCleaner.Modules.Programs;

public partial class ProgramsView : UserControl
{
    public ProgramsView()
    {
        InitializeComponent();
    }

    /// <summary>Right click selects the row under the cursor, so the context menu acts on it.</summary>
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;
        if (FindRow(e.OriginalSource as DependencyObject) is { } row)
        {
            grid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    /// <summary>Keeps the selected row visible (e.g. after "Показать" or a rescan).</summary>
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: { } item } grid && grid.IsLoaded)
            grid.Dispatcher.BeginInvoke(() => grid.ScrollIntoView(item), DispatcherPriority.Background);
    }

    /// <summary>
    /// On a narrow window the summary cards drop their icons so the titles stay readable.
    /// On a low window (the minimum size) the cards switch to two lines with the number in front of the title (Tag = "Compact",
    /// see the CardTitle / CardNumber / CardHint styles), and the action buttons of the details panels move right under the
    /// program's name:
    /// pinned at the bottom they would leave only a few lines for the details, at the end of the details they would be
    /// out of sight.
    /// </summary>
    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visibility = e.NewSize.Width < 900 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var icon in new[] { CardIcon0, CardIcon1, CardIcon2, CardIcon3 })
            icon.Visibility = visibility;
        // The search box shares the row with the tab pills: narrower on a narrow window.
        SearchHost.Width = e.NewSize.Width < 800 ? 190 : 230;

        var compact = e.NewSize.Height < CompactHeight;
        Tag = compact ? "Compact" : null;
        PlaceActions(ProgramDetailsGrid, ProgramDetailsStack, ProgramActions, compact);
        PlaceActions(StoreDetailsGrid, StoreDetailsStack, StoreActions, compact);
    }

    private const double CompactHeight = 600;

    /// <summary>Position of the actions in a compact details panel: after the header and the badges.</summary>
    private const int CompactActionsIndex = 2;

    private static void PlaceActions(Grid detailsGrid, StackPanel detailsStack, Border actions, bool insideScroll)
    {
        if (insideScroll == ReferenceEquals(actions.Parent, detailsStack))
            return;
        if (actions.Parent is Panel current)
            current.Children.Remove(actions);
        if (insideScroll)
        {
            actions.Padding = new Thickness(0, 12, 0, 4);
            actions.Margin = new Thickness(0, 10, 0, 6);
            actions.BorderThickness = new Thickness(0, 1, 0, 1);
            detailsStack.Children.Insert(Math.Min(CompactActionsIndex, detailsStack.Children.Count), actions);
        }
        else
        {
            actions.Padding = new Thickness(16, 12, 16, 8);
            actions.Margin = new Thickness(0);
            actions.BorderThickness = new Thickness(0, 1, 0, 0);
            Grid.SetRow(actions, 1);
            detailsGrid.Children.Add(actions);
        }
    }

    /// <summary>Double-clicking a row opens the folded details panel.</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProgramsViewModel { IsDetailsOpen: false } vm && e.OriginalSource is DependencyObject source &&
            FindRow(source) is not null)
            vm.IsDetailsOpen = true;
    }

    private static DataGridRow? FindRow(DependencyObject? element)
    {
        while (element is not null and not DataGridRow)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as DataGridRow;
    }

    /// <summary>Width of the checkbox column in front of both grids (the thresholds below include it).</summary>
    private const double CheckColumnWidth = 36;

    /// <summary>Hides secondary columns when the grid gets narrow (minimum window size).</summary>
    private void ProgramsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width - CheckColumnWidth;
        var changed = SetVisible(PublisherColumn, width >= 560);
        changed |= SetVisible(DateColumn, width >= 470);
        if (changed)
            RedistributeStarColumns(ProgramsGrid);
    }

    private void StoreGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SetVisible(StorePublisherColumn, e.NewSize.Width - CheckColumnWidth >= 470))
            RedistributeStarColumns(StoreGrid);
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
}
