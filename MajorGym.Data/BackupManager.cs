using System.Diagnostics;
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
        return root.ToJsonString();
    }

    /// <summary>Reads the optional "archivedMembers" array (v5+). Missing key (any older
    /// backup) or a malformed individual row just yields no/fewer archived members — never
    /// a failure, and never affects member or attendance restore, which are parsed
    /// separately. (Android doc comment, preserved.)</summary>
    public static List<ArchivedMember> ImportArchivedMembers(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var arr = root["archivedMembers"] as JsonArray ?? new JsonArray();
        var result = new List<ArchivedMember>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var id = (string?)o["originalMemberId"] ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;
            try
            {
                result.Add(new ArchivedMember
                {
                    OriginalMemberId = id,
                    Name = (string?)o["name"] ?? "",
                    Phone = (string?)o["phone"] ?? "",
                    JoinedMillis = (long?)o["joinedMillis"] ?? 0L,
                    LastPlan = (string?)o["lastPlan"] ?? "",
                    LastFee = (double?)o["lastFee"] ?? 0.0,
                    LastStartMillis = (long?)o["lastStartMillis"] ?? 0L,
                    LastExpiryMillis = (long?)o["lastExpiryMillis"] ?? 0L,
                    IdProof = (string?)o["idProof"] ?? "",
                    ArchivedAtMillis = (long?)o["archivedAtMillis"] ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            catch
            {
                // Skip malformed record — Android parity ("Skipping malformed archived
                // member record ...").
            }
        }
        return result;
    }

    /// <summary>Reads the optional "attendance" array (v4+). Missing key (any older
    /// backup) or a malformed individual row just yields no/fewer attendance records —
    /// never a failure, and never affects member restore, which is parsed and applied
    /// separately by <see cref="ImportJson"/>. (Android doc comment, preserved.)</summary>
    public static List<AttendanceRecord> ImportAttendance(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var arr = root["attendance"] as JsonArray ?? new JsonArray();
        var result = new List<AttendanceRecord>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var memberId = (string?)o["memberId"] ?? "";
            if (string.IsNullOrWhiteSpace(memberId)) continue;
            if (o["timestampMillis"] is null) continue;
            var timestampMillis = (long)o["timestampMillis"]!;
            var sessionText = (string?)o["session"];
            var session = !string.IsNullOrWhiteSpace(sessionText)
                ? sessionText
                : AttendanceSessionExtensions.SessionOf(timestampMillis).ToString();
            var dayEpoch = o["dayEpoch"] is not null
                ? (long)o["dayEpoch"]!
                : DateUtils.ToMillis(DateUtils.ToLocalDate(timestampMillis));

            result.Add(new AttendanceRecord
            {
                MemberId = memberId,
                TimestampMillis = timestampMillis,
                DayEpoch = dayEpoch,
                Session = session!
            });
        }
        return result;
    }

    /// <summary>
    /// Reads "fingerprintTemplateBase64" (current format) as a plain template. A
    /// "fingerprintTemplateProtected" field (pre-v3 backups, portable-encrypted with a
    /// Sync Code) can no longer be decrypted — that key derivation no longer exists
    /// anywhere in this app — so it's skipped with a warning: the member still imports
    /// normally, just without a fingerprint, rather than the whole restore failing or a
    /// new key/prompt being invented to recover it. (Android doc comment, preserved —
    /// same behavior, same reasoning, on Windows.)
    /// </summary>
    public static List<Member> ImportJson(string json, PhotoStore photoStore)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var arr = root["members"] as JsonArray ?? new JsonArray();
        var result = new List<Member>();

        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject o)
            {
                Trace.TraceWarning($"[BackupManager] Skipping backup record {i} - not a JSON object");
                continue;
            }
            var id = (string?)o["id"];
            if (string.IsNullOrEmpty(id))
            {
                Trace.TraceWarning($"[BackupManager] Skipping backup record {i} - missing/invalid id");
                continue;
            }

            string? photoPath = null;
            var b64 = (string?)o["photoBase64"] ?? "";
            if (!string.IsNullOrWhiteSpace(b64))
            {
                try { photoPath = photoStore.WriteMemberPhoto(id, Convert.FromBase64String(b64)); }
                catch (Exception e) { Trace.TraceWarning($"[BackupManager] Skipping photo for record {id}: {e.Message}"); }
            }

            // Old backups never had this field — "" reads the same as "no ID proof
            // provided", never a crash (Android doc comment, preserved).
            var idProofPhotoPath = "";
            var idB64 = (string?)o["idProofPhotoBase64"] ?? "";
            if (!string.IsNullOrWhiteSpace(idB64))
            {
                try { idProofPhotoPath = photoStore.WriteIdProofPhoto(id, Convert.FromBase64String(idB64)) ?? ""; }
                catch (Exception e) { Trace.TraceWarning($"[BackupManager] Skipping ID proof photo for record {id}: {e.Message}"); }
            }

            byte[]? fingerprintTemplate = null;
            var protectedB64 = (string?)o["fingerprintTemplateProtected"] ?? "";
            if (!string.IsNullOrWhiteSpace(protectedB64))
            {
                Trace.TraceWarning($"[BackupManager] Backup contains a pre-v3 protected fingerprint template for {id} - it can no longer be decrypted and will be skipped");
            }
            else
            {
                var fpB64 = (string?)o["fingerprintTemplateBase64"] ?? "";
                if (!string.IsNullOrWhiteSpace(fpB64))
                {
                    try { fingerprintTemplate = Convert.FromBase64String(fpB64); } catch { /* leave null */ }
                }
            }

            try
            {
                var joinedMillis = (long?)o["joinedMillis"] ?? 0L;
                result.Add(new Member
                {
                    Id = id,
                    Name = (string)o["name"]!, // required — a missing "name" throws and is caught below, Android parity
                    Phone = (string?)o["phone"] ?? "",
                    PhotoPath = photoPath,
                    Plan = (string?)o["plan"] ?? "",
                    Fee = (double?)o["fee"] ?? 0.0,
                    JoinedMillis = joinedMillis,
                    ExpiryMillis = (long?)o["expiryMillis"] ?? 0L,
                    UpdatedAtMillis = (long?)o["updatedAtMillis"] ?? 0L,
                    HistoryJson = SafeHistoryArray(o),
                    IdProof = (string?)o["idProof"] ?? "",
                    IdProofPhotoPath = idProofPhotoPath,
                    PasswordHash = (string?)o["passwordHash"] ?? "",
                    CreatedAtMillis = (long?)o["createdAtMillis"] ?? joinedMillis,
                    LastAttendanceMillis = o["lastAttendanceMillis"] is not null ? (long?)o["lastAttendanceMillis"] : null,
                    Archived = (bool?)o["archived"] ?? false,
                    QrToken = (string?)o["qrToken"] ?? "",
                    QrTokenExpiryMillis = (long?)o["qrTokenExpiryMillis"] ?? 0L,
                    FingerprintTemplate = fingerprintTemplate,
                    PendingDeletionMillis = o["pendingDeletionMillis"] is not null ? (long?)o["pendingDeletionMillis"] : null
                });
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"[BackupManager] Skipping malformed backup record {id}: {e.Message}");
            }
        }
        return result;
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
