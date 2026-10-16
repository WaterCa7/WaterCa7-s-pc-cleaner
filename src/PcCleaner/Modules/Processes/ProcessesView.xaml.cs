using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

public partial class ProcessesView : UserControl
{
    private ProcessesViewModel? _subscribed;

    public ProcessesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe(IsLoaded ? Vm : null);
        Loaded += (_, _) => Subscribe(Vm);
        Unloaded += (_, _) => Subscribe(null);
    }

    /// <summary>The shell creates a new view on every visit: only a loaded view listens to the view-model.</summary>
    private void Subscribe(ProcessesViewModel? vm)
    {
        if (ReferenceEquals(_subscribed, vm))
        {
            UpdateSortGlyphs();
            return;
        }
        if (_subscribed is not null)
        {
            _subscribed.RevealRequested -= OnRevealRequested;
            _subscribed.PropertyChanged -= OnVmPropertyChanged;
        }
        _subscribed = vm;
        if (vm is not null)
        {
            vm.RevealRequested += OnRevealRequested;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
        UpdateSortGlyphs();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProcessesViewModel.SortKey) or nameof(ProcessesViewModel.SortDescending))
            UpdateSortGlyphs();
    }

    /// <summary>The view-model picked a row itself (e.g. the first suspicious one): make sure it is visible.</summary>
    private void OnRevealRequested(ProcessItem item)
    {
        try
        {
            ProcessGrid.UpdateLayout();
            ProcessGrid.ScrollIntoView(item);
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: прокрутка к строке: {ex.Message}");
        }
    }

    private ProcessesViewModel? Vm => DataContext as ProcessesViewModel;

    /// <summary>Sorting is done by the view-model (groups keep their processes underneath), the grid only shows the arrow.</summary>
    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        try
        {
            Vm?.SortBy(e.Column.SortMemberPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Процессы: сортировка: {ex.Message}");
        }
    }

    private void UpdateSortGlyphs()
    {
        if (Vm is not { } vm)
            return;
        foreach (var column in ProcessGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == vm.SortKey
                ? vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    /// <summary>Right click selects the row under the cursor, so the context menu acts on it.</summary>
    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
        {
            ProcessGrid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Vm?.SelectedItem is null)
            e.Handled = true;
        RelayCommand.Refresh();
    }

    private void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { Item: ProcessItem item })
            Vm?.ToggleExpand(item);
    }

    /// <summary>Right/Left arrows expand/collapse a program, like in a tree.</summary>
    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedItem is not { } item)
            return;
        switch (e.Key)
        {
            case Key.Right when item.HasChildren && !item.IsExpanded:
                vm.SetExpanded(item, true);
                e.Handled = true;
                break;
            case Key.Left when item.IsExpanded || item.IsChild:
                vm.SetExpanded(item, false);
                e.Handled = true;
                break;
        }
    }

    /// <summary>On narrow windows the publisher column gives its space to the name (it is still in the details panel).</summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visibility = e.NewSize.Width < 600 ? Visibility.Collapsed : Visibility.Visible;
        if (PublisherColumn.Visibility != visibility)
            PublisherColumn.Visibility = visibility;
    }

    /// <summary>Three action buttons per row when there is room for their captions, otherwise two.</summary>
    private void OnDetailsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = e.NewSize.Width < 330 ? 2 : 3;
        if (SecondaryActions.Columns != columns)
            SecondaryActions.Columns = columns;
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}

/// <summary>true/false -> theme brush by resource key (module-local helper).</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public string TrueKey { get; set; } = "AccentBrush";

    public string FalseKey { get; set; } = "MutedBrush";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current?.TryFindResource(value is true ? TrueKey : FalseKey) as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
