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
///  - up to 3 retries with 400ms/800ms/1200ms backoff specifically for a Busy result on
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
    private static readonly int[] BusyRetryDelaysMs = { 400, 800, 1200 };
    private const int AwaitKioskReleaseTimeoutMs = 5000;
    private const int CaptureTimeoutMs = 15000; // generous — this is an attended, one-shot enrollment, not the kiosk's silent background loop

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
                Fail("The scanner is busy. Please try again in a moment.");
                return;
            }
            token.ThrowIfCancellationRequested();

            App.ScannerOwnership.Acquire(ScannerOwnership.Owner.ENROLLMENT);
            ownershipAcquired = true;

            var openResult = await OpenWithRetriesAsync();
            token.ThrowIfCancellationRequested();
            if (openResult is not FingerprintScanner.OpenResult.Success || App.ScannerHub.Current is not { } scanner)
            {
                Fail(DescribeOpenFailure(openResult));
                return;
            }

            StatusText = "Place your finger on the scanner (1 of 2)...";
            var first = await Task.Run(() => scanner.CaptureTemplate(CaptureTimeoutMs), token);
            if (!TryGetTemplate(first, scanner, out var firstTemplate, out var firstFailure))
            {
                Fail(firstFailure!);
                return;
            }

            StatusText = "Lift your finger, then place the SAME finger again (2 of 2)...";
            var second = await Task.Run(() => scanner.CaptureTemplate(CaptureTimeoutMs), token);
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

    /// <summary>Up to 3 retries with 400ms/800ms/1200ms backoff specifically for a Busy
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
        FingerprintScanner.OpenResult.Error => "The scanner could not be opened. Please try again.",
        _ => "The scanner could not be opened. Please try again."
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
