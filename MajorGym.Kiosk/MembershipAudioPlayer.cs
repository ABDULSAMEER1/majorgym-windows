using System.Windows.Media;
using System.Diagnostics;
using MajorGym.Data;

namespace MajorGym.Kiosk;

/// <summary>Finer-grained membership status used only for picking which check-in audio
/// clip to play. Deliberately separate from <see cref="MemberStatus"/> (ACTIVE/EXPIRING/
/// EXPIRED, used everywhere else for badges/UI) rather than changing that enum's meaning —
/// this one distinguishes "expires today" and "expires tomorrow" as their own states,
/// which the existing 7-day EXPIRING bucket doesn't. Ported 1:1 from Android's
/// <c>MembershipAudioStatus</c> enum.</summary>
public enum MembershipAudioStatus { EXPIRED, EXPIRING_TODAY, EXPIRING_IN_1_DAY, ACTIVE, UNIDENTIFIED }

public static class MembershipAudioStatusExtensions
{
    /// <summary>Priority order per spec: expired beats expiring-today beats
    /// expiring-tomorrow beats active. Uses the local calendar date, same as every other
    /// expiry calculation in this app (see <see cref="DateUtils.DaysBetweenNow"/>). Ported
    /// 1:1 from Android's <c>membershipAudioStatusOf</c>.</summary>
    public static MembershipAudioStatus MembershipAudioStatusOf(long expiryMillis)
    {
        var days = DateUtils.DaysBetweenNow(expiryMillis);
        return days switch
        {
            < 0L => MembershipAudioStatus.EXPIRED,
            0L => MembershipAudioStatus.EXPIRING_TODAY,
            1L => MembershipAudioStatus.EXPIRING_IN_1_DAY,
            _ => MembershipAudioStatus.ACTIVE
        };
    }
}

/// <summary>
/// Plays the bundled ACTIVE / EXPIRED / EXPIRING_TODAY / EXPIRING_IN_1_DAY / UNIDENTIFIED
/// audio clips after a fingerprint scan. Windows port of Android's
/// <c>MembershipAudioPlayer</c> object — same clip set, same "only one clip plays at a
/// time, starting a new one always stops any still-playing prior instance first" and
/// "every call is best-effort, a playback failure never takes down the scan loop" contracts
/// (Android doc comment, preserved).
///
/// Uses <see cref="MediaPlayer"/> (WPF) rather than <see cref="System.Media.SoundPlayer"/>
/// because SoundPlayer only supports WAV — the existing clips are the same .mp3 assets
/// Android already ships (res/raw/*.mp3, per Stage 1 report), and MediaPlayer plays MP3
/// natively with no format conversion needed, preserving the actual audio files unchanged.
/// </summary>
public sealed class MembershipAudioPlayer
{
    private MediaPlayer? _current;
    private readonly object _lock = new();

    /// <summary>Maps each status to its clip file, relative to the app's Assets\Audio
    /// directory (see the Windows project structure — Stage 1 report §I). File names
    /// match Android's res/raw/*.mp3 names exactly (active.mp3, expired.mp3,
    /// expiring_today.mp3, expiring_in_1_day.mp3, unidentified.mp3) so the same asset
    /// files can be copied over verbatim with no renaming.</summary>
    private static string ClipFileName(MembershipAudioStatus status) => status switch
    {
        MembershipAudioStatus.ACTIVE => "active.mp3",
        MembershipAudioStatus.EXPIRED => "expired.mp3",
        MembershipAudioStatus.EXPIRING_TODAY => "expiring_today.mp3",
        MembershipAudioStatus.EXPIRING_IN_1_DAY => "expiring_in_1_day.mp3",
        MembershipAudioStatus.UNIDENTIFIED => "unidentified.mp3",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public void Play(string audioAssetsDirectory, MembershipAudioStatus status)
    {
        try
        {
            lock (_lock)
            {
                Stop(); // Stop/release whatever was already playing so clips never overlap.

                var path = Path.Combine(audioAssetsDirectory, ClipFileName(status));
                if (!File.Exists(path))
                {
                    Trace.TraceWarning($"[MembershipAudioPlayer] Clip not found for {status}: {path}");
                    return;
                }

                var player = new MediaPlayer();
                player.Open(new Uri(path, UriKind.Absolute));
                player.MediaEnded += (_, _) => { try { player.Close(); } catch { /* ignore */ } if (ReferenceEquals(_current, player)) _current = null; };
                player.MediaFailed += (_, _) => { try { player.Close(); } catch { /* ignore */ } if (ReferenceEquals(_current, player)) _current = null; };
                _current = player;
                player.Play();
            }
        }
        catch
        {
            // Best-effort — a playback failure here must never take down the scan loop
            // or block the next fingerprint capture (Android parity).
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_current is null) return;
            try { _current.Stop(); _current.Close(); } catch { /* ignore */ }
            _current = null;
        }
    }
}
