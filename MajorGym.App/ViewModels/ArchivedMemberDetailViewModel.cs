using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's Archived Member Detail screen. Two terminal actions:
///  - Restore: recreates a normal, operational Member (new passkey/QR issued — see
///    <see cref="Repository.RestoreArchivedMember"/>'s own doc comment for why those two
///    fields specifically can't just carry over) and shows the new passkey once so it can
///    be given to the member, exactly like a fresh registration's Registered screen.
///  - Delete: permanently erases the archived record with no way back (distinct from
///    Restore — see Repository.DeleteArchivedMemberPermanently's doc comment for why this
///    stays local-only rather than a sync operation).
/// Both are gated behind an inline confirm step, mirroring ProfileViewModel's
/// IsConfirmingDelete pattern (see that file's doc comment for why an inline panel rather
/// than a separate popup Window is the right WPF equivalent of Android's AlertDialog here).
/// </summary>
public sealed class ArchivedMemberDetailViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;

    public ArchivedMember Archived { get; private set; }
    public string ArchivedOn => DateUtils.FormatDate(Archived.ArchivedAtMillis);
    public string JoinedOn => DateUtils.FormatDate(Archived.JoinedMillis);
    public string LastMembershipStart => DateUtils.FormatDate(Archived.LastStartMillis);
    public string LastExpiry => DateUtils.FormatDate(Archived.LastExpiryMillis);
    public string LastFeeText => DateUtils.FormatMoney(Archived.LastFee);
    public string IdProofText => string.IsNullOrWhiteSpace(Archived.IdProof) ? "Not Provided" : Archived.IdProof;

    private bool _isConfirmingDelete;
    public bool IsConfirmingDelete { get => _isConfirmingDelete; private set { _isConfirmingDelete = value; OnPropertyChanged(); } }

    private bool _isConfirmingRestore;
    public bool IsConfirmingRestore { get => _isConfirmingRestore; private set { _isConfirmingRestore = value; OnPropertyChanged(); } }

    public ICommand RequestRestoreCommand { get; }
    public ICommand ConfirmRestoreCommand { get; }
    public ICommand CancelRestoreCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand BackCommand { get; }

    public ArchivedMemberDetailViewModel(Repository repository, NavigationViewModel nav, ArchivedMember archived)
    {
        _repository = repository;
        _nav = nav;
        Archived = archived;

        RequestRestoreCommand = new RelayCommand(() => IsConfirmingRestore = true);
        CancelRestoreCommand = new RelayCommand(() => IsConfirmingRestore = false);
        ConfirmRestoreCommand = new RelayCommand(Restore);

        RequestDeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        ConfirmDeleteCommand = new RelayCommand(Delete);

        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.ExpiredArchive()));
    }

    private void Restore()
    {
        var result = _repository.RestoreArchivedMember(Archived.OriginalMemberId);
        IsConfirmingRestore = false;
        if (result is { } r)
        {
            _nav.NavigateTo(new Screen.Registered(r.Member.Id, r.Passkey));
        }
        else
        {
            // Already restored/removed from another screen/device — nothing left to show here.
            _nav.NavigateTo(new Screen.ExpiredArchive());
        }
    }

    private void Delete()
    {
        _repository.DeleteArchivedMemberPermanently(Archived.OriginalMemberId);
        IsConfirmingDelete = false;
        _nav.NavigateTo(new Screen.ExpiredArchive());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
