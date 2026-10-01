using System.Windows;
using System.Windows.Media;

namespace MajorGym.App.Controls;

/// <summary>
/// The bottom-navigation dock's background/outline — Windows port of Android's <c>DockShape</c> +
/// dock surface (Screens.kt, BottomNav): edge-to-edge dark-navy fill, rounded top corners (26),
/// and a smooth arch rising around the centred Add button (arch half-width 50, flat half-width 16,
/// 26 tall), with the soft glow strokes (9/5/2.5 at alpha .06/.10/.18) and the 1.5 gradient border
/// (violet → blue → cyan → blue → violet). Every number is copied from the Android source.
/// </summary>
public sealed class BottomDock : FrameworkElement
{
    private const double CornerRadius = 26, BarTop = 26, ArchHalfWidth = 50, ArchFlatHalfWidth = 16;

    private static readonly Brush NavBg = Freeze(new LinearGradientBrush(
        Color.FromArgb(0xF2, 0x0A, 0x14, 0x34), Color.FromArgb(0xFA, 0x05, 0x0A, 0x1C), 90));

    private static LinearGradientBrush NavBorder(double opacity) => Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x7B, 0x5C, 0xFF), 0.00),
            new(Color.FromRgb(0x2F, 0x80, 0xFF), 0.25),
            new(Color.FromRgb(0x3F, 0xD0, 0xFF), 0.50),
            new(Color.FromRgb(0x2F, 0x80, 0xFF), 0.75),
            new(Color.FromRgb(0x7B, 0x5C, 0xFF), 1.00)
        }, new Point(0, 0), new Point(1, 0)) { Opacity = opacity });

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double r = CornerRadius, top = BarTop, hw = ArchHalfWidth, fw = ArchFlatHalfWidth;
        double cx = w / 2, k = Math.Max(hw - fw, 1) * 0.55;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, top + r), true, true);
            c.ArcTo(new Point(r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(cx - hw, top), true, false);
            c.BezierTo(new Point(cx - hw + k, top), new Point(cx - fw - k, 0), new Point(cx - fw, 0), true, false);
            c.LineTo(new Point(cx + fw, 0), true, false);
            c.BezierTo(new Point(cx + fw + k, 0), new Point(cx + hw - k, top), new Point(cx + hw, top), true, false);
            c.LineTo(new Point(w - r, top), true, false);
            c.ArcTo(new Point(w, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(w, h), true, false);
            c.LineTo(new Point(0, h), true, false);
        }
        g.Freeze();

        dc.DrawGeometry(NavBg, null, g);
        foreach (var (width, alpha) in new[] { (9.0, 0.06), (5.0, 0.10), (2.5, 0.18) })
            dc.DrawGeometry(null, new Pen(NavBorder(alpha), width), g);
        dc.DrawGeometry(null, new Pen(NavBorder(1.0), 1.5), g);
    }
}
