using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

public sealed class AttendanceHistoryRow
{
    public required string Date { get; init; }
    public required string TimeOfDay { get; init; }
    public required string Session { get; init; }
}

/// <summary>
/// Ported from Android's Member Attendance History screen. The percentage window runs
/// from the member's CURRENT membership-cycle start date (the most recent HistoryEntry's
/// DateMillis — i.e. the last time they joined/renewed — falling back to JoinedMillis for a
/// brand-new member with no history entries yet) through today inclusive, exactly as
/// <see cref="DateUtils.EligibleAttendanceDays"/>/<see cref="DateUtils.AttendancePercentage"/>
/// document. This deliberately does NOT reset per calendar month or renewal-to-renewal
/// segment beyond the current cycle — Android only ever shows the current cycle's figure.
/// </summary>
public sealed class AttendanceHistoryViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;

    public Member Member { get; }
    public BitmapImage? Photo { get; }
    public ObservableCollection<AttendanceHistoryRow> Rows { get; } = new();

    public int AttendancePercentage { get; }
    public int AttendedDays { get; }
    public int EligibleDays { get; }
    public string MembershipStartLabel { get; }

    public ICommand BackCommand { get; }

    public AttendanceHistoryViewModel(Repository repository, NavigationViewModel nav, Member member)
    {
        _nav = nav;
        Member = member;

        if (member.PhotoPath is { Length: > 0 } photoPath && File.Exists(photoPath))
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(photoPath);
            img.EndInit();
            img.Freeze();
            Photo = img;
        }

        var history = History.ToHistoryList(member.HistoryJson);
        var startMillis = history.Count > 0 ? history[^1].DateMillis : member.JoinedMillis;
        MembershipStartLabel = DateUtils.FormatDate(startMillis);

        var startEpoch = DateUtils.ToMillis(DateUtils.ToLocalDate(startMillis));
        var todayEpoch = DateUtils.ToMillis(DateOnly.FromDateTime(DateTime.Now));
        EligibleDays = DateUtils.EligibleAttendanceDays(startMillis);
        AttendedDays = repository.GetDistinctAttendedDayCount(member.Id, startEpoch, todayEpoch);
        AttendancePercentage = DateUtils.AttendancePercentage(AttendedDays, EligibleDays);

        foreach (var rec in repository.GetAttendanceForMember(member.Id))
        {
            Rows.Add(new AttendanceHistoryRow
            {
                Date = DateUtils.FormatDate(rec.TimestampMillis),
                TimeOfDay = DateUtils.FormatTimeOfDay(rec.TimestampMillis),
                Session = rec.Session
            });
        }

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(member.Id)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
