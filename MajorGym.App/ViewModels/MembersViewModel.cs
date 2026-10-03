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
    // Row view-models are built ONCE (not on every keystroke); filtering just picks from this list.
    private readonly List<MemberRowViewModel> _allRows;
    private readonly Debouncer _filterDebounce;

    public BulkObservableCollection<MemberRowViewModel> Rows { get; } = new();

    private string _searchText = "";
    /// <summary>The box updates instantly; the list re-filters ~150 ms after the user stops typing/deleting.</summary>
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; _searchText = value ?? ""; OnPropertyChanged(); _filterDebounce.Trigger(); }
    }

    public ICommand AddMemberCommand { get; }

    public MembersViewModel(Repository repository, NavigationViewModel nav)
    {
        // List screens only display members, so fingerprint templates are not read/decrypted here.
        _allRows = repository.GetAllByNameForList().Select(m => MemberRowViewModel.For(m, nav)).ToList();
        _filterDebounce = new Debouncer(ApplyFilter);
        AddMemberCommand = new RelayCommand(() => nav.NavigateTo(new Screen.Add()));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r => r.Member.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || r.Member.Phone.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || r.Member.IdProof.Contains(query, StringComparison.OrdinalIgnoreCase));

        Rows.ReplaceAll(filtered);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
