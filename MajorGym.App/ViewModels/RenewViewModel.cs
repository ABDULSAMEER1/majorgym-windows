using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>RenewScreen</c> composable (ui/Screens.kt ~1554-1650).
/// Business rules preserved exactly:
///  - Start date defaults to the member's current expiry if it's still in the future,
///    otherwise today — Android's "Feature 2" comment: a renewal where the owner never
///    touches the Start Date field behaves identically to the old hardcoded logic.
///  - New expiry = selected start date + selected plan's months (recomputed live).
///  - On confirm: appends a "Renewed" HistoryEntry dated to the selected START date (not
///    "now"), regenerates the QR token (old QR must stop working — Android parity), and
///    lands on <see cref="Screen.Renewed"/> with justRenewed = true.
/// </summary>
public sealed class RenewViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    public Member Member { get; }

    public IReadOnlyList<string> PlanOptions { get; } = DateUtils.PlanMonths.Keys.ToList();

    private string _plan;
    public string Plan { get => _plan; set { _plan = value; OnPropertyChanged(); RecomputeExpiry(); } }

    private string _feeText;
    // Android: fee = it.filter { c -> c.isDigit() }
    public string FeeText
    {
        get => _feeText;
        set { var c = new string((value ?? "").Where(ch => ch is >= '0' and <= '9').ToArray()); if (_feeText == c) return; _feeText = c; OnPropertyChanged(); }
    }

    private DateTime _startDate;
    public DateTime StartDate { get => _startDate; set { _startDate = value; OnPropertyChanged(); RecomputeExpiry(); } }

    public string CurrentExpiryText => $"Expires {DateUtils.FormatDate(Member.ExpiryMillis)}";
    public MemberStatus Status => MemberStatusExtensions.StatusOf(Member.ExpiryMillis);

    private string _newExpiryText = "";
    public string NewExpiryText { get => _newExpiryText; private set { _newExpiryText = value; OnPropertyChanged(); } }

    public ICommand ConfirmRenewalCommand { get; }
    public ICommand BackCommand { get; }

    public RenewViewModel(Repository repository, NavigationViewModel nav, Member member)
    {
        _repository = repository;
        _nav = nav;
        Member = member;

        _plan = member.Plan;
        _feeText = ((long)member.Fee).ToString(); // Android: member.fee.toInt().toString() — shown even when 0

        var today = DateUtils.ToMillis(DateOnly.FromDateTime(DateTime.Now));
        var startMillis = member.ExpiryMillis > today ? member.ExpiryMillis : today;
        _startDate = DateUtils.ToLocalDate(startMillis).ToDateTime(TimeOnly.MinValue);

        ConfirmRenewalCommand = new RelayCommand(ConfirmRenewal);
        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Profile(Member.Id)));

        RecomputeExpiry();
    }

    private void RecomputeExpiry()
    {
        var months = DateUtils.PlanMonths.GetValueOrDefault(Plan, 1L);
        var startMillis = new DateTimeOffset(StartDate).ToUnixTimeMilliseconds();
        var newExpiry = DateUtils.AddMonthsMillis(startMillis, months);
        NewExpiryText = DateUtils.FormatDate(newExpiry);
    }

    private void ConfirmRenewal()
    {
        var months = DateUtils.PlanMonths.GetValueOrDefault(Plan, 1L);
        var startMillis = new DateTimeOffset(StartDate).ToUnixTimeMilliseconds();
        var newExpiry = DateUtils.AddMonthsMillis(startMillis, months);
        var feeVal = double.TryParse(FeeText, out var f) ? f : 0.0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var history = History.ToHistoryList(Member.HistoryJson);
        history.Add(new HistoryEntry("Renewed", Plan, feeVal, startMillis, newExpiry));

        Member.Plan = Plan;
        Member.Fee = feeVal;
        Member.ExpiryMillis = newExpiry;
        Member.HistoryJson = History.ToJson(history);
        Member.UpdatedAtMillis = now;
        // Old QR must stop working the moment membership terms change underneath it —
        // Android parity (see QrUtils' compatibility-contract doc comment).
        Member.QrToken = QrUtils.FreshToken();
        Member.QrTokenExpiryMillis = now + QrUtils.TokenValidityMillis;

        _repository.Save(Member);
        _nav.NavigateTo(new Screen.Renewed(Member.Id, JustRenewed: true));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
