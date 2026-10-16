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

    /// <summary>
    /// On narrow windows the publisher column gives its space to the name (it is still in the details panel), and the
    /// number columns get slimmer so the "Оценка" badge is never cut off by the scroll bar.
    /// </summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var visibility = width < 600 ? Visibility.Collapsed : Visibility.Visible;
        if (PublisherColumn.Visibility != visibility)
            PublisherColumn.Visibility = visibility;
        var slim = width < 470;
        SetWidth(CpuColumn, slim ? 54 : 62);
        SetWidth(MemoryColumn, slim ? 76 : 84);

        static void SetWidth(DataGridColumn column, double value)
        {
            if (Math.Abs(column.Width.DisplayValue - value) > 0.5 || !column.Width.IsAbsolute)
                column.Width = new DataGridLength(value);
        }
    }

    /// <summary>Below this height the secondary actions shrink to one row of icon buttons (their tooltips name them).</summary>
    private const double IconActionsBelowHeight = 470;

    /// <summary>Below this width the summary cards drop their decorative icons, so "37%  18,1 из 47,9 ГБ" fits.</summary>
    private const double CardIconsBelowWidth = 920;

    /// <summary>Below this width the filters get short captions, so the toolbar stays on one line.</summary>
    private const double ShortFiltersBelowWidth = 980;

    /// <summary>
    /// Tall panel: captioned buttons, three per row (two when narrow). Short panel (small window): one row of icon-only
    /// buttons, which roughly doubles the room for the details above.
    /// </summary>
    private void OnDetailsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var iconsOnly = e.NewSize.Height < IconActionsBelowHeight;
        var narrow = e.NewSize.Width < 330;

        // Narrow panel: "Завершить" takes only what its caption needs, so "Завершить дерево" keeps its whole caption.
        ActionBar.Padding = narrow ? new Thickness(6, 7, 6, 8) : new Thickness(9, 8, 9, 9);
        var first = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        if (KillActions.ColumnDefinitions[0].Width != first)
            KillActions.ColumnDefinitions[0].Width = first;
        foreach (var kill in KillActions.Children.OfType<Button>())
            kill.FontSize = narrow ? 12 : 12.5;

        var buttons = SecondaryActions.Children.OfType<Button>().ToList();
        var columns = iconsOnly ? buttons.Count : narrow ? 2 : 3;
        if (SecondaryActions.Columns != columns)
            SecondaryActions.Columns = columns;
        foreach (var button in buttons)
        {
            if (button.Content is not DockPanel { Children.Count: 2 } panel ||
                panel.Children[0] is not TextBlock icon || panel.Children[1] is not TextBlock caption)
                continue;
            caption.Visibility = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
            icon.Margin = iconsOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            icon.FontSize = iconsOnly ? 14 : 13;
        }
    }

    private void OnSummarySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var visibility = e.NewSize.Width < CardIconsBelowWidth ? Visibility.Collapsed : Visibility.Visible;
        foreach (var icon in new[] { CpuIcon, MemoryIcon, ProcessIcon, SuspiciousIcon })
        {
            if (icon.Visibility != visibility)
                icon.Visibility = visibility;
        }
    }

    /// <summary>Short captions on narrow windows (the tooltips keep the full explanation).</summary>
    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;
        var compact = e.NewSize.Width < ShortFiltersBelowWidth;
        GroupCheck.Content = compact ? "Группировать" : "Группировать по программам";
        HideMicrosoftCheck.Content = compact ? "Скрыть системные" : "Скрыть системные процессы Microsoft";
        SearchPanel.Width = compact ? 196 : 228;
        var gap = new Thickness(0, 8, compact ? 14 : 18, 8);
        GroupCheck.Margin = gap;
        SuspiciousCheck.Margin = gap;
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
