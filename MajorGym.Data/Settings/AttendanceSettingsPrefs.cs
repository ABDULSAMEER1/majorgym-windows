namespace MajorGym.Data.Settings;

/// <summary>The three numerical counts shown at the top of the Attendance Logs page that
/// can each be shown/hidden independently. Ported 1:1 from Android's
/// <c>AttendanceCount</c> enum.</summary>
public enum AttendanceCount { PRESENT, MORNING, EVENING }

/// <summary>
/// Windows port of Android's <c>AttendanceSettingsPrefs</c>. Hiding a count only hides its
/// numeric value — the card itself, its label, and the underlying attendance
/// data/calculations are all unaffected (Android doc comment, preserved). Same logical
/// keys/defaults, backed by <see cref="LocalSettingsStore"/> instead of SharedPreferences.
/// </summary>
public sealed class AttendanceSettingsPrefs
{
    private readonly LocalSettingsStore _store;

    public AttendanceSettingsPrefs(string appDataDirectory) =>
        _store = new LocalSettingsStore(appDataDirectory, "majorgym_attendance_settings");

    public bool IsCountVisible(AttendanceCount count) => _store.GetBool(KeyFor(count), true);

    public void SetCountVisible(AttendanceCount count, bool visible) => _store.SetBool(KeyFor(count), visible);

    private static string KeyFor(AttendanceCount count) => $"count_visible_{count}";
}
