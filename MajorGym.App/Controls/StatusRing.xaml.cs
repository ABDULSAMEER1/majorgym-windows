using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MajorGym.Data;

namespace MajorGym.App.Controls;

public partial class StatusRing : UserControl
{
    public static readonly DependencyProperty PhotoPathProperty = DependencyProperty.Register(
        nameof(PhotoPath), typeof(string), typeof(StatusRing), new PropertyMetadata(null, (d, _) => ((StatusRing)d).Refresh()));
    public static readonly DependencyProperty MemberNameProperty = DependencyProperty.Register(
        nameof(MemberName), typeof(string), typeof(StatusRing), new PropertyMetadata("", (d, _) => ((StatusRing)d).Refresh()));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(MemberStatus), typeof(StatusRing), new PropertyMetadata(MemberStatus.ACTIVE, (d, _) => ((StatusRing)d).Refresh()));
    public static readonly DependencyProperty RingSizeProperty = DependencyProperty.Register(
        nameof(RingSize), typeof(double), typeof(StatusRing), new PropertyMetadata(56.0, (d, _) => ((StatusRing)d).Refresh()));

    public string? PhotoPath { get => (string?)GetValue(PhotoPathProperty); set => SetValue(PhotoPathProperty, value); }
    public string MemberName { get => (string)GetValue(MemberNameProperty); set => SetValue(MemberNameProperty, value); }
    public MemberStatus Status { get => (MemberStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public double RingSize { get => (double)GetValue(RingSizeProperty); set => SetValue(RingSizeProperty, value); }

    public StatusRing()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Android's status colour mapping: ACTIVE green, EXPIRING amber, EXPIRED red.</summary>
    public static Color ColorOf(MemberStatus status) => status switch
    {
        MemberStatus.ACTIVE => Color.FromRgb(0x10, 0xB9, 0x81),
        MemberStatus.EXPIRING => Color.FromRgb(0xF5, 0x9E, 0x0B),
        _ => Color.FromRgb(0xEF, 0x44, 0x44)
    };

    private void Refresh()
    {
        if (Root is null) return;
        Width = RingSize;
        Height = RingSize;
        var color = ColorOf(Status);
        Ring.Stroke = new SolidColorBrush(color);
        Root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.30
        };

        var image = BitmapImageUtils.LoadFromFile(PhotoPath, (int)Math.Ceiling(RingSize * 2));
        if (image is not null)
        {
            Photo.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            Photo.Visibility = Visibility.Visible;
            Initials.Visibility = Visibility.Collapsed;
        }
        else
        {
            Photo.Visibility = Visibility.Collapsed;
            Initials.Visibility = Visibility.Visible;
            // name.split(" ").mapNotNull { it.firstOrNull() }.take(2).joinToString("").uppercase()
            var parts = (MemberName ?? "").Split(' ');
            Initials.Text = new string(parts.Where(p => p.Length > 0).Select(p => p[0]).Take(2).ToArray()).ToUpperInvariant();
            Initials.FontSize = Math.Max(11, RingSize * 0.30);
        }
    }
}
