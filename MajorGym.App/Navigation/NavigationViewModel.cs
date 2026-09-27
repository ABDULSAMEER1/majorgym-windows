using System.ComponentModel;
using System.Runtime.CompilerServices;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.Navigation;

/// <summary>
/// The Windows equivalent of Android's <c>var screen by remember { mutableStateOf(...) }</c>
/// plus its surrounding <c>when(screen)</c> block in MainActivity.kt — a single current-
/// screen field, changed by calling <see cref="NavigateTo"/>, observed by
/// <see cref="MainWindow"/> via <see cref="INotifyPropertyChanged"/> (WPF's native
/// data-binding change-notification, the natural equivalent of Compose's own recomposition-
/// on-state-change here). Ported conceptually, not line-for-line, since the underlying
/// UI frameworks differ — but the DECISION LOGIC (which screen a given action leads to) is
/// preserved: see each ViewModel's navigation calls for the Android screen transition they
/// correspond to.
/// </summary>
public sealed class NavigationViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly PhotoStore _photoStore;

    /// <summary>Constructed once for the app's lifetime (unlike CurrentViewModel, which is
    /// re-created on every navigation) since the kiosk loop itself runs continuously in the
    /// background regardless of which screen is on top — see KioskOverlayViewModel's own
    /// doc comment and MainWindow.xaml's overlay layer.</summary>
    public KioskOverlayViewModel KioskOverlay { get; }

    public NavigationViewModel(Repository repository, PhotoStore photoStore, MajorGym.Kiosk.FingerprintKioskLoop kioskLoop)
    {
        _repository = repository;
        _photoStore = photoStore;
        _current = new Screen.Dashboard();
        KioskOverlay = new KioskOverlayViewModel(kioskLoop);
    }

    private Screen _current;
    public Screen Current
    {
        get => _current;
        private set { _current = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentViewModel)); }
    }

    /// <summary>The active screen's ViewModel, re-created fresh on every navigation —
    /// mirroring Android's own behavior of re-composing a screen's ViewModel-backed state
    /// from scratch each time its route is entered (Android does not preserve
    /// AddEditMemberScreen's typed-but-unsaved draft across a back-and-forth navigation,
    /// for example, and neither does this).</summary>
    public object? CurrentViewModel => Current switch
    {
        Screen.Dashboard => new ViewModels.DashboardViewModel(_repository, this),
        Screen.Members => new ViewModels.MembersViewModel(_repository, this),
        Screen.Add => new ViewModels.AddEditMemberViewModel(_repository, _photoStore, this, existingId: null),
        Screen.Edit e => new ViewModels.AddEditMemberViewModel(_repository, _photoStore, this, existingId: e.MemberId),
        Screen.Profile p => new ViewModels.ProfileViewModel(_repository, this, p.MemberId),

        // ---- Added in Stage 4 (Member workflow) ----
        Screen.Registered r => RequireMember(r.MemberId) is { } m
            ? new ViewModels.RegisteredViewModel(this, m, r.Passkey)
            : null,
        Screen.Renew rn => RequireMember(rn.MemberId) is { } m
            ? new ViewModels.RenewViewModel(_repository, this, m)
            : null,
        Screen.Renewed rd => RequireMember(rd.MemberId) is { } m
            ? new ViewModels.RenewedViewModel(this, m, rd.JustRenewed)
            : null,
        Screen.TotalMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Total),
        Screen.ActiveMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Active),
        Screen.ExpiringMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Expiring),
        Screen.ExpiredMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Expired),
        Screen.DueMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Due),

        // ---- Added in Stage 4b (Attendance / Backup / Fingerprint / Archive workflows) ----
        Screen.Attendance => new ViewModels.AttendanceViewModel(this),
        Screen.AttendanceLogs => new ViewModels.AttendanceLogsViewModel(_repository, this),
        Screen.AttendanceHistory ah => RequireMember(ah.MemberId) is { } ahm
            ? new ViewModels.AttendanceHistoryViewModel(_repository, this, ahm)
            : null,
        Screen.EnrollFingerprint ef => RequireMember(ef.MemberId) is { } efm
            ? new ViewModels.EnrollFingerprintViewModel(_repository, this, efm, ef.ReturnTo)
            : null,
        Screen.Backup => new ViewModels.BackupViewModel(_repository, this),
        Screen.BackupHistory => new ViewModels.BackupHistoryViewModel(this),
        Screen.ExpiredArchive => new ViewModels.ExpiredArchiveViewModel(_repository, this),
        Screen.ArchivedMemberDetail amd => _repository.GetArchivedMemberById(amd.MemberId) is { } archived
            ? new ViewModels.ArchivedMemberDetailViewModel(_repository, this, archived)
            : null,

        _ => null // Not yet implemented in this stage — see the Stage 4b report for the full list.
    };

    /// <summary>A member referenced by a navigation argument (Registered/Renew/Renewed) can
    /// no longer exist if they were deleted from another screen/device in between. Returns
    /// null rather than throwing in that case — deliberately NOT calling NavigateTo here to
    /// redirect, since this is evaluated from inside the CurrentViewModel getter itself and
    /// mutating Current mid-read would be reentrant. The View layer treats a null
    /// CurrentViewModel as "render nothing" (see MainWindow.xaml's ContentControl), which is
    /// an accepted, explicitly-flagged gap for this edge case rather than a graceful
    /// redirect — a real fix (bounce back to Dashboard) belongs in a later stage's
    /// navigation-hardening pass, not bolted on here as a side effect of a property getter.</summary>
    private Member? RequireMember(string memberId) => _repository.GetById(memberId);

    public void NavigateTo(Screen screen) => Current = screen;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
