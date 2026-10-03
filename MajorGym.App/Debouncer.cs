using System.Windows.Threading;

namespace MajorGym.App;

/// <summary>
/// Runs an action once, a short moment after the LAST call to <see cref="Trigger"/>. Used by every search box so
/// that typing or holding Backspace only filters once the user pauses (about 150 ms) instead of re-filtering and
/// re-drawing the list on every keystroke. Must be created and used on the UI thread.
/// </summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private readonly Action _action;

    public Debouncer(Action action, int delayMs = 150)
    {
        _action = action;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _timer.Tick += (_, _) => { _timer.Stop(); action(); };
    }

    /// <summary>(Re)starts the countdown.</summary>
    public void Trigger() { _timer.Stop(); _timer.Start(); }

    /// <summary>Runs a pending action right now (e.g. before a button acts on "what is currently shown").</summary>
    public void Flush()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _action();
    }

    /// <summary>Drops a pending run (e.g. when leaving the screen).</summary>
    public void Cancel() => _timer.Stop();
}
