using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>ProfileScreen</c> composable (Screens.kt ~1268-1512). Phase 1
/// rewrite: the earlier Windows port covered the info rows and Due Payment, but was missing
/// several whole features Android has on this screen — all added here, faithfully:
///  * ID Proof Photo card (own section, tap to view full-screen; "No ID Proof Photo" text
///    when there isn't one).
///  * "View QR" / "Regenerate QR" action — regenerates the token only when it's expired
///    (<see cref="QrUtils.IsTokenValid"/>), then always navigates to the QR screen.
///  * Enroll / Re-enroll Fingerprint action, plus a separate Remove Fingerprint action
///    (only shown once a fingerprint exists) gated behind its own confirmation dialog —
///    Android's own "Fix #11" comment, preserved.
///  * Full-screen photo viewers for the profile photo and the ID proof photo.
///  * The Due Payment error messages now match Android's exact copy, including the
///    overpayment guard Android has and this port previously lacked entirely (a payment
///    greater than the due amount used to be silently accepted, which could not happen on
///    Android and would corrupt figures on Windows).
/// Everything below still follows the same rule Android's own doc comments call out: Due
/// Payment only ever adjusts the due balance — never plan, expiry, or history.
/// </summary>
public sealed class ProfileViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    public Member Member { get; private set; }

    public MemberStatus Status { get; private set; }
    public string DaysRemainingText { get; private set; } = "";
    public string JoinedText => DateUtils.FormatDate(Member.JoinedMillis);
    public string ExpiresText => DateUtils.FormatDate(Member.ExpiryMillis);
    public string DueAmountText => DateUtils.FormatMoney(Member.Fee);
    public bool HasDue => Member.Fee > 0.0;
    public string? LastRenewedText { get; private set; }
    public bool HasFingerprint => Member.FingerprintTemplate is not null;
    public string FingerprintStatusText => HasFingerprint ? "Enrolled" : "Not Enrolled";
    public string EnrollButtonText => HasFingerprint ? "Re-enroll" : "Enroll";
    public string IdProofText => string.IsNullOrWhiteSpace(Member.IdProof) ? "Not Provided" : Member.IdProof;
    public bool HasIdProofPhoto => !string.IsNullOrWhiteSpace(Member.IdProofPhotoPath) && File.Exists(Member.IdProofPhotoPath);
    public string QrButtonText => QrUtils.IsTokenValid(Member) ? "View QR" : "Regenerate QR";

    public IReadOnlyList<HistoryEntry> HistoryEntries { get; private set; } = Array.Empty<HistoryEntry>();

    private string _amountPaidText = "";
    // Android: amountPaidText = it.filter { c -> c.isDigit() }; paymentError = null
    public string AmountPaidText
    {
        get => _amountPaidText;
        set
        {
            var c = new string((value ?? "").Where(ch => ch is >= '0' and <= '9').ToArray());
            if (_amountPaidText == c) return;
            _amountPaidText = c;
            PaymentError = null;
            OnPropertyChanged();
        }
    }

    private string? _paymentError;
    public string? PaymentError { get => _paymentError; private set { _paymentError = value; OnPropertyChanged(); } }

    // ---- Delete confirmation ----
    // Android shows a modal AlertDialog ("Delete member" / "Delete {name}? This cannot be
    // undone." / Delete+Cancel) before ever calling vm.delete(member) — Screens.kt ~1480.
    // Ported as an inline confirm panel (IsConfirmingDelete) rather than a separate popup
    // Window — the BEHAVIORAL contract (must confirm, must be cancellable, deletion is
    // irreversible) is preserved exactly; only the visual presentation differs. See ProfileView.xaml.
    private bool _isConfirmingDelete;
    public bool IsConfirmingDelete { get => _isConfirmingDelete; private set { _isConfirmingDelete = value; OnPropertyChanged(); } }

    // ---- Remove-fingerprint confirmation (Android "Fix #11") ----
    private bool _isConfirmingRemoveFingerprint;
    public bool IsConfirmingRemoveFingerprint { get => _isConfirmingRemoveFingerprint; private set { _isConfirmingRemoveFingerprint = value; OnPropertyChanged(); } }

    public ICommand RecordPaymentCommand { get; }
    public ICommand RenewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand ViewQrCommand { get; }
    public ICommand EnrollFingerprintCommand { get; }
    public ICommand RequestRemoveFingerprintCommand { get; }
    public ICommand ConfirmRemoveFingerprintCommand { get; }
    public ICommand CancelRemoveFingerprintCommand { get; }
    public ICommand ViewPhotoCommand { get; }
    public ICommand ViewIdPhotoCommand { get; }
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
        ViewQrCommand = new RelayCommand(ViewQr);
        EnrollFingerprintCommand = new RelayCommand(() =>
            _nav.NavigateTo(new Screen.EnrollFingerprint(Member.Id, new Screen.Profile(Member.Id))));
        RequestRemoveFingerprintCommand = new RelayCommand(() => IsConfirmingRemoveFingerprint = true);
        ConfirmRemoveFingerprintCommand = new RelayCommand(RemoveFingerprint);
        CancelRemoveFingerprintCommand = new RelayCommand(() => IsConfirmingRemoveFingerprint = false);
        ViewPhotoCommand = new RelayCommand(() => Controls.PhotoViewerWindow.ShowFor(Member.PhotoPath, Member.Name));
        ViewIdPhotoCommand = new RelayCommand(() => Controls.PhotoViewerWindow.ShowFor(Member.IdProofPhotoPath, $"{Member.Name} \u2014 ID Proof"));
        ViewAttendanceHistoryCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.AttendanceHistory(Member.Id)));
        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Members()));
    }

    private void Recompute()
    {
        Status = MemberStatusExtensions.StatusOf(Member.ExpiryMillis);
        var days = DateUtils.DaysBetweenNow(Member.ExpiryMillis);
        DaysRemainingText = days < 0 ? $"Expired {-days} day(s) ago" : $"{days} day(s) remaining";

        var history = History.ToHistoryList(Member.HistoryJson);
        history.Reverse();
        HistoryEntries = history;
        var lastRenewed = history.FirstOrDefault(h => h.Type == "Renewed");
        LastRenewedText = lastRenewed is not null ? DateUtils.FormatDate(lastRenewed.DateMillis) : null;
    }

    /// <summary>Ported from Android's Due Payment flow. Never touches plan, expiry, or
    /// history — only the due balance, clamped so it can never go negative, and rejecting
    /// (rather than silently clamping) any amount greater than what's actually due.</summary>
    private void RecordPayment()
    {
        var paid = double.TryParse(AmountPaidText, out var p) ? p : 0.0;
        if (paid <= 0.0)
        {
            PaymentError = "Enter an amount to record a payment.";
            return;
        }
        if (paid > Member.Fee)
        {
            PaymentError = $"Amount paid cannot exceed the due amount ({DateUtils.FormatMoney(Member.Fee)}).";
            return;
        }
        var newDue = Math.Max(Member.Fee - paid, 0.0);
        Member.Fee = newDue;
        Member.UpdatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repository.Save(Member);
        _amountPaidText = "";
        OnPropertyChanged(nameof(AmountPaidText));
        PaymentError = null;
        OnPropertyChanged(nameof(DueAmountText));
        OnPropertyChanged(nameof(HasDue));
    }

    /// <summary>Android: regenerate the token only if it's currently invalid/expired, then
    /// always navigate to the QR screen (Screen.Renewed reused here exactly as Android
    /// reuses it — see RenewedViewModel's own doc comment for why that's the right route
    /// for a bare QR view/regenerate, not just post-renewal).</summary>
    private void ViewQr()
    {
        if (!QrUtils.IsTokenValid(Member))
        {
            Member.QrToken = QrUtils.FreshToken();
            Member.QrTokenExpiryMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + QrUtils.TokenValidityMillis;
            Member.UpdatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _repository.Save(Member);
        }
        _nav.NavigateTo(new Screen.Renewed(Member.Id, JustRenewed: false));
    }

    /// <summary>Android "Fix #11": clears the stored template and bumps UpdatedAtMillis
    /// (repo.save(member.copy(fingerprintTemplate = null, updatedAtMillis = now))) so the
    /// removal also propagates correctly wherever sync eventually reads UpdatedAtMillis.</summary>
    private void RemoveFingerprint()
    {
        Member.FingerprintTemplate = null;
        Member.UpdatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repository.Save(Member);
        IsConfirmingRemoveFingerprint = false;
        OnPropertyChanged(nameof(HasFingerprint));
        OnPropertyChanged(nameof(FingerprintStatusText));
        OnPropertyChanged(nameof(EnrollButtonText));
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
