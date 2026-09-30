using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MajorGym.Data.Entities;

namespace MajorGym.Data;

/// <summary>
/// Exports/imports the full gym database — including member photos, embedded as Base64 —
/// into a single self-contained JSON document. Ported 1:1, field name for field name, from
/// Android's <c>BackupManager</c> object. This is THE Stage 2 compatibility-critical file
/// (Stage 1 report §6, Stage 2 brief §16): every JSON field name, the top-level object
/// shape, and the schema-version defensive-read conventions below are reproduced exactly
/// so a ZIP produced by the Android app opens correctly here, and one produced here opens
/// correctly on Android.
///
/// <see cref="BackupZip"/> wraps this JSON into the actual "backup.json"-inside-a-ZIP file
/// the owner sees. Every value that ends up as part of a filename (currently just each
/// member's Id) is routed through <see cref="FileSafety.ResolveWithin"/>, which
/// rejects/neutralizes "../", absolute paths, and anything else that isn't a plain safe
/// token — a malicious backup file can never make this app write outside its own
/// photos/id_photos directories. (Android class doc, preserved verbatim.)
/// </summary>
public static class BackupManager
{
    /// <summary>Current backup JSON schema version. Old backups (missing "schemaVersion",
    /// or with a lower one) still restore — every field is read defensively with a safe
    /// fallback below. (Android doc comment, preserved — same version history/meaning:
    /// v3 = plain-Base64 fingerprint templates, v4 = attendance array, v5 = archivedMembers
    /// array. This Windows implementation writes and understands the same v5 shape.)</summary>
    public const int BackupSchemaVersion = 5;

    /// <summary>
    /// <paramref name="attendance"/> and <paramref name="archivedMembers"/> default to
    /// empty so a caller exporting just members (e.g. a future device-sync-style payload)
    /// is unaffected — only the full local backup path is expected to pass real rows for
    /// all three. (Android doc comment, preserved.)
    /// </summary>
    public static string ExportJson(IEnumerable<Member> members, PhotoStore photoStore,
        IEnumerable<AttendanceRecord>? attendance = null, IEnumerable<ArchivedMember>? archivedMembers = null)
    {
        var arr = new JsonArray();
        foreach (var m in members)
        {
            var o = new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["phone"] = m.Phone,
                ["plan"] = m.Plan,
                ["fee"] = m.Fee,
                ["joinedMillis"] = m.JoinedMillis,
                ["expiryMillis"] = m.ExpiryMillis,
                ["updatedAtMillis"] = m.UpdatedAtMillis,
                ["history"] = ParseHistoryArrayOrEmpty(m.HistoryJson),
                ["idProof"] = m.IdProof,
                ["passwordHash"] = m.PasswordHash,
                ["createdAtMillis"] = m.CreatedAtMillis
            };
            if (m.LastAttendanceMillis is not null) o["lastAttendanceMillis"] = m.LastAttendanceMillis;
            o["archived"] = m.Archived;
            o["qrToken"] = m.QrToken;
            o["qrTokenExpiryMillis"] = m.QrTokenExpiryMillis;

            if (!string.IsNullOrWhiteSpace(m.IdProofPhotoPath))
            {
                var bytes = photoStore.ReadBytes(m.IdProofPhotoPath);
                if (bytes is not null) o["idProofPhotoBase64"] = Convert.ToBase64String(bytes);
            }
            if (!string.IsNullOrWhiteSpace(m.PhotoPath))
            {
                var bytes = photoStore.ReadBytes(m.PhotoPath);
                if (bytes is not null) o["photoBase64"] = Convert.ToBase64String(bytes);
            }

            // Fingerprint template: m.FingerprintTemplate arriving here is always already
            // plaintext (Repository decrypts on the way out of the database); written out
            // as plain Base64 so restoring this same backup — on this PC or a new one —
            // never depends on a Sync Code or any other app state a reinstall would wipe
            // out. Re-encrypted at rest with the restoring device's own DPAPI key by
            // Repository the moment it's saved back into the database. (Android doc
            // comment, preserved — same design, same reasoning, DPAPI instead of Keystore.)
            if (m.FingerprintTemplate is not null)
            {
                o["fingerprintTemplateBase64"] = Convert.ToBase64String(m.FingerprintTemplate);
            }
            if (m.PendingDeletionMillis is not null) o["pendingDeletionMillis"] = m.PendingDeletionMillis;
            arr.Add(o);
        }

        var attendanceArr = new JsonArray();
        foreach (var rec in attendance ?? Enumerable.Empty<AttendanceRecord>())
        {
            attendanceArr.Add(new JsonObject
            {
                ["memberId"] = rec.MemberId,
                ["timestampMillis"] = rec.TimestampMillis,
                ["dayEpoch"] = rec.DayEpoch,
                ["session"] = rec.Session
            });
        }

        var archivedArr = new JsonArray();
        foreach (var a in archivedMembers ?? Enumerable.Empty<ArchivedMember>())
        {
            archivedArr.Add(new JsonObject
            {
                ["originalMemberId"] = a.OriginalMemberId,
                ["name"] = a.Name,
                ["phone"] = a.Phone,
                ["joinedMillis"] = a.JoinedMillis,
                ["lastPlan"] = a.LastPlan,
                ["lastFee"] = a.LastFee,
                ["lastStartMillis"] = a.LastStartMillis,
                ["lastExpiryMillis"] = a.LastExpiryMillis,
                ["idProof"] = a.IdProof,
                ["archivedAtMillis"] = a.ArchivedAtMillis
            });
        }

        var root = new JsonObject
        {
            ["app"] = "MajorGym",
            ["schemaVersion"] = BackupSchemaVersion,
            ["exportedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["members"] = arr,
            ["attendance"] = attendanceArr,
            ["archivedMembers"] = archivedArr
        };
        return root.ToJsonString(ExportOptions);
    }

    /// <summary>Compact output that does NOT \u-escape '+' and other harmless characters
    /// in Base64 photo data (the default encoder inflates a photo-heavy backup by several
    /// percent). Both forms are valid JSON that Android's org.json reads identically.</summary>
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>One member decoded from a backup. Photo bytes are held in memory — nothing
    /// touches disk until the database restore has committed (see
    /// <see cref="BackupService.ImportAndRestore"/>). <see cref="Member.PhotoPath"/> /
    /// <see cref="Member.IdProofPhotoPath"/> already hold the final per-member-id paths.</summary>
    public sealed class ParsedMember
    {
        public required Member Member { get; init; }
        public byte[]? PhotoBytes { get; init; }
        public byte[]? IdProofPhotoBytes { get; init; }
    }

    public sealed class ParsedBackup
    {
        public List<ParsedMember> Members { get; } = new();
        public List<AttendanceRecord> Attendance { get; } = new();
        public List<ArchivedMember> Archived { get; } = new();
        public int? SchemaVersion { get; set; }
        /// <summary>Length of the raw "members" array, before any record was skipped.</summary>
        public int RawMemberCount { get; set; }
        /// <summary>Records/photos that were malformed and skipped (Android skips them too).</summary>
        public int SkippedRecords { get; set; }
        public int SkippedPhotos { get; set; }
    }

    private const int MaxPhotoBytes = 64 * 1024 * 1024;

    /// <summary>Parses and validates the whole document without side effects: no file is
    /// written and no database row touched. Every field is read with Android's org.json
    /// coercion rules (a number where a string is expected, a decimal where an integer is
    /// expected, etc. never throws); a malformed record is skipped, never fatal.</summary>
    public static ParsedBackup ParseBackup(string json, PhotoStore photoStore)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new BackupFormatException("This file isn't a valid backup.");
        var result = new ParsedBackup { SchemaVersion = (int?)Js.Lng(root, "schemaVersion") };

        var arr = root["members"] as JsonArray ?? new JsonArray();
        result.RawMemberCount = arr.Count;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject o) { result.SkippedRecords++; Trace.TraceWarning($"[BackupManager] Skipping backup record {i} - not a JSON object"); continue; }
            var id = Js.Str(o, "id");
            if (string.IsNullOrWhiteSpace(id)) { result.SkippedRecords++; Trace.TraceWarning($"[BackupManager] Skipping backup record {i} - missing/invalid id"); continue; }
            var name = Js.Str(o, "name");
            if (name is null) { result.SkippedRecords++; Trace.TraceWarning($"[BackupManager] Skipping backup record {id} - missing name"); continue; }

            byte[]? photoBytes = DecodeBase64(Js.Str(o, "photoBase64"), id, "photo", result);
            var photoPath = photoBytes is null ? null : photoStore.MemberPhotoPathFor(id);
            if (photoPath is null) photoBytes = null;

            byte[]? idBytes = DecodeBase64(Js.Str(o, "idProofPhotoBase64"), id, "ID proof photo", result);
            var idPath = idBytes is null ? null : photoStore.IdProofPhotoPathFor(id);
            if (idPath is null) idBytes = null;

            byte[]? fingerprint = null;
            if (!string.IsNullOrWhiteSpace(Js.Str(o, "fingerprintTemplateProtected")))
            {
                Trace.TraceWarning($"[BackupManager] Backup contains a pre-v3 protected fingerprint template for {id} - it can no longer be decrypted and will be skipped");
            }
            else
            {
                fingerprint = DecodeBase64(Js.Str(o, "fingerprintTemplateBase64"), id, "fingerprint template", result);
            }

            var joined = Js.Lng(o, "joinedMillis") ?? 0L;
            result.Members.Add(new ParsedMember
            {
                PhotoBytes = photoBytes,
                IdProofPhotoBytes = idBytes,
                Member = new Member
                {
                    Id = id,
                    Name = name,
                    Phone = Js.Str(o, "phone") ?? "",
                    PhotoPath = photoPath,
                    Plan = Js.Str(o, "plan") ?? "",
                    Fee = Js.Dbl(o, "fee") ?? 0.0,
                    JoinedMillis = joined,
                    ExpiryMillis = Js.Lng(o, "expiryMillis") ?? 0L,
                    UpdatedAtMillis = Js.Lng(o, "updatedAtMillis") ?? 0L,
                    HistoryJson = SafeHistoryArray(o),
                    IdProof = Js.Str(o, "idProof") ?? "",
                    IdProofPhotoPath = idPath ?? "",
                    PasswordHash = Js.Str(o, "passwordHash") ?? "",
                    CreatedAtMillis = Js.Lng(o, "createdAtMillis") ?? joined,
                    LastAttendanceMillis = Js.Lng(o, "lastAttendanceMillis"),
                    Archived = Js.Bool(o, "archived") ?? false,
                    QrToken = Js.Str(o, "qrToken") ?? "",
                    QrTokenExpiryMillis = Js.Lng(o, "qrTokenExpiryMillis") ?? 0L,
                    FingerprintTemplate = fingerprint,
                    PendingDeletionMillis = Js.Lng(o, "pendingDeletionMillis")
                }
            });
        }

        result.Attendance.AddRange(ReadAttendance(root));
        result.Archived.AddRange(ReadArchived(root));
        return result;
    }

    private static byte[]? DecodeBase64(string? text, string id, string what, ParsedBackup result)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length == 0 || bytes.Length > MaxPhotoBytes) throw new FormatException("empty or oversized");
            return bytes;
        }
        catch (Exception e)
        {
            result.SkippedPhotos++;
            Trace.TraceWarning($"[BackupManager] Skipping {what} for record {id}: {e.Message}");
            return null;
        }
    }

    /// <summary>Reads the optional "archivedMembers" array (v5+). Missing key or a malformed
    /// row just yields no/fewer archived members — never a failure. (Android doc, preserved.)</summary>
    public static List<ArchivedMember> ImportArchivedMembers(string json) =>
        ReadArchived(JsonNode.Parse(json) as JsonObject ?? new JsonObject());

    /// <summary>Reads the optional "attendance" array (v4+). Missing key or a malformed row
    /// just yields no/fewer records — never a failure. (Android doc, preserved.)</summary>
    public static List<AttendanceRecord> ImportAttendance(string json) =>
        ReadAttendance(JsonNode.Parse(json) as JsonObject ?? new JsonObject());

    private static List<ArchivedMember> ReadArchived(JsonObject root)
    {
        var result = new List<ArchivedMember>();
        foreach (var node in root["archivedMembers"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject o) continue;
            var id = Js.Str(o, "originalMemberId") ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;
            result.Add(new ArchivedMember
            {
                OriginalMemberId = id,
                Name = Js.Str(o, "name") ?? "",
                Phone = Js.Str(o, "phone") ?? "",
                JoinedMillis = Js.Lng(o, "joinedMillis") ?? 0L,
                LastPlan = Js.Str(o, "lastPlan") ?? "",
                LastFee = Js.Dbl(o, "lastFee") ?? 0.0,
                LastStartMillis = Js.Lng(o, "lastStartMillis") ?? 0L,
                LastExpiryMillis = Js.Lng(o, "lastExpiryMillis") ?? 0L,
                IdProof = Js.Str(o, "idProof") ?? "",
                ArchivedAtMillis = Js.Lng(o, "archivedAtMillis") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        return result;
    }

    private static List<AttendanceRecord> ReadAttendance(JsonObject root)
    {
        var result = new List<AttendanceRecord>();
        foreach (var node in root["attendance"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject o) continue;
            var memberId = Js.Str(o, "memberId") ?? "";
            if (string.IsNullOrWhiteSpace(memberId)) continue;
            var ts = Js.Lng(o, "timestampMillis");
            if (ts is null) continue;
            var sessionText = Js.Str(o, "session");
            result.Add(new AttendanceRecord
            {
                MemberId = memberId,
                TimestampMillis = ts.Value,
                DayEpoch = Js.Lng(o, "dayEpoch") ?? DateUtils.ToMillis(DateUtils.ToLocalDate(ts.Value)),
                Session = !string.IsNullOrWhiteSpace(sessionText) ? sessionText : AttendanceSessionExtensions.SessionOf(ts.Value).ToString()
            });
        }
        return result;
    }

    /// <summary>Convenience wrapper kept for API parity with Android's importJson: returns
    /// just the members. Unlike <see cref="ParseBackup"/> this WRITES photo files
    /// immediately, so the restore path never uses it.</summary>
    public static List<Member> ImportJson(string json, PhotoStore photoStore)
    {
        var parsed = ParseBackup(json, photoStore);
        foreach (var pm in parsed.Members)
        {
            if (pm.PhotoBytes is not null) photoStore.WriteMemberPhoto(pm.Member.Id, pm.PhotoBytes);
            if (pm.IdProofPhotoBytes is not null) photoStore.WriteIdProofPhoto(pm.Member.Id, pm.IdProofPhotoBytes);
        }
        return parsed.Members.Select(pm => pm.Member).ToList();
    }

    /// <summary>Never lets one corrupted history array fail the whole member record —
    /// falls back to an empty history list. (Android doc comment, preserved.)</summary>
    private static string SafeHistoryArray(JsonObject o)
    {
        try
        {
            return o["history"]?.ToJsonString() ?? "[]";
        }
        catch
        {
            return "[]";
        }
    }

    private static JsonArray ParseHistoryArrayOrEmpty(string historyJson)
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
}

/// <summary>Lenient JSON readers with the same coercion as Android's org.json
/// optString/optLong/optDouble/optBoolean: a JSON null or absent key is "missing"; a
/// number read as string yields its text; a decimal or numeric string read as a long is
/// truncated. A real backup can therefore never be rejected over a representation
/// difference.</summary>
internal static class Js
{
    public static string? Str(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return n.ToJsonString();
    }

    public static long? Lng(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return double.IsNaN(d) ? 0L : (long)d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) return (long)d2;
        return null;
    }

    public static double? Dbl(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) return d2;
        return null;
    }

    public static bool? Bool(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s))
        {
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }
}
