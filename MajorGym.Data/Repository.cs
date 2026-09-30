using Microsoft.Data.Sqlite;
using MajorGym.Data.Entities;

namespace MajorGym.Data;

/// <summary>
/// Windows port of the core (non-backup, non-sync-transport) surface of Android's
/// <c>Repository</c> class. Stage 2 scope: enough real, working CRUD + change-log-on-every-
/// save behavior to be a genuine "database foundation" (Stage 2 brief §17/§18), not a
/// full port of Android's 900+ line Repository (which also owns backup-file plumbing —
/// see <see cref="BackupManager"/> — and full sync reconciliation, both later-stage work
/// per Stage 2 brief §22/§27).
///
/// All member data (including photos) lives entirely on-device: structured fields in a
/// local SQLite database, photos as JPEG files under the app's private data directory
/// (see <see cref="PhotoStore"/>). Nothing here ever touches the network — the Windows app
/// works fully offline, exactly like Android.
///
/// Fingerprint templates are encrypted at rest: everywhere OUTSIDE this class, a
/// Member.FingerprintTemplate is always plaintext ISO 19794-2 bytes ready to hand to
/// FingerprintScanner.Match(); this class alone is responsible for encrypting on the way
/// into SQLite and decrypting on the way out, via <see cref="CryptoUtils"/>. A template
/// that fails to decrypt surfaces as null — "not enrolled" — rather than crashing.
/// (Android class doc, preserved — same contract, DPAPI instead of Keystore underneath.)
/// </summary>
public sealed class Repository
{
    private readonly AppDatabase _db;
    private readonly string _deviceId;

    /// <summary>Serializes every change-log write so two saves racing on the same device
    /// can never be assigned the same SyncChangeLogEntry.Seq — Seq must actually be unique
    /// per device for the version-vector sync protocol to work. C# equivalent of Android's
    /// <c>changeLogMutex: Mutex</c>.</summary>
    private readonly SemaphoreSlim _changeLogLock = new(1, 1);

    public const long AttendanceRetentionMonths = 4L; // Android: ATTENDANCE_RETENTION_MONTHS

    public Repository(AppDatabase db, string deviceId)
    {
        _db = db;
        _deviceId = deviceId;
    }

    // ---------------- Reads ----------------

    public List<Member> GetAll()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM members";
        using var reader = cmd.ExecuteReader();
        var result = new List<Member>();
        while (reader.Read()) result.Add(DecryptedForApp(ReadMember(reader)));
        return result;
    }

    /// <summary>Android parity: <c>MemberDao.getAll()</c> — <c>SELECT * FROM members ORDER BY
    /// name ASC</c> (SQLite's default BINARY collation, exactly what Room uses), which is what
    /// every member list screen (Members, Total/Active/Expiring/Expired/Due) is built from.
    /// <see cref="GetAll"/> above stays unordered because Android's <c>allOnce()</c> (used by
    /// backup export and the archival sweep) is unordered too.</summary>
    public List<Member> GetAllByName()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM members ORDER BY name ASC";
        using var reader = cmd.ExecuteReader();
        var result = new List<Member>();
        while (reader.Read()) result.Add(DecryptedForApp(ReadMember(reader)));
        return result;
    }

    public Member? GetById(string id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM members WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? DecryptedForApp(ReadMember(reader)) : null;
    }

    /// <summary>True if some other member already has this phone number (unique-phone
    /// business rule, ported unchanged).</summary>
    public bool IsPhoneTaken(string phone, string excludingId = "")
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM members WHERE phone = $phone AND id != $excludingId";
        cmd.Parameters.AddWithValue("$phone", phone);
        cmd.Parameters.AddWithValue("$excludingId", excludingId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // ---------------- Save (with change-log diff, matching Android exactly) ----------------

    /// <summary>
    /// Saves a Member and records exactly what changed as a Device Sync change-log entry
    /// — a full ADD snapshot for a brand-new member, or only the fields that actually
    /// differ from what's currently stored for an edit/renewal/attendance-timestamp-bump/
    /// etc. This is the single save path everything else should call, so every kind of
    /// Member mutation is covered automatically without needing to touch each call site.
    /// (Android doc comment, preserved — same design, same call contract.)
    /// </summary>
    public void Save(Member member)
    {
        _changeLogLock.Wait();
        try
        {
            using var tx = _db.Connection.BeginTransaction();
            SaveCore(member, tx, stampMissingUpdatedAt: true);
            tx.Commit();
        }
        finally
        {
            _changeLogLock.Release();
        }
    }

    /// <summary>The body of <see cref="Save"/>, runnable inside a caller-owned transaction
    /// (the caller holds the change-log lock). <paramref name="stampMissingUpdatedAt"/> is
    /// false for backup restore: Android's mergeAll stores the incoming updatedAtMillis
    /// untouched, even when it is 0.</summary>
    private void SaveCore(Member member, SqliteTransaction tx, bool stampMissingUpdatedAt)
    {
        var existing = GetByIdOnceNoLock(member.Id, tx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var toStore = member.UpdatedAtMillis > 0 || !stampMissingUpdatedAt ? member : CloneWithUpdatedAt(member, now);
        var logTs = toStore.UpdatedAtMillis > 0 ? toStore.UpdatedAtMillis : now;

        Upsert(EncryptedForStorage(toStore), tx);

        if (existing is null)
        {
            InsertChangeLog(new SyncChangeLogEntry
            {
                ChangeId = Guid.NewGuid().ToString(),
                EntityType = SyncEntityType.Member,
                RecordId = toStore.Id,
                Operation = SyncOperation.Add,
                OriginDeviceId = _deviceId,
                Seq = NextSeqNoLock(_deviceId, tx),
                TimestampMillis = logTs,
                FieldsJson = SyncChangeCodec.EncodeMember(toStore).ToJsonString()
            }, tx);
        }
        else
        {
            var changedKeys = SyncChangeCodec.DiffKeys(existing, toStore);
            if (changedKeys.Count > 0)
            {
                InsertChangeLog(new SyncChangeLogEntry
                {
                    ChangeId = Guid.NewGuid().ToString(),
                    EntityType = SyncEntityType.Member,
                    RecordId = toStore.Id,
                    Operation = SyncOperation.Update,
                    OriginDeviceId = _deviceId,
                    Seq = NextSeqNoLock(_deviceId, tx),
                    TimestampMillis = logTs,
                    FieldsJson = SyncChangeCodec.EncodeMember(toStore, changedKeys).ToJsonString()
                }, tx);
            }
        }
    }

    /// <summary>Records one real, permanent check-in event, matching Android's
    /// recordAttendanceVisit exactly: inserts the AttendanceRecord row AND a matching
    /// ATTENDANCE/ADD change-log entry in the same transaction. Does NOT bump
    /// Member.LastAttendanceMillis itself — Android's kiosk service does that via a
    /// separate Save() call, and this port preserves that same two-call shape rather than
    /// merging them, so callers porting the kiosk loop see the same call sequence.</summary>
    public void RecordAttendanceVisit(string memberId, long? atMillis = null)
    {
        var at = atMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _changeLogLock.Wait();
        try
        {
            using var tx = _db.Connection.BeginTransaction();

            var day = DateUtils.ToMillis(DateUtils.ToLocalDate(at));
            var session = AttendanceSessionExtensions.SessionOf(at).ToString();
            var globalId = Guid.NewGuid().ToString();

            using (var cmd = _db.Connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO attendance_records (memberId, timestampMillis, dayEpoch, session, globalId)
                    VALUES ($memberId, $ts, $day, $session, $globalId)
                    """;
                cmd.Parameters.AddWithValue("$memberId", memberId);
                cmd.Parameters.AddWithValue("$ts", at);
                cmd.Parameters.AddWithValue("$day", day);
                cmd.Parameters.AddWithValue("$session", session);
                cmd.Parameters.AddWithValue("$globalId", globalId);
                cmd.ExecuteNonQuery();
            }

            var fields = new System.Text.Json.Nodes.JsonObject
            {
                ["memberId"] = memberId,
                ["timestampMillis"] = at,
                ["dayEpoch"] = day,
                ["session"] = session
            };
            InsertChangeLog(new SyncChangeLogEntry
            {
                ChangeId = Guid.NewGuid().ToString(),
                EntityType = SyncEntityType.Attendance,
                RecordId = globalId,
                Operation = SyncOperation.Add,
                OriginDeviceId = _deviceId,
                Seq = NextSeqNoLock(_deviceId, tx),
                TimestampMillis = at,
                FieldsJson = fields.ToJsonString()
            }, tx);

            tx.Commit();
        }
        finally
        {
            _changeLogLock.Release();
        }
    }

    /// <summary>
    /// Deletes a member and everything tied to them: their database row, their profile
    /// photo file, their ID proof photo file, and all of their attendance records —
    /// ported from Android's <c>deleteWithFiles</c>, including its all-in-one-transaction
    /// crash-safety guarantee and its "log a DELETE tombstone per attendance row's
    /// globalId, read BEFORE deleting" sequencing (Android doc comment, preserved: a
    /// paired device must remove the exact same rows rather than only inferring the
    /// deletion from the member itself disappearing).
    /// </summary>
    public void DeleteWithFiles(Member member, PhotoStore photoStore)
    {
        _changeLogLock.Wait();
        try
        {
            photoStore.DeletePhoto(member.Id);
            photoStore.DeleteIdProofPhoto(member.Id);

            using var tx = _db.Connection.BeginTransaction();

            using (var del = _db.Connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM members WHERE id = $id";
                del.Parameters.AddWithValue("$id", member.Id);
                del.ExecuteNonQuery();
            }

            var attendanceGlobalIds = new List<string>();
            using (var sel = _db.Connection.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT globalId FROM attendance_records WHERE memberId = $id";
                sel.Parameters.AddWithValue("$id", member.Id);
                using var r = sel.ExecuteReader();
                while (r.Read()) attendanceGlobalIds.Add(r.GetString(0));
            }

            using (var delA = _db.Connection.CreateCommand())
            {
                delA.Transaction = tx;
                delA.CommandText = "DELETE FROM attendance_records WHERE memberId = $id";
                delA.Parameters.AddWithValue("$id", member.Id);
                delA.ExecuteNonQuery();
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var globalId in attendanceGlobalIds)
            {
                InsertChangeLog(new SyncChangeLogEntry
                {
                    ChangeId = Guid.NewGuid().ToString(),
                    EntityType = SyncEntityType.Attendance,
                    RecordId = globalId,
                    Operation = SyncOperation.Delete,
                    OriginDeviceId = _deviceId,
                    Seq = NextSeqNoLock(_deviceId, tx),
                    TimestampMillis = now,
                    FieldsJson = null
                }, tx);
            }

            InsertChangeLog(new SyncChangeLogEntry
            {
                ChangeId = Guid.NewGuid().ToString(),
                EntityType = SyncEntityType.Member,
                RecordId = member.Id,
                Operation = SyncOperation.Delete,
                OriginDeviceId = _deviceId,
                Seq = NextSeqNoLock(_deviceId, tx),
                TimestampMillis = now,
                FieldsJson = null
            }, tx);

            tx.Commit();
        }
        finally
        {
            _changeLogLock.Release();
        }
    }

    // ---------------- Attendance queries (Stage 4b) ----------------

    /// <summary>All check-ins recorded on a given local calendar day (see
    /// <see cref="DateUtils.ToLocalDate"/>/<see cref="DateUtils.ToMillis"/> for computing
    /// the dayEpoch key), newest first — powers the Attendance Logs screen.</summary>
    public List<AttendanceRecord> GetAttendanceForDay(long dayEpochMillis)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM attendance_records WHERE dayEpoch = $day ORDER BY timestampMillis DESC";
        cmd.Parameters.AddWithValue("$day", dayEpochMillis);
        using var reader = cmd.ExecuteReader();
        var result = new List<AttendanceRecord>();
        while (reader.Read()) result.Add(ReadAttendance(reader));
        return result;
    }

    /// <summary>Every distinct day that has at least one attendance row, newest first —
    /// used to populate the Attendance Logs day picker without the caller needing to guess
    /// which days actually have data.</summary>
    public List<long> GetDistinctAttendanceDayEpochs()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT dayEpoch FROM attendance_records ORDER BY dayEpoch DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<long>();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    /// <summary>One member's full check-in history, newest first — powers the Attendance
    /// History screen (alongside <see cref="GetDistinctAttendedDayCount"/> for the
    /// percentage figure).</summary>
    public List<AttendanceRecord> GetAttendanceForMember(string memberId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM attendance_records WHERE memberId = $id ORDER BY timestampMillis DESC";
        cmd.Parameters.AddWithValue("$id", memberId);
        using var reader = cmd.ExecuteReader();
        var result = new List<AttendanceRecord>();
        while (reader.Read()) result.Add(ReadAttendance(reader));
        return result;
    }

    /// <summary>Count of distinct calendar days (by dayEpoch, inclusive both ends) on which
    /// <paramref name="memberId"/> has at least one recorded visit — the numerator
    /// <see cref="DateUtils.AttendancePercentage"/> expects (distinct days, never the raw
    /// visit count, since a member can check in more than once a day — DateUtils.cs's own
    /// doc comment, preserved).</summary>
    public int GetDistinctAttendedDayCount(string memberId, long fromDayEpoch, long toDayEpochInclusive)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT dayEpoch) FROM attendance_records
            WHERE memberId = $id AND dayEpoch >= $from AND dayEpoch <= $to
            """;
        cmd.Parameters.AddWithValue("$id", memberId);
        cmd.Parameters.AddWithValue("$from", fromDayEpoch);
        cmd.Parameters.AddWithValue("$to", toDayEpochInclusive);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Android AttendanceRetentionWorker: deletes attendance older than
    /// <see cref="AttendanceRetentionMonths"/> months (cutoff = local midnight,
    /// dayEpoch strictly below it). No change-log entry, same as Android. Returns rows deleted.</summary>
    public int CleanupOldAttendance()
    {
        var cutoffDate = DateOnly.FromDateTime(DateTime.Now).AddMonths(-(int)AttendanceRetentionMonths);
        var cutoff = DateUtils.ToMillis(cutoffDate);
        _changeLogLock.Wait();
        try
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM attendance_records WHERE dayEpoch < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
        finally { _changeLogLock.Release(); }
    }

    /// <summary>Every attendance row ever recorded, oldest first — used by the Backup
    /// screen to build a full export via <see cref="BackupManager.ExportJson"/>.</summary>
    public List<AttendanceRecord> GetAllAttendance()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM attendance_records ORDER BY timestampMillis";
        using var reader = cmd.ExecuteReader();
        var result = new List<AttendanceRecord>();
        while (reader.Read()) result.Add(ReadAttendance(reader));
        return result;
    }

    // ---------------- 30-Day Expired Member Archive (Stage 4b) ----------------
    // See Member.cs (PendingDeletionMillis doc comment) and SyncChangeLogEntry.cs
    // (SyncEntityType.ArchivedMember doc comment) for the design this ports: ADD = a
    // member was archived, DELETE = an archived record was restored back to a normal
    // member. There is no defined sync operation for "an archived record was permanently
    // erased without being restored" — Android's own design only ever expects an archived
    // row to end either by staying archived or by being restored — so
    // DeleteArchivedMemberPermanently below is deliberately local-only (see its own doc
    // comment) rather than inventing a fourth sync semantic Android doesn't have.

    /// <summary>Every archived member, most-recently-archived first — powers the Expired
    /// Archive list screen.</summary>
    public List<ArchivedMember> GetArchivedMembers()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM archived_members ORDER BY archivedAtMillis DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<ArchivedMember>();
        while (reader.Read()) result.Add(ReadArchivedMember(reader));
        return result;
    }

    public ArchivedMember? GetArchivedMemberById(string originalMemberId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM archived_members WHERE originalMemberId = $id";
        cmd.Parameters.AddWithValue("$id", originalMemberId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadArchivedMember(reader) : null;
    }

    /// <summary>
    /// Scans every member currently expired <paramref name="thresholdDays"/>+ days and
    /// moves each into archived_members: writes the ArchivedMember snapshot, deletes the
    /// operational Member row and all of their attendance history, and deletes their
    /// photo/ID-proof photo files via <paramref name="photoStore"/> — matching
    /// ArchivedMember.cs's own doc comment ("no photo, no ID proof photo... that's
    /// permanently deleted at archive time"). <see cref="ArchivedMember.LastStartMillis"/>
    /// is derived from ExpiryMillis minus the recognized plan's duration, falling back to
    /// JoinedMillis for an unrecognized plan name (ArchivedMember.cs's own doc comment).
    ///
    /// PLATFORM ADAPTATION (documented, not silent — see Stage 4b report): Android runs
    /// this as a periodic WorkManager job; no equivalent periodic background-task scheduler
    /// exists in this Windows foundation yet (out of Stage 4b's explicit scope — LAN Sync's
    /// transport layer is the only other stubbed subsystem, and this is a comparably-sized
    /// piece of new infrastructure). This method is called once at app startup instead
    /// (see App.xaml.cs) — a deliberate, bounded stand-in, not a claim that it reproduces
    /// Android's exact daily cadence.
    /// </summary>
    public List<ArchivedMember> ArchiveExpiredMembersOnce(PhotoStore photoStore, long thresholdDays = 30)
    {
        var archived = new List<ArchivedMember>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var m in GetAll())
        {
            var days = DateUtils.DaysBetweenNow(m.ExpiryMillis);
            if (days > -thresholdDays) continue; // not expired long enough yet

            var months = DateUtils.PlanMonths.GetValueOrDefault(m.Plan, 0L);
            var lastStart = months > 0 ? DateUtils.AddMonthsMillis(m.ExpiryMillis, -months) : m.JoinedMillis;

            var record = new ArchivedMember
            {
                OriginalMemberId = m.Id,
                Name = m.Name,
                Phone = m.Phone,
                JoinedMillis = m.JoinedMillis,
                LastPlan = m.Plan,
                LastFee = m.Fee,
                LastStartMillis = lastStart,
                LastExpiryMillis = m.ExpiryMillis,
                IdProof = m.IdProof,
                ArchivedAtMillis = now
            };

            _changeLogLock.Wait();
            try
            {
                photoStore.DeletePhoto(m.Id);
                photoStore.DeleteIdProofPhoto(m.Id);

                using var tx = _db.Connection.BeginTransaction();

                using (var ins = _db.Connection.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT OR IGNORE INTO archived_members
                            (originalMemberId, name, phone, joinedMillis, lastPlan, lastFee, lastStartMillis, lastExpiryMillis, idProof, archivedAtMillis)
                        VALUES ($id, $name, $phone, $joined, $plan, $fee, $start, $expiry, $idProof, $archivedAt)
                        """;
                    ins.Parameters.AddWithValue("$id", record.OriginalMemberId);
                    ins.Parameters.AddWithValue("$name", record.Name);
                    ins.Parameters.AddWithValue("$phone", record.Phone);
                    ins.Parameters.AddWithValue("$joined", record.JoinedMillis);
                    ins.Parameters.AddWithValue("$plan", record.LastPlan);
                    ins.Parameters.AddWithValue("$fee", record.LastFee);
                    ins.Parameters.AddWithValue("$start", record.LastStartMillis);
                    ins.Parameters.AddWithValue("$expiry", record.LastExpiryMillis);
                    ins.Parameters.AddWithValue("$idProof", record.IdProof);
                    ins.Parameters.AddWithValue("$archivedAt", record.ArchivedAtMillis);
                    ins.ExecuteNonQuery();
                }

                var attendanceGlobalIds = new List<string>();
                using (var sel = _db.Connection.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT globalId FROM attendance_records WHERE memberId = $id";
                    sel.Parameters.AddWithValue("$id", m.Id);
                    using var r = sel.ExecuteReader();
                    while (r.Read()) attendanceGlobalIds.Add(r.GetString(0));
                }
                using (var delA = _db.Connection.CreateCommand())
                {
                    delA.Transaction = tx;
                    delA.CommandText = "DELETE FROM attendance_records WHERE memberId = $id";
                    delA.Parameters.AddWithValue("$id", m.Id);
                    delA.ExecuteNonQuery();
                }
                using (var delM = _db.Connection.CreateCommand())
                {
                    delM.Transaction = tx;
                    delM.CommandText = "DELETE FROM members WHERE id = $id";
                    delM.Parameters.AddWithValue("$id", m.Id);
                    delM.ExecuteNonQuery();
                }

                foreach (var globalId in attendanceGlobalIds)
                {
                    InsertChangeLog(new SyncChangeLogEntry
                    {
                        ChangeId = Guid.NewGuid().ToString(),
                        EntityType = SyncEntityType.Attendance,
                        RecordId = globalId,
                        Operation = SyncOperation.Delete,
                        OriginDeviceId = _deviceId,
                        Seq = NextSeqNoLock(_deviceId, tx),
                        TimestampMillis = now,
                        FieldsJson = null
                    }, tx);
                }
                InsertChangeLog(new SyncChangeLogEntry
                {
                    ChangeId = Guid.NewGuid().ToString(),
                    EntityType = SyncEntityType.Member,
                    RecordId = m.Id,
                    Operation = SyncOperation.Delete,
                    OriginDeviceId = _deviceId,
                    Seq = NextSeqNoLock(_deviceId, tx),
                    TimestampMillis = now,
                    FieldsJson = null
                }, tx);
                InsertChangeLog(new SyncChangeLogEntry
                {
                    ChangeId = Guid.NewGuid().ToString(),
                    EntityType = SyncEntityType.ArchivedMember,
                    RecordId = record.OriginalMemberId,
                    Operation = SyncOperation.Add,
                    OriginDeviceId = _deviceId,
                    Seq = NextSeqNoLock(_deviceId, tx),
                    TimestampMillis = now,
                    FieldsJson = SyncChangeCodec.EncodeArchivedMember(record).ToJsonString()
                }, tx);

                tx.Commit();
                archived.Add(record);
            }
            finally
            {
                _changeLogLock.Release();
            }
        }
        return archived;
    }

    /// <summary>Recreates a normal, operational Member from an archived record — Android
    /// parity: "DELETE = restored back to a normal member" (SyncChangeLogEntry.cs doc
    /// comment). Since an ArchivedMember never retained a photo, ID-proof photo,
    /// fingerprint template, or QR/passkey (see ArchivedMember.cs doc comment), a restore
    /// is necessarily a fresh re-registration for those fields: a brand-new passkey is
    /// generated (returned here so the caller can show/share it once, exactly like a new
    /// member's Registered screen) and a fresh QR token is issued. Plan, fee, join date,
    /// last expiry, and ID-proof TEXT (not the photo) all carry over unchanged. Returns
    /// null if no such archived record exists (e.g. already restored from another
    /// screen/device).</summary>
    public (Member Member, string Passkey)? RestoreArchivedMember(string originalMemberId)
    {
        _changeLogLock.Wait();
        try
        {
            using var tx = _db.Connection.BeginTransaction();

            ArchivedMember? archivedMember = null;
            using (var sel = _db.Connection.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT * FROM archived_members WHERE originalMemberId = $id";
                sel.Parameters.AddWithValue("$id", originalMemberId);
                using var r = sel.ExecuteReader();
                if (r.Read()) archivedMember = ReadArchivedMember(r);
            }
            if (archivedMember is null)
            {
                tx.Rollback();
                return null;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var passkey = PasskeyUtils.Generate();
            var history = new List<HistoryEntry>
            {
                new("Restored", archivedMember.LastPlan, archivedMember.LastFee, now, archivedMember.LastExpiryMillis)
            };
            var member = new Member
            {
                Id = archivedMember.OriginalMemberId,
                Name = archivedMember.Name,
                Phone = archivedMember.Phone,
                Plan = archivedMember.LastPlan,
                Fee = archivedMember.LastFee,
                JoinedMillis = archivedMember.JoinedMillis,
                ExpiryMillis = archivedMember.LastExpiryMillis,
                HistoryJson = History.ToJson(history),
                UpdatedAtMillis = now,
                PasswordHash = PasskeyUtils.Hash(passkey),
                CreatedAtMillis = now,
                IdProof = archivedMember.IdProof,
                QrToken = QrUtils.FreshToken(),
                QrTokenExpiryMillis = now + QrUtils.TokenValidityMillis
            };

            Upsert(member, tx); // no fingerprint template to encrypt — plain Upsert is safe

            using (var delA = _db.Connection.CreateCommand())
            {
                delA.Transaction = tx;
                delA.CommandText = "DELETE FROM archived_members WHERE originalMemberId = $id";
                delA.Parameters.AddWithValue("$id", originalMemberId);
                delA.ExecuteNonQuery();
            }

            InsertChangeLog(new SyncChangeLogEntry
            {
                ChangeId = Guid.NewGuid().ToString(),
                EntityType = SyncEntityType.Member,
                RecordId = member.Id,
                Operation = SyncOperation.Add,
                OriginDeviceId = _deviceId,
                Seq = NextSeqNoLock(_deviceId, tx),
                TimestampMillis = now,
                FieldsJson = SyncChangeCodec.EncodeMember(member).ToJsonString()
            }, tx);
            InsertChangeLog(new SyncChangeLogEntry
            {
                ChangeId = Guid.NewGuid().ToString(),
                EntityType = SyncEntityType.ArchivedMember,
                RecordId = originalMemberId,
                Operation = SyncOperation.Delete,
                OriginDeviceId = _deviceId,
                Seq = NextSeqNoLock(_deviceId, tx),
                TimestampMillis = now,
                FieldsJson = null
            }, tx);

            tx.Commit();
            return (member, passkey);
        }
        finally
        {
            _changeLogLock.Release();
        }
    }

    /// <summary>Permanently erases an archived record WITHOUT restoring it — a data-hygiene
    /// action ("this person is never coming back, stop showing them in the archive list"),
    /// distinct from <see cref="RestoreArchivedMember"/>. Deliberately does not write a
    /// sync change-log entry: Android's own design (SyncChangeLogEntry.cs's
    /// SyncEntityType.ArchivedMember doc comment) only defines ADD (archived) and DELETE
    /// (restored) for this entity type — there is no third operation for "erased outright"
    /// to port, so this stays a local-only action on this device rather than inventing one
    /// (see this class's own "30-Day Expired Member Archive" section header comment, and
    /// the Stage 4b report's "known limitations").</summary>
    public bool DeleteArchivedMemberPermanently(string originalMemberId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM archived_members WHERE originalMemberId = $id";
        cmd.Parameters.AddWithValue("$id", originalMemberId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ---------------- Backup import (Stage 4b) ----------------

    /// <summary>
    /// Applies a fully-parsed, already-validated backup in ONE transaction: either every
    /// record is applied or (on any exception) none is — SQLite rolls the whole thing back
    /// when the transaction is disposed uncommitted, so the database is never left
    /// half-restored. Ports Android's Repository.mergeAll / restoreAttendance /
    /// restoreArchivedMembersFromBackup semantics:
    ///  - a member already stored with a NEWER updatedAtMillis than the incoming one is kept
    ///    (incoming older copies never overwrite newer local edits);
    ///  - Android's members DAO inserts with OnConflictStrategy.REPLACE, which also evicts a
    ///    row that collides on the UNIQUE phone index. Windows only had ON CONFLICT(id), so a
    ///    phone collision with a different id threw and aborted the restore mid-way; the
    ///    same eviction is reproduced here (the caller has already written the safety
    ///    snapshot, exactly as Android does);
    ///  - attendance / archived rows are INSERT OR IGNORE, so restoring the same file twice
    ///    is a no-op; local-only records are never deleted.
    /// Returns the ids of members actually applied so the caller writes photo files only for
    /// those, and only after this commit succeeded.
    /// </summary>
    public RestoreCounts RestoreBackup(
        IReadOnlyList<Member> members, IReadOnlyList<AttendanceRecord> attendance, IReadOnlyList<ArchivedMember> archivedMembers)
    {
        _changeLogLock.Wait();
        try
        {
            using var tx = _db.Connection.BeginTransaction();
            var applied = new HashSet<string>();
            var phoneReplaced = 0;

            foreach (var m in members)
            {
                var existing = GetByIdOnceNoLock(m.Id, tx);
                if (existing is not null && m.UpdatedAtMillis < existing.UpdatedAtMillis) continue;

                using (var del = _db.Connection.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM members WHERE phone = $phone AND id <> $id";
                    del.Parameters.AddWithValue("$phone", m.Phone);
                    del.Parameters.AddWithValue("$id", m.Id);
                    phoneReplaced += del.ExecuteNonQuery();
                }

                SaveCore(m, tx, stampMissingUpdatedAt: false);
                applied.Add(m.Id);
            }

            var attendanceCount = 0;
            foreach (var rec in attendance)
            {
                using var cmd = _db.Connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO attendance_records (memberId, timestampMillis, dayEpoch, session, globalId)
                    VALUES ($memberId, $ts, $day, $session, $globalId)
                    """;
                cmd.Parameters.AddWithValue("$memberId", rec.MemberId);
                cmd.Parameters.AddWithValue("$ts", rec.TimestampMillis);
                cmd.Parameters.AddWithValue("$day", rec.DayEpoch);
                cmd.Parameters.AddWithValue("$session", rec.Session);
                cmd.Parameters.AddWithValue("$globalId", Guid.NewGuid().ToString());
                attendanceCount += cmd.ExecuteNonQuery();
            }

            var archivedCount = 0;
            foreach (var a in archivedMembers)
            {
                using var cmd = _db.Connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO archived_members
                        (originalMemberId, name, phone, joinedMillis, lastPlan, lastFee, lastStartMillis, lastExpiryMillis, idProof, archivedAtMillis)
                    VALUES ($id, $name, $phone, $joined, $plan, $fee, $start, $expiry, $idProof, $archivedAt)
                    """;
                cmd.Parameters.AddWithValue("$id", a.OriginalMemberId);
                cmd.Parameters.AddWithValue("$name", a.Name);
                cmd.Parameters.AddWithValue("$phone", a.Phone);
                cmd.Parameters.AddWithValue("$joined", a.JoinedMillis);
                cmd.Parameters.AddWithValue("$plan", a.LastPlan);
                cmd.Parameters.AddWithValue("$fee", a.LastFee);
                cmd.Parameters.AddWithValue("$start", a.LastStartMillis);
                cmd.Parameters.AddWithValue("$expiry", a.LastExpiryMillis);
                cmd.Parameters.AddWithValue("$idProof", a.IdProof);
                cmd.Parameters.AddWithValue("$archivedAt", a.ArchivedAtMillis);
                archivedCount += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return new RestoreCounts(applied.Count, attendanceCount, archivedCount, phoneReplaced, applied);
        }
        finally
        {
            _changeLogLock.Release();
        }
    }

    // ---------------- Internal helpers ----------------

    private Member? GetByIdOnceNoLock(string id, SqliteTransaction? tx = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM members WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? DecryptedForApp(ReadMember(reader)) : null;
    }

    private long NextSeqNoLock(string deviceId, SqliteTransaction tx)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT MAX(seq) FROM sync_change_log WHERE originDeviceId = $deviceId";
        cmd.Parameters.AddWithValue("$deviceId", deviceId);
        var result = cmd.ExecuteScalar();
        var maxSeq = result is DBNull or null ? 0L : Convert.ToInt64(result);
        return maxSeq + 1;
    }

    private void InsertChangeLog(SyncChangeLogEntry entry, SqliteTransaction tx)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO sync_change_log
                (changeId, entityType, recordId, operation, originDeviceId, seq, timestampMillis, fieldsJson)
            VALUES
                ($changeId, $entityType, $recordId, $operation, $originDeviceId, $seq, $timestampMillis, $fieldsJson)
            """;
        cmd.Parameters.AddWithValue("$changeId", entry.ChangeId);
        cmd.Parameters.AddWithValue("$entityType", entry.EntityType);
        cmd.Parameters.AddWithValue("$recordId", entry.RecordId);
        cmd.Parameters.AddWithValue("$operation", entry.Operation);
        cmd.Parameters.AddWithValue("$originDeviceId", entry.OriginDeviceId);
        cmd.Parameters.AddWithValue("$seq", entry.Seq);
        cmd.Parameters.AddWithValue("$timestampMillis", entry.TimestampMillis);
        cmd.Parameters.AddWithValue("$fieldsJson", (object?)entry.FieldsJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Upsert(Member m, SqliteTransaction tx)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO members
                (id, name, phone, photoPath, plan, fee, joinedMillis, expiryMillis, historyJson,
                 updatedAtMillis, passwordHash, createdAtMillis, lastAttendanceMillis, archived,
                 qrToken, qrTokenExpiryMillis, idProof, idProofPhotoPath, fingerprintTemplate, pendingDeletionMillis)
            VALUES
                ($id, $name, $phone, $photoPath, $plan, $fee, $joinedMillis, $expiryMillis, $historyJson,
                 $updatedAtMillis, $passwordHash, $createdAtMillis, $lastAttendanceMillis, $archived,
                 $qrToken, $qrTokenExpiryMillis, $idProof, $idProofPhotoPath, $fingerprintTemplate, $pendingDeletionMillis)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, phone=excluded.phone, photoPath=excluded.photoPath, plan=excluded.plan,
                fee=excluded.fee, joinedMillis=excluded.joinedMillis, expiryMillis=excluded.expiryMillis,
                historyJson=excluded.historyJson, updatedAtMillis=excluded.updatedAtMillis,
                passwordHash=excluded.passwordHash, createdAtMillis=excluded.createdAtMillis,
                lastAttendanceMillis=excluded.lastAttendanceMillis, archived=excluded.archived,
                qrToken=excluded.qrToken, qrTokenExpiryMillis=excluded.qrTokenExpiryMillis,
                idProof=excluded.idProof, idProofPhotoPath=excluded.idProofPhotoPath,
                fingerprintTemplate=excluded.fingerprintTemplate, pendingDeletionMillis=excluded.pendingDeletionMillis
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$name", m.Name);
        cmd.Parameters.AddWithValue("$phone", m.Phone);
        cmd.Parameters.AddWithValue("$photoPath", (object?)m.PhotoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plan", m.Plan);
        cmd.Parameters.AddWithValue("$fee", m.Fee);
        cmd.Parameters.AddWithValue("$joinedMillis", m.JoinedMillis);
        cmd.Parameters.AddWithValue("$expiryMillis", m.ExpiryMillis);
        cmd.Parameters.AddWithValue("$historyJson", m.HistoryJson);
        cmd.Parameters.AddWithValue("$updatedAtMillis", m.UpdatedAtMillis);
        cmd.Parameters.AddWithValue("$passwordHash", m.PasswordHash);
        cmd.Parameters.AddWithValue("$createdAtMillis", m.CreatedAtMillis);
        cmd.Parameters.AddWithValue("$lastAttendanceMillis", (object?)m.LastAttendanceMillis ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$archived", m.Archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$qrToken", m.QrToken);
        cmd.Parameters.AddWithValue("$qrTokenExpiryMillis", m.QrTokenExpiryMillis);
        cmd.Parameters.AddWithValue("$idProof", m.IdProof);
        cmd.Parameters.AddWithValue("$idProofPhotoPath", m.IdProofPhotoPath);
        cmd.Parameters.AddWithValue("$fingerprintTemplate", (object?)m.FingerprintTemplate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pendingDeletionMillis", (object?)m.PendingDeletionMillis ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static Member ReadMember(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Phone = r.GetString(r.GetOrdinal("phone")),
        PhotoPath = r.IsDBNull(r.GetOrdinal("photoPath")) ? null : r.GetString(r.GetOrdinal("photoPath")),
        Plan = r.GetString(r.GetOrdinal("plan")),
        Fee = r.GetDouble(r.GetOrdinal("fee")),
        JoinedMillis = r.GetInt64(r.GetOrdinal("joinedMillis")),
        ExpiryMillis = r.GetInt64(r.GetOrdinal("expiryMillis")),
        HistoryJson = r.GetString(r.GetOrdinal("historyJson")),
        UpdatedAtMillis = r.GetInt64(r.GetOrdinal("updatedAtMillis")),
        PasswordHash = r.GetString(r.GetOrdinal("passwordHash")),
        CreatedAtMillis = r.GetInt64(r.GetOrdinal("createdAtMillis")),
        LastAttendanceMillis = r.IsDBNull(r.GetOrdinal("lastAttendanceMillis")) ? null : r.GetInt64(r.GetOrdinal("lastAttendanceMillis")),
        Archived = r.GetInt64(r.GetOrdinal("archived")) != 0,
        QrToken = r.GetString(r.GetOrdinal("qrToken")),
        QrTokenExpiryMillis = r.GetInt64(r.GetOrdinal("qrTokenExpiryMillis")),
        IdProof = r.GetString(r.GetOrdinal("idProof")),
        IdProofPhotoPath = r.GetString(r.GetOrdinal("idProofPhotoPath")),
        FingerprintTemplate = r.IsDBNull(r.GetOrdinal("fingerprintTemplate")) ? null : (byte[])r["fingerprintTemplate"],
        PendingDeletionMillis = r.IsDBNull(r.GetOrdinal("pendingDeletionMillis")) ? null : r.GetInt64(r.GetOrdinal("pendingDeletionMillis"))
    };

    private static AttendanceRecord ReadAttendance(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        MemberId = r.GetString(r.GetOrdinal("memberId")),
        TimestampMillis = r.GetInt64(r.GetOrdinal("timestampMillis")),
        DayEpoch = r.GetInt64(r.GetOrdinal("dayEpoch")),
        Session = r.GetString(r.GetOrdinal("session")),
        GlobalId = r.GetString(r.GetOrdinal("globalId"))
    };

    private static ArchivedMember ReadArchivedMember(SqliteDataReader r) => new()
    {
        OriginalMemberId = r.GetString(r.GetOrdinal("originalMemberId")),
        Name = r.GetString(r.GetOrdinal("name")),
        Phone = r.GetString(r.GetOrdinal("phone")),
        JoinedMillis = r.GetInt64(r.GetOrdinal("joinedMillis")),
        LastPlan = r.GetString(r.GetOrdinal("lastPlan")),
        LastFee = r.GetDouble(r.GetOrdinal("lastFee")),
        LastStartMillis = r.GetInt64(r.GetOrdinal("lastStartMillis")),
        LastExpiryMillis = r.GetInt64(r.GetOrdinal("lastExpiryMillis")),
        IdProof = r.GetString(r.GetOrdinal("idProof")),
        ArchivedAtMillis = r.GetInt64(r.GetOrdinal("archivedAtMillis"))
    };

    private static Member CloneWithUpdatedAt(Member m, long updatedAtMillis) => new()
    {
        Id = m.Id, Name = m.Name, Phone = m.Phone, PhotoPath = m.PhotoPath, Plan = m.Plan, Fee = m.Fee,
        JoinedMillis = m.JoinedMillis, ExpiryMillis = m.ExpiryMillis, HistoryJson = m.HistoryJson,
        UpdatedAtMillis = updatedAtMillis, PasswordHash = m.PasswordHash, CreatedAtMillis = m.CreatedAtMillis,
        LastAttendanceMillis = m.LastAttendanceMillis, Archived = m.Archived, QrToken = m.QrToken,
        QrTokenExpiryMillis = m.QrTokenExpiryMillis, IdProof = m.IdProof, IdProofPhotoPath = m.IdProofPhotoPath,
        FingerprintTemplate = m.FingerprintTemplate, PendingDeletionMillis = m.PendingDeletionMillis
    };

    /// <summary>Mirrors Android's private <c>Member.decryptedForApp()</c> extension exactly.</summary>
    private static Member DecryptedForApp(Member m)
    {
        if (m.FingerprintTemplate is null) return m;
        var plain = CryptoUtils.DecryptAtRestOrLegacy(m.FingerprintTemplate);
        // Android logs a warning here ("template could not be decrypted - treating as not
        // enrolled"); logging wiring is left to the caller/host app in this Stage 2
        // foundation rather than baked into MajorGym.Data.
        return ReferenceEquals(plain, m.FingerprintTemplate) ? m : CloneWithFingerprint(m, plain);
    }

    /// <summary>Mirrors Android's private <c>Member.encryptedForStorage()</c> extension exactly.</summary>
    private static Member EncryptedForStorage(Member m)
    {
        if (m.FingerprintTemplate is null) return m;
        var encrypted = CryptoUtils.EncryptAtRest(m.FingerprintTemplate);
        return CloneWithFingerprint(m, encrypted);
    }

    private static Member CloneWithFingerprint(Member m, byte[]? fp)
    {
        var clone = CloneWithUpdatedAt(m, m.UpdatedAtMillis);
        clone.FingerprintTemplate = fp;
        return clone;
    }
}

/// <summary>What <see cref="Repository.RestoreBackup"/> actually applied.</summary>
public sealed record RestoreCounts(int Members, int Attendance, int Archived, int PhoneReplaced, HashSet<string> AppliedMemberIds);
