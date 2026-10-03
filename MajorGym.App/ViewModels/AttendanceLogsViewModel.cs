using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;
using MajorGym.Data.Settings;
using MajorGym.Kiosk;

namespace MajorGym.App.ViewModels;

/// <summary>Android AttendanceFilter enum (label text verbatim).</summary>
public enum AttendanceFilter { All, Morning, Evening, Active, Expired }

public sealed record AttendanceFilterOption(AttendanceFilter Value, string Label, ICommand Command);

/// <summary>One row of the day list (Android AttendanceRecordCard).</summary>
public sealed class AttendanceEntryViewModel
{
    public required string MemberId { get; init; }
    public required string MemberName { get; init; }
    public required string Phone { get; init; }
    public string? PhotoPath { get; init; }
    public required MemberStatus Status { get; init; }
    public required string StatusLine { get; init; }
    public required string TimeText { get; init; }
    public required string SessionText { get; init; }
    public required ICommand OpenCommand { get; init; }
}

/// <summary>
/// Phase 2 port of Android's AttendanceLogsScreen (verified line by line):
///  - one row per MEMBER per selected day — that member's EARLIEST scan that day
///    (records grouped by memberId, minBy timestamp); records whose member no longer exists
///    are skipped, exactly like Android's membersById lookup;
///  - search matches name (case-insensitive), phone (plain contains) or ID proof (case-insens.);
///  - filter: All / Morning / Evening (by the row's session) / Active (status == ACTIVE only,
///    "expiring" is NOT active) / Expired;
///  - sorted newest scan first; the Present/Morning/Evening counts are taken from the
///    searched+filtered list, and each card's number can be hidden in the settings dialog
///    (the card and its label always stay);
///  - tapping a row opens that member's Attendance History.
/// Android observes Room flows so the list updates live; here a kiosk check-in triggers a
/// refresh (KioskBus.CurrentChanged) while this screen is on top.
/// </summary>
public sealed class AttendanceLogsViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly AttendanceSettingsPrefs _prefs;
    private readonly DispatcherTimer _debounce;
    private readonly Debouncer _queryDebounce;
    private List<(AttendanceRecord Rec, Member Member)> _dayEntries = new();

    private DateTime _selectedDate = DateTime.Today;
    private string _query = "";
    private AttendanceFilter _filter = AttendanceFilter.All;
    private bool _isDatePickerOpen, _isFilterMenuOpen, _isSettingsOpen;
    private string? _presentText, _morningText, _eveningText;
    private string _emptyMessage = "";

    public BulkObservableCollection<AttendanceEntryViewModel> Entries { get; } = new();

    public IReadOnlyList<AttendanceFilterOption> FilterOptions { get; }

    // ---- header ----
    public string DateSubtitle =>
        $"\U0001F4C5 {DateUtils.FormatDate(DateUtils.ToMillis(DateOnly.FromDateTime(_selectedDate)))}" +
        (_selectedDate.Date == DateTime.Today ? " (Today)" : "");
    public string SelectedDateText => DateUtils.FormatDate(DateUtils.ToMillis(DateOnly.FromDateTime(_selectedDate)));
    public string FilterLabel => FilterOptions.First(o => o.Value == _filter).Label;

    /// <summary>Two-way with the calendar popup; picking a date closes the popup.</summary>
    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (value is null || value.Value.Date == _selectedDate.Date) return;
            _selectedDate = value.Value.Date;
            OnPropertyChanged(); OnPropertyChanged(nameof(DateSubtitle)); OnPropertyChanged(nameof(SelectedDateText));
            IsDatePickerOpen = false;
            Refresh();
        }
    }

    public string Query
    {
        get => _query;
        // The box updates instantly; the list re-filters ~150 ms after the user stops typing/deleting. Searching only
        // filters the day's records already in memory - it no longer re-reads the database on every keystroke.
        set { if (_query == value) return; _query = value ?? ""; OnPropertyChanged(); _queryDebounce.Trigger(); }
    }

    public bool IsDatePickerOpen { get => _isDatePickerOpen; set { _isDatePickerOpen = value; OnPropertyChanged(); } }
    public bool IsFilterMenuOpen { get => _isFilterMenuOpen; set { _isFilterMenuOpen = value; OnPropertyChanged(); } }
    public bool IsSettingsOpen { get => _isSettingsOpen; private set { _isSettingsOpen = value; OnPropertyChanged(); } }

    // ---- stat cards (null = number hidden, card + label stay) ----
    public string? PresentText { get => _presentText; private set { _presentText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPresentText)); } }
    public string? MorningText { get => _morningText; private set { _morningText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMorningText)); } }
    public string? EveningText { get => _eveningText; private set { _eveningText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasEveningText)); } }
    public bool HasPresentText => _presentText is not null;
    public bool HasMorningText => _morningText is not null;
    public bool HasEveningText => _eveningText is not null;

    // ---- settings dialog switches (persisted immediately, like Android) ----
    public bool ShowPresent { get => _prefs.IsCountVisible(AttendanceCount.PRESENT); set { _prefs.SetCountVisible(AttendanceCount.PRESENT, value); OnPropertyChanged(); ApplyFilters(); } }
    public bool ShowMorning { get => _prefs.IsCountVisible(AttendanceCount.MORNING); set { _prefs.SetCountVisible(AttendanceCount.MORNING, value); OnPropertyChanged(); ApplyFilters(); } }
    public bool ShowEvening { get => _prefs.IsCountVisible(AttendanceCount.EVENING); set { _prefs.SetCountVisible(AttendanceCount.EVENING, value); OnPropertyChanged(); ApplyFilters(); } }

    public string EmptyMessage { get => _emptyMessage; private set { _emptyMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasEmptyMessage)); } }
    public bool HasEmptyMessage => _emptyMessage.Length > 0;

    public ICommand OpenDatePickerCommand { get; }
    public ICommand ToggleFilterMenuCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }

    public AttendanceLogsViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;
        _prefs = new AttendanceSettingsPrefs(App.AppDataDirectory);

        OpenDatePickerCommand = new RelayCommand(() => IsDatePickerOpen = true);
        ToggleFilterMenuCommand = new RelayCommand(() => IsFilterMenuOpen = !IsFilterMenuOpen);
        AttendanceFilterOption Option(AttendanceFilter f, string label) => new(f, label, new RelayCommand(() =>
        {
            _filter = f; IsFilterMenuOpen = false; OnPropertyChanged(nameof(FilterLabel)); ApplyFilters();
        }));
        FilterOptions = new[]
        {
            Option(AttendanceFilter.All, "All"),
            Option(AttendanceFilter.Morning, "Morning"),
            Option(AttendanceFilter.Evening, "Evening"),
            Option(AttendanceFilter.Active, "Active Members"),
            Option(AttendanceFilter.Expired, "Expired Members")
        };
        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);

        // A check-in publishes to the kiosk bus just BEFORE its attendance row is written, so the
        // refresh is debounced a moment (and runs again on the bus's clear event).
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        _queryDebounce = new Debouncer(ApplyFilters);

        Refresh();
    }

    /// <summary>Called by the view's Loaded/Unloaded so the bus subscription never outlives the screen.</summary>
    public void Activate() { App.KioskLoop.Bus.CurrentChanged += OnKioskChanged; Refresh(); }
    public void Deactivate() { App.KioskLoop.Bus.CurrentChanged -= OnKioskChanged; _debounce.Stop(); _queryDebounce.Cancel(); }

    private void OnKioskChanged(object? sender, KioskEvent? e) =>
        Application.Current.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    public void Refresh()
    {
        var dayEpoch = DateUtils.ToMillis(DateOnly.FromDateTime(_selectedDate));
        var dayRecords = _repository.GetAttendanceForDay(dayEpoch);

        // one entry per member = earliest scan of the day; missing members skipped
        var memberCache = new Dictionary<string, Member?>();
        var dayEntries = new List<(AttendanceRecord Rec, Member Member)>();
        foreach (var group in dayRecords.GroupBy(r => r.MemberId))
        {
            var earliest = group.OrderBy(r => r.TimestampMillis).First();
            if (!memberCache.TryGetValue(group.Key, out var member))
                memberCache[group.Key] = member = _repository.GetByIdForList(group.Key); // display-only (no template decrypt)
            if (member is null) continue;
            dayEntries.Add((earliest, member));
        }

        _dayEntries = dayEntries;
        ApplyFilters();
    }

    /// <summary>Search / filter / counts over the day's records already loaded by <see cref="Refresh"/> (no database access).</summary>
    private void ApplyFilters()
    {
        var dayEntries = _dayEntries;
        IEnumerable<(AttendanceRecord Rec, Member Member)> searched = dayEntries;
        if (!string.IsNullOrWhiteSpace(_query))
        {
            searched = dayEntries.Where(e =>
                e.Member.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                e.Member.Phone.Contains(_query, StringComparison.Ordinal) ||
                e.Member.IdProof.Contains(_query, StringComparison.OrdinalIgnoreCase));
        }

        var morning = AttendanceSession.MORNING.ToString();
        var evening = AttendanceSession.EVENING.ToString();
        IEnumerable<(AttendanceRecord Rec, Member Member)> filtered = _filter switch
        {
            AttendanceFilter.Morning => searched.Where(e => e.Rec.Session == morning),
            AttendanceFilter.Evening => searched.Where(e => e.Rec.Session == evening),
            AttendanceFilter.Active => searched.Where(e => MemberStatusExtensions.StatusOf(e.Member.ExpiryMillis) == MemberStatus.ACTIVE),
            AttendanceFilter.Expired => searched.Where(e => MemberStatusExtensions.StatusOf(e.Member.ExpiryMillis) == MemberStatus.EXPIRED),
            _ => searched
        };
        var list = filtered.OrderByDescending(e => e.Rec.TimestampMillis).ToList();

        PresentText = _prefs.IsCountVisible(AttendanceCount.PRESENT) ? list.Count.ToString() : null;
        MorningText = _prefs.IsCountVisible(AttendanceCount.MORNING) ? list.Count(e => e.Rec.Session == morning).ToString() : null;
        EveningText = _prefs.IsCountVisible(AttendanceCount.EVENING) ? list.Count(e => e.Rec.Session == evening).ToString() : null;

        var rows = new List<AttendanceEntryViewModel>(list.Count);
        foreach (var (rec, member) in list)
        {
            var status = MemberStatusExtensions.StatusOf(member.ExpiryMillis);
            var id = member.Id;
            rows.Add(new AttendanceEntryViewModel
            {
                MemberId = id,
                MemberName = member.Name,
                Phone = member.Phone,
                PhotoPath = member.PhotoPath,
                Status = status,
                StatusLine = $"{StatusLabel(status)} \u2022 {member.Plan}",
                TimeText = DateUtils.FormatTimeOfDay(rec.TimestampMillis),
                SessionText = rec.Session.ToUpperInvariant(),
                OpenCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.AttendanceHistory(id)))
            });
        }
        Entries.ReplaceAll(rows);

        EmptyMessage = list.Count > 0 ? ""
            : !string.IsNullOrWhiteSpace(_query) ? "No attendance records match your search."
            : dayEntries.Count == 0 ? "No attendance recorded for this date."
            : "No attendance records match this filter.";
    }

    /// <summary>Android's status label helper (AttendanceLogsScreen line ~359).</summary>
    public static string StatusLabel(MemberStatus s) => s switch
    {
        MemberStatus.ACTIVE => "ACTIVE",
        MemberStatus.EXPIRING => "EXPIRING SOON",
        _ => "EXPIRED"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
