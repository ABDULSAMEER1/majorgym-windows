using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>Which of the five Dashboard stat-card destinations this instance represents —
/// mirrors the five separate <c>when(screen)</c> branches in Android's MainActivity.kt that
/// all call the same <c>FilteredMembersScreen</c> composable with different arguments.</summary>
public enum FilteredMembersKind { Total, Active, Expiring, Expired, Due }

/// <summary>One row, reusing the exact same status-color mapping <see cref="MembersViewModel"/>
/// uses (see <see cref="MemberRowViewModel"/>), plus the Due-amount column shown only on the
/// Due Members destination.</summary>
public sealed class FilteredMemberRowViewModel
{
    public required Member Member { get; init; }
    public required string StatusText { get; init; }
    public required Brush StatusBrush { get; init; }
    public required ICommand OpenProfile { get; init; }
}

/// <summary>
/// Ported from Android's <c>FilteredMembersScreen</c> composable (ui/MemberListScreens.kt)
/// plus the five call sites in MainActivity.kt that feed it. The caller (this constructor)
/// is responsible for pre-filtering the member list using the exact same
/// <see cref="MemberStatusExtensions.StatusOf"/> logic the Dashboard used to compute the
/// tapped card's number — Android's own doc comment on FilteredMembersScreen calls this out
/// explicitly ("the list shown here can never disagree with the count that was tapped to
/// get here") — so this ViewModel does that filtering itself per <see cref="Kind"/> rather
/// than trusting a pre-filtered list handed in, removing any chance of the two disagreeing.
/// </summary>
public sealed class FilteredMembersViewModel : INotifyPropertyChanged
{
    private readonly NavigationViewModel _nav;
    private readonly List<Member> _source;

    public FilteredMembersKind Kind { get; }
    public string Title { get; }
    public bool ShowSearch { get; }
    public bool ShowDueAmount { get; }
    private readonly string _emptyText;

    public ObservableCollection<FilteredMemberRowViewModel> Rows { get; } = new();

    private string _searchText = "";
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); ApplyFilter(); } }

    private string _emptyMessage = "";
    public string EmptyMessage { get => _emptyMessage; private set { _emptyMessage = value; OnPropertyChanged(); } }

    public bool HasRows => Rows.Count > 0;

    public ICommand BackCommand { get; }

    public FilteredMembersViewModel(Repository repository, NavigationViewModel nav, FilteredMembersKind kind)
    {
        _nav = nav;
        Kind = kind;

        var all = repository.GetAll();
        (Title, _source, ShowSearch, ShowDueAmount, _emptyText) = kind switch
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

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var query = SearchText.Trim();
        var shown = (ShowSearch && !string.IsNullOrEmpty(query))
            ? _source.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || m.Phone.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || m.IdProof.Contains(query, StringComparison.OrdinalIgnoreCase))
            : _source;

        foreach (var m in shown)
        {
            var status = MemberStatusExtensions.StatusOf(m.ExpiryMillis);
            var id = m.Id;
            Rows.Add(new FilteredMemberRowViewModel
            {
                Member = m,
                StatusText = status switch
                {
                    MemberStatus.ACTIVE => "Active",
                    MemberStatus.EXPIRING => "Expiring Soon",
                    MemberStatus.EXPIRED => "Expired",
                    _ => "Unknown"
                },
                StatusBrush = status switch
                {
                    MemberStatus.ACTIVE => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
                    MemberStatus.EXPIRING => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                    MemberStatus.EXPIRED => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                    _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
                },
                OpenProfile = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(id)))
            });
        }

        EmptyMessage = (ShowSearch && !string.IsNullOrEmpty(query)) ? "No members match your search." : _emptyText;
        OnPropertyChanged(nameof(HasRows));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
