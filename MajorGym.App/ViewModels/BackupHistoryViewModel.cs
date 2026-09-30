using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Settings;

namespace MajorGym.App.ViewModels;

/// <summary>One Backup History row, exactly Android's: History icon + date on the left, time on
/// the right.</summary>
public sealed record BackupHistoryRow(string DateText, string TimeText);

/// <summary>Ported from Android's Backup History screen. BackupHistoryPrefs only ever records
/// WHEN a backup was taken (latest 3 months, newest first) — never backup content.</summary>
public sealed class BackupHistoryViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;

    public ObservableCollection<BackupHistoryRow> Entries { get; } = new();
    public bool IsEmpty => Entries.Count == 0;

    public ICommand BackCommand { get; }

    public BackupHistoryViewModel(NavigationViewModel nav)
    {
        _nav = nav;
        var prefs = new BackupHistoryPrefs(App.AppDataDirectory);
        foreach (var millis in prefs.Entries())
            Entries.Add(new BackupHistoryRow(DateUtils.FormatDate(millis), DateUtils.FormatTimeOfDay(millis)));

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Backup()));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
