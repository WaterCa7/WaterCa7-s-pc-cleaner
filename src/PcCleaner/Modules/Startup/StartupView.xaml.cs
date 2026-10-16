using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcCleaner.Modules.Startup;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    /// <summary>
    /// Keeps the page readable from the minimum window size (~740 px of content) up to wide screens:
    /// the details panel narrows and less important columns are hidden (their data stays in the details panel).
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
            return;
        var wide = width >= 960;
        var medium = width >= 840;
        DetailsColumn.Width = new GridLength(wide ? 300 : medium ? 280 : 250);
        CommandColumn.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        SourceColumn.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
        RefreshText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;

        // Card icons give way to the captions on narrow screens.
        var iconStyle = TryFindResource("StatIcon");
        foreach (var border in FindChildren<Border>(Cards))
        {
            if (ReferenceEquals(border.Style, iconStyle))
                border.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindChildren<T>(child))
                yield return nested;
        }
    }
}
