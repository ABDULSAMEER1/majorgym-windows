using System.Media;

namespace MajorGym.Kiosk;

/// <summary>
/// Simple system beeps for the match/no-match feedback — Windows port of Android's
/// <c>KioskSound</c> object. Android uses <c>ToneGenerator</c> (a synthesized tone, no
/// bundled audio asset needed).
///
/// Windows has no public API for an arbitrary synthesized short tone as simple as
/// ToneGenerator without shipping/authoring an audio asset. Two candidates were
/// considered: <see cref="Console.Beep"/> (a true synthesized tone, closer in spirit to
/// ToneGenerator) was rejected because it drives the legacy PC-speaker hardware via the
/// Win32 Beep() API, which many modern motherboards don't populate at all — on such
/// machines it would silently produce no sound whatsoever, a worse outcome than Android's
/// always-audible tone. <see cref="SystemSounds"/> plays through the actual sound
/// card/output device (Windows' own notification sound scheme) and is used instead —
/// reliable on real hardware, at the cost of the exact tone being whatever the user's
/// Windows sound scheme defines rather than a fixed pitch. Two different system sounds
/// (Asterisk vs. Hand) preserve the same functional distinction ToneGenerator's two tones
/// (TONE_PROP_ACK vs. TONE_PROP_NACK) provided: a felt difference between "recognized" and
/// "not recognized", not identical audio content on both platforms (a documented,
/// deliberate adaptation — Stage 2 brief §12/§13 concern only the loop's TIMING constants,
/// not any exact-audio-byte requirement).
/// </summary>
internal static class KioskSound
{
    public static void PlaySuccess()
    {
        try { SystemSounds.Asterisk.Play(); } catch { /* best-effort, never blocks the scan loop */ }
    }

    public static void PlayError()
    {
        try { SystemSounds.Hand.Play(); } catch { /* best-effort, never blocks the scan loop */ }
    }
}
