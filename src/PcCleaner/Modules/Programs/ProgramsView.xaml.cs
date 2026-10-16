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
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        if (element is DataGridRow row)
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

    /// <summary>On a narrow window the summary cards drop their icons so the titles stay readable.</summary>
    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visibility = e.NewSize.Width < 900 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var icon in new[] { CardIcon0, CardIcon1, CardIcon2, CardIcon3 })
            icon.Visibility = visibility;
    }

    /// <summary>Hides secondary columns when the grid gets narrow (minimum window size).</summary>
    private void ProgramsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var changed = SetVisible(PublisherColumn, width >= 560);
        changed |= SetVisible(DateColumn, width >= 470);
        if (changed)
            RedistributeStarColumns(ProgramsGrid);
    }

    private void StoreGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SetVisible(StorePublisherColumn, e.NewSize.Width >= 470))
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
