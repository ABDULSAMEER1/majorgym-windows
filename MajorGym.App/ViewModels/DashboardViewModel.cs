using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;
using MajorGym.Data.Settings;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Simple ICommand — WPF has no built-in RelayCommand, unlike Compose's plain lambda
/// onClick; this is the minimal equivalent every ViewModel in this app uses for
/// button/card click bindings.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    /// <summary>
    /// Phase 1 fix (systematic, not a per-button workaround): this used to be a plain
    /// auto-event that nothing ever raised, so WPF asked <see cref="CanExecute"/> exactly
    /// once when the binding was first applied and never again — a Save button whose
    /// CanExecute depends on validation state (Add/Edit Member) stayed permanently disabled
    /// after the initial "invalid" check even once every field was valid. Routing the event
    /// through <see cref="CommandManager.RequerySuggested"/> makes WPF re-query every bound
    /// command after each input/focus/keyboard event (the standard WPF RelayCommand pattern),
    /// and <see cref="RaiseCanExecuteChanged"/> forces that re-query immediately for state
    /// changes that don't come from user input (e.g. the async-style duplicate-phone check).
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>One "Needs Attention" row (Android: the member rows under the Expired Archive card).</summary>
public sealed class AttentionRowViewModel
{
    public required Member Member { get; init; }
    public required MemberStatus Status { get; init; }
    public required string DaysText { get; init; }
    public required ICommand OpenProfile { get; init; }
}

/// <summary>
/// Ported from Android's <c>DashboardScreen</c> composable (Screens.kt lines ~402-560).
///
/// CORRECTION TO THE STAGE 1 REPORT: Stage 1's screen inventory listed a "Revenue" card as
/// part of the Dashboard. Re-inspecting the actual golden-reference source for this stage
/// found no such card, field, or calculation anywhere in the Android UI — the real
/// Dashboard is Total/Active/Expiring/Expired (2x2 stat grid), a Due Members row, and an
/// Expired Archive row, plus the master-privacy and per-card-visibility settings. This
/// implementation follows the real source, not the earlier report — flagged here rather
/// than silently perpetuating the inaccuracy (Stage 3 brief §2/§24: "do not invent new
/// product behavior").
/// </summary>
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly DashboardPrivacyPrefs _privacy;

    public DashboardViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;
        _privacy = new DashboardPrivacyPrefs(App.AppDataDirectory);

        var members = _repository.GetAll();
        TotalCount = members.Count;
        ActiveCount = members.Count(m => MemberStatusExtensions.StatusOf(m.ExpiryMillis) == MemberStatus.ACTIVE);
        ExpiringCount = members.Count(m => MemberStatusExtensions.StatusOf(m.ExpiryMillis) == MemberStatus.EXPIRING);
        ExpiredCount = members.Count(m => MemberStatusExtensions.StatusOf(m.ExpiryMillis) == MemberStatus.EXPIRED);
        // "Due Members" — a payment-status filter, independent of membership status
        // (Fee here means "outstanding due amount", not the plan price — Android doc
        // comment, preserved: an ACTIVE member with a due amount still appears in both
        // Active/Total AND here).
        DueCount = members.Count(m => m.Fee > 0.0);

        // Android "NEEDS ATTENTION": 0..7 days remaining, soonest expiry first. (Android filters on
        // daysBetweenNow(expiryMillis) in 0..7 and sorts by expiryMillis.)
        foreach (var m in members
                     .Where(m => { var d = DateUtils.DaysBetweenNow(m.ExpiryMillis); return d >= 0 && d <= 7; })
                     .OrderBy(m => m.ExpiryMillis))
        {
            var id = m.Id;
            var days = DateUtils.DaysBetweenNow(m.ExpiryMillis);
            Attention.Add(new AttentionRowViewModel
            {
                Member = m,
                Status = MemberStatusExtensions.StatusOf(m.ExpiryMillis),
                DaysText = days < 0 ? $"Expired {-days}d ago" : $"Expires in {days}d",
                OpenProfile = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(id)))
            });
        }

        MasterPrivacyOn = _privacy.MasterPrivacyOn;
        TotalVisible = _privacy.IsNumberVisible(DashboardCard.TOTAL);
        ActiveVisible = _privacy.IsNumberVisible(DashboardCard.ACTIVE);
        ExpiringVisible = _privacy.IsNumberVisible(DashboardCard.EXPIRING);
        ExpiredVisible = _privacy.IsNumberVisible(DashboardCard.EXPIRED);
        DueVisible = _privacy.IsNumberVisible(DashboardCard.DUE);

        GoTotal = new RelayCommand(() => _nav.NavigateTo(new Screen.TotalMembers()));
        GoActive = new RelayCommand(() => _nav.NavigateTo(new Screen.ActiveMembers()));
        GoExpiring = new RelayCommand(() => _nav.NavigateTo(new Screen.ExpiringMembers()));
        GoExpired = new RelayCommand(() => _nav.NavigateTo(new Screen.ExpiredMembers()));
        GoDue = new RelayCommand(() => _nav.NavigateTo(new Screen.DueMembers()));
        GoExpiredArchive = new RelayCommand(() => _nav.NavigateTo(new Screen.ExpiredArchive()));
        GoAddMember = new RelayCommand(() => _nav.NavigateTo(new Screen.Add()));
    }

    /// <summary>Members expiring within 7 days, shown below the Expired Archive card.</summary>
    public List<AttentionRowViewModel> Attention { get; } = new();
    public bool HasAttention => Attention.Count > 0;
    public string AttentionHeader => $"NEEDS ATTENTION ({Attention.Count})";

    public bool MasterPrivacyOn { get; }
    public bool TotalVisible { get; }
    public bool ActiveVisible { get; }
    public bool ExpiringVisible { get; }
    public bool ExpiredVisible { get; }
    public bool DueVisible { get; }

    public int TotalCount { get; }
    public int ActiveCount { get; }
    public int ExpiringCount { get; }
    public int ExpiredCount { get; }
    public int DueCount { get; }

    public string TotalDisplay => TotalVisible ? TotalCount.ToString() : "—";
    public string ActiveDisplay => ActiveVisible ? ActiveCount.ToString() : "—";
    public string ExpiringDisplay => ExpiringVisible ? ExpiringCount.ToString() : "—";
    public string ExpiredDisplay => ExpiredVisible ? ExpiredCount.ToString() : "—";
    public string DueDisplay => DueVisible ? DueCount.ToString() : "—";

    public ICommand GoTotal { get; }
    public ICommand GoActive { get; }
    public ICommand GoExpiring { get; }
    public ICommand GoExpired { get; }
    public ICommand GoDue { get; }
    public ICommand GoExpiredArchive { get; }
    public ICommand GoAddMember { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
