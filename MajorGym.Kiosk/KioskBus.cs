namespace MajorGym.Kiosk;

/// <summary>
/// A resolved fingerprint scan, deliberately kept free of any UI types (no Member entity,
/// no view-model) so the background kiosk loop — which has no window/view context — can
/// produce these on its own. The UI layer looks up the full Member by
/// <see cref="MatchedMemberId"/> and maps this into whatever it needs to render. Ported
/// 1:1 from Android's <c>KioskEvent</c> data class.
/// </summary>
public sealed record KioskEvent(string? MatchedMemberId, bool Recognized, bool Expired);

/// <summary>
/// In-memory hand-off point between the kiosk background loop (always the one doing the
/// actual scanning) and whatever is currently rendering the result (the main window's
/// overlay — later stage). Ported 1:1 from Android's <c>KioskBus</c> object.
///
/// <see cref="Current"/> is null while idle/listening. The loop sets it the moment a scan
/// resolves, holds it for the configured display duration, then clears it back to null
/// itself — the UI layer only ever reads this, it never clears it (Android doc comment,
/// preserved — same contract on Windows, using a plain C# event instead of a Kotlin
/// StateFlow for subscribers, since WPF's data-binding/INotifyPropertyChanged conventions
/// are the natural equivalent here rather than introducing a reactive-streams library
/// Android didn't need either).
/// </summary>
public sealed class KioskBus
{
    private KioskEvent? _current;

    public KioskEvent? Current
    {
        get => _current;
        private set
        {
            _current = value;
            CurrentChanged?.Invoke(this, value);
        }
    }

    /// <summary>Raised whenever <see cref="Current"/> changes (a scan resolved, or the
    /// display duration elapsed and the loop cleared it back to null). A later-stage
    /// WPF overlay subscribes to this instead of polling.</summary>
    public event EventHandler<KioskEvent?>? CurrentChanged;

    /// <summary>Only the kiosk loop should call this.</summary>
    internal void Publish(KioskEvent? kioskEvent) => Current = kioskEvent;
}
