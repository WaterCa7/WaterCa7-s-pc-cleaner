using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

public enum CardActionKind
{
    Normal,
    Primary,
    Danger,
}

/// <summary>A button on a check card.</summary>
public sealed class CardAction
{
    public CardAction(string label, ICommand command, CardActionKind kind = CardActionKind.Normal, string? toolTip = null,
        object? parameter = null)
    {
        Label = label;
        Command = command;
        Kind = kind;
        ToolTip = toolTip;
        Parameter = parameter;
    }

    public string Label { get; }
    public ICommand Command { get; }
    public CardActionKind Kind { get; }
    public string? ToolTip { get; }
    public object? Parameter { get; }
}

/// <summary>One status card in the checks grid.</summary>
public sealed class SecurityCheckCard
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required SecurityStatus Status { get; init; }
    public required string State { get; init; }
    public string Details { get; init; } = "";
    public string Explanation { get; init; } = "";
    public IReadOnlyList<CardAction> Actions { get; init; } = Array.Empty<CardAction>();

    /// <summary>Number of grid columns the card spans (the antivirus card is wide).</summary>
    public int ColumnSpan { get; init; } = 1;

    /// <summary>A card that is fine: the details text is shown only in a tooltip.</summary>
    public bool IsCompact { get; init; }

    public bool ShowDetails => !IsCompact && !string.IsNullOrWhiteSpace(Details);

    /// <summary>
    /// Tooltip of a compact card: its details, then what the check is about. A compact card takes keyboard focus (Tab)
    /// and WPF shows the tooltip on keyboard focus too, so the details are reachable without a mouse.
    /// Cards with problems show their details on the card and have no card tooltip.
    /// </summary>
    public string? CardToolTip
    {
        get
        {
            if (!IsCompact)
                return null;
            var parts = new[] { Details, Explanation }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
    }

    public string StatusText => Status.ToRussian();
}

/// <summary>Picks the button style of a <see cref="CardAction"/>.</summary>
public sealed class CardActionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Normal { get; set; }
    public DataTemplate? Primary { get; set; }
    public DataTemplate? Danger { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        CardAction { Kind: CardActionKind.Primary } => Primary,
        CardAction { Kind: CardActionKind.Danger } => Danger,
        _ => Normal,
    };
}

public enum StatusConverterMode
{
    Brush,
    SoftBrush,
    Glyph,
    Text,
}

/// <summary>SecurityStatus → theme brush / soft brush / Segoe glyph / Russian text.</summary>
public sealed class SecurityStatusConverter : IValueConverter
{
    public StatusConverterMode Mode { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is SecurityStatus s ? s : SecurityStatus.Unknown;
        return Mode switch
        {
            StatusConverterMode.Glyph => status switch
            {
                SecurityStatus.Ok => "",
                SecurityStatus.Info => "",
                SecurityStatus.Warning => "",
                SecurityStatus.Danger => "",
                _ => "",
            },
            StatusConverterMode.Text => status.ToRussian(),
            StatusConverterMode.SoftBrush => Find(status switch
            {
                SecurityStatus.Ok => "AccentSoftBrush",
                SecurityStatus.Info => "InfoSoftBrush",
                SecurityStatus.Warning => "WarningSoftBrush",
                SecurityStatus.Danger => "DangerSoftBrush",
                _ => "Surface3Brush",
            }),
            _ => Find(status switch
            {
                SecurityStatus.Ok => "SuccessBrush",
                SecurityStatus.Info => "InfoBrush",
                SecurityStatus.Warning => "WarningBrush",
                SecurityStatus.Danger => "DangerBrush",
                _ => "MutedBrush",
            }),
        };
    }

    private static Brush Find(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Responsive card grid: as many columns as fit (each at least <see cref="MinColumnWidth"/>), items flow left to right,
/// every row is as tall as its tallest card. Items can span several columns via the attached ColumnSpan.
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached(
        "ColumnSpan", typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

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

    public static int GetColumnSpan(DependencyObject d) => (int)d.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(DependencyObject d, int value) => d.SetValue(ColumnSpanProperty, value);

    private int ColumnsFor(double width)
        => Math.Clamp((int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing)), 1, Math.Max(1, MaxColumns));

    private double CellWidth(double width, int cols, int span)
    {
        var col = (width - Spacing * (cols - 1)) / cols;
        return Math.Max(0, col * span + Spacing * (span - 1));
    }

    /// <summary>Places children into rows: returns (row, column, span) per visible child.</summary>
    private List<(UIElement Child, int Row, int Col, int Span)> Place(int cols)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        return Pack(children.Select(GetColumnSpan).ToList(), cols)
            .Select(p => (children[p.Index], p.Row, p.Col, p.Span))
            .ToList();
    }

    /// <summary>
    /// Packs items with the given column spans into rows without holes: when a wide item does not fit into the rest
    /// of a row, the next items that fit are moved up into it; if nothing fits, the row's last item is stretched.
    /// Returns (index of the item, row, column, span) in placement order.
    /// </summary>
    internal static List<(int Index, int Row, int Col, int Span)> Pack(IReadOnlyList<int> spans, int cols)
    {
        cols = Math.Max(1, cols);
        var result = new List<(int Index, int Row, int Col, int Span)>();
        var placed = new bool[spans.Count];
        int row = 0, col = 0;

        int SpanOf(int i) => Math.Clamp(spans[i], 1, cols);

        void EndRow()
        {
            if (col > 0 && col < cols)
            {
                var last = result[^1];
                result[^1] = (last.Index, last.Row, last.Col, last.Span + (cols - col));
            }
            row++;
            col = 0;
        }

        for (var i = 0; i < spans.Count; i++)
        {
            if (placed[i])
                continue;
            var span = SpanOf(i);
            if (col + span > cols)
            {
                for (var j = i + 1; j < spans.Count && col < cols; j++)
                {
                    if (placed[j] || col + SpanOf(j) > cols)
                        continue;
                    result.Add((j, row, col, SpanOf(j)));
                    placed[j] = true;
                    col += SpanOf(j);
                }
                EndRow();
            }
            result.Add((i, row, col, span));
            placed[i] = true;
            col += span;
            if (col >= cols)
                EndRow();
        }
        if (col > 0)
            EndRow();
        return result;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? MinColumnWidth * MaxColumns + Spacing * (MaxColumns - 1)
            : availableSize.Width;
        var cols = ColumnsFor(width);
        var placed = Place(cols);
        var rowHeights = new Dictionary<int, double>();
        foreach (var (child, row, _, span) in placed)
        {
            child.Measure(new Size(CellWidth(width, cols, span), double.PositiveInfinity));
            rowHeights[row] = Math.Max(rowHeights.GetValueOrDefault(row), child.DesiredSize.Height);
        }
        var height = rowHeights.Values.Sum() + Spacing * Math.Max(0, rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var cols = ColumnsFor(width);
        var placed = Place(cols);
        var rowHeights = new Dictionary<int, double>();
        foreach (var (child, row, _, _) in placed)
            rowHeights[row] = Math.Max(rowHeights.GetValueOrDefault(row), child.DesiredSize.Height);

        var colWidth = (width - Spacing * (cols - 1)) / cols;
        var y = 0.0;
        var currentRow = 0;
        foreach (var (child, row, col, span) in placed)
        {
            while (currentRow < row)
            {
                y += rowHeights.GetValueOrDefault(currentRow) + Spacing;
                currentRow++;
            }
            var x = col * (colWidth + Spacing);
            child.Arrange(new Rect(x, y, CellWidth(width, cols, span), rowHeights.GetValueOrDefault(row)));
        }
        return finalSize;
    }
}
