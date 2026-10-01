using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MajorGym.App.ViewModels;

namespace MajorGym.App.Views;

public partial class AttendanceLogsView : UserControl
{
    public AttendanceLogsView()
    {
        InitializeComponent();
    }

    // The screen only listens for live check-ins while it is actually on screen.
    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as AttendanceLogsViewModel)?.Activate();
    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as AttendanceLogsViewModel)?.Deactivate();

    // WPF's Calendar keeps mouse capture after a month/year drill-down, swallowing the next click
    // outside it (a known Calendar-in-Popup quirk) — release it so the calendar keeps working.
    // Clicking a day also closes the picker (covers re-clicking the already-selected date, which
    // raises no SelectedDatesChanged and therefore never reached the ViewModel's close logic).
    private void OnCalendarPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured is CalendarItem) Mouse.Capture(null);

        if (FindAncestor<System.Windows.Controls.Primitives.CalendarDayButton>(e.OriginalSource as DependencyObject) is not null &&
            DataContext is AttendanceLogsViewModel vm)
        {
            // Deferred so the selection binding has applied before the popup goes away.
            Dispatcher.BeginInvoke(new Action(() => vm.IsDatePickerOpen = false), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // Click-away for the two popups (they are StaysOpen="True", see the XAML). A click anywhere that
    // is not inside the popup itself, nor on the control that toggles it, closes it. Clicks on the
    // toggling controls are left alone so their own command (open / toggle) stays in charge.
    private void OnViewPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AttendanceLogsViewModel vm) return;
        var source = e.OriginalSource as DependencyObject;

        if (vm.IsDatePickerOpen &&
            !IsWithin(source, DatePopup.Child) && !IsWithin(source, DateTile) && !IsWithin(source, HeaderDateButton))
            vm.IsDatePickerOpen = false;

        if (vm.IsFilterMenuOpen &&
            !IsWithin(source, FilterPopup.Child) && !IsWithin(source, FilterTile))
            vm.IsFilterMenuOpen = false;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject? container)
    {
        if (container is null) return false;
        while (node is not null)
        {
            if (ReferenceEquals(node, container)) return true;
            // Run/Hyperlink etc. are not Visuals — step through the logical tree for those.
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
