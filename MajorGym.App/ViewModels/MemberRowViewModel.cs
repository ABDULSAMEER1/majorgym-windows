using System.Windows.Input;
using MajorGym.App.Navigation;
using MajorGym.Data;
using MajorGym.Data.Entities;

namespace MajorGym.App.ViewModels;

/// <summary>
/// Ported from Android's <c>MemberRow</c> composable (Screens.kt ~809-861) — its own doc
/// comment says it's "extracted... so the Dashboard's four filtered list pages... can reuse
/// the exact same member-card UI and the exact same profile/renew navigation, instead of a
/// second copy," which is exactly what this shared row ViewModel (paired with
/// Controls/MemberRowView) does on Windows too, used by both <see cref="MembersViewModel"/>
/// and <see cref="FilteredMembersViewModel"/> — a single implementation instead of the two
/// diverging near-duplicates this port previously had.
/// </summary>
public sealed class MemberRowViewModel
{
    public required Member Member { get; init; }
    public required MemberStatus Status { get; init; }
    /// <summary>Android: <c>showDueAmount &amp;&amp; m.fee > 0.0</c> — only the Due Members list sets this true.</summary>
    public bool ShowDueAmount { get; init; }
    /// <summary>Android: <c>showDueAmount &amp;&amp; m.fee > 0.0</c> — exact gate for the "Due: ₹X" line.</summary>
    public bool ShowDueLine => ShowDueAmount && Member.Fee > 0.0;
    public string PhonePlanText => $"{Member.Phone} · {Member.Plan}";
    public string ExpiresText => $"Expires {DateUtils.FormatDate(Member.ExpiryMillis)}";
    public string DueText => $"Due: {DateUtils.FormatMoney(Member.Fee)}";
    public required ICommand OpenProfile { get; init; }
    public required ICommand Renew { get; init; }

    public static MemberRowViewModel For(Member m, NavigationViewModel nav, bool showDueAmount = false) => new()
    {
        Member = m,
        Status = MemberStatusExtensions.StatusOf(m.ExpiryMillis),
        ShowDueAmount = showDueAmount,
        OpenProfile = new RelayCommand(() => nav.NavigateTo(new Screen.Profile(m.Id))),
        Renew = new RelayCommand(() => nav.NavigateTo(new Screen.Renew(m.Id)))
    };
}
