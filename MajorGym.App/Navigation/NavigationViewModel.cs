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
    private object? _currentViewModel;

    public Screen Current
    {
        get => _current;
        private set { _current = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedNav)); }
    }

    /// <summary>Which bottom-navigation item is lit for the current screen — Android's
    /// <c>BottomNav</c> "highlighted" rule: an item is lit only on its own screen, except that a
    /// member's Attendance Details page (AttendanceHistory) keeps Attendance lit. The Add item is
    /// never lit (Android: <c>active = !isAdd &amp;&amp; screen == highlighted</c>). Purely visual — it
    /// never affects what tapping an item does.</summary>
    public string SelectedNav => _current switch
    {
        Screen.Dashboard => "Dashboard",
        Screen.AttendanceLogs or Screen.AttendanceHistory => "Attendance",
        Screen.Backup => "Backup",
        Screen.Sync => "Sync",
        _ => ""
    };

    /// <summary>The active screen's ViewModel. Phase 1 change: this is now built ONCE per
    /// navigation inside <see cref="NavigateTo"/> and cached, instead of being a computed
    /// property that constructed a brand-new ViewModel every time it was read. That also lets
    /// navigation resolve a missing member up front (see <see cref="Resolve"/>) instead of the
    /// getter returning null — which rendered a blank screen (Registered/Renew/Renewed/etc.)
    /// or, for Profile/Edit, threw straight out of a ViewModel constructor. Android shows
    /// nothing for a route whose member no longer exists; a blank window is never an
    /// acceptable Windows equivalent, so those routes now land on the member list instead.
    /// Each navigation still re-creates the screen's state from scratch, exactly like
    /// Android re-composing a route (an un-saved Add Member draft is not preserved across a
    /// back-and-forth, in either app).</summary>
    public object? CurrentViewModel => _currentViewModel ??= BuildViewModel(_current);

    /// <summary>Screens that need a live member. If that member is gone (deleted from another
    /// screen/device in between), returns the screen to show instead.</summary>
    private Screen Resolve(Screen screen) => screen switch
    {
        Screen.Edit e when _repository.GetById(e.MemberId) is null => new Screen.Members(),
        Screen.Profile p when _repository.GetById(p.MemberId) is null => new Screen.Members(),
        Screen.Registered r when _repository.GetById(r.MemberId) is null => new Screen.Members(),
        Screen.Renew rn when _repository.GetById(rn.MemberId) is null => new Screen.Members(),
        Screen.Renewed rd when _repository.GetById(rd.MemberId) is null => new Screen.Members(),
        Screen.EnrollFingerprint ef when _repository.GetById(ef.MemberId) is null => new Screen.Members(),
        Screen.AttendanceHistory ah when _repository.GetById(ah.MemberId) is null => new Screen.AttendanceLogs(),
        Screen.ArchivedMemberDetail amd when _repository.GetArchivedMemberById(amd.MemberId) is null => new Screen.ExpiredArchive(),
        _ => screen
    };

    private object? BuildViewModel(Screen screen) => screen switch
    {
        Screen.Dashboard => new ViewModels.DashboardViewModel(_repository, this),
        Screen.Members => new ViewModels.MembersViewModel(_repository, this),
        Screen.Add => new ViewModels.AddEditMemberViewModel(_repository, _photoStore, this, existingId: null),
        Screen.Edit e => new ViewModels.AddEditMemberViewModel(_repository, _photoStore, this, existingId: e.MemberId),
        Screen.Profile p => new ViewModels.ProfileViewModel(_repository, this, p.MemberId),

        // ---- Added in Stage 4 (Member workflow) ----
        Screen.Registered r => new ViewModels.RegisteredViewModel(this, RequireMember(r.MemberId), r.Passkey),
        Screen.Renew rn => new ViewModels.RenewViewModel(_repository, this, RequireMember(rn.MemberId)),
        Screen.Renewed rd => new ViewModels.RenewedViewModel(this, RequireMember(rd.MemberId), rd.JustRenewed),
        Screen.TotalMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Total),
        Screen.ActiveMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Active),
        Screen.ExpiringMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Expiring),
        Screen.ExpiredMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Expired),
        Screen.DueMembers => new ViewModels.FilteredMembersViewModel(_repository, this, ViewModels.FilteredMembersKind.Due),

        // ---- Added in Stage 4b (Attendance / Backup / Fingerprint / Archive workflows) ----
        Screen.Attendance => new ViewModels.AttendanceViewModel(this),
        Screen.AttendanceLogs => new ViewModels.AttendanceLogsViewModel(_repository, this),
        Screen.AttendanceHistory ah => new ViewModels.AttendanceHistoryViewModel(_repository, this, RequireMember(ah.MemberId)),
        Screen.EnrollFingerprint ef => new ViewModels.EnrollFingerprintViewModel(_repository, this, RequireMember(ef.MemberId), ef.ReturnTo),
        Screen.Backup => new ViewModels.BackupViewModel(_repository, this),
        Screen.BackupHistory => new ViewModels.BackupHistoryViewModel(this),
        Screen.ExpiredArchive => new ViewModels.ExpiredArchiveViewModel(_repository, this),
        Screen.ArchivedMemberDetail amd => new ViewModels.ArchivedMemberDetailViewModel(_repository, this, _repository.GetArchivedMemberById(amd.MemberId)!),

        // ---- Device Sync (LAN sync with Android phones / other PCs) ----
        Screen.Sync => new ViewModels.SyncViewModel(App.SyncPrefs, App.SyncManager),

        _ => new ViewModels.DashboardViewModel(_repository, this) // defensive default — never a blank screen
    };

    /// <summary>Only ever called for a screen <see cref="Resolve"/> has already confirmed has
    /// a live member, so this cannot return null in practice.</summary>
    private Member RequireMember(string memberId) =>
        _repository.GetById(memberId) ?? throw new InvalidOperationException($"Member {memberId} not found");

    public void NavigateTo(Screen screen)
    {
        var resolved = Resolve(screen);
        var previous = _currentViewModel;
        _currentViewModel = BuildViewModel(resolved);
        Current = resolved;
        OnPropertyChanged(nameof(CurrentViewModel));
        // A screen that holds a resource (the Enroll Fingerprint screen holds the scanner between its two scans)
        // releases it the moment it is left — Android's DisposableEffect.onDispose.
        (previous as IDisposable)?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
