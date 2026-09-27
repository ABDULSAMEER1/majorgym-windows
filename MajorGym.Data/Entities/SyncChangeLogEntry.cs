namespace MajorGym.Data.Entities;

/// <summary><see cref="SyncChangeLogEntry.EntityType"/> values — ported verbatim from
/// Android's top-level <c>ENTITY_MEMBER</c> / <c>ENTITY_ATTENDANCE</c> / <c>ENTITY_ARCHIVED_MEMBER</c>
/// constants. String values must match exactly — they are replicated to Android peers over
/// the LAN sync protocol and stored as literal text in this table.</summary>
public static class SyncEntityType
{
    public const string Member = "MEMBER";
    public const string Attendance = "ATTENDANCE";

    /// <summary>30-Day Expired Member Archive sync — <see cref="SyncChangeLogEntry.RecordId"/>
    /// is the <see cref="ArchivedMember.OriginalMemberId"/>. Uses the same Add/Delete
    /// operations as everything else: ADD = archived, DELETE = restored back to a normal
    /// member. There is no UPDATE — an archive row is never edited, only created once or
    /// removed on restore. (Android doc comment, preserved.)</summary>
    public const string ArchivedMember = "ARCHIVED_MEMBER";
}

/// <summary><see cref="SyncChangeLogEntry.Operation"/> values — ported verbatim from
/// Android's <c>OP_ADD</c> / <c>OP_UPDATE</c> / <c>OP_DELETE</c> constants.</summary>
public static class SyncOperation
{
    public const string Add = "ADD";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
}

/// <summary>
/// One real, permanent record of a change to a Member or Attendance record — an "event",
/// not just a snapshot. Ported 1:1 from Android's <c>com.majorgym.app.data.SyncChangeLogEntry</c>
/// (table "sync_change_log"). This is what makes Device Sync fault tolerant: instead of
/// comparing whole records and guessing what happened from a single "last modified time",
/// every Add/Update/Delete is captured here with everything needed to replay it
/// deterministically on any other device — including a Windows PC syncing with an Android
/// phone, which is exactly why this table's shape must not drift from the Android original.
///
/// - <see cref="ChangeId"/> — unique id for this exact change, so receiving the same change
///   twice (repeated sync, gossip relaying it a second time) is a trivially detectable
///   no-op (an INSERT OR IGNORE on this primary key) — the idempotency the design requires.
/// - <see cref="RecordId"/> — which Member (its Id) or Attendance visit (its GlobalId) this
///   change applies to.
/// - <see cref="Operation"/> — ADD / UPDATE / DELETE.
/// - <see cref="OriginDeviceId"/> — which device actually made this change.
/// - <see cref="Seq"/> — that device's own monotonically increasing counter (1, 2, 3...),
///   scoped to OriginDeviceId. Together, (OriginDeviceId, Seq) is how two devices figure
///   out — without a central server — exactly which changes each one is still missing:
///   "send me everything from device X after seq N" (a version vector).
/// - <see cref="TimestampMillis"/> — wall-clock time, used only to order changes to the
///   SAME record for field-level merge — never used as the sole signal for "did this
///   happen" the way Member.UpdatedAtMillis used to be.
/// - <see cref="FieldsJson"/> — for ADD, every field of the new record; for UPDATE, only
///   the fields that actually changed (so two devices editing different fields of the same
///   Member never clobber each other); null for DELETE, which needs no payload beyond
///   "this id is gone".
///
/// Deliberately never modified or deleted once written (an immutable log, not a mutable
/// "current state" table) — the merged/current state of a record is always DERIVED from
/// replaying its full history, so that replay gives the same answer on every device
/// regardless of the order changes arrived in. (Android class doc, preserved verbatim —
/// this is a load-bearing design contract, not incidental commentary.)
/// </summary>
public sealed class SyncChangeLogEntry
{
    public required string ChangeId { get; set; }
    public required string EntityType { get; set; }
    public required string RecordId { get; set; }
    public required string Operation { get; set; }
    public required string OriginDeviceId { get; set; }
    public long Seq { get; set; }
    public long TimestampMillis { get; set; }
    public string? FieldsJson { get; set; }

    // Unique indexes matching Android exactly:
    //   CREATE UNIQUE INDEX ... ON sync_change_log (originDeviceId, seq)
    //   CREATE INDEX ... ON sync_change_log (recordId)
    //   CREATE INDEX ... ON sync_change_log (entityType)
}

/// <summary>One row of a version-vector aggregate query (MAX(seq) GROUP BY originDeviceId)
/// — ported from Android's <c>DeviceSeq</c> data class, which existed only because Room
/// needs a concrete return type for a multi-column aggregate query; kept here for the same
/// reason and so <c>SyncChangeCodec</c>/<c>SyncManager</c> logic ports with minimal change.</summary>
public sealed record DeviceSeq(string OriginDeviceId, long MaxSeq);
