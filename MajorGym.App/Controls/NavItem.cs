using System.Windows;

namespace MajorGym.App.Controls;

/// <summary>Attached "this bottom-nav item is the lit one" flag (Android BottomNav's
/// <c>active</c>), bound one-way to <see cref="Navigation.NavigationViewModel.SelectedNav"/> so a
/// click can never overwrite the binding the way toggling a ToggleButton would.</summary>
public static class NavItem
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(NavItem), new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject d, bool v) => d.SetValue(IsActiveProperty, v);
}
