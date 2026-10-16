using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcCleaner.Modules.Dashboard;

/// <summary>
/// Attached behavior for collapsible text: set <c>CollapsedHeight</c> on a wrapping TextBlock and bind to
/// <c>IsOverflowing</c> to show "Показать полностью" only when the full text really does not fit.
/// </summary>
public static class TextOverflow
{
    public static readonly DependencyProperty CollapsedHeightProperty = DependencyProperty.RegisterAttached(
        "CollapsedHeight", typeof(double), typeof(TextOverflow), new PropertyMetadata(0.0, OnCollapsedHeightChanged));

    private static readonly DependencyPropertyKey IsOverflowingKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsOverflowing", typeof(bool), typeof(TextOverflow), new PropertyMetadata(false));

    public static readonly DependencyProperty IsOverflowingProperty = IsOverflowingKey.DependencyProperty;

    public static double GetCollapsedHeight(DependencyObject d) => (double)d.GetValue(CollapsedHeightProperty);

    public static void SetCollapsedHeight(DependencyObject d, double value) => d.SetValue(CollapsedHeightProperty, value);

    public static bool GetIsOverflowing(DependencyObject d) => (bool)d.GetValue(IsOverflowingProperty);

    private static void OnCollapsedHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;
        tb.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0)
            tb.SizeChanged += OnSizeChanged;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is TextBlock tb)
            Update(tb);
    }

    private static void Update(TextBlock tb)
    {
        var limit = GetCollapsedHeight(tb);
        var width = tb.ActualWidth - tb.Padding.Left - tb.Padding.Right;
        if (limit <= 0 || width <= 1 || string.IsNullOrEmpty(tb.Text))
        {
            tb.SetValue(IsOverflowingKey, false);
            return;
        }
        try
        {
            var text = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip)
            {
                MaxTextWidth = width,
                Trimming = TextTrimming.None,
            };
            tb.SetValue(IsOverflowingKey, text.Height > limit + 1);
        }
        catch (ArgumentException)
        {
            tb.SetValue(IsOverflowingKey, false);
        }
    }
}
