using System.ComponentModel;
using MajorGym.Kiosk;

namespace MajorGym.App.Navigation;

/// <summary>
/// The kiosk-loop UI surface, deliberately minimal per Stage 4b brief §6/§7: when a
/// registered fingerprint is recognized, this shows NOTHING — no success overlay, no member
/// profile popup, no scan-success animation, no result screen — the loop itself already
/// records attendance silently (see <see cref="FingerprintKioskLoop"/>). The only thing this
/// class ever surfaces is the brief "Member Not Found" message for an unrecognized scan,
/// and it does so as a transient overlay banner (see MainWindow.xaml) layered ON TOP of
/// whatever screen is already showing — never inserted into the normal layout flow — so
/// showing/hiding it can never push other content around or leave the page scrolled to a
/// different position, which is exactly the Android UI bug brief §7 calls out to avoid
/// reproducing.
/// </summary>
public sealed class KioskOverlayViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    public bool IsVisible { get => _isVisible; private set { _isVisible = value; OnPropertyChanged(); } }

    public string Message => "Member Not Found";

    public KioskOverlayViewModel(FingerprintKioskLoop kioskLoop)
    {
        kioskLoop.Bus.CurrentChanged += (_, evt) =>
        {
            // evt is null when the loop clears its own transient display (see
            // FingerprintKioskLoop.RunLoopAsync's `Bus.Publish(null)` after each result, and
            // RequestStopAsync's publish(null) on stop) — always hide on that. A Recognized
            // event is intentionally never rendered here at all (per this class's own doc
            // comment above); only an explicit Recognized == false gets shown.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke((Action)(() => IsVisible = evt is { Recognized: false }));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
