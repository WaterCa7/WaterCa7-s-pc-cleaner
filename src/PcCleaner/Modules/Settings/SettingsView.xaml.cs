using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PcCleaner.Modules.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Right-click on an unselected row selects it first, so context-menu commands act on the row under the cursor.
    /// </summary>
    private void OnGridPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
            return;

        var element = source;
        while (element is not null and not DataGridRow)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);

        if (element is not DataGridRow row || row.IsSelected)
            return;

        if (grid.SelectionMode == DataGridSelectionMode.Single)
        {
            grid.SelectedItem = row.Item;
        }
        else
        {
            grid.SelectedItems.Clear();
            row.IsSelected = true;
        }
        row.Focus();
    }
}
