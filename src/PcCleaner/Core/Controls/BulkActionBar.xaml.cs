using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PcCleaner.Core.Controls;

/// <summary>
/// "Отмечено: N · [the page's actions] · [Снять отметки]" strip shown above a list with a checkbox column.
/// Bind <see cref="CheckedCount"/> to <c>CheckedSet.Count</c>; the bar is collapsed while it is 0.
/// </summary>
public partial class BulkActionBar : UserControl
{
    public static readonly DependencyProperty CheckedCountProperty = DependencyProperty.Register(
        nameof(CheckedCount), typeof(int), typeof(BulkActionBar), new PropertyMetadata(0));

    /// <summary>Where the page puts its action buttons (any element, usually a horizontal StackPanel).</summary>
    public static readonly DependencyProperty ActionsContentProperty = DependencyProperty.Register(
        nameof(ActionsContent), typeof(object), typeof(BulkActionBar), new PropertyMetadata(null));

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(BulkActionBar), new PropertyMetadata(null));

    public BulkActionBar()
    {
        InitializeComponent();
    }

    public int CheckedCount
    {
        get => (int)GetValue(CheckedCountProperty);
        set => SetValue(CheckedCountProperty, value);
    }

    public object? ActionsContent
    {
        get => GetValue(ActionsContentProperty);
        set => SetValue(ActionsContentProperty, value);
    }

    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }
}
