using System.IO;
using System.Windows;
using MajorGym.Data;
using MajorGym.Data.Settings;
using MajorGym.Fingerprint;
using MajorGym.Kiosk;

namespace MajorGym.App;

/// <summary>
/// Stage 2 application entry point, extended in Stage 4b with: the optional startup-video
/// splash (brief §10), starting the kiosk loop so it actually runs continuously in the
/// background for the whole app lifetime (brief §6 — nothing previously called
/// <see cref="FingerprintKioskLoop.RequestStart"/> anywhere), and the one-time 30-day
/// expired-member archive sweep (see <see cref="Repository.ArchiveExpiredMembersOnce"/>'s
/// own doc comment for why this is a startup-time stand-in rather than a real periodic
/// scheduler in this stage).
/// </summary>
public partial class App : System.Windows.Application
{
    public static AppDatabase Database { get; private set; } = null!;
    public static Repository Repository { get; private set; } = null!;
    public static PhotoStore PhotoStore { get; private set; } = null!;
    public static ScannerHub ScannerHub { get; private set; } = null!;
    public static ScannerOwnership ScannerOwnership { get; private set; } = null!;
    public static FingerprintKioskLoop KioskLoop { get; private set; } = null!;
    public static SyncPrefs SyncPrefs { get; private set; } = null!;
    public static Navigation.NavigationViewModel Nav { get; private set; } = null!;

    /// <summary>%LOCALAPPDATA%\MajorGym — the Windows equivalent root of Android's private
    /// app storage (filesDir), holding major_gym.db, \photos, \id_photos, \settings, and
    /// (once real audio assets are added in a later stage) \Assets\Audio (Stage 1 report §I).</summary>
    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MajorGym");

    /// <summary>Where a real startup_video.mp4 would live if one is ever supplied — see
    /// OnStartup below. NOT created/populated by this stage (brief §10: "If the required
    /// video asset is already present in the cumulative project, reuse it. If the asset is
    /// missing from the supplied ZIP, do NOT invent or substitute another video."). The
    /// Assets\Video directory itself IS created so a real file can simply be dropped in
    /// later with no further code changes.</summary>
    public static string StartupVideoPath { get; } =
        Path.Combine(AppDataDirectory, "Assets", "Video", "startup_video.mp4");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // no MainWindow exists yet while the optional splash plays

        Directory.CreateDirectory(AppDataDirectory);
        Database = AppDatabase.OpenOrCreate(AppDataDirectory);
        SyncPrefs = new SyncPrefs(AppDataDirectory);
        Repository = new Repository(Database, SyncPrefs.DeviceId);
        PhotoStore = new PhotoStore(AppDataDirectory);

        ScannerHub = new ScannerHub();
        ScannerOwnership = new ScannerOwnership();
        var audioDir = Path.Combine(AppDataDirectory, "Assets", "Audio");
        Directory.CreateDirectory(audioDir);
        Directory.CreateDirectory(Path.GetDirectoryName(StartupVideoPath)!);
        KioskLoop = new FingerprintKioskLoop(Repository, ScannerHub, ScannerOwnership, new MembershipAudioPlayer(), audioDir);

        Nav = new Navigation.NavigationViewModel(Repository, PhotoStore, KioskLoop);

        // 30-Day Expired Member Archive sweep — see Repository.ArchiveExpiredMembersOnce's
        // own doc comment for the platform-adaptation this represents (no periodic
        // scheduler exists in this Windows foundation yet).
        try { Repository.ArchiveExpiredMembersOnce(PhotoStore); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"[App] Expired-member archive sweep failed: {ex.Message}"); }

        if (File.Exists(StartupVideoPath))
        {
            var splash = new Views.SplashWindow(StartupVideoPath);
            splash.Finished += () =>
            {
                splash.Close();
                LaunchMainWindow();
            };
            splash.Show();
        }
        else
        {
            LaunchMainWindow();
        }
    }

    private void LaunchMainWindow()
    {
        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();

        KioskLoop.RequestStart(); // runs continuously in the background for the app's whole lifetime from here on
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Mirrors Android's onDestroy: stop the loop and release ownership, but never
        // force-close the persistent ScannerHub session except as part of process exit
        // itself (Stage 2 brief §10 — only close on shutdown, a genuine detach, or an
        // unrecoverable SDK state).
        KioskLoop.RequestStopAsync().GetAwaiter().GetResult();
        ScannerHub.Dispose();
        Database.Dispose();
        base.OnExit(e);
    }
}
