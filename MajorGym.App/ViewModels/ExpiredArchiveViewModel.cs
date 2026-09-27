using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

public sealed class ArchivedMemberRow
{
    public required ArchivedMember Archived { get; init; }
    public required ICommand Open { get; init; }
    public string Name => Archived.Name;
    public string Phone => Archived.Phone;
    public string LastPlan => Archived.LastPlan;
    public string ArchivedOn => DateUtils.FormatDate(Archived.ArchivedAtMillis);
}

/// <summary>
/// Ported from Android's Expired Archive screen: every member archived after being expired
/// 30+ days with no renewal (see <see cref="Repository.ArchiveExpiredMembersOnce"/>), newest
/// archived first. Tapping a row opens Archived Member Detail for restore/permanent-delete.
/// </summary>
public sealed class ExpiredArchiveViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;

    public ObservableCollection<ArchivedMemberRow> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;

    public ICommand BackCommand { get; }

    public ExpiredArchiveViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;

        Reload();

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));
    }

    public void Reload()
    {
        Rows.Clear();
        foreach (var a in _repository.GetArchivedMembers())
        {
            var id = a.OriginalMemberId;
            Rows.Add(new ArchivedMemberRow
            {
                Archived = a,
                Open = new RelayCommand(() => _nav.NavigateTo(new Screen.ArchivedMemberDetail(id)))
            });
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
