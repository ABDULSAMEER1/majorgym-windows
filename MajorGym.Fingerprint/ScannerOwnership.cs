using System.Diagnostics;

namespace MajorGym.Fingerprint;

/// <summary>
/// A SecuGen USB fingerprint reader is a single physical device — only one consumer, the
/// kiosk background loop or the enrollment screen, should be actively capturing on it at a
/// time. Windows port of Android's <c>ScannerOwnership</c> object
/// (com.majorgym.app.kiosk.ScannerOwnership.kt) — the conceptual states (NONE/KIOSK/
/// ENROLLMENT) and the acquire/release/await-released call contract are preserved exactly
/// (Stage 2 brief §11).
///
/// The connection itself is not opened/closed by this class at all — see
/// <see cref="ScannerHub"/>, which owns one persistent native connection for the whole
/// application. This class only arbitrates TURN-TAKING for capture calls on that shared
/// connection: whichever owner holds it is the one whose capture loop should be running
/// right now. (Android doc comment, preserved — same narrower job on Windows.)
/// </summary>
public sealed class ScannerOwnership
{
    public enum Owner { NONE, KIOSK, ENROLLMENT }

    private readonly object _lock = new();
    private Owner _owner = Owner.NONE;
    private TaskCompletionSource<bool>? _releasedSignal;

    public Owner Current
    {
        get { lock (_lock) return _owner; }
    }

    /// <summary>Marks <paramref name="who"/> as currently holding the physical device open.</summary>
    public void Acquire(Owner who)
    {
        lock (_lock)
        {
            Trace.TraceInformation($"[ScannerOwnership] SCANNER_OWNER_ACQUIRE owner={who}");
            _owner = who;
        }
    }

    /// <summary>Marks the device as released, but only if <paramref name="who"/> was
    /// actually the current owner — prevents a stale release from clobbering a different
    /// owner that has since acquired it (e.g. a delayed cleanup call). (Android doc
    /// comment, preserved.)</summary>
    public void Release(Owner who)
    {
        lock (_lock)
        {
            if (_owner != who) return;
            Trace.TraceInformation($"[ScannerOwnership] SCANNER_OWNER_RELEASE owner={who}");
            _owner = Owner.NONE;
            _releasedSignal?.TrySetResult(true);
            _releasedSignal = null;
        }
    }

    /// <summary>
    /// Waits until nobody owns the device (or <paramref name="timeoutMs"/> elapses). Used
    /// by the enrollment screen before it opens its own scanner, so it never opens
    /// concurrently with the background kiosk loop. Returns true if the device was
    /// confirmed free, false on timeout (caller decides how to proceed). Android parity
    /// with <c>awaitReleased(timeoutMs)</c> — a TaskCompletionSource stands in for
    /// Kotlin's <c>StateFlow.first { it == NONE }</c> under a timeout.
    /// </summary>
    public async Task<bool> AwaitReleasedAsync(int timeoutMs)
    {
        TaskCompletionSource<bool> signal;
        lock (_lock)
        {
            if (_owner == Owner.NONE) return true;
            _releasedSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = _releasedSignal;
        }

        var completed = await Task.WhenAny(signal.Task, Task.Delay(timeoutMs));
        return completed == signal.Task;
    }
}
