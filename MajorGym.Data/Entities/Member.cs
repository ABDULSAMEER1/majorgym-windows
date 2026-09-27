namespace MajorGym.Data.Entities;

/// <summary>
/// Ported 1:1 from the Android golden reference's <c>com.majorgym.app.data.Member</c>
/// (Room entity, table "members", unique index on Phone). Every field, its logical type,
/// and its default is preserved exactly — including fields the Android app no longer
/// actively writes for their original purpose (see <see cref="PendingDeletionMillis"/>)
/// — because the same physical column must still round-trip through an Android backup
/// import without loss. Do not remove, rename, or "clean up" any field here without a
/// corresponding, explicitly approved decision — see Stage 1 report §H risk #2 and
/// Stage 2 brief §17/§25/§26.
/// </summary>
public sealed class Member
{
    /// <summary>Primary key. Android generates this as a UUID string; Windows must too
    /// (a new Windows-created member must be indistinguishable, in ID shape, from one
    /// synced from an Android device).</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Unique (enforced by a unique index on this column, matching Android's
    /// <c>Index(value = ["phone"], unique = true)</c>).</summary>
    public required string Phone { get; set; }

    /// <summary>Relative path (not absolute — see PhotoStore) to the member's photo file,
    /// or null if none. Android's field is a nullable String; represented the same way here.</summary>
    public string? PhotoPath { get; set; }

    public required string Plan { get; set; }

    public double Fee { get; set; }

    public long JoinedMillis { get; set; }

    public long ExpiryMillis { get; set; }

    /// <summary>JSON-encoded list of <see cref="History.HistoryEntry"/> — kept as a JSON
    /// string column, exactly as Android stores it, rather than a normalized child table,
    /// so the backup JSON shape (a single "historyJson" string field per member) needs no
    /// translation on export/import.</summary>
    public string HistoryJson { get; set; } = "[]";

    /// <summary>Used to resolve conflicts when merging records synced from another device:
    /// whichever copy of a record was edited most recently wins. (Android doc comment,
    /// preserved verbatim because it documents a real behavioral contract other code relies on.)</summary>
    public long UpdatedAtMillis { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>PBKDF2 hash of the member's passkey. The plaintext passkey is shown to the
    /// owner exactly once at registration time (see PasskeyUtils) and never stored.</summary>
    public string PasswordHash { get; set; } = "";

    public long CreatedAtMillis { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long? LastAttendanceMillis { get; set; }

    /// <summary>
    /// Members expired 180+ days used to be described here as "archived, not deleted" but
    /// that was never actually implemented anywhere — this field is repurposed as the
    /// auto-delete safeguard instead (see MembershipCleanupWorker): true only in the brief
    /// window between a member first becoming eligible for deletion and the cleanup job
    /// confirming it a second time ~a day later before actually deleting them. Lets the UI
    /// (if ever needed) flag "pending removal" without hiding the member outright.
    /// (Android doc comment, preserved verbatim.)
    /// </summary>
    public bool Archived { get; set; }

    /// <summary>Unique, single-use-window token behind the member's QR. Regenerated on
    /// registration, on every renewal, and whenever the owner taps "Regenerate QR" — so a
    /// QR captured before a renewal can never be replayed to claim the member's updated
    /// plan/expiry. See <see cref="QrUtils"/>. Must stay a bare string field — the Flutter
    /// client app's QR payload depends on this exact shape (Stage 1 report §H risk #3).</summary>
    public string QrToken { get; set; } = "";

    /// <summary>Epoch millis after which <see cref="QrToken"/> is no longer valid.</summary>
    public long QrTokenExpiryMillis { get; set; }

    /// <summary>Optional government/institution ID reference (Aadhaar, PAN, college ID,
    /// etc.) — letters and digits only, validated at the UI layer in a later stage. Empty
    /// string means "not provided", matching the backup JSON's "idProof":"" convention.</summary>
    public string IdProof { get; set; } = "";

    /// <summary>Relative path to an optional photo of the member's ID proof document.
    /// Empty string means "no photo", never null, so old records/backups without this
    /// field default in safely.</summary>
    public string IdProofPhotoPath { get; set; } = "";

    /// <summary>
    /// ISO 19794-2 fingerprint template captured via a SecuGen USB scanner. Null means
    /// "not enrolled". This is a small (a few hundred byte) mathematical template, not a
    /// fingerprint image — the raw scan image is never stored.
    ///
    /// STORAGE CONTRACT (see Stage 1 report §4.5, §F, §H risk #1 and Stage 2 brief §15/§16):
    /// at rest in the Windows SQLite database, these bytes are ALWAYS wrapped by
    /// <see cref="CryptoUtils"/>'s Windows-side (DPAPI-backed) at-rest encryption before
    /// being written to this property's backing column — never stored as raw/plaintext
    /// ISO 19794-2 bytes. The in-memory <see cref="Member"/> object (as used by
    /// application code, fingerprint matching, enrollment, etc.) holds the DECRYPTED
    /// plaintext template bytes; MajorGym.Data.Repository is the only place that
    /// encrypts/decrypts on the way to/from the database, exactly mirroring the Android
    /// Repository's <c>decryptedForApp()</c> convention. A template that fails to decrypt
    /// must surface as null ("not enrolled"), never throw or crash.
    /// </summary>
    public byte[]? FingerprintTemplate { get; set; }

    /// <summary>
    /// No longer written or read for its original purpose by any current business logic
    /// (Android's MembershipCleanupWorker now implements the 30-Day Expired Member Archive
    /// instead of the old two-step "flag then delete after 4 months" safeguard this field
    /// originally supported). Left in place — not dropped — purely so old rows/backups
    /// that still carry a value continue to read in safely. Do not remove this column or
    /// repurpose it without an explicit decision (Stage 1 report §H risk #2).
    /// </summary>
    public long? PendingDeletionMillis { get; set; }
}
