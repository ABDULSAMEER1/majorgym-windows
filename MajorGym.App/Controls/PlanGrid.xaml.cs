using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MajorGym.Data;

namespace MajorGym.App.Controls;

/// <summary>Android's <c>PlanGrid</c>: the plan options (1 / 3 / 6 / 12 Months, in
/// <see cref="DateUtils.PlanMonths"/> order) as a two-column grid of tappable tiles, the selected
/// one filled with the accent colour. <see cref="SelectedPlan"/> is two-way bindable, so
/// choosing a tile immediately updates every dependent value (expiry projection, etc.).</summary>
public partial class PlanGrid : UserControl
{
    public static readonly DependencyProperty SelectedPlanProperty = DependencyProperty.Register(
        nameof(SelectedPlan), typeof(string), typeof(PlanGrid),
        new FrameworkPropertyMetadata("1 Month", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((PlanGrid)d).Refresh()));

    public string SelectedPlan { get => (string)GetValue(SelectedPlanProperty); set => SetValue(SelectedPlanProperty, value); }

    private readonly Dictionary<string, (Border Tile, TextBlock Text)> _tiles = new();

    public PlanGrid()
    {
        InitializeComponent();
        foreach (var plan in DateUtils.PlanMonths.Keys)
        {
            var text = new TextBlock
            {
                Text = plan, FontSize = 13, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var tile = new Border
            {
                Child = text, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 12, 0, 12), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand
            };
            var captured = plan;
            tile.MouseLeftButtonUp += (_, _) => SelectedPlan = captured;
            _tiles[plan] = (tile, text);
            Grid.Children.Add(tile);
        }
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (plan, (tile, text)) in _tiles)
        {
            var active = plan == SelectedPlan;
            tile.Background = (System.Windows.Media.Brush)FindResource(active ? "GymAccent" : "GymSurfaceCard");
            tile.BorderBrush = (System.Windows.Media.Brush)FindResource(active ? "GymAccent" : "GymBorder");
            text.Foreground = active ? System.Windows.Media.Brushes.Black : (System.Windows.Media.Brush)FindResource("GymTextMuted");
        }
    }
}
