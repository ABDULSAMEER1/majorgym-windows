using System.Diagnostics;
using MajorGym.Data;
using MajorGym.Data.Entities;
using MajorGym.Fingerprint;
using SecuGen.FDxSDKPro.Windows;

namespace MajorGym.Kiosk;

/// <summary>
/// Owns the continuous fingerprint scan/match loop for kiosk mode. Windows FOUNDATION port
/// of Android's <c>FingerprintKioskService</c> (Stage 2 brief §12: "do NOT yet integrate
/// the complete UI"). Matching, sound, and attendance-recording logic below is a faithful
/// port of the Android loop body; the surrounding UI wiring (the actual overlay window,
/// minimized/background detection, tray icon, etc.) is intentionally left for a later
/// stage — this class exposes <see cref="Bus"/> (a <see cref="KioskBus"/>) for that future
/// UI to subscribe to, exactly as Android's overlay reads <c>KioskBus</c>.
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// PRESERVED EXACTLY, PER STAGE 2 BRIEF §12/§13 ("parity, not optimization") — DO NOT
/// change any of these without an explicit decision:
///   - ListenSliceMs = 4000 (capture timeout per slice)
///   - MatchedDisplayMs = 3000 (successful-match display/hold)
///   - NotRecognizedDisplayMs = 1500 (not-recognized display)
///   - MaxConsecutiveCaptureErrors = 5 (before treating as a genuine disconnect)
///   - CaptureErrorRetryDelayMs = 500 (backoff between tolerated errors)
///   - A single Error does NOT mean the device is gone — only 5 in a row does. A Timeout
///     is not an error at all — it resets the consecutive-error counter to 0, same as a
///     successful capture (Stage 1 report §4.3 — this hardware-reliability lesson from
///     Android's own history is a behavioral requirement being carried over, not
///     incidental Android trivia).
/// ══════════════════════════════════════════════════════════════════════════════════
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// PLATFORM ADAPTATIONS (documented, not silent — Stage 2 brief §28):
///  - No Android foreground-Service/notification concept exists on Windows. This loop
///    runs as a plain background Task inside the always-running desktop process — there
///    is no equivalent of "survives Home button but stops when swiped from Recents" to
///    reproduce, since a WPF app has no such OS-imposed background execution limit while
///    it's running at all (minimized or not). Stage 1 report §H risk #8 flagged this
///    foreground/background distinction as a genuine judgment call for a later stage —
///    this class exposes no notification logic at all pending that decision, rather than
///    inventing a Windows toast-notification behavior nobody has asked for yet.
///  - Android's reactive Room Flow (`repository.observeAll().collect { ... }`) keeps the
///    enrolled-members cache continuously live. This SQLite-direct-access foundation has
///    no equivalent reactive query layer (building one is a larger undertaking than
///    Stage 2's database foundation scope), so the cache is refreshed by periodic re-query
///    (every <see cref="CacheRefreshIntervalMs"/>) instead of on every single database
///    write. For a kiosk that runs for hours with occasional new enrollments, this is a
///    materially equivalent practical behavior (a newly enrolled member becomes
///    recognizable within one refresh interval rather than instantly) — flagged here as a
///    deliberate, bounded simplification rather than a silent behavior change.
///  - ScannerHub's device-gone detection (poll + reactive error-code check, since this SDK
///    has no attach/detach event — see ScannerHub.cs) is fed here via
///    <see cref="ScannerHub.ReportOperationError"/> whenever a capture returns an error
///    code, mirroring the intent of Android's broadcast-driven detach handling as closely
///    as the platform allows.
/// ══════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class FingerprintKioskLoop
{
    private const int MatchedDisplayMs = 3000;
    private const int NotRecognizedDisplayMs = 1500;
    private const int ListenSliceMs = 4000;
    private const int MaxConsecutiveCaptureErrors = 5;
    private const int CaptureErrorRetryDelayMs = 500;
    private const int CacheRefreshIntervalMs = 10_000; // see "PLATFORM ADAPTATIONS" above

    private readonly Repository _repository;
    private readonly ScannerHub _scannerHub;
    private readonly ScannerOwnership _ownership;
    private readonly MembershipAudioPlayer _audioPlayer;
    private readonly string _audioAssetsDirectory;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1); // Android: scannerLifecycleMutex
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private volatile List<Member> _enrolledCache = new();

    public KioskBus Bus { get; } = new();

    /// <summary>Optional hook for a later stage's UI to receive "member scanned while
    /// minimized" style notifications — deliberately left as a plain callback rather than
    /// a Windows-notification implementation, per the "PLATFORM ADAPTATIONS" note above.</summary>
    public Action<KioskEvent>? NotifyIfBackgrounded { get; set; }

    public FingerprintKioskLoop(Repository repository, ScannerHub scannerHub, ScannerOwnership ownership,
        MembershipAudioPlayer audioPlayer, string audioAssetsDirectory)
    {
        _repository = repository;
        _scannerHub = scannerHub;
        _ownership = ownership;
        _audioPlayer = audioPlayer;
        _audioAssetsDirectory = audioAssetsDirectory;
    }

    /// <summary>Ask the loop to try opening the scanner and, if found, start listening.
    /// Safe/cheap to call repeatedly — no-ops if already running (Android parity with
    /// <c>requestStart</c>, minus the foreground-service/notification machinery — see
    /// class doc).</summary>
    public void RequestStart()
    {
        if (_loopTask is { IsCompleted: false }) return; // already trying/listening
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Used while another consumer (enrollment) needs exclusive scanner access.
    /// Cancels the loop and waits for its own cleanup (ownership release) to actually
    /// finish before returning — Android parity with <c>requestStop</c> +
    /// <c>stopListeningAndSelf</c>'s <c>cancelAndJoin</c>.</summary>
    public async Task RequestStopAsync()
    {
        var cts = _cts;
        var task = _loopTask;
        _loopTask = null;
        Bus.Publish(null);
        if (cts is null || task is null) return;

        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { /* expected */ }
        _audioPlayer.Stop();
    }

    /// <summary>Windows adaptation of Android's cheap "is anything worth trying" USB
    /// vendor-ID check (Stage 1 report §4.2/§4.3, isScannerConnected). There is no
    /// side-effect-free Windows equivalent that avoids touching the SDK entirely, so this
    /// creates a short-lived probe manager purely to call EnumerateDevice() — cheap and
    /// side-effect-free from the CALLER's perspective (no persistent handle kept open),
    /// even though it does briefly touch the SDK, unlike Android's pure OS-device-list
    /// read. Documented adaptation, not a silent behavior change.</summary>
    public static bool IsScannerConnected()
    {
        try
        {
            using var probe = new SGFingerPrintManager();
            probe.EnumerateDevice();
            return probe.NumberOfDevice > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        await _lifecycleLock.WaitAsync(token);
        try
        {
            Trace.TraceInformation("[FingerprintKioskLoop] SCANNER_ENSURE_OPEN background loop");
            var openResult = await _scannerHub.EnsureOpenAsync();
            var fp = _scannerHub.Current;
            if (openResult is not FingerprintScanner.OpenResult.Success || fp is null)
            {
                Trace.TraceWarning($"[FingerprintKioskLoop] SCANNER_OPEN_FAILED background loop result={openResult.GetType().Name}");
                return;
            }

            Trace.TraceInformation("[FingerprintKioskLoop] SCANNER_BACKGROUND_SCAN_START");
            // Claim ownership only once we're actually about to start capturing, and
            // release it in the finally block below — no matter whether the loop exits
            // normally, hits an SDK error, or is cancelled (Android doc comment, preserved).
            _ownership.Acquire(ScannerOwnership.Owner.KIOSK);

            using var cacheCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cacheTask = RefreshEnrolledCacheLoopAsync(cacheCts.Token);

            try
            {
                RefreshEnrolledCacheOnce(); // seed the cache before the first capture

                var consecutiveErrors = 0;
                while (!token.IsCancellationRequested)
                {
                    var capture = fp.CaptureTemplate(timeoutMs: ListenSliceMs);
                    switch (capture)
                    {
                        case FingerprintScanner.CaptureResult.Success success:
                        {
                            consecutiveErrors = 0;
                            Member? matched = null;
                            foreach (var m in _enrolledCache)
                            {
                                if (m.FingerprintTemplate is not null && fp.Match(m.FingerprintTemplate, success.Template))
                                {
                                    matched = m;
                                    break;
                                }
                            }

                            if (matched is not null)
                            {
                                var audioStatus = MembershipAudioStatusExtensions.MembershipAudioStatusOf(matched.ExpiryMillis);
                                var expired = MemberStatusExtensions.StatusOf(matched.ExpiryMillis) == MemberStatus.EXPIRED;
                                KioskSound.PlaySuccess();
                                var kioskEvent = new KioskEvent(matched.Id, Recognized: true, Expired: expired);
                                Bus.Publish(kioskEvent);
                                _audioPlayer.Play(_audioAssetsDirectory, audioStatus);
                                NotifyIfBackgrounded?.Invoke(kioskEvent);

                                // Best-effort attendance write, fire-and-forget — a save
                                // failure here must never crash the kiosk loop or block
                                // the next scan (Android doc comment, preserved).
                                var matchedForWrite = matched;
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        matchedForWrite.LastAttendanceMillis = now;
                                        matchedForWrite.UpdatedAtMillis = now;
                                        _repository.Save(matchedForWrite);
                                        _repository.RecordAttendanceVisit(matchedForWrite.Id, now);
                                    }
                                    catch (Exception e)
                                    {
                                        Trace.TraceError($"[FingerprintKioskLoop] Failed to record attendance for {matchedForWrite.Id}: {e.Message}");
                                    }
                                }, CancellationToken.None);

                                await Task.Delay(MatchedDisplayMs, token).ConfigureAwait(false);
                            }
                            else
                            {
                                KioskSound.PlayError();
                                var kioskEvent = new KioskEvent(null, Recognized: false, Expired: false);
                                Bus.Publish(kioskEvent);
                                _audioPlayer.Play(_audioAssetsDirectory, MembershipAudioStatus.UNIDENTIFIED);
                                NotifyIfBackgrounded?.Invoke(kioskEvent);
                                await Task.Delay(NotRecognizedDisplayMs, token).ConfigureAwait(false);
                            }
                            Bus.Publish(null);
                            break;
                        }
                        case FingerprintScanner.CaptureResult.Timeout:
                            // Nobody scanned during this slice — keep listening silently.
                            consecutiveErrors = 0;
                            break;
                        case FingerprintScanner.CaptureResult.Error error:
                        {
                            // A single Error does NOT necessarily mean the device is gone
                            // — see class doc "PRESERVED EXACTLY" section above. Tolerate
                            // isolated errors like a Timeout; only give up after several
                            // land in a row.
                            consecutiveErrors++;
                            Trace.TraceWarning($"[FingerprintKioskLoop] SCANNER_CAPTURE_FAILED background loop (consecutive={consecutiveErrors})");
                            _scannerHub.ReportOperationError(error.Code);
                            if (consecutiveErrors >= MaxConsecutiveCaptureErrors)
                            {
                                Trace.TraceWarning($"[FingerprintKioskLoop] SCANNER_CAPTURE_FAILED giving up after {consecutiveErrors} consecutive errors, stopping");
                                goto loopEnd;
                            }
                            await Task.Delay(CaptureErrorRetryDelayMs, token).ConfigureAwait(false);
                            break;
                        }
                    }
                }
                loopEnd: ;
            }
            catch (OperationCanceledException)
            {
                // Expected path when enrollment (or app shutdown) asks the loop to stop —
                // fall through to the finally block to actually release ownership.
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintKioskLoop] SCANNER_EXCEPTION in background loop: {e.Message}");
            }
            finally
            {
                cacheCts.Cancel();
                try { await cacheTask; } catch { /* ignore */ }

                // This finally block does NOT close the native device — see ScannerHub's
                // doc for why. All that happens here is: stop OUR polling loop, hand
                // turn-taking ownership back. The physical connection stays open and
                // ready for whoever asks next (enrollment, or this loop again on the next
                // RequestStart). (Android doc comment, preserved.)
                Trace.TraceInformation("[FingerprintKioskLoop] SCANNER_BACKGROUND_SCAN_STOP (session stays open)");
                _ownership.Release(ScannerOwnership.Owner.KIOSK);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void RefreshEnrolledCacheOnce()
    {
        try
        {
            _enrolledCache = _repository.GetAll()
                .Where(m => m.FingerprintTemplate is not null)
                .OrderByDescending(m => m.LastAttendanceMillis ?? 0L)
                .ToList();
        }
        catch (Exception e)
        {
            Trace.TraceError($"[FingerprintKioskLoop] Failed to refresh enrolled cache: {e.Message}");
        }
    }

    private async Task RefreshEnrolledCacheLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(CacheRefreshIntervalMs, token).ConfigureAwait(false);
                RefreshEnrolledCacheOnce();
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }
}
