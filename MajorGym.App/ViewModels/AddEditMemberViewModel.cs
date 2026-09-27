using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>AddEditMemberScreen</c> composable (Screens.kt lines ~870-1157).
/// Business rules preserved exactly: name must be at least 3 trimmed characters, phone at
/// least 10 characters and not already used by another member, due amount ("fee") is
/// optional (defaults to 0 — Android's own "Fix #2: Due Amount is optional" comment,
/// preserved), a passkey is generated and hashed ONLY for a brand-new member (an edit never
/// regenerates or re-displays it), and every already-set field (history, QR token, password
/// hash, fingerprint template, created/last-attendance timestamps) is carried forward
/// untouched on an edit rather than reset — including the exact fingerprint-preservation
/// fix Android's own comment documents ("this constructor previously omitted
/// fingerprintTemplate... wiping out an enrolled fingerprint any time the member was
/// edited").
/// </summary>
public sealed class AddEditMemberViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly PhotoStore _photoStore;
    private readonly NavigationViewModel _nav;
    private readonly Member? _existing;
    private readonly string _id;

    public bool IsEditing => _existing is not null;
    public string HeaderText => IsEditing ? "EDIT MEMBER" : "ADD MEMBER";

    private string _name;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); Revalidate(); } }

    private string _phone;
    public string Phone { get => _phone; set { _phone = value; OnPropertyChanged(); RecheckPhone(); } }

    public DateTime Joined { get; set; }

    /// <summary>Available plan lengths, in Android's declared order (DateUtils.PlanMonths
    /// is a LinkedHashMap specifically so this order is stable) — bound to the plan
    /// selector in the view.</summary>
    public IReadOnlyList<string> PlanOptions { get; } = DateUtils.PlanMonths.Keys.ToList();

    private string _plan;
    public string Plan { get => _plan; set { _plan = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpiryPreview)); } }

    private string _feeText;
    public string FeeText { get => _feeText; set { _feeText = value; OnPropertyChanged(); } }

    public string? PhotoPath { get; private set; }
    public string IdProof { get; set; }
    public string IdProofPhotoPath { get; private set; }

    /// <summary>Generated fresh for a new member only, shown once on the Registered
    /// screen — never regenerated or re-shown for an existing member (Android parity).</summary>
    public string? GeneratedPasskey { get; }

    private bool _phoneTaken;
    public bool PhoneTaken { get => _phoneTaken; private set { _phoneTaken = value; OnPropertyChanged(); Revalidate(); } }

    private bool _isValid;
    public bool IsValid { get => _isValid; private set { _isValid = value; OnPropertyChanged(); } }

    public string ExpiryPreview
    {
        get
        {
            var months = DateUtils.PlanMonths.GetValueOrDefault(Plan, 1L);
            var expiry = DateUtils.AddMonthsMillis(new DateTimeOffset(Joined).ToUnixTimeMilliseconds(), months);
            return DateUtils.FormatDate(expiry);
        }
    }

    public ICommand ChoosePhotoCommand { get; }
    public ICommand ChooseIdPhotoCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public AddEditMemberViewModel(Repository repository, PhotoStore photoStore, NavigationViewModel nav, string? existingId)
    {
        _repository = repository;
        _photoStore = photoStore;
        _nav = nav;
        _existing = existingId is not null ? _repository.GetById(existingId) : null;
        _id = _existing?.Id ?? Guid.NewGuid().ToString();

        _name = _existing?.Name ?? "";
        _phone = _existing?.Phone ?? "";
        Joined = _existing is not null ? DateUtils.ToLocalDate(_existing.JoinedMillis).ToDateTime(TimeOnly.MinValue) : DateTime.Now;
        _plan = _existing?.Plan ?? "1 Month";
        _feeText = _existing is { Fee: > 0 } ? ((int)_existing.Fee).ToString() : "";
        PhotoPath = _existing?.PhotoPath;
        IdProof = _existing?.IdProof ?? "";
        IdProofPhotoPath = _existing?.IdProofPhotoPath ?? "";
        GeneratedPasskey = _existing is null ? PasskeyUtils.Generate() : null;

        ChoosePhotoCommand = new RelayCommand(ChoosePhoto);
        ChooseIdPhotoCommand = new RelayCommand(ChooseIdPhoto);
        SaveCommand = new RelayCommand(Save, () => IsValid);
        CancelCommand = new RelayCommand(() =>
            _nav.NavigateTo(_existing is not null ? new Screen.Profile(_existing.Id) : new Screen.Members()));

        Revalidate();
    }

    private void RecheckPhone()
    {
        PhoneTaken = Phone.Length >= 10 && _repository.IsPhoneTaken(Phone, excludingId: _id);
    }

    private void Revalidate() =>
        IsValid = Name.Trim().Length >= 3 && Phone.Length >= 10 && !PhoneTaken;

    private void ChoosePhoto()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dlg.ShowDialog() == true)
        {
            var saved = _photoStore.SaveMemberPhoto(_id, dlg.FileName);
            if (saved is not null) { PhotoPath = saved; OnPropertyChanged(nameof(PhotoPath)); }
        }
    }

    private void ChooseIdPhoto()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dlg.ShowDialog() == true)
        {
            var saved = _photoStore.SaveIdProofPhoto(_id, dlg.FileName);
            IdProofPhotoPath = saved;
            OnPropertyChanged(nameof(IdProofPhotoPath));
        }
    }

    private void Save()
    {
        var joinedMillis = new DateTimeOffset(Joined).ToUnixTimeMilliseconds();
        var months = DateUtils.PlanMonths.GetValueOrDefault(Plan, 1L);
        var expiryMillis = DateUtils.AddMonthsMillis(joinedMillis, months);
        var feeVal = double.TryParse(FeeText, out var f) ? f : 0.0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var history = _existing is null
            ? new List<HistoryEntry> { new("Joined", Plan, feeVal, joinedMillis, expiryMillis) }
            : History.ToHistoryList(_existing.HistoryJson);

        var member = new Member
        {
            Id = _id,
            Name = Name.Trim(),
            Phone = Phone,
            PhotoPath = PhotoPath,
            Plan = Plan,
            Fee = feeVal,
            JoinedMillis = joinedMillis,
            ExpiryMillis = expiryMillis,
            HistoryJson = History.ToJson(history),
            UpdatedAtMillis = now,
            PasswordHash = _existing is null ? PasskeyUtils.Hash(GeneratedPasskey!) : _existing.PasswordHash,
            CreatedAtMillis = _existing?.CreatedAtMillis ?? now,
            LastAttendanceMillis = _existing?.LastAttendanceMillis,
            Archived = _existing?.Archived ?? false,
            QrToken = _existing?.QrToken ?? QrUtils.FreshToken(),
            QrTokenExpiryMillis = _existing?.QrTokenExpiryMillis ?? (now + QrUtils.TokenValidityMillis),
            IdProof = IdProof,
            IdProofPhotoPath = IdProofPhotoPath,
            // Same fix Android's own doc comment calls out: always carry the existing
            // fingerprint template forward untouched on an edit — it is only ever
            // changed via the dedicated enroll/replace/remove actions, never here.
            FingerprintTemplate = _existing?.FingerprintTemplate,
            PendingDeletionMillis = _existing?.PendingDeletionMillis
        };

        _repository.Save(member);

        _nav.NavigateTo(_existing is null
            ? new Screen.Registered(_id, GeneratedPasskey!)
            : new Screen.Profile(_id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
