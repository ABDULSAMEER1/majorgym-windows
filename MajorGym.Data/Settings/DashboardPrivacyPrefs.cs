namespace MajorGym.Data.Settings;

/// <summary>The five Dashboard cards/sections that display a member count and can be
/// individually hidden. Ported 1:1 from Android's <c>DashboardCard</c> enum.</summary>
public enum DashboardCard { TOTAL, ACTIVE, EXPIRING, EXPIRED, DUE }

/// <summary>
/// Dashboard privacy settings — purely a display preference, never touches member data,
/// membership status, Sync, Backup, or anything else. Windows port of Android's
/// <c>DashboardPrivacyPrefs</c>.
///
/// - <see cref="MasterPrivacyOn"/>: when true, the Dashboard shows only the MAJOR GYM
///   header and the master toggle itself — every count/card below it is hidden.
///   Independent of the per-card settings below.
/// - Per-<see cref="DashboardCard"/> number visibility: each of the five count-showing
///   cards remembers its own ON/OFF choice independently. Defaults to true (visible) for
///   every card. (Android doc comment, preserved — identical semantics on Windows.)
/// </summary>
public sealed class DashboardPrivacyPrefs
{
    private const string KeyMasterPrivacy = "master_privacy_on";
    private readonly LocalSettingsStore _store;

    public DashboardPrivacyPrefs(string appDataDirectory) =>
        _store = new LocalSettingsStore(appDataDirectory, "majorgym_dashboard_privacy");

    public bool MasterPrivacyOn
    {
        get => _store.GetBool(KeyMasterPrivacy, false);
        set => _store.SetBool(KeyMasterPrivacy, value);
    }

    public bool IsNumberVisible(DashboardCard card) => _store.GetBool(KeyFor(card), true);

    public void SetNumberVisible(DashboardCard card, bool visible) => _store.SetBool(KeyFor(card), visible);

    private static string KeyFor(DashboardCard card) => $"number_visible_{card}";
}
