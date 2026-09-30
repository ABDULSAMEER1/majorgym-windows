using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MajorGym.App.Navigation;
using MajorGym.Data;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Phase 2 port of Android's AttendanceScreen + GymAttendanceQrCard. Android's screen is
/// exactly: a back arrow, the "Attendance" title, and the QR card (fixed gym QR, "Display"
/// fullscreen dialog, "Share" image). The QR payload is <see cref="QrUtils.GymAttendanceCode"/>
/// ("MAJOR_GYM_ATTENDANCE_2026", verified against Android's QrUtils.GYM_ATTENDANCE_CODE), rendered at
/// Android's 512x512 with ZXing's default 4-module quiet zone.
///
/// The attendance LOGS screen is not reached from here: on Android the bottom-nav "Attendance"
/// slot opens the Logs screen and this scanner screen is opened from the Backup screen's
/// "Open Attendance Scanner" card (see MainWindow nav and BackupViewModel).
/// </summary>
public sealed class AttendanceViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;
    private bool _isFullscreen;
    private string? _shareMessage;

    public BitmapImage QrImage { get; }

    public bool IsFullscreen { get => _isFullscreen; private set { _isFullscreen = value; OnPropertyChanged(); } }
    public string? ShareMessage { get => _shareMessage; private set { _shareMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasShareMessage)); } }
    public bool HasShareMessage => !string.IsNullOrEmpty(_shareMessage);

    public ICommand BackCommand { get; }
    public ICommand DisplayCommand { get; }
    public ICommand CloseFullscreenCommand { get; }
    public ICommand ShareCommand { get; }

    public AttendanceViewModel(NavigationViewModel nav)
    {
        _nav = nav;
        using var bmp = QrUtils.GymQrBitmap(QrUtils.GymAttendanceCode, 512);
        QrImage = BitmapImageUtils.FromGdiBitmap(bmp);

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));
        DisplayCommand = new RelayCommand(() => IsFullscreen = true);
        CloseFullscreenCommand = new RelayCommand(() => IsFullscreen = false);
        ShareCommand = new RelayCommand(Share);
    }

    /// <summary>Android QrShareUtils.shareQrImage: writes a PNG into a cache "shared_qr" folder and
    /// hands it to the share sheet. Windows equivalent: same PNG in the same-named cache folder,
    /// copied to the clipboard as an image and highlighted in Explorer.</summary>
    private void Share()
    {
        try
        {
            var dir = Path.Combine(App.AppDataDirectory, "cache", "shared_qr");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "gym_attendance_qr.png");
            using (var bmp = QrUtils.GymQrBitmap(QrUtils.GymAttendanceCode, 512)) bmp.Save(file, ImageFormat.Png);

            try { Clipboard.SetImage(QrImage); } catch { /* clipboard busy */ }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            ShareMessage = "QR image copied to the clipboard and saved as gym_attendance_qr.png.";
        }
        catch (Exception e)
        {
            ShareMessage = $"Couldn't share the QR image: {e.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
