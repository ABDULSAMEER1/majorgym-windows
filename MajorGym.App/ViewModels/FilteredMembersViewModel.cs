using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>Which of the five Dashboard stat-card destinations this instance represents —
/// mirrors the five separate <c>when(screen)</c> branches in Android's MainActivity.kt that
/// all call the same <c>FilteredMembersScreen</c> composable with different arguments.</summary>
public enum FilteredMembersKind { Total, Active, Expiring, Expired, Due }

/// <summary>
/// Ported from Android's <c>FilteredMembersScreen</c> composable (ui/MemberListScreens.kt)
/// plus the five call sites in MainActivity.kt that feed it. The caller (this constructor)
/// is responsible for pre-filtering the member list using the exact same
/// <see cref="MemberStatusExtensions.StatusOf"/> logic the Dashboard used to compute the
/// tapped card's number — Android's own doc comment on FilteredMembersScreen calls this out
/// explicitly ("the list shown here can never disagree with the count that was tapped to
/// get here") — so this ViewModel does that filtering itself per <see cref="Kind"/> rather
/// than trusting a pre-filtered list handed in.
///
/// Phase 1 fix: the source list is now <see cref="Repository.GetAllByName"/> (Android's own
/// <c>vm.members</c> is name-sorted at the SQL level — see MembersViewModel's doc comment)
/// instead of the previous unordered <c>GetAll()</c>, so every one of these five lists shows
/// members in the same order Android does. Rows now use the same shared
/// <see cref="MemberRowViewModel"/>/Controls/MemberRowView as the Members tab (Android
/// reuses its own MemberRow composable here too — see that class's doc comment).
/// </summary>
public sealed class FilteredMembersViewModel : INotifyPropertyChanged
{
    // Row view-models are built once; search just picks from this list (no per-keystroke object creation).
    private readonly List<MemberRowViewModel> _sourceRows;
    private readonly Debouncer _filterDebounce;

    public FilteredMembersKind Kind { get; }
    public string Title { get; }
    public bool ShowSearch { get; }
    public bool ShowDueAmount { get; }
    private readonly string _emptyText;

    public BulkObservableCollection<MemberRowViewModel> Rows { get; } = new();

    private string _searchText = "";
    /// <summary>The box updates instantly; the list re-filters ~150 ms after the user stops typing/deleting.</summary>
    public string SearchText { get => _searchText; set { if (_searchText == value) return; _searchText = value ?? ""; OnPropertyChanged(); _filterDebounce.Trigger(); } }

    private string _emptyMessage = "";
    public string EmptyMessage { get => _emptyMessage; private set { _emptyMessage = value; OnPropertyChanged(); } }

    public bool HasRows => Rows.Count > 0;

    public ICommand BackCommand { get; }

    public FilteredMembersViewModel(Repository repository, NavigationViewModel nav, FilteredMembersKind kind)
    {
        Kind = kind;
        // List screens only display members, so fingerprint templates are not read/decrypted here.
        var all = repository.GetAllByNameForList();

        (Title, var source, ShowSearch, ShowDueAmount, _emptyText) = kind switch
        {
            FilteredMembersKind.Total => ("Total Members", all, true, false, "No members yet."),
            FilteredMembersKind.Active => ("Active Members",
                all.Where(m => MemberStatusExtensions.StatusOf(m.ExpiryMillis) == MemberStatus.ACTIVE).ToList(),
                false, false, "No active members."),
            FilteredMembersKind.Expiring => ("Expiring Soon",
                all.Where(m => MemberStatusExtensions.StatusOf(m.ExpiryMillis) == MemberStatus.EXPIRING).ToList(),
                false, false, "No members expiring soon."),
            FilteredMembersKind.Expired => ("Expired Members",
                all.Where(m => MemberStatusExtensions.StatusOf(m.ExpiryMillis) == MemberStatus.EXPIRED).ToList(),
                false, false, "No expired members."),
            FilteredMembersKind.Due => ("Due Members",
                all.Where(m => m.Fee > 0.0).ToList(),
                true, true, "No members with a due amount."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        BackCommand = new RelayCommand(() => nav.NavigateTo(new Screen.Dashboard()));
        _nav = nav;
        _sourceRows = source.Select(m => MemberRowViewModel.For(m, nav, showDueAmount: ShowDueAmount)).ToList();
        _filterDebounce = new Debouncer(ApplyFilter);
        ApplyFilter();
    }

    private readonly NavigationViewModel _nav;

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        // Android: it.name.contains(query, ignoreCase = true) || it.phone.contains(query) || it.idProof.contains(query, ignoreCase = true)
        var shown = (ShowSearch && !string.IsNullOrEmpty(query))
            ? _sourceRows.Where(r => r.Member.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || r.Member.Phone.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || r.Member.IdProof.Contains(query, StringComparison.OrdinalIgnoreCase))
            : _sourceRows;

        Rows.ReplaceAll(shown);

        EmptyMessage = (ShowSearch && !string.IsNullOrEmpty(query)) ? "No members match your search." : _emptyText;
        OnPropertyChanged(nameof(HasRows));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
