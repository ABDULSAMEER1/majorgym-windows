using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;
using MajorGym.Data.Settings;

namespace MajorGym.App.ViewModels;

public sealed class AttendanceLogRow
{
    public required string MemberName { get; init; }
    public required string TimeOfDay { get; init; }
    public required string Session { get; init; }
}

/// <summary>
/// Ported from Android's Attendance Logs screen: a day picker (defaulting to today, or the
/// most recent day with any recorded attendance if today has none — so the screen never
/// opens to a blank list purely because no one has checked in yet today) plus the day's
/// check-in rows, newest first, and the three independently-hideable counts from
/// <see cref="AttendanceSettingsPrefs"/>. "Present" is the count of distinct members seen
/// that day (a member can check in more than once), Morning/Evening are raw visit counts
/// for that session — see <see cref="AttendanceSessionExtensions.SessionOf"/> for the
/// noon-cutoff rule.
/// </summary>
public sealed class AttendanceLogsViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly AttendanceSettingsPrefs _settings;
    private readonly NavigationViewModel _nav;
    private readonly List<long> _dayEpochs;
    private int _dayIndex;

    public ObservableCollection<AttendanceLogRow> Rows { get; } = new();

    public string DayLabel => _dayEpochs.Count == 0 ? "No attendance recorded yet" : DateUtils.FormatDate(_dayEpochs[_dayIndex]);

    public bool HasDays => _dayEpochs.Count > 0;

    public int PresentCount { get; private set; }
    public int MorningCount { get; private set; }
    public int EveningCount { get; private set; }

    public bool PresentVisible => _settings.IsCountVisible(AttendanceCount.PRESENT);
    public bool MorningVisible => _settings.IsCountVisible(AttendanceCount.MORNING);
    public bool EveningVisible => _settings.IsCountVisible(AttendanceCount.EVENING);

    public bool CanGoNewer => _dayIndex > 0;
    public bool CanGoOlder => _dayIndex < _dayEpochs.Count - 1;

    public ICommand NewerDayCommand { get; }
    public ICommand OlderDayCommand { get; }
    public ICommand TogglePresentCommand { get; }
    public ICommand ToggleMorningCommand { get; }
    public ICommand ToggleEveningCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand BackCommand { get; }

    public AttendanceLogsViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;
        _settings = new AttendanceSettingsPrefs(App.AppDataDirectory);
        _dayEpochs = _repository.GetDistinctAttendanceDayEpochs();
        _dayIndex = 0;

        NewerDayCommand = new RelayCommand(() => { _dayIndex--; LoadDay(); }, () => CanGoNewer);
        OlderDayCommand = new RelayCommand(() => { _dayIndex++; LoadDay(); }, () => CanGoOlder);
        TogglePresentCommand = new RelayCommand(() => { _settings.SetCountVisible(AttendanceCount.PRESENT, !PresentVisible); OnPropertyChanged(nameof(PresentVisible)); });
        ToggleMorningCommand = new RelayCommand(() => { _settings.SetCountVisible(AttendanceCount.MORNING, !MorningVisible); OnPropertyChanged(nameof(MorningVisible)); });
        ToggleEveningCommand = new RelayCommand(() => { _settings.SetCountVisible(AttendanceCount.EVENING, !EveningVisible); OnPropertyChanged(nameof(EveningVisible)); });
        OpenSettingsCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Attendance()));
        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));

        LoadDay();
    }

    private void LoadDay()
    {
        Rows.Clear();
        if (_dayEpochs.Count == 0)
        {
            PresentCount = MorningCount = EveningCount = 0;
        }
        else
        {
            var records = _repository.GetAttendanceForDay(_dayEpochs[_dayIndex]);
            var memberNames = new Dictionary<string, string>();
            foreach (var rec in records)
            {
                if (!memberNames.TryGetValue(rec.MemberId, out var name))
                {
                    name = _repository.GetById(rec.MemberId)?.Name ?? "(deleted member)";
                    memberNames[rec.MemberId] = name;
                }
                Rows.Add(new AttendanceLogRow
                {
                    MemberName = name,
                    TimeOfDay = DateUtils.FormatTimeOfDay(rec.TimestampMillis),
                    Session = rec.Session
                });
            }
            PresentCount = memberNames.Count;
            MorningCount = records.Count(r => r.Session == nameof(AttendanceSession.MORNING));
            EveningCount = records.Count(r => r.Session == nameof(AttendanceSession.EVENING));
        }

        OnPropertyChanged(nameof(DayLabel));
        OnPropertyChanged(nameof(HasDays));
        OnPropertyChanged(nameof(PresentCount));
        OnPropertyChanged(nameof(MorningCount));
        OnPropertyChanged(nameof(EveningCount));
        OnPropertyChanged(nameof(CanGoNewer));
        OnPropertyChanged(nameof(CanGoOlder));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
