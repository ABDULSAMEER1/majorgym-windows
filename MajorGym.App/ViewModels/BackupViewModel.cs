using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Settings;
using Microsoft.Win32;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Phase 2: the Windows Backup &amp; Restore screen, mirroring Android's BackupScreen section by
/// section (Open Attendance Scanner card, Export All Records, Restore Records, Share Backup
/// File with its latest-file info box, Backup History). The data work is delegated to
/// <see cref="BackupService"/>, which is a port of Android's BackupService, so the file
/// format is exactly Android's. Windows-only substitutions are the file dialogs (Android's
/// SAF pickers) and Explorer/clipboard for Share (Android's share sheet).
///
/// Threading: the database is opened on one shared SQLite connection, so every database
/// read/write stays on the UI thread; only the slow database-free work (JSON building with
/// photos, ZIP compression, unzip/parse/validate) runs on a background thread.
/// </summary>
public sealed class BackupViewModel : INotifyPropertyChanged
{
    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly BackupHistoryPrefs _history;
    private readonly LocalBackupStore _store;

    private bool _isExporting, _isRestoring, _isSharing, _messageIsError;
    private string? _message, _shareMessage, _latestName, _latestDetail;

    public bool IsExporting { get => _isExporting; private set { _isExporting = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExportLabel)); Requery(); } }
    public bool IsRestoring { get => _isRestoring; private set { _isRestoring = value; OnPropertyChanged(); OnPropertyChanged(nameof(RestoreLabel)); Requery(); } }
    public bool IsSharing { get => _isSharing; private set { _isSharing = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShareLabel)); Requery(); } }

    public string ExportLabel => IsExporting ? "Exporting\u2026" : "Export Backup";
    public string RestoreLabel => IsRestoring ? "Restoring\u2026" : "Choose Backup File";
    public string ShareLabel => IsSharing ? "Preparing\u2026" : "Share Backup";

    public string? Message { get => _message; private set { _message = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool MessageIsError { get => _messageIsError; private set { _messageIsError = value; OnPropertyChanged(); } }

    public string? ShareMessage { get => _shareMessage; private set { _shareMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasShareMessage)); } }
    public bool HasShareMessage => !string.IsNullOrEmpty(_shareMessage);

    public string? LatestBackupName { get => _latestName; private set { _latestName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLatestBackup)); } }
    public string? LatestBackupDetail { get => _latestDetail; private set { _latestDetail = value; OnPropertyChanged(); } }
    public bool HasLatestBackup => _latestName is not null;

    public ICommand OpenAttendanceCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand HistoryCommand { get; }

    public BackupViewModel(Repository repository, NavigationViewModel nav)
    {
        _repository = repository;
        _nav = nav;
        _history = new BackupHistoryPrefs(App.AppDataDirectory);
        _store = new LocalBackupStore(App.AppDataDirectory);

        OpenAttendanceCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.Attendance()));
        ExportCommand = new RelayCommand(Export, () => !IsBusy);
        ImportCommand = new RelayCommand(Import, () => !IsBusy);
        ShareCommand = new RelayCommand(Share, () => !IsBusy);
        HistoryCommand = new RelayCommand(() => _nav.NavigateTo(new Screen.BackupHistory()));

        RefreshLatestBackup();
    }

    private bool IsBusy => IsExporting || IsRestoring || IsSharing;
    private static void Requery() => CommandManager.InvalidateRequerySuggested();

    private void SetMessage(string? text, bool isError) { Message = text; MessageIsError = isError; }

    /// <summary>Android refreshLatestBackup(): newest internal ZIP → name, size · date · time.</summary>
    private void RefreshLatestBackup()
    {
        try
        {
            var f = _store.LatestInternalBackupFile();
            if (f is null) { LatestBackupName = null; LatestBackupDetail = null; return; }
            var ms = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            LatestBackupName = f.Name;
            LatestBackupDetail = $"{DateUtils.FormatBackupSize(f.Length)} \u00B7 {DateUtils.FormatDate(ms)} \u00B7 {DateUtils.FormatTimeOfDay(ms)}";
        }
        catch { LatestBackupName = null; LatestBackupDetail = null; }
    }

    /// <summary>Lets WPF paint the "Exporting…"/"Preparing…" state before the (UI-thread) database
    /// reads begin.</summary>
    private static Task PaintAsync() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task;

    // -------- Export (Android: CreateDocument launcher → vm.exportZipBackup) --------

    private async void Export()
    {
        var label = LocalBackupStore.TimestampLabel();
        var dlg = new SaveFileDialog
        {
            FileName = $"MajorGym_Backup_{label}.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            Title = "Export Backup"
        };
        if (dlg.ShowDialog() != true) return;

        IsExporting = true;
        SetMessage(null, false);
        var temp = Path.Combine(App.AppDataDirectory, "cache", $"export_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.zip");
        try
        {
            await PaintAsync();
            var members = _repository.GetAll();
            var attendance = _repository.GetAllAttendance();
            var archived = _repository.GetArchivedMembers();
            var internalCopy = _store.NewManualBackupFile(label);

            // Generator → Compressor → Validator, then the internal copy Share Backup File uses.
            await Task.Run(() =>
            {
                var json = BackupManager.ExportJson(members, App.PhotoStore, attendance, archived);
                BackupService.WriteAndVerify(json, temp);
                File.Copy(temp, internalCopy, overwrite: true);
            });
            _history.RecordBackupTaken();

            File.Copy(temp, dlg.FileName, overwrite: true);
            SetMessage("Backup exported.", false);
        }
        catch (Exception e)
        {
            var reason = string.IsNullOrWhiteSpace(e.Message) ? "The backup could not be completed." : e.Message;
            SetMessage($"Backup failed\n\n{reason}\n\nYour previous backup has been kept safe.", true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            IsExporting = false;
            RefreshLatestBackup();
        }
    }

    // -------- Restore (Android: OpenDocument launcher → vm.importBackup; no confirm dialog) --------

    private async void Import()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Major Gym backup (*.zip;*.json)|*.zip;*.json|All files (*.*)|*.*",
            Title = "Choose Backup File",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        IsRestoring = true;
        SetMessage(null, false);
        try
        {
            await PaintAsync();
            // Validate + decode entirely off the UI thread and off the database.
            var path = dlg.FileName;
            var (parsed, failure) = await Task.Run(() => BackupService.Prepare(App.AppDataDirectory, App.PhotoStore, path));

            // Single atomic database transaction (UI thread — shared connection).
            var outcome = failure ?? BackupService.Commit(_repository, App.PhotoStore, _store, parsed!);
            switch (outcome)
            {
                case RestoreOutcome.Success s:
                    SetMessage("Records restored." + (s.AttendanceRestored > 0 ? $" {s.AttendanceRestored} attendance record(s) restored." : ""), false);
                    break;
                case RestoreOutcome.InvalidBackup i:
                    SetMessage(i.Reason, true);
                    break;
                case RestoreOutcome.RestoreFailed f:
                    SetMessage($"Restore failed\n\n{f.Reason}", true);
                    break;
            }
        }
        catch (Exception e)
        {
            SetMessage($"Restore failed\n\n{e.Message}", true);
        }
        finally
        {
            IsRestoring = false;
            RefreshLatestBackup();
        }
    }

    // -------- Share (Android: getOrCreateLatestBackup → ACTION_SEND chooser) --------

    private async void Share()
    {
        IsSharing = true;
        ShareMessage = HasLatestBackup ? null : "No backup found. Creating a backup\u2026";
        try
        {
            await PaintAsync();
            var file = _store.LatestInternalBackupFile();
            if (file is null)
            {
                var members = _repository.GetAll();
                var attendance = _repository.GetAllAttendance();
                var archived = _repository.GetArchivedMembers();
                var dest = _store.NewManualBackupFile(LocalBackupStore.TimestampLabel());
                await Task.Run(() =>
                {
                    var json = BackupManager.ExportJson(members, App.PhotoStore, attendance, archived);
                    BackupService.WriteAndVerify(json, dest);
                });
                _history.RecordBackupTaken();
                file = new FileInfo(dest);
            }

            // Windows "share sheet": the file goes on the clipboard (paste into WhatsApp Web, mail,
            // etc.) and is highlighted in Explorer, whose context menu also has Share.
            try { Clipboard.SetFileDropList(new StringCollection { file.FullName }); } catch { /* clipboard busy */ }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullName}\"") { UseShellExecute = true });
            ShareMessage = "Backup ready to share.";
        }
        catch
        {
            ShareMessage = "Unable to create backup.";
        }
        finally
        {
            IsSharing = false;
            RefreshLatestBackup();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
