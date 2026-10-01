using System.Text.Json.Nodes;
using MajorGym.Data.Entities;

namespace MajorGym.Data;

/// <summary>
/// Encodes/decodes the "fieldsJson" payload carried by a <see cref="SyncChangeLogEntry"/>,
/// and the wire format used to exchange changes/version vectors during LAN Device Sync.
/// Ported 1:1 from Android's <c>SyncChangeCodec</c> object — every JSON field key below
/// is preserved EXACTLY, since a Windows PC and an Android phone must be able to
/// exchange these change-log entries and parse each other's payloads (Stage 2 brief §22:
/// "must NOT silently replace the synchronization model... Preserve that design").
///
/// A Member's photo, ID-proof photo, and fingerprint template are represented here by
/// their actual bytes (Base64), not by <c>Member.PhotoPath</c> / <c>Member.IdProofPhotoPath</c>
/// — those are local paths that mean nothing on another device. The receiving side
/// (<see cref="DecodeMemberFields"/>) writes the bytes to its own local photo storage and
/// computes its own local path, the same way <see cref="BackupManager"/> already does for
/// backup restore.
///
/// NOTE ON STAGE 2 SCOPE: this class is pure, portable encode/decode logic with no network
/// dependency — it is needed by <see cref="Repository"/>'s change-log writes regardless of
/// whether the LAN transport (mDNS discovery + TCP socket) is implemented yet. The actual
/// network transport is intentionally left for a later stage (Stage 2 brief §22: "Do not
/// fully implement the Sync UI in Stage 2") — see SyncManager.cs's foundation/stub.
/// </summary>
public static class SyncChangeCodec
{
    private const string KName = "name";
    private const string KPhone = "phone";
    private const string KPlan = "plan";
    private const string KFee = "fee";
    private const string KJoined = "joinedMillis";
    private const string KExpiry = "expiryMillis";
    private const string KHistory = "history";
    private const string KPasswordHash = "passwordHash";
    private const string KCreated = "createdAtMillis";
    private const string KLastAttendance = "lastAttendanceMillis";
    private const string KArchived = "archived";
    private const string KQrToken = "qrToken";
    private const string KQrExpiry = "qrTokenExpiryMillis";
    private const string KIdProof = "idProof";
    private const string KPendingDeletion = "pendingDeletionMillis";
    private const string KPhoto = "photoBase64";
    private const string KIdPhoto = "idProofPhotoBase64";
    private const string KFingerprint = "fingerprintTemplateBase64";

    /// <summary>Public aliases of the photo field keys so callers outside the codec (backup restore)
    /// can force a photo into an UPDATE entry.</summary>
    public const string PhotoKey = KPhoto;
    public const string IdPhotoKey = KIdPhoto;

    private static readonly HashSet<string> AllKeys = new()
    {
        KName, KPhone, KPlan, KFee, KJoined, KExpiry, KHistory, KPasswordHash,
        KCreated, KLastAttendance, KArchived, KQrToken, KQrExpiry, KIdProof,
        KPendingDeletion, KPhoto, KIdPhoto, KFingerprint
    };

    /// <summary>Encodes <paramref name="m"/> into a change-log field payload. Pass
    /// <paramref name="keys"/> = null for a full ADD snapshot (every field), or a subset
    /// for an UPDATE (only the fields that actually changed — see <see cref="DiffKeys"/>).
    /// <paramref name="m"/> is expected to already carry a PLAINTEXT fingerprint template
    /// (i.e. called before Repository's at-rest encryption) — same convention as
    /// everywhere else outside Repository.</summary>
    public static JsonObject EncodeMember(Member m, ISet<string>? keys = null, Func<string, byte[]?>? readPhotoBytes = null)
    {
        var want = keys ?? AllKeys;
        var o = new JsonObject();
        if (want.Contains(KName)) o[KName] = m.Name;
        if (want.Contains(KPhone)) o[KPhone] = m.Phone;
        if (want.Contains(KPlan)) o[KPlan] = m.Plan;
        if (want.Contains(KFee)) o[KFee] = m.Fee;
        if (want.Contains(KJoined)) o[KJoined] = m.JoinedMillis;
        if (want.Contains(KExpiry)) o[KExpiry] = m.ExpiryMillis;
        if (want.Contains(KHistory)) o[KHistory] = SafeHistory(m.HistoryJson);
        if (want.Contains(KPasswordHash)) o[KPasswordHash] = m.PasswordHash;
        if (want.Contains(KCreated)) o[KCreated] = m.CreatedAtMillis;
        if (want.Contains(KLastAttendance) && m.LastAttendanceMillis is not null) o[KLastAttendance] = m.LastAttendanceMillis;
        if (want.Contains(KArchived)) o[KArchived] = m.Archived;
        if (want.Contains(KQrToken)) o[KQrToken] = m.QrToken;
        if (want.Contains(KQrExpiry)) o[KQrExpiry] = m.QrTokenExpiryMillis;
        if (want.Contains(KIdProof)) o[KIdProof] = m.IdProof;
        if (want.Contains(KPendingDeletion) && m.PendingDeletionMillis is not null) o[KPendingDeletion] = m.PendingDeletionMillis;
        if (want.Contains(KPhoto)) o[KPhoto] = ReadFileBase64(m.PhotoPath, readPhotoBytes);
        if (want.Contains(KIdPhoto)) o[KIdPhoto] = ReadFileBase64(string.IsNullOrWhiteSpace(m.IdProofPhotoPath) ? null : m.IdProofPhotoPath, readPhotoBytes);
        if (want.Contains(KFingerprint)) o[KFingerprint] = m.FingerprintTemplate is not null ? Convert.ToBase64String(m.FingerprintTemplate) : "";
        return o;
    }

    /// <summary>Which fields actually changed between the currently-stored
    /// <paramref name="existing"/> record and the <paramref name="updated"/> one about to
    /// be saved. Both are expected decrypted (plaintext fingerprint template). Photo/ID-photo
    /// changes are detected by the file's last-write time against existing.UpdatedAtMillis,
    /// since PhotoPath itself is a stable per-member path that doesn't change when the same
    /// photo slot is re-uploaded. (Android doc comment, preserved.)</summary>
    public static HashSet<string> DiffKeys(Member existing, Member updated)
    {
        var keys = new HashSet<string>();
        if (existing.Name != updated.Name) keys.Add(KName);
        if (existing.Phone != updated.Phone) keys.Add(KPhone);
        if (existing.Plan != updated.Plan) keys.Add(KPlan);
        if (existing.Fee != updated.Fee) keys.Add(KFee);
        if (existing.JoinedMillis != updated.JoinedMillis) keys.Add(KJoined);
        if (existing.ExpiryMillis != updated.ExpiryMillis) keys.Add(KExpiry);
        if (existing.HistoryJson != updated.HistoryJson) keys.Add(KHistory);
        if (existing.PasswordHash != updated.PasswordHash) keys.Add(KPasswordHash);
        if (existing.CreatedAtMillis != updated.CreatedAtMillis) keys.Add(KCreated);
        if (existing.LastAttendanceMillis != updated.LastAttendanceMillis) keys.Add(KLastAttendance);
        if (existing.Archived != updated.Archived) keys.Add(KArchived);
        if (existing.QrToken != updated.QrToken) keys.Add(KQrToken);
        if (existing.QrTokenExpiryMillis != updated.QrTokenExpiryMillis) keys.Add(KQrExpiry);
        if (existing.IdProof != updated.IdProof) keys.Add(KIdProof);
        if (existing.PendingDeletionMillis != updated.PendingDeletionMillis) keys.Add(KPendingDeletion);

        var fpChanged = (existing.FingerprintTemplate, updated.FingerprintTemplate) switch
        {
            (null, null) => false,
            (null, _) or (_, null) => true,
            var (a, b) => !a!.AsSpan().SequenceEqual(b!)
        };
        if (fpChanged) keys.Add(KFingerprint);

        if (PathContentChanged(existing.PhotoPath, updated.PhotoPath, existing.UpdatedAtMillis)) keys.Add(KPhoto);
        if (PathContentChanged(
                string.IsNullOrWhiteSpace(existing.IdProofPhotoPath) ? null : existing.IdProofPhotoPath,
                string.IsNullOrWhiteSpace(updated.IdProofPhotoPath) ? null : updated.IdProofPhotoPath,
                existing.UpdatedAtMillis))
            keys.Add(KIdPhoto);

        return keys;
    }

    /// <summary>Rebuilds a <see cref="Member"/> from a merged (ADD snapshot + replayed
    /// UPDATEs) field map. Writes any embedded photo/ID-photo/fingerprint bytes to this
    /// device's own storage via <paramref name="photoStore"/>. Returns null only if the
    /// payload is too malformed to use (missing even a name), so one bad record can't
    /// break sync for everything else. (Android doc comment, preserved.)
    /// Windows hardening with no Android counterpart: <paramref name="existingPhotoPath"/> is the
    /// profile photo this device already holds for the member. If the merged record carries no
    /// usable photo (empty/missing field, or the write failed) that existing photo is KEPT rather
    /// than silently clearing the member's picture — the app can only replace a profile photo,
    /// never remove it, so a blank here always means "no data", never "deleted".</summary>
    public static Member? DecodeMemberFields(string recordId, JsonObject fields, long timestampMillis, PhotoStore photoStore,
        string? existingPhotoPath = null)
    {
        if (fields[KName] is null) return null;

        string? photoPath = null;
        var photoB64 = (string?)fields[KPhoto] ?? "";
        if (!string.IsNullOrWhiteSpace(photoB64))
        {
            try { photoPath = photoStore.WriteMemberPhoto(recordId, Convert.FromBase64String(photoB64)); }
            catch { /* skip this photo, rest of the record still applies */ }
        }
        if (photoPath is null && !string.IsNullOrWhiteSpace(existingPhotoPath) && File.Exists(existingPhotoPath))
            photoPath = existingPhotoPath;

        var idProofPhotoPath = "";
        var idPhotoB64 = (string?)fields[KIdPhoto] ?? "";
        if (!string.IsNullOrWhiteSpace(idPhotoB64))
        {
            try { idProofPhotoPath = photoStore.WriteIdProofPhoto(recordId, Convert.FromBase64String(idPhotoB64)) ?? ""; }
            catch { /* skip */ }
        }

        byte[]? fingerprintTemplate = null;
        var fpB64 = (string?)fields[KFingerprint] ?? "";
        if (!string.IsNullOrWhiteSpace(fpB64))
        {
            try { fingerprintTemplate = Convert.FromBase64String(fpB64); } catch { /* leave null */ }
        }

        return new Member
        {
            Id = recordId,
            Name = (string?)fields[KName] ?? "",
            Phone = (string?)fields[KPhone] ?? "",
            PhotoPath = photoPath,
            Plan = (string?)fields[KPlan] ?? "",
            Fee = (double?)fields[KFee] ?? 0.0,
            JoinedMillis = (long?)fields[KJoined] ?? 0L,
            ExpiryMillis = (long?)fields[KExpiry] ?? 0L,
            HistoryJson = fields[KHistory]?.ToJsonString() ?? "[]",
            UpdatedAtMillis = timestampMillis,
            PasswordHash = (string?)fields[KPasswordHash] ?? "",
            CreatedAtMillis = (long?)fields[KCreated] ?? 0L,
            LastAttendanceMillis = fields[KLastAttendance] is not null ? (long?)fields[KLastAttendance] : null,
            Archived = (bool?)fields[KArchived] ?? false,
            QrToken = (string?)fields[KQrToken] ?? "",
            QrTokenExpiryMillis = (long?)fields[KQrExpiry] ?? 0L,
            IdProof = (string?)fields[KIdProof] ?? "",
            IdProofPhotoPath = idProofPhotoPath,
            FingerprintTemplate = fingerprintTemplate,
            PendingDeletionMillis = fields[KPendingDeletion] is not null ? (long?)fields[KPendingDeletion] : null
        };
    }

    // ---------- 30-Day Expired Member Archive ----------

    private const string KaName = "name";
    private const string KaPhone = "phone";
    private const string KaJoined = "joinedMillis";
    private const string KaLastPlan = "lastPlan";
    private const string KaLastFee = "lastFee";
    private const string KaLastStart = "lastStartMillis";
    private const string KaLastExpiry = "lastExpiryMillis";
    private const string KaIdProof = "idProof";
    private const string KaArchivedAt = "archivedAtMillis";

    /// <summary>Full snapshot of an <see cref="ArchivedMember"/> for its ADD change-log
    /// entry — there's no partial/UPDATE variant since an archive row is never edited.
    /// Deliberately carries no photo/fingerprint bytes: ArchivedMember never had any to
    /// begin with. (Android doc comment, preserved.)</summary>
    public static JsonObject EncodeArchivedMember(ArchivedMember a) => new()
    {
        [KaName] = a.Name,
        [KaPhone] = a.Phone,
        [KaJoined] = a.JoinedMillis,
        [KaLastPlan] = a.LastPlan,
        [KaLastFee] = a.LastFee,
        [KaLastStart] = a.LastStartMillis,
        [KaLastExpiry] = a.LastExpiryMillis,
        [KaIdProof] = a.IdProof,
        [KaArchivedAt] = a.ArchivedAtMillis
    };

    /// <summary>Rebuilds an <see cref="ArchivedMember"/> from a synced ADD entry's fields.
    /// Returns null only if the payload is too malformed to use (missing even a name) —
    /// same one-bad-record-can't-break-sync convention as <see cref="DecodeMemberFields"/>.</summary>
    public static ArchivedMember? DecodeArchivedMemberFields(string recordId, JsonObject fields)
    {
        if (fields[KaName] is null) return null;
        return new ArchivedMember
        {
            OriginalMemberId = recordId,
            Name = (string?)fields[KaName] ?? "",
            Phone = (string?)fields[KaPhone] ?? "",
            JoinedMillis = (long?)fields[KaJoined] ?? 0L,
            LastPlan = (string?)fields[KaLastPlan] ?? "",
            LastFee = (double?)fields[KaLastFee] ?? 0.0,
            LastStartMillis = (long?)fields[KaLastStart] ?? 0L,
            LastExpiryMillis = (long?)fields[KaLastExpiry] ?? 0L,
            IdProof = (string?)fields[KaIdProof] ?? "",
            ArchivedAtMillis = (long?)fields[KaArchivedAt] ?? 0L
        };
    }

    // ---------- Wire format (version vectors + change batches) ----------

    public static JsonObject EncodeVersionVector(IReadOnlyDictionary<string, long> v)
    {
        var o = new JsonObject();
        foreach (var (k, value) in v) o[k] = value;
        return o;
    }

    public static Dictionary<string, long> DecodeVersionVector(JsonObject o)
    {
        var m = new Dictionary<string, long>();
        foreach (var kv in o) m[kv.Key] = (long?)kv.Value ?? 0L;
        return m;
    }

    public static JsonArray EncodeChangeLog(IEnumerable<SyncChangeLogEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject
            {
                ["changeId"] = e.ChangeId,
                ["entityType"] = e.EntityType,
                ["recordId"] = e.RecordId,
                ["operation"] = e.Operation,
                ["originDeviceId"] = e.OriginDeviceId,
                ["seq"] = e.Seq,
                ["timestampMillis"] = e.TimestampMillis
            };
            if (e.FieldsJson is not null) o["fields"] = JsonNode.Parse(e.FieldsJson);
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>Never throws on a malformed individual entry — it's just skipped, the
    /// same "one bad record can't break the whole exchange" principle as everywhere else
    /// in this app's import paths.</summary>
    public static List<SyncChangeLogEntry> DecodeChangeLog(JsonArray arr)
    {
        var result = new List<SyncChangeLogEntry>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            try
            {
                result.Add(new SyncChangeLogEntry
                {
                    ChangeId = (string)o["changeId"]!,
                    EntityType = (string)o["entityType"]!,
                    RecordId = (string)o["recordId"]!,
                    Operation = (string)o["operation"]!,
                    OriginDeviceId = (string)o["originDeviceId"]!,
                    Seq = (long)o["seq"]!,
                    TimestampMillis = (long?)o["timestampMillis"] ?? 0L,
                    FieldsJson = o["fields"]?.ToJsonString()
                });
            }
            catch
            {
                // Skip malformed entry — Android parity.
            }
        }
        return result;
    }

    private static JsonArray SafeHistory(string historyJson)
    {
        try
        {
            return (JsonNode.Parse(string.IsNullOrWhiteSpace(historyJson) ? "[]" : historyJson) as JsonArray) ?? new JsonArray();
        }
        catch
        {
            return new JsonArray();
        }
    }

    private static string ReadFileBase64(string? path, Func<string, byte[]?>? readPhotoBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var bytes = readPhotoBytes?.Invoke(path) ?? (File.Exists(path) ? File.ReadAllBytes(path) : null);
            return bytes is not null ? Convert.ToBase64String(bytes) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>True if newPath's file content looks like it changed since sinceMillis
    /// (i.e. was written during the edit that's being saved right now), or if the photo
    /// was added/removed outright.</summary>
    private static bool PathContentChanged(string? oldPath, string? newPath, long sinceMillis)
    {
        if (string.IsNullOrWhiteSpace(oldPath) && string.IsNullOrWhiteSpace(newPath)) return false;
        if (string.IsNullOrWhiteSpace(oldPath) != string.IsNullOrWhiteSpace(newPath)) return true;
        if (!File.Exists(newPath)) return false;
        var lastWriteMillis = new DateTimeOffset(File.GetLastWriteTimeUtc(newPath!)).ToUnixTimeMilliseconds();
        return lastWriteMillis >= sinceMillis;
    }
}
