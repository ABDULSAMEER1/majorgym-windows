using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;
using MajorGym.Data.Settings;
using Microsoft.Win32;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's Backup screen. Android used the system document picker
/// (ACTION_CREATE_DOCUMENT / ACTION_OPEN_DOCUMENT) for Export/Import and an ACTION_SEND
/// chooser for Share; the Windows equivalents are <see cref="SaveFileDialog"/>/
/// <see cref="OpenFileDialog"/> and Explorer's "select this file" behavior respectively —
/// the only changes here are those platform-appropriate picker substitutions (brief §4),
/// the actual backup data contract (<see cref="BackupManager"/>/<see cref="BackupZip"/>) is
/// untouched. Local-only throughout: no cloud service, no Google Drive, no backend.
/// </summary>
public sealed class BackupViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly BackupHistoryPrefs _history;

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }

    private bool _isError;
    public bool IsError { get => _isError; private set { _isError = value; OnPropertyChanged(); } }

    private string? _lastBackupPath;
    public string? LastBackupPath { get => _lastBackupPath; private set { _lastBackupPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanShare)); } }

    public bool CanShare => !string.IsNullOrEmpty(LastBackupPath) && File.Exists(LastBackupPath);

    // ---- Import confirmation (inline panel, matching ProfileViewModel.IsConfirmingDelete's
    // pattern — see that file's doc comment for why an inline panel is the WPF-appropriate
    // equivalent of Android's confirm AlertDialog here) ----
    private bool _isConfirmingImport;
    public bool IsConfirmingImport { get => _isConfirmingImport; private set { _isConfirmingImport = value; OnPropertyChanged(); } }
    private string? _pendingImportPath;

    public ICommand ExportCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand RequestImportCommand { get; }
    public ICommand ConfirmImportCommand { get; }
    public ICommand CancelImportCommand { get; }
    public ICommand GoHistoryCommand { get; }
    public ICommand BackCommand { get; }

    public BackupViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;
        _history = new BackupHistoryPrefs(App.AppDataDirectory);

        ExportCommand = new RelayCommand(Export);
        ShareCommand = new RelayCommand(Share, () => CanShare);
        RequestImportCommand = new RelayCommand(RequestImport);
        ConfirmImportCommand = new RelayCommand(ConfirmImport);
        CancelImportCommand = new RelayCommand(() => { IsConfirmingImport = false; _pendingImportPath = null; });
        GoHistoryCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.BackupHistory()));
        BackCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Dashboard()));
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Major Gym Backup",
            Filter = "Major Gym Backup (*.zip)|*.zip",
            FileName = $"MajorGym_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = BackupManager.ExportJson(
                _repository.GetAll(), App.PhotoStore, _repository.GetAllAttendance(), _repository.GetArchivedMembers());
            BackupZip.Write(json, dlg.FileName);
            BackupZip.ReadAndVerify(dlg.FileName); // confirm the file is actually readable before calling it a success

            _history.RecordBackupTaken();
            LastBackupPath = dlg.FileName;

            var size = DateUtils.FormatBackupSize(new FileInfo(dlg.FileName).Length);
            IsError = false;
            StatusMessage = $"Backup saved ({size}) to {dlg.FileName}.";
        }
        catch (Exception ex)
        {
            IsError = true;
            StatusMessage = $"Backup failed: {ex.Message}";
        }
    }

    private void Share()
    {
        if (!CanShare) return;
        try
        {
            // Windows equivalent of Android's ACTION_SEND chooser for a local file: reveal
            // it selected in Explorer so the owner can right-click → Share/Send To/copy it
            // to a USB drive themselves — there is no single universal "share sheet" API on
            // desktop Windows the way there is on Android.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastBackupPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            IsError = true;
            StatusMessage = $"Couldn't open Explorer: {ex.Message}";
        }
    }

    private void RequestImport()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Restore Major Gym Backup",
            Filter = "Major Gym Backup (*.zip;*.json)|*.zip;*.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        _pendingImportPath = dlg.FileName;
        IsError = false;
        StatusMessage = null;
        IsConfirmingImport = true;
    }

    private void ConfirmImport()
    {
        IsConfirmingImport = false;
        var path = _pendingImportPath;
        _pendingImportPath = null;
        if (path is null) return;

        try
        {
            string json;
            if (BackupZip.LooksLikeZip(path))
            {
                var tempFile = BackupZip.ExtractJsonToTemp(App.AppDataDirectory, path);
                try { json = File.ReadAllText(tempFile); }
                finally { BackupZip.CleanupTemp(App.AppDataDirectory); }
            }
            else
            {
                json = File.ReadAllText(path);
            }

            var members = BackupManager.ImportJson(json, App.PhotoStore);
            var attendance = BackupManager.ImportAttendance(json);
            var archived = BackupManager.ImportArchivedMembers(json);
            var (memberCount, attendanceCount, archivedCount) = _repository.ImportBackup(members, attendance, archived);

            IsError = false;
            StatusMessage = $"Restored {memberCount} member(s), {attendanceCount} attendance record(s), and {archivedCount} archived member(s).";
        }
        catch (BackupFormatException ex)
        {
            IsError = true;
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            IsError = true;
            StatusMessage = $"Restore failed: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
