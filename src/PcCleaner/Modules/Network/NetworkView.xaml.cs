using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcCleaner.Modules.Network;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnViewSizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is NetworkViewModel oldVm)
        {
            oldVm.SocketsRequested -= ScrollToSockets;
            oldVm.ConnectionSortChanged -= UpdateConnectionSortGlyphs;
        }
        if (e.NewValue is NetworkViewModel newVm)
        {
            newVm.SocketsRequested += ScrollToSockets;
            newVm.ConnectionSortChanged += UpdateConnectionSortGlyphs;
            UpdateConnectionSortGlyphs();
        }
    }

    /// <summary>On a narrow window the summary cards drop their icons so the values and hints stay readable.</summary>
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visibility = e.NewSize.Width < 900 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var icon in new[] { CardIcon0, CardIcon1, CardIcon2, CardIcon3 })
        {
            if (icon.Visibility != visibility)
                icon.Visibility = visibility;
        }
    }

    /// <summary>On a narrow grid the "Служба" column gives its space to the program name (the service is in the tooltips).</summary>
    private void OnConnectionsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SetVisible(ConnServiceColumn, e.NewSize.Width >= 720))
            RedistributeStarColumns(ConnectionsGrid);
    }

    /// <summary>On a narrow grid the "Что это" column is hidden (the explanation stays in the tooltips).</summary>
    private void OnPortsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SetVisible(PortServiceColumn, e.NewSize.Width >= 720))
            RedistributeStarColumns(PortsGrid);
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

    /// <summary>Programs stay together in the connections list, so the view model sorts it (not the DataGrid).</summary>
    private void OnConnectionsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is NetworkViewModel vm)
            vm.SortConnections(e.Column.SortMemberPath);
    }

    private void UpdateConnectionSortGlyphs()
    {
        if (DataContext is not NetworkViewModel vm)
            return;
        foreach (var column in ConnectionsGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == vm.ConnectionSortKey
                ? vm.ConnectionSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    /// <summary>Scrolls the page so the connections/ports card is at the top.</summary>
    private void ScrollToSockets()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!SocketsCard.IsVisible)
                return;
            var point = SocketsCard.TranslatePoint(new Point(0, 0), PageRoot);
            Scroller.ScrollToVerticalOffset(Math.Max(0, point.Y - 4));
        }, DispatcherPriority.Loaded);
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        if (DataContext is NetworkViewModel vm)
            vm.SearchText = "";
        SearchBox.Focus();
    }

    /// <summary>Lets the page scroll when a grid is already at its top/bottom (or cannot scroll at all).</summary>
    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject grid)
            return;
        var inner = FindChild<ScrollViewer>(grid);
        var canScrollGrid = inner is not null && inner.ScrollableHeight > 0 &&
                            ((e.Delta > 0 && inner.VerticalOffset > 0) ||
                             (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight));
        if (canScrollGrid)
            return;
        e.Handled = true;
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
    }

    /// <summary>Selects the row under the mouse before the context menu opens.</summary>
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not DataGridRow)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        if (current is DataGridRow row)
            row.IsSelected = true;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var nested = FindChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
