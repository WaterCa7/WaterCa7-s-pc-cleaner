using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PcCleaner.Modules.Optimizer;

/// <summary><see cref="Tone"/> -> theme brush (text/icon colour).</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Tone.Good => "SuccessBrush",
            Tone.Info => "InfoBrush",
            Tone.Warning => "WarningBrush",
            Tone.Danger => "DangerBrush",
            _ => "MutedBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary><see cref="Tone"/> -> soft theme brush (badge background).</summary>
public sealed class ToneToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Tone.Good => "AccentSoftBrush",
            Tone.Info => "InfoSoftBrush",
            Tone.Warning => "WarningSoftBrush",
            Tone.Danger => "DangerSoftBrush",
            _ => "Surface3Brush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Available width -> number of UniformGrid columns. ConverterParameter = minimal column width (default 240).
/// Makes card grids responsive (4 columns at the default window size, 3 at the minimum).
/// </summary>
public sealed class ColumnsForWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d && !double.IsNaN(d) ? d : 0;
        var min = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 240;
        if (width <= 0)
            return 1;
        return Math.Clamp((int)(width / min), 1, 6);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// [available width, item count] -> UniformGrid columns: as many as fit (ConverterParameter = minimal column width),
/// then balanced so the rows stay full — 9 cards become 3 × 3 instead of 4 + 4 + 1 lonely card.
/// </summary>
public sealed class BalancedColumnsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double d && !double.IsNaN(d) ? d : 0;
        var count = values.Length > 1 && values[1] is int n ? n : 0;
        var min = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 240;
        return Columns(width, count, min);
    }

    /// <summary>Most columns that fit, reduced to the fewest that still need the same number of rows.</summary>
    public static int Columns(double width, int count, double minColumnWidth)
    {
        var max = width <= 0 || minColumnWidth <= 0 ? 1 : Math.Clamp((int)(width / minColumnWidth), 1, 6);
        if (count <= max)
            return max;
        var rows = (count + max - 1) / max;
        return (count + rows - 1) / rows;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
