using System.ComponentModel;
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
public sealed class EnrollFingerprintViewModel : INotifyPropertyChanged
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

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); } }

    private bool _isError;
    public bool IsError { get => _isError; private set { _isError = value; OnPropertyChanged(); } }

    private bool _isSuccess;
    public bool IsSuccess { get => _isSuccess; private set { _isSuccess = value; OnPropertyChanged(); } }

    public bool CanStart => !IsBusy;
    public bool AlreadyEnrolled => Member.FingerprintTemplate is not null;

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DoneCommand { get; }

    public EnrollFingerprintViewModel(Repository repository, NavigationViewModel nav, Member member, Screen returnTo)
    {
        _repository = repository;
        _nav = nav;
        Member = member;
        _returnTo = returnTo;

        StartCommand = new RelayCommand(() => _ = RunEnrollmentAsync(), () => CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        DoneCommand = new RelayCommand(() => _nav.NavigateTo(_returnTo));
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    private async Task RunEnrollmentAsync()
    {
        IsBusy = true;
        IsError = false;
        IsSuccess = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var ownershipAcquired = false;

        try
        {
            StatusText = "Waiting for the scanner...";
            await App.KioskLoop.RequestStopAsync();

            var released = await App.ScannerOwnership.AwaitReleasedAsync(AwaitKioskReleaseTimeoutMs);
            if (!released)
            {
                // Android parity: log and carry on ("trying anyway") rather than failing outright.
                // Native calls on the shared session are serialized inside FingerprintScanner, and
                // RequestStopAsync above has already waited for the kiosk loop task to finish.
                Trace.TraceWarning($"[EnrollFingerprintViewModel] SCANNER_OPEN_FAILED kiosk did not release in time (owner={App.ScannerOwnership.Current}), trying anyway");
            }
            token.ThrowIfCancellationRequested();

            App.ScannerOwnership.Acquire(ScannerOwnership.Owner.ENROLLMENT);
            ownershipAcquired = true;

            var openResult = await OpenWithRetriesAsync();
            token.ThrowIfCancellationRequested();
            if (openResult is not FingerprintScanner.OpenResult.Success || App.ScannerHub.Current is not { } scanner)
            {
                Trace.TraceWarning($"[EnrollFingerprintViewModel] scanner open failed result={openResult.GetType().Name} detail={ScannerDiagnostics.LastFailure ?? "(none)"} owner={App.ScannerOwnership.Current}");
                Fail(DescribeOpenFailure(openResult));
                return;
            }

            StatusText = "Place your finger on the scanner (1 of 2)...";
            var (first, firstScanner) = await CaptureWithRecoveryAsync(scanner, token);
            scanner = firstScanner;
            if (!TryGetTemplate(first, scanner, out var firstTemplate, out var firstFailure))
            {
                Fail(firstFailure!);
                return;
            }

            StatusText = "Lift your finger, then place the SAME finger again (2 of 2)...";
            var (second, secondScanner) = await CaptureWithRecoveryAsync(scanner, token);
            scanner = secondScanner;
            if (!TryGetTemplate(second, scanner, out var secondTemplate, out var secondFailure))
            {
                Fail(secondFailure!);
                return;
            }

            if (!scanner.Match(firstTemplate!, secondTemplate!))
            {
                Fail("The two scans didn't match. Please try again with the same finger, placed the same way both times.");
                return;
            }

            // Cross-member duplicate detection — this fingerprint must not already belong
            // to a different member.
            foreach (var other in _repository.GetAll())
            {
                token.ThrowIfCancellationRequested();
                if (other.Id == Member.Id || other.FingerprintTemplate is null) continue;
                if (scanner.Match(other.FingerprintTemplate, firstTemplate!))
                {
                    Fail($"This fingerprint is already enrolled for {other.Name}. Each fingerprint can only belong to one member.");
                    return;
                }
            }

            Member.FingerprintTemplate = firstTemplate;
            Member.UpdatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _repository.Save(Member);

            IsSuccess = true;
            StatusText = "Fingerprint enrolled successfully.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Enrollment cancelled.";
        }
        catch (Exception e)
        {
            Trace.TraceError($"[EnrollFingerprintViewModel] Unexpected error: {e.Message}");
            Fail("Something went wrong during enrollment. Please try again.");
        }
        finally
        {
            if (ownershipAcquired) App.ScannerOwnership.Release(ScannerOwnership.Owner.ENROLLMENT);
            App.KioskLoop.RequestStart(); // hand the scanner back to the kiosk loop, whatever happened here
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
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
                failureMessage = "No finger was detected in time. Please try again.";
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
        FingerprintScanner.OpenResult.SdkUnavailable => "The fingerprint scanner software could not be started. Install the SecuGen FDx SDK Pro / device driver for this PC (and the Microsoft Visual C++ 2015-2022 Redistributable), then restart the app. Details: %LOCALAPPDATA%\\MajorGym\\logs\\scanner.log",
        FingerprintScanner.OpenResult.Error => "The scanner could not be opened. Please try again.",
        _ => "The scanner could not be opened. Please try again."
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
