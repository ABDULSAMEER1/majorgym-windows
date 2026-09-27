namespace MajorGym.Data.Entities;

/// <summary>
/// One real, permanent check-in event. Ported 1:1 from Android's
/// <c>com.majorgym.app.data.AttendanceRecord</c> (table "attendance_records").
///
/// This is additive, not a replacement: <see cref="Member.LastAttendanceMillis"/> still
/// exists, is still written exactly where it always was, and still means exactly what it
/// always meant ("most recent check-in"), overwritten every time. That field alone can't
/// power a Logs screen because history is lost the moment a member checks in again —
/// there is nothing to show for yesterday once today happens. This table exists purely so
/// a full per-day / per-member attendance history can actually be displayed; it changes
/// nothing about how, when, or why attendance gets recorded. (Android class doc, preserved.)
/// </summary>
public sealed class AttendanceRecord
{
    /// <summary>Device-local autoincrement id (Android: <c>@PrimaryKey(autoGenerate = true)</c>).
    /// NOT the identifier used for sync/dedup — see <see cref="GlobalId"/>.</summary>
    public long Id { get; set; }

    public required string MemberId { get; set; }

    public long TimestampMillis { get; set; }

    /// <summary>Local calendar day (local midnight, epoch millis) this visit belongs to —
    /// computed once at write time so day lookups are a plain indexed equality check
    /// instead of per-row time-zone math at query time.</summary>
    public long DayEpoch { get; set; }

    /// <summary><see cref="AttendanceSession"/> serialized as its name (e.g. "MORNING"),
    /// exactly as Android stores it — text rather than an ordinal so existing rows keep
    /// reading correctly even if the enum's declaration order ever changes later.</summary>
    public required string Session { get; set; }

    /// <summary>Globally unique (device-independent) id, assigned once when the visit is
    /// first recorded and carried unchanged through every sync/backup round-trip. This is
    /// the field the unique index and the sync change-log key on — see
    /// <c>SyncChangeLogEntry</c> / Android's <c>Repository.recomputeAndApplyAttendance</c>.</summary>
    public string GlobalId { get; set; } = Guid.NewGuid().ToString();

    // Unique indexes to be created in AppDatabase's schema DDL, matching Android exactly:
    //   CREATE UNIQUE INDEX ... ON attendance_records (memberId, timestampMillis)
    //   CREATE UNIQUE INDEX ... ON attendance_records (globalId)
    //   CREATE INDEX ... ON attendance_records (memberId)
    //   CREATE INDEX ... ON attendance_records (dayEpoch)
}

/// <summary>Ported from Android's <c>AttendanceSession</c> enum. Kept as two members only —
/// do not add a third without confirming the Android app actually has one (it doesn't, as
/// of the Stage 1 golden-reference audit).</summary>
public enum AttendanceSession
{
    MORNING,
    EVENING
}

public static class AttendanceSessionExtensions
{
    /// <summary>Gym convention used for the Morning/Evening split: anything before noon
    /// local time counts as the Morning batch, noon onward is Evening. Ported verbatim
    /// from Android's <c>sessionOf(millis)</c> — do not change the noon cutoff.</summary>
    public static AttendanceSession SessionOf(long epochMillis)
    {
        var localHour = DateTimeOffset.FromUnixTimeMilliseconds(epochMillis).ToLocalTime().Hour;
        return localHour < 12 ? AttendanceSession.MORNING : AttendanceSession.EVENING;
    }
}
