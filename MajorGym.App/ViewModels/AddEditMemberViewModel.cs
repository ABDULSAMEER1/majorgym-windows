using System.IO;
using System.ComponentModel;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>AddEditMemberScreen</c> composable (Screens.kt ~870-1268).
/// Business rules preserved exactly: name must be at least 3 trimmed characters, phone exactly
/// the 10 digits Android's filter allows (validity is <c>length &gt;= 10</c>) and not already used
/// by another member, due amount ("fee") is optional (defaults to 0 — Android's own "Fix #2"),
/// a passkey is generated and hashed ONLY for a brand-new member, joined/expiry are the
/// start-of-local-day millis <c>joined.toMillis()</c> Android stores, a new member gets the single
/// "Joined" history entry while an edit leaves history untouched, and every already-set field
/// (QR token, password hash, fingerprint template, created/last-attendance timestamps) is carried
/// forward on an edit — including the fingerprint-preservation fix Android's own comment documents.
///
/// Phase 1 fixes over the earlier Windows port:
///  * Joined date was a plain auto-property with no UI control (not selectable) and was captured
///    as <c>DateTime.Now</c> (time of day included) instead of the start-of-day millis Android
///    stores — so join/expiry millis differed from Android's and the expiry preview never moved
///    when the date changed. It is now a notifying property bound to a date picker, converted
///    with <see cref="DateUtils.ToMillis"/>.
///  * Every dependent value (expiry projection, validity, duplicate-phone error) now raises
///    PropertyChanged when its inputs change, and validity re-queries the Save command — the
///    Save button used to stay disabled forever (see <see cref="RelayCommand"/>).
///  * ID proof text is restricted to ASCII letters/digits with Android's error message; fee and
///    phone are digits-only (see Controls/InputGuard); photo previews and Delete-ID-photo added.
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
    public string SaveButtonText => IsEditing ? "Save Changes" : "Add Member";

    private string _name;
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; OnPropertyChanged(); Revalidate(); }
    }

    private string _phone;
    public string Phone
    {
        get => _phone;
        set
        {
            // Android: phone = it.filter { isDigit }.take(10)
            var cleaned = new string((value ?? "").Where(c => c is >= '0' and <= '9').Take(10).ToArray());
            if (_phone == cleaned) return;
            _phone = cleaned;
            OnPropertyChanged();
            RecheckPhone();
        }
    }

    private DateTime _joined;
    /// <summary>Date-only (always midnight local) — see <see cref="JoinedMillis"/>.</summary>
    public DateTime Joined
    {
        get => _joined;
        set
        {
            if (_joined == value.Date) return;
            _joined = value.Date;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpiryPreview));
        }
    }

    private long JoinedMillis => DateUtils.ToMillis(DateOnly.FromDateTime(_joined));

    private string _plan;
    public string Plan
    {
        get => _plan;
        set
        {
            if (_plan == value) return;
            _plan = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpiryPreview));
        }
    }

    private string _feeText;
    public string FeeText
    {
        get => _feeText;
        // Android: fee = it.filter { isDigit }
        set { var c = new string((value ?? "").Where(ch => ch is >= '0' and <= '9').ToArray()); if (_feeText == c) return; _feeText = c; OnPropertyChanged(); }
    }

    private string? _photoPath;
    public string? PhotoPath { get => _photoPath; private set { _photoPath = value; OnPropertyChanged(); } }

    private string _idProof;
    public string IdProof
    {
        get => _idProof;
        // Android: filter { isLetterOrDigit && code < 128 }; idProofError = filtered != input.
        set
        {
            var input = value ?? "";
            var filtered = new string(input.Where(c => c < 128 && char.IsLetterOrDigit(c)).ToArray());
            IdProofError = filtered != input;
            if (_idProof == filtered) return;
            _idProof = filtered;
            OnPropertyChanged();
        }
    }

    private bool _idProofError;
    public bool IdProofError { get => _idProofError; private set { if (_idProofError == value) return; _idProofError = value; OnPropertyChanged(); } }

    private string _idProofPhotoPath;
    public string IdProofPhotoPath { get => _idProofPhotoPath; private set { _idProofPhotoPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasIdProofPhoto)); } }

    public bool HasIdProofPhoto => !string.IsNullOrWhiteSpace(_idProofPhotoPath) && File.Exists(_idProofPhotoPath);

    /// <summary>Generated fresh for a new member only, shown once on the Registered screen.</summary>
    public string? GeneratedPasskey { get; }

    private bool _phoneTaken;
    public bool PhoneTaken { get => _phoneTaken; private set { if (_phoneTaken == value) return; _phoneTaken = value; OnPropertyChanged(); Revalidate(); } }

    private bool _isValid;
    public bool IsValid { get => _isValid; private set { if (_isValid == value) return; _isValid = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }

    private bool _confirmDeleteIdPhoto;
    public bool ConfirmDeleteIdPhoto { get => _confirmDeleteIdPhoto; private set { _confirmDeleteIdPhoto = value; OnPropertyChanged(); } }

    private string? _saveError;
    public string? SaveError { get => _saveError; private set { _saveError = value; OnPropertyChanged(); } }

    /// <summary>"Calculated expiry projection" — recomputed from the CURRENT plan + joining date.</summary>
    public string ExpiryPreview
    {
        get
        {
            var months = DateUtils.PlanMonths.GetValueOrDefault(Plan, 1L);
            return DateUtils.FormatDate(DateUtils.AddMonthsMillis(JoinedMillis, months));
        }
    }

    public ICommand ChoosePhotoCommand { get; }
    public ICommand ChooseIdPhotoCommand { get; }
    public ICommand AskDeleteIdPhotoCommand { get; }
    public ICommand ConfirmDeleteIdPhotoCommand { get; }
    public ICommand CancelDeleteIdPhotoCommand { get; }
    public ICommand IdProofRejectedCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BackCommand { get; }

    public AddEditMemberViewModel(Repository repository, PhotoStore photoStore, NavigationViewModel nav, string? existingId)
    {
        _repository = repository;
        _photoStore = photoStore;
        _nav = nav;
        _existing = existingId is not null ? _repository.GetById(existingId) : null;
        _id = _existing?.Id ?? Guid.NewGuid().ToString();

        _name = _existing?.Name ?? "";
        _phone = _existing?.Phone ?? "";
        // joined = existing?.joinedMillis?.toLocalDate() ?: LocalDate.now()
        _joined = (_existing is not null ? DateUtils.ToLocalDate(_existing.JoinedMillis) : DateOnly.FromDateTime(DateTime.Now))
            .ToDateTime(TimeOnly.MinValue);
        _plan = _existing?.Plan ?? "1 Month";
        // existing?.fee?.toInt()?.toString() ?: ""  -> an existing member with fee 0 shows "0".
        _feeText = _existing is not null ? ((long)_existing.Fee).ToString() : "";
        _photoPath = _existing?.PhotoPath;
        _idProof = _existing?.IdProof ?? "";
        _idProofPhotoPath = _existing?.IdProofPhotoPath ?? "";
        GeneratedPasskey = _existing is null ? PasskeyUtils.Generate() : null;

        ChoosePhotoCommand = new RelayCommand(ChoosePhoto);
        ChooseIdPhotoCommand = new RelayCommand(ChooseIdPhoto);
        AskDeleteIdPhotoCommand = new RelayCommand(() => ConfirmDeleteIdPhoto = true);
        CancelDeleteIdPhotoCommand = new RelayCommand(() => ConfirmDeleteIdPhoto = false);
        ConfirmDeleteIdPhotoCommand = new RelayCommand(() =>
        {
            // vm.deleteIdProofPhoto(id); idProofPhotoPath = ""; confirmDeleteIdPhoto = false
            _photoStore.DeleteIdProofPhoto(_id);
            IdProofPhotoPath = "";
            ConfirmDeleteIdPhoto = false;
        });
        IdProofRejectedCommand = new RelayCommand(() => IdProofError = true);
        SaveCommand = new RelayCommand(Save, () => IsValid);
        BackCommand = new RelayCommand(() =>
            _nav.NavigateTo(_existing is not null ? new Screen.Profile(_existing.Id) : new Screen.Members()));

        RecheckPhone();
        Revalidate();
    }

    private void RecheckPhone()
    {
        // LaunchedEffect(phone) { phoneTaken = if (phone.length >= 10) vm.isPhoneTaken(phone, excludingId = id) else false }
        PhoneTaken = Phone.Length >= 10 && _repository.IsPhoneTaken(Phone, excludingId: _id);
        Revalidate();
    }

    // Fix #2 (Android): Due Amount is optional - not part of the validity gate.
    private void Revalidate() =>
        IsValid = Name.Trim().Length >= 3 && Phone.Length >= 10 && !PhoneTaken;

    private static Microsoft.Win32.OpenFileDialog NewImageDialog() => new()
    {
        Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
    };

    private void ChoosePhoto()
    {
        var dlg = NewImageDialog();
        if (dlg.ShowDialog() != true) return;
        var saved = _photoStore.SaveMemberPhoto(_id, dlg.FileName);
        if (saved is not null) PhotoPath = saved;
    }

    private void ChooseIdPhoto()
    {
        var dlg = NewImageDialog();
        if (dlg.ShowDialog() != true) return;
        IdProofPhotoPath = _photoStore.SaveIdProofPhoto(_id, dlg.FileName);
    }

    private void Save()
    {
        SaveError = null;
        try
        {
            // Re-check at the moment of saving (another entry could have taken the number).
            if (_repository.IsPhoneTaken(Phone, excludingId: _id)) { PhoneTaken = true; return; }

            var joinedMillis = JoinedMillis;
            var months = DateUtils.PlanMonths.GetValueOrDefault(Plan, 1L);
            var expiryMillis = DateUtils.AddMonthsMillis(joinedMillis, months);
            // fee.toDoubleOrNull() ?: 0.0
            var feeVal = double.TryParse(FeeText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0.0;
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
        }
        catch (Exception ex)
        {
            // Nothing was navigated away from, so the owner keeps everything they typed.
            SaveError = $"Could not save the member: {ex.Message}";
            return;
        }

        _nav.NavigateTo(_existing is null
            ? new Screen.Registered(_id, GeneratedPasskey!)
            : new Screen.Profile(_id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
