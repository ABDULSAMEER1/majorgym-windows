using System.Windows;
using System.Windows.Media;

namespace MajorGym.App.Controls;

/// <summary>Circular progress ring used for the attendance percentage on the Member History hero
/// card (Android AdHeroCard's arc: dark track + rounded-cap progress arc starting at 12 o'clock).</summary>
public sealed class ArcProgress : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcProgress),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcProgress),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ArcProgress),
        new FrameworkPropertyMetadata(Brushes.DarkSlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(ArcProgress),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–100.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var t = Thickness;
        var r = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(null, new Pen(TrackBrush, t), c, r, r);

        var frac = Math.Clamp(Value / 100.0, 0.0, 1.0);
        if (frac <= 0) return;
        if (frac >= 0.9999)
        {
            dc.DrawEllipse(null, new Pen(ProgressBrush, t), c, r, r);
            return;
        }

        var start = -Math.PI / 2;
        var end = start + 2 * Math.PI * frac;
        var p0 = new Point(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start));
        var p1 = new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(ProgressBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}
