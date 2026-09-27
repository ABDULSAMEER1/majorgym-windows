using System.Diagnostics;
using System.Windows;

namespace MajorGym.App.Views;

/// <summary>
/// Windows equivalent of Android's startup video splash (brief §10). Plays exactly one
/// video, once, then signals completion via <see cref="Finished"/> — App.xaml.cs listens
/// for that to know when it's safe to show MainWindow. If the video fails to open/play for
/// any reason (missing file, unsupported codec, no media backend installed), this fires
/// Finished immediately rather than hanging the startup sequence — a broken/missing splash
/// asset must never prevent the actual application from opening.
///
/// This does NOT invent a substitute video if the real asset is missing — see App.xaml.cs,
/// which only ever constructs this window at all when the configured file already exists on
/// disk (Stage 4b brief §10: "If the asset is missing from the supplied ZIP, do NOT invent
/// or substitute another video. Clearly report the missing asset."). This class has no
/// knowledge of whether the asset is real or a placeholder — asset-existence is checked
/// once, by the caller, before this window is ever created.
/// </summary>
public partial class SplashWindow : Window
{
    public event Action? Finished;
    private bool _finished;

    public SplashWindow(string videoPath)
    {
        InitializeComponent();
        try
        {
            Player.Source = new Uri(videoPath, UriKind.Absolute);
            Player.Play();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"[SplashWindow] Failed to start startup video: {e.Message}");
            SignalFinished();
        }
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => SignalFinished();

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Trace.TraceWarning($"[SplashWindow] Startup video failed to play: {e.ErrorException?.Message}");
        SignalFinished();
    }

    private void SignalFinished()
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke();
    }
}
