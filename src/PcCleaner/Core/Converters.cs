using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PcCleaner.Core;

/// <summary>true -> Visible, false -> Collapsed. ConverterParameter="Invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is "Invert")
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible ^ parameter is "Invert";
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>null/empty string/0 -> Collapsed, otherwise Visible. ConverterParameter="Invert" flips it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "Invert")
            has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>long/ulong bytes -> "1,5 ГБ".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => Format.Bytes(l),
        ulong u => Format.Bytes(u),
        int i => Format.Bytes(i),
        double d => Format.Bytes((long)d),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IssueSeverity / SuspicionLevel / SignatureStatus -> theme brush.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            IssueSeverity.Critical or IssueSeverity.High => "DangerBrush",
            IssueSeverity.Medium => "WarningBrush",
            IssueSeverity.Low => "InfoBrush",
            IssueSeverity.Info => "MutedBrush",
            SuspicionLevel.High => "DangerBrush",
            SuspicionLevel.Medium => "WarningBrush",
            SuspicionLevel.Low => "InfoBrush",
            SuspicionLevel.None => "SuccessBrush",
            SignatureStatus.Valid => "SuccessBrush",
            SignatureStatus.Invalid => "DangerBrush",
            SignatureStatus.Unsigned or SignatureStatus.Untrusted => "WarningBrush",
            true => "SuccessBrush",
            false => "DangerBrush",
            _ => "MutedBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IssueSeverity -> Russian label.</summary>
public sealed class SeverityToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        IssueSeverity.Critical => "Критично",
        IssueSeverity.High => "Важно",
        IssueSeverity.Medium => "Средне",
        IssueSeverity.Low => "Низко",
        IssueSeverity.Info => "Совет",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>0..100 percent -> brush (green / amber / red). Use for disk/ram usage bars.</summary>
public sealed class PercentToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0,
        };
        var key = p >= 90 ? "DangerBrush" : p >= 75 ? "WarningBrush" : "AccentBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
