using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

public sealed class ArchivedMemberRow : INotifyPropertyChanged
{
    public required ArchivedMember Archived { get; init; }
    public required ICommand Open { get; init; }
    public string Name => Archived.Name;
    public string Phone => Archived.Phone;
    public string LastPlan => Archived.LastPlan;
    public string ArchivedOn => DateUtils.FormatDate(Archived.ArchivedAtMillis);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Ported from Android's Expired Archive screen: every member archived after being expired
/// 30+ days with no renewal (see <see cref="Repository.ArchiveExpiredMembersOnce"/>), newest
/// archived first. Tapping a row opens Archived Member Detail for restore/permanent-delete.
///
/// Windows additions for tidying a long archive quickly:
///  - search box (name / phone / last plan),
///  - multi-select: tick boxes, Shift+click for a range, Ctrl+click to add/remove one, Ctrl+A for all shown,
///  - "select first N" count filter, Select all / Clear,
///  - bulk "Delete selected" behind an inline confirmation (works for a single member too).
/// Deleting is permanent and local-only, exactly like the single-member delete on the detail screen.
/// </summary>
public sealed class ExpiredArchiveViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly List<ArchivedMemberRow> _all = new();
    private ArchivedMemberRow? _anchor;

    /// <summary>The rows currently shown (after the search filter).</summary>
    public ObservableCollection<ArchivedMemberRow> Rows { get; } = new();

    public bool IsEmpty => Rows.Count == 0;
    public string EmptyText => _all.Count == 0 ? "No archived members." : "No archived members match your search.";

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value ?? ""; OnPropertyChanged(); ApplyFilter(); }
    }

    private string _selectCountText = "";
    /// <summary>"How many to select" — digits only; Select picks that many from the top of the shown list.</summary>
    public string SelectCountText
    {
        get => _selectCountText;
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).Take(6).ToArray());
            if (_selectCountText == digits) return;
            _selectCountText = digits;
            OnPropertyChanged();
        }
    }

    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => SelectedCount == 0 ? "None selected" : $"{SelectedCount} selected";
    public string ShowingText => _all.Count == Rows.Count ? $"{_all.Count} archived" : $"Showing {Rows.Count} of {_all.Count}";
    public string DeleteButtonText => SelectedCount == 1 ? "Delete 1 member" : $"Delete {SelectedCount} members";
    public string ConfirmText => SelectedCount == 1
        ? "Permanently delete 1 archived member? This cannot be undone."
        : $"Permanently delete {SelectedCount} archived members? This cannot be undone.";

    private bool _isConfirmingDelete;
    public bool IsConfirmingDelete { get => _isConfirmingDelete; private set { _isConfirmingDelete = value; OnPropertyChanged(); } }

    private string? _notice;
    public string? Notice { get => _notice; private set { _notice = value; OnPropertyChanged(); } }

    public ICommand BackCommand { get; }
    public ICommand SelectFirstNCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    public ExpiredArchiveViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));
        SelectFirstNCommand = new RelayCommand(SelectFirstN);
        SelectAllCommand = new RelayCommand(SelectAllShown);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        RequestDeleteCommand = new RelayCommand(() => { if (HasSelection) IsConfirmingDelete = true; });
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        ConfirmDeleteCommand = new RelayCommand(DeleteSelected);

        Reload();
    }

    public void Reload()
    {
        _all.Clear();
        foreach (var a in _repository.GetArchivedMembers())
        {
            var id = a.OriginalMemberId;
            _all.Add(new ArchivedMemberRow
            {
                Archived = a,
                Open = new RelayCommand(() => _nav.NavigateTo(new Screen.ArchivedMemberDetail(id)))
            });
        }
        _anchor = null;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        IEnumerable<ArchivedMemberRow> shown = _all;
        if (q.Length > 0)
            shown = _all.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || r.Phone.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || r.LastPlan.Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = shown.ToList();

        // Never leave a member selected while hidden by the search: a bulk delete must only touch what is on screen.
        foreach (var r in _all) if (!list.Contains(r)) r.IsSelected = false;
        if (_anchor is not null && !list.Contains(_anchor)) _anchor = null;

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        IsConfirmingDelete = false;
        RaiseSelectionChanged();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ShowingText));
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(ConfirmText));
        if (SelectedCount == 0) IsConfirmingDelete = false;
    }

    /// <summary>Windows-style click selection, called from the view's mouse handlers.
    /// <paramref name="shift"/>: select the range from the last clicked row to this one (replaces the selection unless
    /// <paramref name="toggle"/> is also held). <paramref name="toggle"/> (Ctrl, or a click on the tick box): flip just this row.
    /// Neither: select only this row.</summary>
    public void ClickRow(ArchivedMemberRow row, bool shift, bool toggle)
    {
        Notice = null;
        var index = Rows.IndexOf(row);
        if (index < 0) return;

        if (shift && _anchor is not null && Rows.IndexOf(_anchor) is var a && a >= 0)
        {
            var (lo, hi) = a <= index ? (a, index) : (index, a);
            if (!toggle) foreach (var r in Rows) r.IsSelected = false;
            for (var i = lo; i <= hi; i++) Rows[i].IsSelected = true;
            // anchor stays put so extending the range from the same start works, like Explorer
        }
        else if (toggle)
        {
            row.IsSelected = !row.IsSelected;
            _anchor = row;
        }
        else
        {
            var only = row.IsSelected && SelectedCount == 1;
            foreach (var r in Rows) r.IsSelected = false;
            row.IsSelected = !only;
            _anchor = row;
        }
        RaiseSelectionChanged();
    }

    /// <summary>Plain click on a card body: open the member, unless a selection is under way — then it toggles
    /// the row (so a click never throws the user out of the screen mid-selection).</summary>
    public void ClickCard(ArchivedMemberRow row, bool shift, bool ctrl)
    {
        if (shift || ctrl || HasSelection) ClickRow(row, shift, toggle: ctrl || (!shift && HasSelection));
        else row.Open.Execute(null);
    }

    private void SelectFirstN()
    {
        Notice = null;
        if (!int.TryParse(SelectCountText, out var n) || n <= 0)
        {
            Notice = "Type how many members to select (e.g. 10).";
            return;
        }
        foreach (var r in Rows) r.IsSelected = false;
        var take = Math.Min(n, Rows.Count);
        for (var i = 0; i < take; i++) Rows[i].IsSelected = true;
        _anchor = take > 0 ? Rows[take - 1] : null;
        if (n > Rows.Count && Rows.Count > 0) Notice = $"Only {Rows.Count} shown - selected all of them.";
        RaiseSelectionChanged();
    }

    private void SelectAllShown()
    {
        Notice = null;
        foreach (var r in Rows) r.IsSelected = true;
        _anchor = Rows.Count > 0 ? Rows[0] : null;
        RaiseSelectionChanged();
    }

    private void ClearSelection()
    {
        Notice = null;
        foreach (var r in _all) r.IsSelected = false;
        _anchor = null;
        RaiseSelectionChanged();
    }

    private void DeleteSelected()
    {
        var ids = Rows.Where(r => r.IsSelected).Select(r => r.Archived.OriginalMemberId).ToList();
        IsConfirmingDelete = false;
        if (ids.Count == 0) return;
        try
        {
            var removed = _repository.DeleteArchivedMembersPermanently(ids);
            Reload();
            Notice = removed == 1 ? "Deleted 1 archived member." : $"Deleted {removed} archived members.";
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.TraceError($"[ExpiredArchiveViewModel] bulk delete failed: {e.Message}");
            Notice = "Could not delete the selected members. Nothing was changed.";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
