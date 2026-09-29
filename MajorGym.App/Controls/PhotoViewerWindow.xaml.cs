using System.Windows;
using System.Windows.Input;

namespace MajorGym.App.Controls;

public partial class PhotoViewerWindow : Window
{
    private PhotoViewerWindow(string? path, string title)
    {
        InitializeComponent();
        TitleText.Text = title;
        Picture.Source = BitmapImageUtils.LoadFromFile(path);
    }

    /// <summary>Shows the full-screen viewer for a member photo / ID proof photo. Does nothing if
    /// there is no photo file to show (Android: the viewer is only reachable by tapping a photo
    /// that exists).</summary>
    public static void ShowFor(string? path, string title)
    {
        if (BitmapImageUtils.LoadFromFile(path) is null) return;
        var w = new PhotoViewerWindow(path, title) { Owner = Application.Current?.MainWindow };
        w.ShowDialog();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Window_Click(object sender, MouseButtonEventArgs e) => Close();
}
