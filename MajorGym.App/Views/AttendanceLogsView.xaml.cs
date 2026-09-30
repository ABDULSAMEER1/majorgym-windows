using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    // outside it (a known Calendar-in-Popup quirk) — release it so the popup closes/selects normally.
    private void OnCalendarPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured is CalendarItem) Mouse.Capture(null);
    }
}
