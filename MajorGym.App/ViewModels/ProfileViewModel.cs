using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>ProfileScreen</c> composable (Screens.kt lines ~1270-1420).
/// Shows the same info rows in the same order (Phone, Joined, Renewed-if-any, Expires,
/// Current Plan, Due Amount, ID Proof, Fingerprint status), the same Due Payment flow
/// (only visible while Fee &gt; 0, reduces — never goes negative — the due balance without
/// touching plan/expiry/history), and the same navigation entry points (Renew, Edit,
/// Delete, Enroll Fingerprint).
/// </summary>
public sealed class ProfileViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    public Member Member { get; private set; }

    public string StatusText { get; private set; } = "";
    public string DaysRemainingText { get; private set; } = "";
    public string JoinedText => DateUtils.FormatDate(Member.JoinedMillis);
    public string DueAmountText => DateUtils.FormatMoney(Member.Fee);
    public string DueAmountBrushKey => Member.Fee > 0.0 ? "GymDanger" : "GymSuccess";
    public bool HasDue => Member.Fee > 0.0;
    public string? LastRenewedText { get; private set; }
    public string FingerprintStatusText => Member.FingerprintTemplate is not null ? "Enrolled" : "Not Enrolled";
    public string IdProofText => string.IsNullOrWhiteSpace(Member.IdProof) ? "Not Provided" : Member.IdProof;

    private string _amountPaidText = "";
    public string AmountPaidText { get => _amountPaidText; set { _amountPaidText = value; OnPropertyChanged(); } }

    private string? _paymentError;
    public string? PaymentError { get => _paymentError; private set { _paymentError = value; OnPropertyChanged(); } }

    // ---- Delete confirmation (Stage 4) ----
    // Android shows a modal AlertDialog ("Delete member" / "Delete {name}? This cannot be
    // undone." / Delete+Cancel buttons) before ever calling vm.delete(member) — Screens.kt
    // ~1480. WPF has no equivalent of composing a dialog inline over the current screen
    // content the same lightweight way Compose does, so this is ported as an inline
    // confirm panel toggled by IsConfirmingDelete rather than a separate popup Window: the
    // BEHAVIORAL contract Android's brief cares about ("must confirm before delete, must be
    // able to cancel, deletion is irreversible") is preserved exactly — only the visual
    // presentation of the confirmation differs. See ProfileView.xaml.
    private bool _isConfirmingDelete;
    public bool IsConfirmingDelete { get => _isConfirmingDelete; private set { _isConfirmingDelete = value; OnPropertyChanged(); } }

    public ICommand RecordPaymentCommand { get; }
    public ICommand RenewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand EnrollFingerprintCommand { get; }
    public ICommand ViewAttendanceHistoryCommand { get; }
    public ICommand BackCommand { get; }

    public ProfileViewModel(Repository repository, NavigationViewModel nav, string memberId)
    {
        _repository = repository;
        _nav = nav;
        Member = _repository.GetById(memberId) ?? throw new InvalidOperationException($"Member {memberId} not found");
        Recompute();

        RecordPaymentCommand = new RelayCommand(RecordPayment);
        RenewCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Renew(Member.Id)));
        EditCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Edit(Member.Id)));
        RequestDeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        ConfirmDeleteCommand = new RelayCommand(Delete);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        EnrollFingerprintCommand = new RelayCommand(() =>
            _nav.NavigateTo(new Screen.EnrollFingerprint(Member.Id, new Screen.Profile(Member.Id))));
        ViewAttendanceHistoryCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.AttendanceHistory(Member.Id)));
        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Members()));
    }

    private void Recompute()
    {
        var status = MemberStatusExtensions.StatusOf(Member.ExpiryMillis);
        StatusText = status switch
        {
            MemberStatus.ACTIVE => "Active",
            MemberStatus.EXPIRING => "Expiring Soon",
            MemberStatus.EXPIRED => "Expired",
            _ => "Unknown"
        };
        var days = DateUtils.DaysBetweenNow(Member.ExpiryMillis);
        DaysRemainingText = days < 0 ? $"Expired {-days} day(s) ago" : $"{days} day(s) remaining";

        var history = History.ToHistoryList(Member.HistoryJson);
        history.Reverse();
        var lastRenewed = history.FirstOrDefault(h => h.Type == "Renewed");
        LastRenewedText = lastRenewed is not null ? DateUtils.FormatDate(lastRenewed.DateMillis) : null;
    }

    /// <summary>Ported from Android's Due Payment flow: reduces (never below zero) the
    /// due balance by the amount entered — never touches plan, expiry, or history, and
    /// never accepts an amount greater than what's actually due (Android doc comment,
    /// preserved: "the newDue = (member.fee - paid).coerceAtLeast(0.0)" clamp).</summary>
    private void RecordPayment()
    {
        if (!double.TryParse(AmountPaidText, out var paid) || paid <= 0)
        {
            PaymentError = "Enter a valid amount";
            return;
        }
        PaymentError = null;
        var newDue = Math.Max(Member.Fee - paid, 0.0);
        Member.Fee = newDue;
        Member.UpdatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repository.Save(Member);
        AmountPaidText = "";
        OnPropertyChanged(nameof(DueAmountText));
        OnPropertyChanged(nameof(DueAmountBrushKey));
        OnPropertyChanged(nameof(HasDue));
    }

    private void Delete()
    {
        // Photo cleanup is intentionally not wired through this ViewModel constructor to
        // avoid a PhotoStore dependency here purely for delete — App.xaml.cs's shared
        // singleton PhotoStore instance is used directly, matching how Android's
        // Repository.deleteWithFiles is itself the single owner of that file-cleanup step.
        _repository.DeleteWithFiles(Member, App.PhotoStore);
        _nav.NavigateTo(new Screen.Members());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
