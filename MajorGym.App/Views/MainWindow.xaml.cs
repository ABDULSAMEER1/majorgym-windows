using System.Windows;
using MajorGym.App.Navigation;

namespace MajorGym.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Nav;
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e) =>
        App.Nav.NavigateTo(new Screen.Dashboard());

    private void NavAttendanceLogs_Click(object sender, RoutedEventArgs e) =>
        App.Nav.NavigateTo(new Screen.AttendanceLogs());

    private void NavBackup_Click(object sender, RoutedEventArgs e) =>
        App.Nav.NavigateTo(new Screen.Backup());
}
