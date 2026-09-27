using System.Globalization;

namespace MajorGym.Data;

/// <summary>
/// Ported line-for-line from Android's <c>com.majorgym.app.data.DateUtils.kt</c>. This file
/// has zero Android dependencies in the golden reference (pure Kotlin over java.time), so
/// every formula, threshold, and rounding rule below is preserved exactly — this is a
/// straight language port, not a reimplementation. Do not "fix" or simplify any of it.
/// </summary>
public static class DateUtils
{
    public static DateOnly ToLocalDate(long epochMillis) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMillis).ToLocalTime().DateTime);

    public static long ToMillis(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)))
            .ToUnixTimeMilliseconds();

    public static long AddMonthsMillis(long baseMillis, long months) =>
        ToMillis(ToLocalDate(baseMillis).AddMonths((int)months));

    public static long DaysBetweenNow(long targetMillis)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return ToLocalDate(targetMillis).DayNumber - today.DayNumber;
    }

    public static string FormatDate(long millis)
    {
        var d = ToLocalDate(millis);
        var month = d.ToDateTime(TimeOnly.MinValue).ToString("MMM", CultureInfo.InvariantCulture);
        return $"{d.Day:D2} {month} {d.Year}";
    }

    /// <summary>Same as <see cref="FormatDate"/> but with a time component — used for the
    /// QR token expiry, where "today" isn't precise enough since the token expires at a
    /// specific hour.</summary>
    public static string FormatDateTime(long millis)
    {
        var zoned = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime();
        var month = zoned.ToString("MMM", CultureInfo.InvariantCulture);
        var hour24 = zoned.Hour;
        var amPm = hour24 < 12 ? "AM" : "PM";
        var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
        return $"{zoned.Day:D2} {month} {zoned.Year}, {hour12}:{zoned.Minute:D2} {amPm}";
    }

    public static string FormatMoney(double v)
    {
        // Android uses NumberFormat.getIntegerInstance(Locale("en","IN")) — Indian digit
        // grouping (##,##,###). "en-IN" is the closest .NET culture equivalent.
        var nf = (long)v;
        return "\u20B9" + nf.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    }

    /// <summary>Time-only companion to <see cref="FormatDate"/>, used for the Share Backup
    /// File card (spec wants Backup Date and Backup Time shown as separate items).</summary>
    public static string FormatTimeOfDay(long millis)
    {
        var zoned = DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime();
        var hour24 = zoned.Hour;
        var amPm = hour24 < 12 ? "AM" : "PM";
        var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
        return $"{hour12}:{zoned.Minute:D2} {amPm}";
    }

    /// <summary>Human-readable file size for the Share Backup File card.</summary>
    public static string FormatBackupSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB"
    };

    public static readonly IReadOnlyDictionary<string, long> PlanMonths = new Dictionary<string, long>
    {
        ["1 Month"] = 1L,
        ["3 Months"] = 3L,
        ["6 Months"] = 6L,
        ["12 Months"] = 12L
    };

    /// <summary>
    /// Attendance percentage window: membership start date -> today (inclusive of both
    /// ends), never the calendar month, a fixed 30/31-day block, the date of first
    /// attendance, or the app install date. Recomputing this from startMillis and "today"
    /// each time it's called (rather than caching it) is what makes the percentage update
    /// automatically as the date changes, with no extra wiring needed.
    ///
    /// There is currently no gym "rest day" (e.g. Sunday) concept anywhere else in this
    /// codebase, so eligible days here is every calendar day in the window — if/when a
    /// rest-day setting is added elsewhere, this is the one place that would need to start
    /// excluding those days from the count. (Android doc comment, preserved verbatim.)
    /// </summary>
    public static int EligibleAttendanceDays(long startMillis, DateOnly? today = null)
    {
        var t = today ?? DateOnly.FromDateTime(DateTime.Now);
        var start = ToLocalDate(startMillis);
        if (start > t) return 0;
        return (t.DayNumber - start.DayNumber) + 1;
    }

    /// <summary>Percentage of <see cref="EligibleAttendanceDays"/> on which the member
    /// actually attended, rounded to the nearest whole percent. attendedDays should be the
    /// count of distinct calendar days with at least one recorded visit — never the raw
    /// visit count, since a member can check in more than once a day. (Android doc comment,
    /// preserved verbatim.)</summary>
    public static int AttendancePercentage(int attendedDays, int eligibleDays)
    {
        if (eligibleDays <= 0) return 0;
        var pct = (int)Math.Round((attendedDays / (double)eligibleDays) * 100, MidpointRounding.AwayFromZero);
        return Math.Clamp(pct, 0, 100);
    }
}

public enum MemberStatus { ACTIVE, EXPIRING, EXPIRED }

public static class MemberStatusExtensions
{
    public static MemberStatus StatusOf(long expiryMillis)
    {
        var days = DateUtils.DaysBetweenNow(expiryMillis);
        if (days < 0) return MemberStatus.EXPIRED;
        if (days <= 7) return MemberStatus.EXPIRING;
        return MemberStatus.ACTIVE;
    }
}
