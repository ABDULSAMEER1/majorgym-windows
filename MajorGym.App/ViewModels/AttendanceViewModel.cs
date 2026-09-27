using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MajorGym.App.Navigation;
using MajorGym.Data;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's Attendance screen. Shows the single, static, gym-wide attendance
/// QR (<see cref="QrUtils.GymAttendanceCode"/> — never rotated, unlike a member's own QR;
/// see QrUtils.cs's class doc for the compatibility contract this code must not change)
/// that members/clients scan to check in, plus navigation into the Attendance Logs and
/// Attendance History screens. This screen itself does not record attendance — the actual
/// check-in write path is the fingerprint kiosk loop (<see cref="MajorGym.Kiosk.FingerprintKioskLoop"/>)
/// and/or whatever consumes this QR on the client-app side; this screen is purely the
/// gym-facing display + navigation surface, matching what Stage 1's audit found the
/// Android Attendance screen actually does (no separate "manual attendance" write path
/// exists anywhere else in this codebase to preserve).
/// </summary>
public sealed class AttendanceViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;

    public BitmapImage QrImage { get; }
    public string GymName => QrUtils.GymName;

    public ICommand GoLogsCommand { get; }
    public ICommand BackCommand { get; }

    public AttendanceViewModel(NavigationViewModel nav)
    {
        _nav = nav;
        QrImage = BitmapImageUtils.FromGdiBitmap(QrUtils.GymQrBitmap(QrUtils.GymAttendanceCode));

        GoLogsCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.AttendanceLogs()));
        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
