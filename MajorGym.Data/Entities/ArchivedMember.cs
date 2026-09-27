namespace MajorGym.Data.Entities;

/// <summary>
/// Lightweight historical record created once a member has been expired 30+ days with no
/// renewal. Ported 1:1 from Android's <c>com.majorgym.app.data.ArchivedMember</c> (table
/// "archived_members"). Deliberately NOT a copy of the full <see cref="Member"/> entity —
/// no photo, no ID proof photo, no fingerprint template, no QR token, no password hash, and
/// no attendance history (that's permanently deleted at archive time on Android — the
/// Windows implementation of the archiving operation itself is a later stage, not Stage 2)
/// — just enough to identify a returning customer and show what they last had.
///
/// <see cref="OriginalMemberId"/> is the primary key so archiving the same member twice
/// (e.g. a cleanup job running again, or applying the same backup twice) is a no-op
/// (INSERT OR IGNORE on insert) rather than a duplicate row. (Android class doc, preserved.)
/// </summary>
public sealed class ArchivedMember
{
    public required string OriginalMemberId { get; set; }

    public required string Name { get; set; }

    public required string Phone { get; set; }

    /// <summary>Original join date, unchanged from the operational Member.JoinedMillis.</summary>
    public long JoinedMillis { get; set; }

    public required string LastPlan { get; set; }

    public double LastFee { get; set; }

    /// <summary>Start of the member's last membership cycle — derived at archive time from
    /// LastExpiryMillis minus the plan's duration; falls back to JoinedMillis if the plan
    /// name isn't recognized. (Android doc comment, preserved — this derivation logic
    /// belongs to the archiving operation, ported in a later stage, not to this entity.)</summary>
    public long LastStartMillis { get; set; }

    public long LastExpiryMillis { get; set; }

    /// <summary>Optional government/institution ID reference, carried over as-is — no photo.</summary>
    public string IdProof { get; set; } = "";

    public long ArchivedAtMillis { get; set; }

    // Non-unique indexes matching Android exactly:
    //   CREATE INDEX ... ON archived_members (phone)
    //   CREATE INDEX ... ON archived_members (name)
}
