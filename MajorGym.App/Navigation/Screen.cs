namespace MajorGym.App.Navigation;

/// <summary>
/// Every reachable screen in Major Gym, ported from Android's <c>Screen</c> sealed class
/// (Screen.kt). Android's navigation is a hand-rolled state machine — a single
/// <c>var screen by remember { mutableStateOf&lt;Screen&gt;(Screen.Dashboard) }</c> swapped
/// inside an AnimatedContent — not Jetpack Navigation Compose (Stage 1 report §1.1). This
/// record hierarchy is the direct Windows port of that same pattern: a plain record type
/// per route, carrying only the route parameters Android's own route carries (a member id,
/// a "return to" screen for enrollment, etc.), consumed by <see cref="NavigationViewModel"/>
/// exactly the way MainActivity's `when` block consumes Android's sealed class.
///
/// Not every Android route has a screen implemented in this stage — see the Stage 3 report
/// for exactly which ones do. Routes for not-yet-implemented screens are still declared
/// here (so the navigation contract for later stages is visible now), but
/// <see cref="NavigationViewModel"/> does not yet know how to render most of them.
/// </summary>
public abstract record Screen
{
    public sealed record Dashboard : Screen;
    public sealed record Members : Screen;
    public sealed record Add : Screen;
    public sealed record Edit(string MemberId) : Screen;
    public sealed record Registered(string MemberId, string Passkey) : Screen;
    public sealed record Profile(string MemberId) : Screen;
    public sealed record Renew(string MemberId) : Screen;
    public sealed record Renewed(string MemberId, bool JustRenewed) : Screen;
    public sealed record Backup : Screen;
    public sealed record BackupHistory : Screen;
    public sealed record Sync : Screen;
    public sealed record TotalMembers : Screen;
    public sealed record ActiveMembers : Screen;
    public sealed record ExpiringMembers : Screen;
    public sealed record ExpiredMembers : Screen;
    public sealed record DueMembers : Screen;
    public sealed record Attendance : Screen;
    public sealed record EnrollFingerprint(string MemberId, Screen ReturnTo) : Screen;
    public sealed record AttendanceLogs : Screen;
    public sealed record AttendanceHistory(string MemberId) : Screen;
    public sealed record ExpiredArchive : Screen;
    public sealed record ArchivedMemberDetail(string MemberId) : Screen;
}
