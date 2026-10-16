using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PcCleaner.Modules.Dashboard;

/// <summary>
/// Circular 0–100 gauge: a background ring plus an arc that animates to <see cref="Value"/>.
/// <see cref="DisplayValue"/> is the animated value (bind the center number to it to make it count up).
/// </summary>
public sealed class HealthGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(HealthGauge), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(HealthGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(HealthGauge),
        new FrameworkPropertyMetadata(Brushes.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(HealthGauge),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(HealthGauge),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double DisplayValue
    {
        get => (double)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (HealthGauge)d;
        var to = Math.Clamp(e.NewValue is double v && !double.IsNaN(v) ? v : 0, 0, 100);
        if (!gauge.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            gauge.BeginAnimation(DisplayValueProperty, null);
            gauge.DisplayValue = to;
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        gauge.BeginAnimation(DisplayValueProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 140 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 140 : availableSize.Height;
        var size = Math.Min(w, h);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var thickness = Math.Max(1, Thickness);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= thickness * 2)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - thickness) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);

        var value = Math.Clamp(DisplayValue, 0, 100);
        if (value <= 0.05)
            return;

        var pen = new Pen(RingBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 99.95)
        {
            dc.DrawEllipse(null, new Pen(RingBrush, thickness), center, radius, radius);
            return;
        }

        var sweep = 360.0 * value / 100.0;
        var start = new Point(center.X, center.Y - radius);
        var radians = (sweep - 90) * Math.PI / 180;
        var end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
