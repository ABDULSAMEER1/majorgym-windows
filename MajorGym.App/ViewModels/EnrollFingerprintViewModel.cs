using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;
using MajorGym.Fingerprint;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's fingerprint enrollment screen/flow. Uses the shared
/// <see cref="MajorGym.App.App.ScannerHub"/> persistent connection and
/// <see cref="MajorGym.App.App.ScannerOwnership"/> turn-taking exactly the way
/// <see cref="MajorGym.Kiosk.FingerprintKioskLoop"/> already does — see those two classes'
/// own doc comments for why (single physical device, one persistent native session for the
/// whole process). This screen and the kiosk loop are the only two ScannerOwnership
/// consumers; enrollment always wins the handoff (it asks the kiosk loop to stop and waits
/// for it to actually finish releasing ownership before acquiring ENROLLMENT — see
/// <see cref="MajorGym.Kiosk.FingerprintKioskLoop.RequestStopAsync"/>), and always hands the
/// scanner back to the kiosk loop when it's done, whether it finished, failed, or was
/// cancelled by the user.
///
/// Enrollment reliability behavior preserved from the Android-derived brief:
///  - up to 2 retries with 400ms/800ms backoff (Android: 3 attempts total) specifically for a Busy result on
///    Open (<see cref="OpenWithRetriesAsync"/>) — distinct from the kiosk loop's own
///    5-consecutive-error/500ms capture-retry policy, which is a different situation
///    (an already-open scanner intermittently failing to read a finger) from this one (the
///    scanner refusing to open in the first place because something else briefly still has
///    it).
///  - two consecutive matching captures of the same finger before it's accepted, so a
///    single bad/noisy capture never gets silently enrolled.
///  - cross-member duplicate detection against every other member's already-enrolled
///    template before saving.
/// </summary>
public sealed class EnrollFingerprintViewModel : INotifyPropertyChanged, IDisposable
{
    // Android parity (FingerprintScreens.kt): OPEN_MAX_ATTEMPTS = 3 with OPEN_RETRY_DELAY_MS * attempt
    // (400 ms, then 800 ms) — i.e. two retries after the first attempt; RELEASE_WAIT_MS = 7000;
    // enrollment captures with FingerprintScanner's default 10 000 ms timeout.
    private static readonly int[] BusyRetryDelaysMs = { 400, 800 };
    private const int AwaitKioskReleaseTimeoutMs = 7000;
    private const int CaptureTimeoutMs = 10000;

    private readonly Repository _repository;
    private readonly NavigationViewModel _nav;
    private readonly Screen _returnTo;
    private CancellationTokenSource? _cts;

    public Member Member { get; }

    private string _statusText = "Ready to enroll a fingerprint.";
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }

    // Android parity (FingerprintScreens.kt): enrollment is TWO separate scans of the same finger. Scan 1 is held in
    // memory (firstScan); the user then scans the same finger again to confirm and the two are matched against each
    // other before anything is saved. The scanner session/ownership stays held between the two scans (Android's
    // `sessionAcquired`) so the kiosk loop cannot grab the scanner or record an attendance from the confirm scan.
    private byte[]? _firstScan;
    private bool _sessionAcquired;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(ShowScanButtons)); }
    }

    /// <summary>Android's button text: "Start Scan" for the first scan, "Scan Again to Confirm" for the second.</summary>
    public string ScanButtonText => _firstScan is null ? "Start Scan" : "Scan Again to Confirm";
    public string StepText => IsSuccess ? "Both scans matched" : _firstScan is null ? "Step 1 of 2 - first scan" : "Step 2 of 2 - confirm scan";
    public bool Scan1Done => _firstScan is not null || IsSuccess;
    public bool Scan2Done => IsSuccess;
    public bool ShowScanButtons => !IsBusy && !IsSuccess;
    public bool ShowCancel => !IsSuccess;

    private void RaiseStepChanged()
    {
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(Scan1Done));
        OnPropertyChanged(nameof(Scan2Done));
        OnPropertyChanged(nameof(ShowScanButtons));
        OnPropertyChanged(nameof(ShowCancel));
    }

    private bool _isError;
    public bool IsError { get => _isError; private set { _isError = value; OnPropertyChanged(); } }

    private bool _isSuccess;
    public bool IsSuccess { get => _isSuccess; private set { _isSuccess = value; OnPropertyChanged(); RaiseStepChanged(); } }

    private bool _needsDriver;
    /// <summary>True when the SecuGen SDK/driver could not start — shows the "Install Scanner Driver" button.</summary>
    public bool NeedsDriver { get => _needsDriver; private set { _needsDriver = value; OnPropertyChanged(); } }

    public bool CanStart => !IsBusy;
    public bool AlreadyEnrolled => Member.FingerprintTemplate is not null;

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DoneCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand CheckScannerCommand { get; }

    public EnrollFingerprintViewModel(Repository repository, NavigationViewModel nav, Member member, Screen returnTo)
    {
        _repository = repository;
        _nav = nav;
        Member = member;
        _returnTo = returnTo;

        StartCommand = new RelayCommand(() => _ = RunScanAsync(), () => CanStart);
        CancelCommand = new RelayCommand(Cancel);
        DoneCommand = new RelayCommand(() => _nav.NavigateTo(_returnTo));
        InstallDriverCommand = new RelayCommand(() => _ = InstallDriverAsync());
        CheckScannerCommand = new RelayCommand(() => _ = CheckScannerAsync(), () => CanStart);
    }

    /// <summary>Installs what the scanner needs, in order, each with the standard Windows administrator prompt:
    /// (1) Microsoft Visual C++ 2015-2022 runtime if this PC lacks it, then (2) the SecuGen driver installer.
    /// Nothing is installed silently without the user approving the Windows prompt.</summary>
    private async Task InstallDriverAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Drivers");
        var sg = Path.Combine(dir, "SgDrvSetupUniversal.exe");
        var vc = Path.Combine(dir, "vc_redist.x64.exe");
        if (!File.Exists(sg))
        {
            Fail("The driver installer was not found next to the application. Download the SecuGen Windows Driver Installer from secugen.com/drivers.");
            return;
        }
        IsError = false;
        try
        {
            var vcPresent = File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140.dll"))
                         && File.Exists(Path.Combine(Environment.SystemDirectory, "msvcp140.dll"));
            if (!vcPresent && Environment.Is64BitProcess && File.Exists(vc))
            {
                StatusText = "Installing the Microsoft Visual C++ runtime... approve the Windows prompt.";
                // 0 = ok, 3010 = ok (restart needed), 1638 = a newer version is already installed.
                await RunElevatedAsync(vc, "/install /passive /norestart");
            }

            StatusText = "Installing the SecuGen scanner driver... approve the Windows prompt and finish the installer.";
            await RunElevatedAsync(sg, "");
            NeedsDriver = false;
            StatusText = "Driver installed. Unplug and re-plug the scanner (restart the PC if asked), then press Start Scan.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Fail("The installer needs administrator permission. Press Install Scanner Driver and choose Yes.");
        }
        catch (Exception e)
        {
            Trace.TraceError($"[EnrollFingerprintViewModel] driver install failed: {e.Message}");
            Fail("The driver installer could not be started.");
        }
    }

    private static async Task RunElevatedAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe)! };
        using var p = Process.Start(psi);
        if (p is not null) await Task.Run(() => p.WaitForExit());
    }

    /// <summary>The manual troubleshooting checklist as one button: shows the runtime / SecuGen DLL / driver-module /
    /// USB-presence results in plain language.</summary>
    private async Task CheckScannerAsync()
    {
        IsError = false;
        StatusText = "Checking the scanner setup...";
        StatusText = await Task.Run(ScannerDiagnostics.BuildReport);
    }

    /// <summary>Android: Cancel always leaves the screen. While a scan is waiting for a finger it first cancels that
    /// wait (this screen stays so the user can simply tap Scan again); pressing Cancel with nothing running goes back.</summary>
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }
        _nav.NavigateTo(_returnTo); // leaving the screen disposes this ViewModel, which hands the scanner back
    }

    /// <summary>Hands the scanner back to the background kiosk loop. Safe to call more than once.</summary>
    private void ReleaseSession()
    {
        if (!_sessionAcquired) return;
        _sessionAcquired = false;
        App.ScannerOwnership.Release(ScannerOwnership.Owner.ENROLLMENT);
        App.KioskLoop.RequestStart();
    }

    /// <summary>Called by NavigationViewModel when the user leaves this screen (any way), exactly like the
    /// DisposableEffect.onDispose in Android's EnrollFingerprintScreen.</summary>
    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        ReleaseSession();
        // Even if no session was acquired (e.g. only "Check Scanner" was used) make sure the kiosk is running.
        App.KioskLoop.RequestStart();
    }

    /// <summary>One scan. Called once for scan 1 and once more for the confirm scan (Android's runScan()).</summary>
    private async Task RunScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsError = false;
        IsSuccess = false;
        NeedsDriver = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // Only the first scan goes through the stop-kiosk / wait-for-release / open dance; the confirm scan
            // reuses the already-open shared connection and already-held ownership.
            if (!_sessionAcquired)
            {
                StatusText = "Stopping background scanner...";
                await App.KioskLoop.RequestStopAsync();

                var released = await App.ScannerOwnership.AwaitReleasedAsync(AwaitKioskReleaseTimeoutMs);
                if (!released)
                    Trace.TraceWarning($"[EnrollFingerprintViewModel] SCANNER_OPEN_FAILED kiosk did not release in time (owner={App.ScannerOwnership.Current}), trying anyway");
                token.ThrowIfCancellationRequested();

                App.ScannerOwnership.Acquire(ScannerOwnership.Owner.ENROLLMENT);
                _sessionAcquired = true;

                StatusText = "Connecting to scanner...";
                var openResult = await OpenWithRetriesAsync();
                token.ThrowIfCancellationRequested();
                if (openResult is not FingerprintScanner.OpenResult.Success || App.ScannerHub.Current is null)
                {
                    Trace.TraceWarning($"[EnrollFingerprintViewModel] scanner open failed result={openResult.GetType().Name} detail={ScannerDiagnostics.LastFailure ?? "(none)"}");
                    NeedsDriver = openResult is FingerprintScanner.OpenResult.SdkUnavailable;
                    ReleaseSession(); // nothing to hold on to: give the scanner back to the kiosk loop
                    Fail(DescribeOpenFailure(openResult));
                    return;
                }
            }

            var scanner = App.ScannerHub.Current;
            if (scanner is null)
            {
                ReleaseSession();
                Fail("Fingerprint scanner unavailable. Please reconnect the scanner.");
                return;
            }

            var isConfirmScan = _firstScan is not null;
            StatusText = isConfirmScan
                ? "Place the SAME finger on the scanner again (scan 2 of 2)..."
                : "Place your finger on the scanner (scan 1 of 2)...";

            var (capture, usedScanner) = await CaptureWithRecoveryAsync(scanner, token);
            scanner = usedScanner;

            // Timeout / capture error: the first scan (if any) is kept, so the user just taps again (Android).
            if (!TryGetTemplate(capture, scanner, out var template, out var failure))
            {
                Fail(failure!);
                return;
            }

            if (_firstScan is null)
            {
                _firstScan = template;
                IsError = false;
                StatusText = "First scan captured. Scan the same finger again to confirm.";
                RaiseStepChanged();
                return;
            }

            if (!scanner.Match(_firstScan, template!))
            {
                _firstScan = null;
                RaiseStepChanged();
                Fail("The two scans didn't match. Starting over - scan the same finger twice.");
                return;
            }

            // Reject a fingerprint already assigned to a DIFFERENT member (this member's own old template is
            // excluded so re-enrolling a worn print keeps working). Members are read on this (UI) thread, which
            // owns the database connection; the CPU-heavy matching runs off-thread.
            StatusText = "Checking for duplicates...";
            var others = _repository.GetAll().Where(o => o.Id != Member.Id && o.FingerprintTemplate is not null).ToList();
            var captured = template!;
            var matcher = scanner;
            var duplicateOwner = await Task.Run(() =>
            {
                foreach (var other in others)
                {
                    token.ThrowIfCancellationRequested();
                    if (matcher.Match(other.FingerprintTemplate!, captured)) return other;
                }
                return null;
            });

            if (duplicateOwner is not null)
            {
                _firstScan = null;
                RaiseStepChanged();
                Fail($"This fingerprint is already enrolled for {duplicateOwner.Name}.");
                return;
            }

            // Android stores the confirm (second) capture.
            Member.FingerprintTemplate = captured;
            Member.UpdatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _repository.Save(Member);

            StatusText = "Fingerprint enrolled successfully.";
            IsSuccess = true;
            ReleaseSession(); // finished: scanner goes back to the kiosk loop right away
        }
        catch (OperationCanceledException)
        {
            StatusText = _firstScan is null ? "Scan cancelled." : "Scan cancelled. Scan the same finger again to confirm.";
            RaiseStepChanged();
        }
        catch (Exception e)
        {
            Trace.TraceError($"[EnrollFingerprintViewModel] SCANNER_EXCEPTION in enrollment flow: {e.Message}");
            Fail("Scanner error. Please try again.");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RaiseStepChanged();
        }
    }

    private void Fail(string message)
    {
        IsError = true;
        StatusText = message;
    }

    private bool TryGetTemplate(FingerprintScanner.CaptureResult result, FingerprintScanner scanner, out byte[]? template, out string? failureMessage)
    {
        switch (result)
        {
            case FingerprintScanner.CaptureResult.Success success:
                template = success.Template;
                failureMessage = null;
                return true;
            case FingerprintScanner.CaptureResult.Timeout:
                template = null;
                failureMessage = "No finger detected - try again.";
                return false;
            case FingerprintScanner.CaptureResult.Error error:
                App.ScannerHub.ReportOperationError(error.Code);
                template = null;
                failureMessage = FingerprintScanner.IndicatesDeviceGone(error.Code)
                    ? "The fingerprint scanner appears to have been disconnected. Please reconnect it and try again."
                    : "The scanner couldn't read that finger. Please try again.";
                return false;
            default:
                template = null;
                failureMessage = "Something went wrong reading the scanner.";
                return false;
        }
    }

    private const int CaptureSliceMs = 1000;

    /// <summary>Waits up to <see cref="CaptureTimeoutMs"/> for a finger, but in short slices so Cancel (and leaving
    /// the screen) takes effect within about a second. Previously one 10 s native capture was started and merely
    /// abandoned on Cancel: it kept running in the background holding the scanner, and silently swallowed the
    /// next finger placed on it.</summary>
    private async Task<FingerprintScanner.CaptureResult> CaptureCancellableAsync(FingerprintScanner scanner, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + CaptureTimeoutMs;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() => scanner.CaptureTemplate(CaptureSliceMs));
            if (result is not FingerprintScanner.CaptureResult.Timeout) return result;
            if (Environment.TickCount64 >= deadline) return result;
        }
    }

    /// <summary>One capture, with a single recovery attempt: if the shared session returns a capture error (typically
    /// a stale session after the scanner was unplugged/replugged — Windows has no detach event, unlike Android's
    /// broadcast), drop the session, open a clean one and capture once more. Bounded to ONE reopen so it can
    /// never turn into the repeated Init/Open cycling ScannerHub exists to prevent.</summary>
    private async Task<(FingerprintScanner.CaptureResult Result, FingerprintScanner Scanner)> CaptureWithRecoveryAsync(
        FingerprintScanner scanner, CancellationToken token)
    {
        var result = await CaptureCancellableAsync(scanner, token);
        if (result is not FingerprintScanner.CaptureResult.Error err) return (result, scanner);

        Trace.TraceWarning($"[EnrollFingerprintViewModel] capture error code={err.Code}; reopening the scanner session once");
        StatusText = "Reconnecting to the scanner...";
        await App.ScannerHub.ReleaseSessionAsync($"enrollment capture error {err.Code}");
        token.ThrowIfCancellationRequested();
        var reopen = await OpenWithRetriesAsync();
        if (reopen is not FingerprintScanner.OpenResult.Success || App.ScannerHub.Current is not { } fresh)
            return (result, scanner);

        StatusText = "Place your finger on the scanner...";
        return (await CaptureCancellableAsync(fresh, token), fresh);
    }

    /// <summary>Up to 2 retries with 400ms/800ms backoff (Android parity) specifically for a Busy
    /// Open() result — see class doc comment.</summary>
    private async Task<FingerprintScanner.OpenResult> OpenWithRetriesAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await App.ScannerHub.EnsureOpenAsync();
            if (result is not FingerprintScanner.OpenResult.Busy || attempt >= BusyRetryDelaysMs.Length)
                return result;

            StatusText = $"Scanner is busy, retrying ({attempt + 1}/{BusyRetryDelaysMs.Length})...";
            await Task.Delay(BusyRetryDelaysMs[attempt]);
        }
    }

    private static string DescribeOpenFailure(FingerprintScanner.OpenResult result) => result switch
    {
        FingerprintScanner.OpenResult.DeviceNotFound => "No fingerprint scanner was found. Please connect the SecuGen Hamster 20 and try again.",
        FingerprintScanner.OpenResult.Busy => "The scanner is busy. Please try again in a moment.",
        FingerprintScanner.OpenResult.SdkUnavailable => "The fingerprint scanner software could not be started. Press 'Install Scanner Driver' below (also install the Microsoft Visual C++ 2015-2022 Redistributable if it is missing), then re-plug the scanner and try again. Details: %LOCALAPPDATA%\\MajorGym\\logs\\scanner.log",
        FingerprintScanner.OpenResult.Error => "The scanner could not be opened. Please try again.",
        _ => "The scanner could not be opened. Please try again."
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
