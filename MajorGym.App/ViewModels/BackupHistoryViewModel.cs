using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Settings;

namespace MajorGym.App.ViewModels;

/// <summary>Ported from Android's Backup History screen. Per BackupHistoryPrefs.cs's own
/// doc comment, this only ever records WHEN a backup was taken — date and time, newest
/// first, nothing else — never a second copy of backup content.</summary>
public sealed class BackupHistoryViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;

    public ObservableCollection<string> Entries { get; } = new();
    public bool IsEmpty => Entries.Count == 0;

    public ICommand BackCommand { get; }

    public BackupHistoryViewModel(NavigationViewModel nav)
    {
        _nav = nav;
        var prefs = new BackupHistoryPrefs(App.AppDataDirectory);
        foreach (var millis in prefs.Entries())
            Entries.Add(DateUtils.FormatDateTime(millis));

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Backup()));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
