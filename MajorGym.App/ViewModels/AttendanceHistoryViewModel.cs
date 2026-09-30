using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>One visit row: "20 | Sep 2026", tick, time, "Morning"/"Evening" (Android AdHistoryRow).</summary>
public sealed record AttendanceHistoryRow(string Day, string Month, string Year, string TimeText, string SessionLabel);

/// <summary>A calendar-month section, newest month first (Android AdMonthHeader + its rows).</summary>
public sealed record AttendanceMonthGroup(string Header, IReadOnlyList<AttendanceHistoryRow> Rows);

/// <summary>
/// Phase 2 port of Android's AttendanceHistoryScreen ("MEMBER PROFILE"). Figures follow the
/// Android source exactly:
///  - attendedDays = distinct dayEpoch across ALL retained records of the member;
///  - eligibleDays = DateUtils.EligibleAttendanceDays(member.joinedMillis) — the ORIGINAL join
///    date: renewal on Android never changes joinedMillis, so the window does not restart;
///  - percentage = DateUtils.AttendancePercentage(attended, eligible) (Sundays excluded there);
///  - "N Days Left" / "Expired" from DaysBetweenNow(expiryMillis);
///  - visits grouped by calendar month, newest first, newest visit first inside a month.
/// Back returns to the Attendance Logs list (Android onBack = Screen.AttendanceLogs).
/// </summary>
public sealed class AttendanceHistoryViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;

    public Member Member { get; }
    public BitmapImage? Photo { get; }
    public bool HasPhoto => Photo is not null;
    public string Initials { get; }

    public MemberStatus Status { get; }
    public string StatusLabel { get; }
    public string StatusGlyph { get; }
    public Brush StatusBrush { get; }
    public Brush StatusTint { get; }

    public int AttendancePercentage { get; }
    public double PercentValue => AttendancePercentage;
    public string PercentText => $"{AttendancePercentage}%";
    public string DaysLeftLabel { get; }

    public string PlanText { get; }
    public string StartDateText { get; }
    public string ExpiryDateText { get; }

    public int AttendedDays { get; }
    public int EligibleDays { get; }
    public string AttendedText => $"{AttendedDays} / {EligibleDays} Days";
    public GridLength ProgressFilled { get; }
    public GridLength ProgressRest { get; }

    public ObservableCollection<AttendanceMonthGroup> Months { get; } = new();
    public bool HasNoRecords => Months.Count == 0;

    public ICommand BackCommand { get; }

    public AttendanceHistoryViewModel(Repository repository, NavigationViewModel nav, Member member)
    {
        _nav = nav;
        Member = member;
        Photo = BitmapImageUtils.LoadFromFile(member.PhotoPath, 256);
        Initials = string.Concat(member.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p[0]).Take(2)).ToUpperInvariant();

        Status = MemberStatusExtensions.StatusOf(member.ExpiryMillis);
        Brush statusBrush;
        switch (Status)
        {
            case MemberStatus.ACTIVE:
                StatusLabel = "ACTIVE"; StatusGlyph = "\uE73E"; statusBrush = Res("GymSuccess"); break;
            case MemberStatus.EXPIRING:
                StatusLabel = "EXPIRING SOON"; StatusGlyph = "\uE823"; statusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)); break;
            default:
                StatusLabel = "EXPIRED"; StatusGlyph = "\uE711"; statusBrush = Res("GymDanger"); break;
        }
        StatusBrush = statusBrush;
        StatusBrush.Freeze();
        var c = ((SolidColorBrush)StatusBrush).Color;
        StatusTint = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        StatusTint.Freeze();

        var records = repository.GetAttendanceForMember(member.Id); // newest first
        AttendedDays = records.Select(r => r.DayEpoch).Distinct().Count();
        EligibleDays = DateUtils.EligibleAttendanceDays(member.JoinedMillis);
        AttendancePercentage = DateUtils.AttendancePercentage(AttendedDays, EligibleDays);

        var days = DateUtils.DaysBetweenNow(member.ExpiryMillis);
        DaysLeftLabel = days < 0 ? "Expired" : $"{days} Days Left";

        PlanText = member.Plan;
        StartDateText = DateUtils.FormatDate(member.JoinedMillis);
        ExpiryDateText = DateUtils.FormatDate(member.ExpiryMillis);

        var fraction = EligibleDays > 0 ? Math.Clamp(AttendedDays / (double)EligibleDays, 0.0, 1.0) : 0.0;
        ProgressFilled = new GridLength(fraction, GridUnitType.Star);
        ProgressRest = new GridLength(1.0 - fraction, GridUnitType.Star);

        foreach (var group in records.GroupBy(r =>
        {
            var d = DateUtils.ToLocalDate(r.TimestampMillis);
            return (d.Year, d.Month);
        }))
        {
            var header = new DateTime(group.Key.Year, group.Key.Month, 1)
                .ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-US"));
            var rows = group.Select(r =>
            {
                var parts = DateUtils.FormatDate(r.TimestampMillis).Split(' ');
                return new AttendanceHistoryRow(
                    parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", parts.ElementAtOrDefault(2) ?? "",
                    DateUtils.FormatTimeOfDay(r.TimestampMillis),
                    r.Session == AttendanceSession.MORNING.ToString() ? "Morning" : "Evening");
            }).ToList();
            Months.Add(new AttendanceMonthGroup(header, rows));
        }

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.AttendanceLogs()));
    }

    private static Brush Res(string key)
    {
        var b = (SolidColorBrush)Application.Current.FindResource(key);
        return new SolidColorBrush(b.Color);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
