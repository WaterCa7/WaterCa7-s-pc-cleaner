using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PcCleaner.Modules.Disk;

/// <summary>
/// <see cref="Tone"/> -> theme brush. ConverterParameter "Soft" returns the translucent background variant.
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var soft = parameter is "Soft";
        var tone = value is Tone t ? t : Tone.Neutral;
        var key = (tone, soft) switch
        {
            (Tone.Accent, false) => "AccentBrush",
            (Tone.Accent, true) => "AccentSoftBrush",
            (Tone.Info, false) => "InfoBrush",
            (Tone.Info, true) => "InfoSoftBrush",
            (Tone.Warning, false) => "WarningBrush",
            (Tone.Warning, true) => "WarningSoftBrush",
            (Tone.Danger, false) => "DangerBrush",
            (Tone.Danger, true) => "DangerSoftBrush",
            (_, false) => "MutedBrush",
            _ => "Surface3Brush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary><see cref="SuggestionAction"/> -> AccentButton / DangerButton / default button style.</summary>
public sealed class ActionStyleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            SuggestionAction { IsDanger: true } => "DangerButton",
            SuggestionAction { IsAccent: true } => "AccentButton",
            _ => null,
        };
        if (key is not null)
            return Application.Current?.TryFindResource(key) as Style;
        return Application.Current?.TryFindResource(typeof(System.Windows.Controls.Button)) as Style;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
