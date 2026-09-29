using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MajorGym.App.Controls;

public partial class ProfileRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ProfileRow), new PropertyMetadata("", (d, e) => ((ProfileRow)d).LabelText.Text = (string)e.NewValue));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ProfileRow), new PropertyMetadata("", (d, e) => ((ProfileRow)d).ValueText.Text = (string)e.NewValue));
    public static readonly DependencyProperty IsLastProperty = DependencyProperty.Register(
        nameof(IsLast), typeof(bool), typeof(ProfileRow), new PropertyMetadata(false, (d, e) => ((ProfileRow)d).Divider.Visibility = (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible));
    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(ProfileRow), new PropertyMetadata(null, (d, e) => { if (e.NewValue is Brush b) ((ProfileRow)d).ValueText.Foreground = b; }));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsLast { get => (bool)GetValue(IsLastProperty); set => SetValue(IsLastProperty, value); }
    public Brush? ValueBrush { get => (Brush?)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    public ProfileRow() => InitializeComponent();
}
