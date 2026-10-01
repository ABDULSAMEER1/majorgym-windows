using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using MajorGym.Data;
using MajorGym.Data.Settings;

namespace MajorGym.App.ViewModels;

/// <summary>One row of Android's "Paired Devices" list.</summary>
public sealed record PairedDeviceRow(string Name, string LastSyncedText);

/// <summary>
/// Windows port of Android's <c>SyncScreen</c> state (SyncScreen.kt). Same sections in the same
/// order: header (title + relocated Dashboard number-visibility gear + privacy eye), "This Device"
/// card (device name), "Sync Circle Code" card (Generate New / Save Code), the Sync Now button with
/// its status line, and the Paired Devices list. The actual synchronisation is delegated to
/// <see cref="SyncManager"/> — nothing here fakes success: every message shown comes from a real
/// <see cref="SyncOutcome"/> or a real status callback from the transport.
///
/// Status strings and outcome messages are copied verbatim from Android.
/// </summary>
public sealed class SyncViewModel : INotifyPropertyChanged
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Android: no 0/O/1/I

    private readonly SyncPrefs _prefs;
    private readonly SyncManager _sync;
    private readonly DashboardPrivacyPrefs _privacy;

    private string _deviceName;
    private string _codeInput;
    private string? _status;
    private bool _isSyncing;
    private bool _masterPrivacyOn;
    private bool _showCardSettings;
    private bool _totalVisible, _activeVisible, _expiringVisible, _expiredVisible, _dueVisible;

    public SyncViewModel(SyncPrefs prefs, SyncManager sync)
    {
        _prefs = prefs;
        _sync = sync;
        _privacy = new DashboardPrivacyPrefs(App.AppDataDirectory);

        _deviceName = prefs.DeviceName;
        _codeInput = prefs.SyncCode ?? "";
        RefreshPaired();

        _masterPrivacyOn = _privacy.MasterPrivacyOn;
        _totalVisible = _privacy.IsNumberVisible(DashboardCard.TOTAL);
        _activeVisible = _privacy.IsNumberVisible(DashboardCard.ACTIVE);
        _expiringVisible = _privacy.IsNumberVisible(DashboardCard.EXPIRING);
        _expiredVisible = _privacy.IsNumberVisible(DashboardCard.EXPIRED);
        _dueVisible = _privacy.IsNumberVisible(DashboardCard.DUE);

        GenerateNewCommand = new RelayCommand(GenerateNew, () => !IsSyncing);
        SaveCodeCommand = new RelayCommand(() => _prefs.SyncCode = _codeInput, () => _codeInput.Length >= 4 && !IsSyncing);
        SyncNowCommand = new RelayCommand(StartSync, () => !IsSyncing);
        ToggleMasterPrivacyCommand = new RelayCommand(() => MasterPrivacyOn = !MasterPrivacyOn);
        OpenCardSettingsCommand = new RelayCommand(() => ShowCardSettings = true);
        CloseCardSettingsCommand = new RelayCommand(() => ShowCardSettings = false);
    }

    // ---- This Device ----
    public string DeviceName
    {
        get => _deviceName;
        set { _deviceName = value; _prefs.DeviceName = value; OnPropertyChanged(); } // Android: persisted on every keystroke
    }

    // ---- Sync Circle Code ----
    public string CodeInput
    {
        get => _codeInput;
        set { _codeInput = value ?? ""; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public ICommand GenerateNewCommand { get; }
    public ICommand SaveCodeCommand { get; }

    private void GenerateNew()
    {
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        CodeInput = new string(chars);
        _prefs.SyncCode = _codeInput; // Android: Generate New also saves it
    }

    // ---- Sync Now ----
    public bool IsSyncing
    {
        get => _isSyncing;
        private set { _isSyncing = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotSyncing)); CommandManager.InvalidateRequerySuggested(); }
    }
    public bool IsNotSyncing => !_isSyncing;

    public string? Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public ICommand SyncNowCommand { get; }

    private async void StartSync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        Status = "Starting sync\u2026";
        var dispatcher = Application.Current.Dispatcher;
        SyncOutcome outcome;
        try
        {
            // Networking runs off the UI thread; SyncManager marshals every database call back onto
            // it (single shared SQLite connection — see WpfDbThread).
            outcome = await Task.Run(() => _sync.RunSyncAsync(msg => dispatcher.BeginInvoke(new Action(() => Status = msg))));
        }
        catch (Exception e)
        {
            outcome = new SyncOutcome.Error(e.Message);
        }

        IsSyncing = false;
        Status = outcome switch
        {
            SyncOutcome.Success s => $"Synced with {s.PeerName} \u2014 {s.RecordCount} record(s) merged.",
            SyncOutcome.NoCodeSet => "Set a sync code first.",
            SyncOutcome.NotFound => "No authorized device found. Make sure all phones are on the same Wi-Fi or hotspot, have the same sync code, and have this Sync screen open.",
            SyncOutcome.Error e => $"Sync failed: {e.Message}",
            _ => "Sync failed"
        };
        RefreshPaired();
    }

    // ---- Paired Devices ----
    public ObservableCollection<PairedDeviceRow> Paired { get; } = new();
    public string PairedHeader => $"PAIRED DEVICES ({Paired.Count})";
    public bool HasPaired => Paired.Count > 0;
    public bool HasNoPaired => Paired.Count == 0;

    private void RefreshPaired()
    {
        Paired.Clear();
        foreach (var d in _prefs.PairedDevices())
            Paired.Add(new PairedDeviceRow(d.Name, $"Last synced {DateUtils.FormatDate(d.LastSyncedMillis)}"));
        OnPropertyChanged(nameof(PairedHeader));
        OnPropertyChanged(nameof(HasPaired));
        OnPropertyChanged(nameof(HasNoPaired));
    }

    // ---- Relocated Dashboard controls (same DashboardPrivacyPrefs storage as Android) ----
    public bool MasterPrivacyOn
    {
        get => _masterPrivacyOn;
        set
        {
            _masterPrivacyOn = value;
            _privacy.MasterPrivacyOn = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(ShowGear));
        }
    }
    /// <summary>Android shows the gear only while privacy mode is off.</summary>
    public bool ShowGear => !_masterPrivacyOn;

    public bool ShowCardSettings { get => _showCardSettings; private set { _showCardSettings = value; OnPropertyChanged(); } }

    public bool TotalVisible { get => _totalVisible; set { _totalVisible = value; _privacy.SetNumberVisible(DashboardCard.TOTAL, value); OnPropertyChanged(); } }
    public bool ActiveVisible { get => _activeVisible; set { _activeVisible = value; _privacy.SetNumberVisible(DashboardCard.ACTIVE, value); OnPropertyChanged(); } }
    public bool ExpiringVisible { get => _expiringVisible; set { _expiringVisible = value; _privacy.SetNumberVisible(DashboardCard.EXPIRING, value); OnPropertyChanged(); } }
    public bool ExpiredVisible { get => _expiredVisible; set { _expiredVisible = value; _privacy.SetNumberVisible(DashboardCard.EXPIRED, value); OnPropertyChanged(); } }
    public bool DueVisible { get => _dueVisible; set { _dueVisible = value; _privacy.SetNumberVisible(DashboardCard.DUE, value); OnPropertyChanged(); } }

    public ICommand ToggleMasterPrivacyCommand { get; }
    public ICommand OpenCardSettingsCommand { get; }
    public ICommand CloseCardSettingsCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
