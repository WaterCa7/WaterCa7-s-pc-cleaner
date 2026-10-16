using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PcCleaner.Modules.Disk;

public partial class DiskView : UserControl
{
    public DiskView()
    {
        InitializeComponent();
    }

    private DiskViewModel? ViewModel => DataContext as DiskViewModel;

    private void FoldersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowItem(e.OriginalSource) is FolderRow row && ViewModel?.OpenFolderCommand.CanExecute(row) == true)
        {
            ViewModel.OpenFolderCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void FoldersGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
            return;
        if (e.Key == Key.Enter && FoldersGrid.SelectedItem is FolderRow row)
        {
            vm.OpenFolderCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && vm.UpCommand.CanExecute(null))
        {
            vm.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<ToggleButton>(source) is not null)
            return; // double click on the checkbox
        if (RowItem(e.OriginalSource) is LargeFileRow row)
        {
            ViewModel?.ShowInExplorerCommand.Execute(row);
            e.Handled = true;
        }
    }

    private static object? RowItem(object? source)
        => source is DependencyObject d ? FindParent<DataGridRow>(d)?.Item : null;

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T found)
                return found;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
