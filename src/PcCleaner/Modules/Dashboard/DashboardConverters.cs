using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

internal static class ThemeBrushes
{
    public static Brush Get(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Health score (int?/double) → green / amber / red theme brush; muted when there is no score.</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int? score = value switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            _ => null,
        };
        return ThemeBrushes.Get(HealthScoring.ScoreBrushKey(score));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// IssueSeverity → accent brush of the dashboard (Critical/High red, Medium amber, Low blue, Info green).
/// ConverterParameter="Soft" returns the matching translucent background brush.
/// </summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var soft = parameter is "Soft";
        var key = value switch
        {
            IssueSeverity.Critical or IssueSeverity.High => soft ? "DangerSoftBrush" : "DangerBrush",
            IssueSeverity.Medium => soft ? "WarningSoftBrush" : "WarningBrush",
            IssueSeverity.Low => soft ? "InfoSoftBrush" : "InfoBrush",
            _ => soft ? "AccentSoftBrush" : "AccentBrush",
        };
        return ThemeBrushes.Get(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IssueSeverity → Fluent icon glyph.</summary>
public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IssueSeverity.Critical => "",
        IssueSeverity.High => "",
        IssueSeverity.Medium => "",
        IssueSeverity.Low => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Available width → number of columns for responsive UniformGrids.
/// ConverterParameter="900:3:2" means: width ≥ 900 → 3 columns, otherwise 2.
/// </summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d ? d : 0;
        var parts = (parameter as string ?? "900:3:2").Split(':');
        if (parts.Length != 3
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            || !int.TryParse(parts[1], out var wide)
            || !int.TryParse(parts[2], out var narrow))
            return 2;
        return width <= 0 || width >= threshold ? wide : narrow;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Pure layout helpers of the dashboard.</summary>
public static class LayoutMath
{
    /// <summary>
    /// Column count for <paramref name="count"/> equal tiles that leaves the last row as full as possible. Tries
    /// <paramref name="maxColumns"/> down to max(2, maxColumns − 2) and picks the fewest empty cells (ties → more
    /// columns). E.g. 8 tiles: 4 columns when 4 fit, otherwise 2 — never 3 + 3 + 2.
    /// </summary>
    public static int BalancedColumns(int count, int maxColumns)
    {
        if (count <= 1 || maxColumns <= 1)
            return 1;
        var max = Math.Min(maxColumns, count);
        var min = Math.Max(2, max - 2);
        var best = max;
        var bestEmpty = int.MaxValue;
        for (var columns = max; columns >= min; columns--)
        {
            var empty = (columns - count % columns) % columns;
            if (empty < bestEmpty)
            {
                best = columns;
                bestEmpty = empty;
            }
        }
        return best;
    }
}

/// <summary>
/// [available width, item count] → balanced column count for a UniformGrid (see <see cref="LayoutMath.BalancedColumns"/>).
/// ConverterParameter = minimum cell width in pixels, e.g. "220".
/// </summary>
public sealed class BalancedColumnsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double d && !double.IsNaN(d) ? d : 0;
        var count = values.Length > 1 && values[1] is int n ? n : 0;
        var minCell = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 200;
        var max = width <= 0 ? 4 : Math.Max(1, (int)(width / minCell));
        return LayoutMath.BalancedColumns(Math.Max(1, count), max);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ScoreTrend → chip background (Soft) or foreground brush: green when better, red when worse, neutral otherwise.</summary>
public sealed class TrendBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var soft = parameter is "Soft";
        var key = value switch
        {
            ScoreTrend.Better => soft ? "SuccessSoftBrush" : "SuccessBrush",
            ScoreTrend.Worse => soft ? "DangerSoftBrush" : "DangerBrush",
            _ => soft ? "Surface2Brush" : "TextBrush",
        };
        return ThemeBrushes.Get(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Available width → Visibility. ConverterParameter="820": visible when width ≥ 820 (or not measured yet);
/// "&lt;820": visible when 0 &lt; width &lt; 820. Used to move content between a wide and a narrow layout.
/// </summary>
public sealed class WidthToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d && !double.IsNaN(d) ? d : 0;
        var text = (parameter as string ?? "").Trim();
        var narrow = text.StartsWith('<');
        if (!double.TryParse(narrow ? text[1..] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
            threshold = 0;
        var isWide = width <= 0 || width >= threshold;
        return (narrow ? !isWide : isWide) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>All bound values are true → Visible, otherwise Collapsed.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public static readonly AllTrueToVisibilityConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
