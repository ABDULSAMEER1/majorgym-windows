using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MajorGym.Data;

namespace MajorGym.App.Controls;

public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(MemberStatus), typeof(StatusBadge), new PropertyMetadata(MemberStatus.ACTIVE, (d, _) => ((StatusBadge)d).Refresh()));

    public MemberStatus Status { get => (MemberStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public StatusBadge()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        if (Pill is null) return;
        var color = StatusRing.ColorOf(Status);
        Label.Text = Status switch
        {
            MemberStatus.ACTIVE => "ACTIVE",
            MemberStatus.EXPIRING => "EXPIRING SOON",
            _ => "EXPIRED"
        };
        Label.Foreground = new SolidColorBrush(color);
        Pill.Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));   // 15%
        Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));  // 40%
    }
}
