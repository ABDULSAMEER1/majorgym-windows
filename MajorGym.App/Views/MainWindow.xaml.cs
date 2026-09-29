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

    private void NavAttendance_Click(object sender, RoutedEventArgs e) =>
        App.Nav.NavigateTo(new Screen.Attendance());

    private void NavAdd_Click(object sender, RoutedEventArgs e) =>
        App.Nav.NavigateTo(new Screen.Add());

    private void NavBackup_Click(object sender, RoutedEventArgs e) =>
        App.Nav.NavigateTo(new Screen.Backup());
}
