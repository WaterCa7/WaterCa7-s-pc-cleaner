using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

/// <summary>ObservableCollection that can replace all items with a single Reset notification (one view refresh).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public enum ToneBrushMode
{
    /// <summary>Status colour (neutral = muted).</summary>
    Brush,
    /// <summary>Soft background of the status colour (neutral = Surface3).</summary>
    Soft,
    /// <summary>Text colour: only warnings and dangers are coloured, everything else is normal text.</summary>
    Text,
    /// <summary>Icon colour: warnings and dangers coloured, everything else accent.</summary>
    Accent,
    /// <summary>Icon background: warnings and dangers soft-coloured, everything else accent-soft.</summary>
    AccentSoft,
}

/// <summary>NetTone / IssueSeverity → theme brush; see <see cref="ToneBrushMode"/>.</summary>
public sealed class NetToneBrushConverter : IValueConverter
{
    public ToneBrushMode Mode { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value switch
        {
            NetTone t => t,
            IssueSeverity.Critical or IssueSeverity.High => NetTone.Danger,
            IssueSeverity.Medium => NetTone.Warning,
            IssueSeverity.Low => NetTone.Info,
            IssueSeverity.Info => NetTone.Neutral,
            true => NetTone.Ok,
            false => NetTone.Neutral,
            _ => NetTone.Neutral,
        };
        var key = Mode switch
        {
            ToneBrushMode.Soft => tone switch
            {
                NetTone.Ok => "SuccessSoftBrush",
                NetTone.Info => "InfoSoftBrush",
                NetTone.Warning => "WarningSoftBrush",
                NetTone.Danger => "DangerSoftBrush",
                _ => "Surface3Brush",
            },
            ToneBrushMode.Text => tone switch
            {
                NetTone.Warning => "WarningBrush",
                NetTone.Danger => "DangerBrush",
                _ => "TextBrush",
            },
            ToneBrushMode.Accent => tone switch
            {
                NetTone.Warning => "WarningBrush",
                NetTone.Danger => "DangerBrush",
                _ => "AccentBrush",
            },
            ToneBrushMode.AccentSoft => tone switch
            {
                NetTone.Warning => "WarningSoftBrush",
                NetTone.Danger => "DangerSoftBrush",
                _ => "AccentSoftBrush",
            },
            _ => tone switch
            {
                NetTone.Ok => "SuccessBrush",
                NetTone.Info => "InfoBrush",
                NetTone.Warning => "WarningBrush",
                NetTone.Danger => "DangerBrush",
                _ => "MutedBrush",
            },
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>NetTone → Segoe glyph for status icons.</summary>
public sealed class NetToneGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NetTone.Ok => "",
        NetTone.Info => "",
        NetTone.Warning => "",
        NetTone.Danger => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Number → "12 connections" / "12 соединений". ConverterParameter names the word ("connection", "port"): the Russian forms and their
/// English entries live in <see cref="Words"/>, where the catalog finds them (a plural form cannot be written in XAML as a text).
/// </summary>
public sealed class PluralConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => l,
            _ => 0,
        };
        return Words(parameter as string, n);
    }

    /// <summary>The number with its word in the UI language; a name that is not known gives the bare number.</summary>
    public static string Words(string? word, long n) => word switch
    {
        "connection" => Format.Count(n, "соединение", "соединения", "соединений"),
        "port" => Format.Count(n, "порт", "порта", "портов"),
        _ => Format.Number(n),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Card grid: as many equal columns as fit (each at least <see cref="MinColumnWidth"/>, at most <see cref="MaxColumns"/>);
/// every row is as tall as its tallest card; an incomplete last row is stretched to the full width.
/// </summary>
public sealed class NetworkCardGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(NetworkCardGrid),
        new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(NetworkCardGrid),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(NetworkCardGrid),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int Columns(double width)
        => Math.Clamp((int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing)), 1, Math.Max(1, MaxColumns));

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private double CellWidth(double width, int cols, int index, int count)
    {
        var col = (width - Spacing * (cols - 1)) / cols;
        // The last card of an incomplete row takes the remaining columns.
        var isLast = index == count - 1;
        var used = index % cols;
        var span = isLast ? cols - used : 1;
        return Math.Max(0, col * span + Spacing * (span - 1));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth * MaxColumns : availableSize.Width;
        var cols = Columns(width);
        var items = Visible();
        double height = 0, rowHeight = 0;
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Measure(new Size(CellWidth(width, cols, i, items.Count), double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, items[i].DesiredSize.Height);
            if (i % cols == cols - 1 || i == items.Count - 1)
            {
                height += rowHeight + (height > 0 ? Spacing : 0);
                rowHeight = 0;
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var cols = Columns(width);
        var items = Visible();
        var colWidth = (width - Spacing * (cols - 1)) / cols;
        var y = 0.0;
        for (var start = 0; start < items.Count; start += cols)
        {
            var row = items.Skip(start).Take(cols).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var j = 0; j < row.Count; j++)
            {
                var index = start + j;
                row[j].Arrange(new Rect(j * (colWidth + Spacing), y, CellWidth(width, cols, index, items.Count), rowHeight));
            }
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
