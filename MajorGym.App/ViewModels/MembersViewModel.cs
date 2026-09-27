using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>One row in the Members list, pre-computed so the view's DataTemplate stays
/// simple XAML bindings rather than converters — same idea as Android's <c>MemberRow</c>
/// composable receiving a plain <c>Member</c> and computing status/ring color itself.</summary>
public sealed class MemberRowViewModel
{
    public required Member Member { get; init; }
    public required string StatusText { get; init; }
    public required Brush StatusBrush { get; init; }
    public required ICommand OpenProfile { get; init; }
}

/// <summary>
/// Ported from Android's <c>MembersScreen</c> composable (Screens.kt) — full member list
/// with a live search box filtering by name or phone, each row navigating to
/// <see cref="Screen.Profile"/> on click, same as Android's <c>onNavigate(Screen.Profile(m.id))</c>.
/// </summary>
public sealed class MembersViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly List<Member> _all;

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
        _repository = repository;
        _nav = nav;
        _all = _repository.GetAll().OrderBy(m => m.Name).ToList();
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
                            || m.Phone.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var m in filtered)
        {
            var status = MemberStatusExtensions.StatusOf(m.ExpiryMillis);
            var id = m.Id;
            Rows.Add(new MemberRowViewModel
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
                    MemberStatus.ACTIVE => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),   // GymSuccess
                    MemberStatus.EXPIRING => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),  // GymWarning
                    MemberStatus.EXPIRED => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),   // GymDanger
                    _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))                       // GymTextMuted
                },
                OpenProfile = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(id)))
            });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
