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

/// <summary>All bound values are true → Visible, otherwise Collapsed.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public static readonly AllTrueToVisibilityConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
