using System.Diagnostics;
using SecuGen.FDxSDKPro.Windows;

namespace MajorGym.Fingerprint;

/// <summary>
/// Thin, thread-safe wrapper around SecuGen's FDx SDK Pro for Windows
/// (<see cref="SGFingerPrintManager"/>), for a USB fingerprint scanner plugged into the
/// front-desk PC. This is the Windows port of Android's <c>FingerprintScanner</c>
/// (com.majorgym.app.data.FingerprintScanner.kt) — the public shape
/// (OpenResult/CaptureResult, Open/CaptureTemplate/Match/Close) is preserved deliberately
/// (Stage 2 brief §7) so <see cref="ScannerHub"/>, the kiosk loop, and the enrollment
/// screen need no conceptual changes above this class, only a different implementation
/// underneath.
///
/// Only one <see cref="FingerprintScanner"/> should have the device open at a time — see
/// <see cref="ScannerOwnership"/> for how callers coordinate that across the background
/// kiosk loop and the enrollment screen (same design as Android).
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// CONFIRMED FROM THE SDK'S OWN DOCUMENTATION (FDx SDK Pro .NET Programming Manual,
/// SG1-0030B-019 — not guessed; see Stage 2 report §3 "Documentation findings"):
///
///  - No USB permission dialog / BroadcastReceiver equivalent exists or is needed on
///    Windows — the SecuGen Windows driver owns USB device access once installed. The
///    entire Android usbReceiver / requestUsbPermission / PendingIntent machinery has NO
///    Windows counterpart and is correctly just absent below, not replaced by anything.
///  - GetImageEx's Windows signature takes FOUR parameters — (byte[] buffer, int timeout,
///    int dispWnd, ref int quality) — one more than Android's three-parameter
///    (image, timeoutMs, minQuality) overload. `dispWnd` is a window handle SecuGen can
///    optionally paint a live preview into. Neither the Android kiosk loop nor the
///    enrollment screen shows a live camera-style preview (Stage 1 report, FingerprintScreens.kt
///    audit) — only status text — so this port passes dispWnd = 0 (no window) uniformly, to
///    reproduce that same "no live preview" behavior. NOTE: the manual does not explicitly
///    document 0 as a valid "no preview" sentinel — this is a reasoned inference (0 is not
///    a valid HWND) that should be confirmed against a real device before Stage 3 kiosk
///    integration is finalized (Stage 2 brief §28 stop-condition candidate; not blocking
///    Stage 2 since no physical device is available in this environment to test against).
///  - SGFPMSecurityLevel.NORMAL (value 5) is the confirmed Windows equivalent of Android's
///    SGFDxSecurityLevel.SL_NORMAL.
///  - SGFPMTemplateFormat.ISO19794 (value 0x0300) is the confirmed Windows equivalent of
///    Android's SGFDxTemplateFormat.TEMPLATE_FORMAT_ISO19794 — same template format on
///    both platforms (Stage 1 report §4.5/§F — this is what makes template portability
///    between platforms plausible in the first place).
///  - There is NO documented device attach/detach event or Windows message in this SDK
///    (only SGFPMMessages.DEV_AUTOONEVENT for finger-on-sensor detection, unrelated to
///    plug/unplug). Detach must be inferred from an operation failing with
///    SGFPMError.ERROR_DEVICE_NOT_FOUND (55) — a documented, deliberate deviation from
///    Android's broadcast-based detach detection; see ScannerHub.cs for how this is used.
/// ══════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class FingerprintScanner : IDisposable
{
    public abstract class OpenResult
    {
        public sealed class Success : OpenResult { public static readonly Success Instance = new(); }
        public sealed class DeviceNotFound : OpenResult { public static readonly DeviceNotFound Instance = new(); }
        public sealed class Busy : OpenResult { public static readonly Busy Instance = new(); }
        public sealed class Error(int code) : OpenResult { public int Code { get; } = code; }
        // Note: no PermissionDenied case — Windows has no USB-permission-dialog step
        // (see class doc above); a missing/inaccessible device surfaces as DeviceNotFound
        // or Busy instead, exactly as Android's own OpenDevice-failure branch already did
        // for every non-permission failure mode.
    }

    public abstract class CaptureResult
    {
        public sealed class Success(byte[] template, int imageQuality) : CaptureResult
        {
            public byte[] Template { get; } = template;
            public int ImageQuality { get; } = imageQuality;
        }
        public sealed class Timeout : CaptureResult { public static readonly Timeout Instance = new(); }
        public sealed class Error(int code) : CaptureResult { public int Code { get; } = code; }
    }

    private SGFingerPrintManager? _fpm;
    private int _imageWidth;
    private int _imageHeight;
    private int _maxTemplateSize = 400;
    private bool _initialized;
    private bool _deviceOpened;

    /// <summary>Serializes every native call this instance makes (Open/CaptureTemplate/
    /// Match/Close) — C# equivalent of Android's <c>callMutex: Mutex</c>. A plain lock is
    /// sufficient here (unlike Android, there is no suspend/coroutine machinery to
    /// interact with), but the effect — no two native calls on this instance from
    /// different threads at once — is identical.</summary>
    private readonly object _callLock = new();

    /// <summary>
    /// Initializes the SDK, opens the (auto-detected) attached SecuGen device, reads its
    /// image dimensions, and configures it for ISO 19794-2 template capture. Never throws —
    /// any SDK-level exception comes back as <see cref="OpenResult.Error"/> instead of
    /// propagating up and crashing the caller (Android parity, Stage 2 brief §7 "handle SDK
    /// failures without crashing the application").
    /// </summary>
    public OpenResult Open()
    {
        lock (_callLock)
        {
            // Defensive cleanup — Android parity: if this instance still has a handle open
            // from a previous Open() call, fully release it before touching the SDK again.
            if (_fpm is not null)
            {
                Trace.TraceWarning("[FingerprintScanner] SCANNER_REOPEN stale handle found on Open() — releasing it first");
                ReleaseNowLocked();
            }

            SGFingerPrintManager fpm;
            try
            {
                fpm = new SGFingerPrintManager();
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_INIT_FAILED constructing SGFingerPrintManager: {e.Message}");
                return OpenResult.DeviceNotFound.Instance;
            }
            _fpm = fpm;

            int initError;
            try
            {
                initError = fpm.Init(SGFPMDeviceName.DEV_AUTO);
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_INIT_FAILED exception: {e.Message}");
                _fpm = null;
                return OpenResult.DeviceNotFound.Instance;
            }
            if (initError != (int)SGFPMError.ERROR_NONE)
            {
                Trace.TraceWarning($"[FingerprintScanner] SCANNER_INIT_FAILED code={initError}");
                _fpm = null;
                return OpenResult.DeviceNotFound.Instance;
            }
            _initialized = true;

            // No USB-permission step here — see class doc above: the Windows SecuGen
            // driver owns device access once installed, unlike Android's UsbManager
            // per-app permission grant.

            int openError;
            try
            {
                // USB_AUTO_DETECT (not device id 0): the .NET Programming Manual
                // recommends this sentinel specifically for the single-reader case, which
                // is Major Gym's only supported configuration (Stage 1 report §4.2 — one
                // scanner per kiosk PC). This is the Windows-side equivalent of Android's
                // OpenDevice(0L) auto-first-device behavior, not a behavioral change.
                openError = fpm.OpenDevice((int)SGFPMPortAddr.USB_AUTO_DETECT);
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_OPEN_FAILED exception: {e.Message}");
                return OpenResult.Busy.Instance;
            }
            if (openError != (int)SGFPMError.ERROR_NONE)
            {
                Trace.TraceWarning($"[FingerprintScanner] SCANNER_OPEN_FAILED code={openError}");
                // A nonzero code here most often means the device is already claimed by
                // another open handle — e.g. the kiosk loop didn't release it in time
                // (Android doc comment, preserved — same meaning on Windows).
                return OpenResult.Busy.Instance;
            }
            _deviceOpened = true;

            try
            {
                var deviceInfo = new SGFPMDeviceInfoParam();
                fpm.GetDeviceInfo(deviceInfo);
                _imageWidth = deviceInfo.ImageWidth;
                _imageHeight = deviceInfo.ImageHeight;
                fpm.SetTemplateFormat(SGFPMTemplateFormat.ISO19794);
                var maxSize = 0;
                fpm.GetMaxTemplateSize(ref maxSize);
                if (maxSize > 0) _maxTemplateSize = maxSize;
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION reading device info: {e.Message}");
                return new OpenResult.Error(-1);
            }

            Trace.TraceInformation("[FingerprintScanner] SCANNER_OPEN_SUCCESS");
            return OpenResult.Success.Instance;
        }
    }

    /// <summary>
    /// Blocks (synchronously — call from a background thread) until a finger is placed on
    /// the sensor or <paramref name="timeoutMs"/> elapses, then builds an ISO 19794-2
    /// template from the scan. Call <see cref="Open"/> first. Never throws — any SDK-level
    /// exception during capture, quality scoring, or template creation/sizing comes back as
    /// <see cref="CaptureResult.Error"/> (Android parity).
    /// </summary>
    public CaptureResult CaptureTemplate(int timeoutMs = 10000, int minQuality = 50)
    {
        lock (_callLock)
        {
            var fpm = _fpm;
            if (fpm is null || !_deviceOpened || _imageWidth == 0 || _imageHeight == 0)
            {
                Trace.TraceWarning($"[FingerprintScanner] SCANNER_CAPTURE_FAILED not open (fpm={fpm is not null}, deviceOpened={_deviceOpened})");
                return new CaptureResult.Error(-1);
            }

            var image = new byte[_imageWidth * _imageHeight];
            int captureError;
            try
            {
                // CONFIRMED FROM THE REAL VENDOR DLL'S METADATA (empirically decoded — see
                // Stage 3 report): GetImageEx(byte[] buffer, Int32 timeout, Int64 dispWnd,
                // Int32 minQuality) — all four parameters are BY VALUE, none are ref/out.
                // The 4th parameter is an INPUT minimum-quality threshold (matching Android's
                // GetImageEx(image, timeoutMs, minQuality) semantics closely), not an output —
                // this corrects an incorrect assumption made in the Stage 2 draft (which
                // guessed it was an output "actual quality" ref parameter). The actual
                // captured image's quality is read afterward via the separate GetImageQuality
                // call below, exactly as Android's own capture flow does.
                // dispWnd's declared type is architecture-dependent in the vendor DLL itself
                // (Int64 on the x64 build, Int32 on the x86 build — confirmed by compiling
                // against both) — a plain literal 0 widens implicitly to either, so no
                // platform-conditional code is needed here.
                captureError = fpm.GetImageEx(image, timeoutMs, 0, minQuality);
                if (captureError == (int)SGFPMError.ERROR_TIME_OUT) return CaptureResult.Timeout.Instance;
                if (captureError != (int)SGFPMError.ERROR_NONE)
                {
                    Trace.TraceWarning($"[FingerprintScanner] SCANNER_CAPTURE_FAILED code={captureError}");
                    return new CaptureResult.Error(captureError);
                }
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during GetImageEx: {e.Message}");
                return new CaptureResult.Error(-1);
            }
            Trace.TraceInformation("[FingerprintScanner] SCANNER_FINGER_DETECTED");

            var finalQuality = 0;
            try
            {
                fpm.GetImageQuality(_imageWidth, _imageHeight, image, ref finalQuality);
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during GetImageQuality: {e.Message}");
            }

            var fpInfo = new SGFPMFingerInfo
            {
                FingerNumber = SGFPMFingerPosition.FINGPOS_UK,
                ImageQuality = (short)finalQuality,
                ImpressionType = (short)SGFPMImpressionType.IMPTYPE_LP,
                ViewNumber = (short)1
            };
            var template = new byte[_maxTemplateSize];
            int templateError;
            try
            {
                templateError = fpm.CreateTemplate(fpInfo, image, template);
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during CreateTemplate: {e.Message}");
                return new CaptureResult.Error(-1);
            }
            if (templateError != (int)SGFPMError.ERROR_NONE)
            {
                Trace.TraceWarning($"[FingerprintScanner] SCANNER_TEMPLATE_FAILED code={templateError}");
                return new CaptureResult.Error(templateError);
            }

            System.ValueType sizeBoxed = 0;
            try
            {
                fpm.GetTemplateSize(template, sizeBoxed);
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during GetTemplateSize: {e.Message}");
            }
            var size = sizeBoxed is int i ? i : 0;
            var trimmed = (size > 0 && size < template.Length) ? template[..size] : template;

            Trace.TraceInformation($"[FingerprintScanner] SCANNER_CAPTURE_SUCCESS quality={finalQuality}");
            return new CaptureResult.Success(trimmed, finalQuality);
        }
    }

    /// <summary>Compares two ISO 19794-2 templates at normal security level (Windows
    /// SGFPMSecurityLevel.NORMAL == Android SGFDxSecurityLevel.SL_NORMAL — see class doc).
    /// Returns false (never throws) if the scanner was never successfully opened or the SDK
    /// throws during the compare (Android parity).</summary>
    public bool Match(byte[] template1, byte[] template2)
    {
        lock (_callLock)
        {
            var fpm = _fpm;
            if (fpm is null) return false;
            try
            {
                var matched = false;
                fpm.MatchTemplate(template1, template2, SGFPMSecurityLevel.NORMAL, ref matched);
                return matched;
            }
            catch (Exception e)
            {
                Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during MatchTemplate: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>Releases the device, if it was ever actually opened. Safe to call multiple
    /// times and never throws (Android parity with <c>close()</c>/<c>closeAndAwait()</c> —
    /// collapsed into one synchronous method here since C# has no coroutine-cancellation
    /// analog forcing the async/NonCancellable split Android needed; the underlying release
    /// logic and guarantees are identical).</summary>
    public void Close()
    {
        lock (_callLock)
        {
            ReleaseNowLocked();
        }
    }

    public void Dispose() => Close();

    private void ReleaseNowLocked()
    {
        var fpm = _fpm;
        if (fpm is not null)
        {
            if (_deviceOpened)
            {
                try { fpm.CloseDevice(); }
                catch (Exception e) { Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during CloseDevice: {e.Message}"); }
                _deviceOpened = false;
            }
            if (_initialized)
            {
                try { fpm.Dispose(); }
                catch (Exception e) { Trace.TraceError($"[FingerprintScanner] SCANNER_EXCEPTION during Close: {e.Message}"); }
                _initialized = false;
            }
        }
        _fpm = null;
        Trace.TraceInformation("[FingerprintScanner] SCANNER_RELEASE");
    }

    /// <summary>True if the last known SDK error on this instance's most recent operation
    /// indicates the device is genuinely gone (per SGFPMError.ERROR_DEVICE_NOT_FOUND=55) —
    /// used by <see cref="ScannerHub"/> in place of Android's USB-detach broadcast, since
    /// this SDK has no attach/detach event (see class doc above).</summary>
    public static bool IndicatesDeviceGone(int sdkErrorCode) =>
        sdkErrorCode == (int)SGFPMError.ERROR_DEVICE_NOT_FOUND;
}
