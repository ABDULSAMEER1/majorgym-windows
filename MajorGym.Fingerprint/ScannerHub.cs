using System.Diagnostics;
using SecuGen.FDxSDKPro.Windows;

namespace MajorGym.Fingerprint;

/// <summary>
/// Owns the ONE persistent native connection to the physical SecuGen scanner for the
/// whole application process, instead of every consumer — the kiosk background loop, the
/// enrollment screen — opening and closing its own <see cref="FingerprintScanner"/> around
/// every single handoff between them. Windows port of Android's <c>ScannerHub</c> object
/// (com.majorgym.app.data.ScannerHub.kt) — the persistent-connection architecture and its
/// underlying reasoning are preserved exactly (Stage 2 brief §10): a cheap USB fingerprint
/// reader's own firmware can get wedged from being repeatedly re-initialized in quick
/// succession, so the fix on both platforms is the same — open the native device ONCE,
/// keep that same connection open for as long as the reader is physically attached, and
/// let <see cref="ScannerOwnership"/> coordinate only WHOSE TURN it is to actively
/// capture, never whether the device itself is open or closed.
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// PLATFORM ADAPTATION — DETACH DETECTION (Stage 1 report §H risk #5, Stage 2 brief §10
/// "the SDK reports that the device is no longer usable and recovery requires closing it"):
/// Android releases the device only in response to a real ACTION_USB_DEVICE_DETACHED
/// broadcast — the OS telling the app the hardware is gone. The SecuGen Windows SDK has
/// no equivalent attach/detach event (confirmed absent from the .NET Programming Manual —
/// see FingerprintScanner.cs class doc and the Stage 2 deliverables report). This class
/// therefore detects detachment two ways instead of one broadcast:
///   1. REACTIVE: any consumer whose capture/match call fails with
///      SGFPMError.ERROR_DEVICE_NOT_FOUND (55) calls <see cref="ReportOperationError"/>,
///      which force-releases the session immediately — this covers the common case, since
///      the kiosk loop is continuously calling CaptureTemplate whenever nothing owns the
///      scanner for enrollment.
///   2. PERIODIC POLL (a low-frequency safety net for the rarer case where nothing happens
///      to be actively capturing at the exact moment of a real detach): a background timer
///      calls SGFingerPrintManager.EnumerateDevice() every few seconds and force-releases
///      if the count of attached SecuGen devices drops to zero. This is intentionally
///      infrequent (not a tight poll loop) — Stage 2 brief §10 explicitly warns against
///      "unnecessary reopen cycles"; this only ever CLOSES an already-dead session, it
///      never proactively reopens one (reopening only happens the next time a consumer
///      calls <see cref="EnsureOpenAsync"/>, exactly as on Android).
/// ══════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class ScannerHub : IDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private volatile FingerprintScanner? _scanner;
    private Timer? _detachPollTimer;

    private static readonly TimeSpan DetachPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Opens the device if no session is live yet; otherwise returns Success
    /// immediately without touching the native SDK at all. This is what both the kiosk
    /// loop and enrollment call now instead of their own <c>new FingerprintScanner().Open()</c>
    /// (Android parity with <c>ensureOpen</c>).</summary>
    public async Task<FingerprintScanner.OpenResult> EnsureOpenAsync()
    {
        EnsureDetachPollStarted();
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_scanner is not null) return FingerprintScanner.OpenResult.Success.Instance;

            Trace.TraceInformation("[ScannerHub] SCANNER_HUB_INIT_START opening the one persistent session");
            var fresh = new FingerprintScanner();
            var result = fresh.Open();
            if (result is FingerprintScanner.OpenResult.Success)
            {
                _scanner = fresh;
                Trace.TraceInformation("[ScannerHub] SCANNER_HUB_INIT_SUCCESS");
            }
            else
            {
                // Don't leave a half-open instance sitting around — the next
                // EnsureOpenAsync() call should start completely clean.
                try { fresh.Close(); } catch { /* best-effort */ }
            }
            return result;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>The live, already-open scanner, or null if no session is currently open
    /// (never attached yet, or released after a real detach). Callers should always call
    /// <see cref="EnsureOpenAsync"/> first and use its result rather than assuming this is
    /// non-null.</summary>
    public FingerprintScanner? Current => _scanner;

    /// <summary>True once a session has genuinely been opened — for logging/diagnostics
    /// only, not control flow (Android parity with <c>isOpen()</c>).</summary>
    public bool IsOpen => _scanner is not null;

    /// <summary>Called by a consumer (kiosk loop, enrollment) after any capture/match
    /// operation fails, so ScannerHub can react immediately if the failure means the
    /// device is genuinely gone — see class doc's "REACTIVE" detection path above.</summary>
    public void ReportOperationError(int sdkErrorCode)
    {
        if (FingerprintScanner.IndicatesDeviceGone(sdkErrorCode))
        {
            Trace.TraceWarning("[ScannerHub] SCANNER_HUB_DEVICE_NOT_FOUND on an active call — releasing session");
            _ = ForceReleaseAsync();
        }
    }

    private void EnsureDetachPollStarted()
    {
        if (_detachPollTimer is not null) return;
        _detachPollTimer = new Timer(_ => PollForDetach(), null, DetachPollInterval, DetachPollInterval);
    }

    private void PollForDetach()
    {
        // Only bother polling while a session is actually open — an already-closed hub
        // has nothing to detect detachment for (Stage 2 brief §10: "do not introduce
        // unnecessary reopen cycles" — this extends to "unnecessary poll work" too).
        if (_scanner is null) return;
        try
        {
            using var probe = new SGFingerPrintManager();
            var count = 0;
            probe.EnumerateDevice();
            count = probe.NumberOfDevice;
            if (count == 0)
            {
                Trace.TraceWarning("[ScannerHub] SCANNER_HUB_DETACHED (poll) — releasing session; next attach starts a clean one");
                _ = ForceReleaseAsync();
            }
        }
        catch
        {
            // A probe failure here is not itself conclusive evidence of detachment
            // (could be a transient SDK hiccup); leave the decision to the next poll
            // tick or to a reactive ReportOperationError call, exactly as Android
            // tolerates isolated SDK errors without treating each one as fatal
            // (Stage 1 report §4.3 error-tolerance discussion).
        }
    }

    private async Task ForceReleaseAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            var existing = _scanner;
            _scanner = null;
            if (existing is not null)
            {
                try { existing.Close(); }
                catch (Exception e) { Trace.TraceError($"[ScannerHub] SCANNER_HUB_EXCEPTION during detach release: {e.Message}"); }
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        _detachPollTimer?.Dispose();
        _scanner?.Close();
    }
}
