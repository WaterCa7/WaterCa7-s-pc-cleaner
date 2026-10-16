using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcCleaner.Modules.Security;

public partial class SecurityView : UserControl
{
    public SecurityView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SecurityViewModel oldVm)
            oldVm.DetailsRequested -= ScrollToDetails;
        if (e.NewValue is SecurityViewModel newVm)
            newVm.DetailsRequested += ScrollToDetails;
    }

    /// <summary>Scrolls the page so the details tabs are at the top.</summary>
    private void ScrollToDetails()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!DetailsCard.IsVisible)
                return;
            var point = DetailsCard.TranslatePoint(new Point(0, 0), PageRoot);
            Scroller.ScrollToVerticalOffset(Math.Max(0, point.Y - 4));
        }, DispatcherPriority.Loaded);
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
