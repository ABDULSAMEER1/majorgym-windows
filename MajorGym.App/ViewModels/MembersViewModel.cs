using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>MembersScreen</c> composable (Screens.kt ~778-802) — full member
/// list with a live search box filtering by name, phone, OR ID proof (Android:
/// <c>it.name.contains(query, ignoreCase = true) || it.phone.contains(query) ||
/// it.idProof.contains(query, ignoreCase = true)</c>), each row the shared
/// <see cref="MemberRowViewModel"/>/Controls/MemberRowView Android's own MemberRow doc
/// comment says is reused across every member list in the app.
///
/// Phase 1 fixes over the earlier Windows port: ID proof was not searchable at all; the list
/// was sorted with .NET's culture-aware string comparer instead of the SQLite
/// <c>ORDER BY name ASC</c> (BINARY collation) Android's own <c>MemberDao.getAll()</c> uses —
/// <see cref="Repository.GetAllByName"/> now does the ordering in SQL so the two match
/// exactly; and the row itself was a bare name/phone/status line missing the ring, plan,
/// expiry date, and Renew shortcut Android's MemberRow always shows.
/// </summary>
public sealed class MembersViewModel : INotifyPropertyChanged
{
    private readonly List<Data.Entities.Member> _all;
    private readonly NavigationViewModel _nav;

    public ObservableCollection<MemberRowViewModel> Rows { get; } = new();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public ICommand AddMemberCommand { get; }

    public MembersViewModel(Repository repository, NavigationViewModel nav)
    {
        _nav = nav;
        _all = repository.GetAllByName();
        AddMemberCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Add()));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || m.Phone.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || m.IdProof.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var m in filtered) Rows.Add(MemberRowViewModel.For(m, _nav));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
