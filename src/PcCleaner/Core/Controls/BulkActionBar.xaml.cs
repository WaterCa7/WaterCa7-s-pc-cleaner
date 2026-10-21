using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Controls;

/// <summary>
/// "Отмечено: N · [the page's actions] · [Снять отметки]" strip shown above a list with a checkbox column.
/// Bind <see cref="CheckedCount"/> to <c>CheckedSet.Count</c>; the bar is collapsed while it is 0.
/// </summary>
public partial class BulkActionBar : UserControl
{
    public static readonly DependencyProperty CheckedCountProperty = DependencyProperty.Register(
        nameof(CheckedCount), typeof(int), typeof(BulkActionBar), new PropertyMetadata(0, OnCheckedCountChanged));

    private static readonly DependencyPropertyKey CheckedTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CheckedText), typeof(string), typeof(BulkActionBar), new PropertyMetadata(""));

    /// <summary>"Отмечено: N" in the language of the UI (a text with a number: built here, not by a StringFormat in the XAML).</summary>
    public static readonly DependencyProperty CheckedTextProperty = CheckedTextPropertyKey.DependencyProperty;

    /// <summary>Where the page puts its action buttons (any element, usually a horizontal StackPanel).</summary>
    public static readonly DependencyProperty ActionsContentProperty = DependencyProperty.Register(
        nameof(ActionsContent), typeof(object), typeof(BulkActionBar), new PropertyMetadata(null));

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(BulkActionBar), new PropertyMetadata(null));

    public BulkActionBar()
    {
        SetValue(CheckedTextPropertyKey, CheckedTextFor(0));
        InitializeComponent();
    }

    private static void OnCheckedCountChanged(DependencyObject bar, DependencyPropertyChangedEventArgs e)
        => bar.SetValue(CheckedTextPropertyKey, CheckedTextFor((int)e.NewValue));

    internal static string CheckedTextFor(int count) => T.F("Отмечено: {0}", count);

    public string CheckedText => (string)GetValue(CheckedTextProperty);

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
