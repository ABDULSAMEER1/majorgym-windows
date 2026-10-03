using Microsoft.Data.Sqlite;

namespace MajorGym.Data;

/// <summary>
/// Windows-side SQLite schema foundation for the four tables that make up the Android
/// golden reference's Room database (logical version 12): members, attendance_records,
/// sync_change_log, archived_members.
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// WHY Microsoft.Data.Sqlite DIRECTLY, NOT EF Core (Stage 2 brief §18 — "choose the
/// simplest reliable approach that preserves the exact logical schema and behavior"):
/// Android's Repository does its own hand-written SQL-shaped queries via Room's DAOs
/// (see MemberDao.kt, AttendanceDao.kt, etc. — Stage 1 report), not a heavyweight
/// change-tracking ORM; its business logic (Repository.kt) already owns exactly which
/// fields changed and writes its own change-log entries by hand. EF Core's own
/// change-tracking/migration-model machinery would be redundant with logic the app
/// already does itself, and would introduce an additional abstraction layer with its own
/// conventions (e.g. shadow properties, migration snapshots) that have no Android
/// equivalent to stay faithful to. Direct Microsoft.Data.Sqlite — parameterized SQL,
/// same shape as Room's own generated SQL — is the more literal, lower-risk translation
/// of "how Android actually talks to this database" and was chosen for that reason, not
/// because ORMs in general are disfavored.
/// ══════════════════════════════════════════════════════════════════════════════════
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// WHY A SINGLE "CREATE AT v12" SCHEMA RATHER THAN REPLAYING ALL 12 ROOM MIGRATIONS
/// (flagged as an open judgment call in Stage 1 report §G.1 — surfaced here again,
/// explicitly, rather than silently decided):
/// Every Windows installation is a brand-new database — there is no pre-existing
/// Windows v1..v11 database to migrate FROM, unlike Android where real devices exist at
/// every historical version. Replaying all 12 migrations on an empty database and
/// creating the same end result directly are logically equivalent for a fresh install.
/// This class therefore creates the v12 end-state schema directly in one step, but
/// every column below is annotated with exactly which Android migration introduced it
/// (MIGRATION_x_y in AppDatabase.kt) and why, so the full history stays visible and
/// auditable rather than being silently collapsed away. If it turns out a genuine
/// Windows-to-Windows upgrade path across schema versions is needed later (e.g. this
/// Stage 2 foundation itself evolves after real installs exist), THIS decision — not
/// Android's migration history — is what would need real migration scripts added.
/// PRAGMA user_version is set to 12 to keep the logical version number comparable to
/// Android's Room version for anyone diffing the two schemas.
/// ══════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class AppDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public const int LogicalSchemaVersion = 12; // matches Android's Room @Database(version = 12)

    private AppDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (creating if necessary) major_gym.db under the given application-data
    /// directory — same file name as Android's "major_gym.db", for consistency when a
    /// support engineer is looking at both platforms' data directories side by side. This
    /// is a DIFFERENT physical file than any Android device's copy; there is no shared
    /// file access across platforms — only the backup-JSON import path (BackupManager)
    /// moves data between them.</summary>
    public static AppDatabase OpenOrCreate(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        var dbPath = Path.Combine(appDataDirectory, "major_gym.db");
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // Standard SQLite hardening/perf pragmas — not an Android-parity concern either
            // way (Room/Android's default SQLite pragmas are not part of the app's documented
            // behavior contract), but foreign_keys=ON is set because CASCADE-free integrity
            // is assumed by the schema below exactly as it was assumed on Android (no
            // explicit FK constraints were declared there either — see Entities/*.cs comments).
            // cache_size is in KiB when negative (16 MB page cache); temp_store keeps sorts/temp b-trees in memory.
            // Durability settings (synchronous) are deliberately left at SQLite's default.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA cache_size=-16000; PRAGMA temp_store=MEMORY;";
            pragma.ExecuteNonQuery();
        }

        var db = new AppDatabase(connection);
        db.EnsureSchema();
        return db;
    }

    public SqliteConnection Connection => _connection;

    private void EnsureSchema()
    {
        using var tx = _connection.BeginTransaction();

        Exec($"PRAGMA user_version = {LogicalSchemaVersion};");

        // ---------------------------------------------------------------------------
        // members — base columns (v1, implicit Room-generated schema, inferred from
        // Member.kt's fields that are NOT introduced by a named migration below) plus
        // every column added by MIGRATION_1_2 through MIGRATION_6_7.
        // ---------------------------------------------------------------------------
        Exec("""
            CREATE TABLE IF NOT EXISTS members (
                id                      TEXT    NOT NULL PRIMARY KEY,
                name                    TEXT    NOT NULL,
                phone                   TEXT    NOT NULL,
                photoPath               TEXT,
                plan                    TEXT    NOT NULL,
                fee                     REAL    NOT NULL,
                joinedMillis            INTEGER NOT NULL,
                expiryMillis            INTEGER NOT NULL,
                historyJson             TEXT    NOT NULL,
                updatedAtMillis         INTEGER NOT NULL DEFAULT 0,   -- MIGRATION_1_2
                passwordHash            TEXT    NOT NULL DEFAULT '',  -- MIGRATION_2_3
                createdAtMillis         INTEGER NOT NULL DEFAULT 0,   -- MIGRATION_2_3
                lastAttendanceMillis    INTEGER,                      -- MIGRATION_2_3
                archived                INTEGER NOT NULL DEFAULT 0,   -- MIGRATION_2_3
                qrToken                 TEXT    NOT NULL DEFAULT '',  -- MIGRATION_3_4
                qrTokenExpiryMillis     INTEGER NOT NULL DEFAULT 0,   -- MIGRATION_3_4
                idProof                 TEXT    NOT NULL DEFAULT '',  -- MIGRATION_4_5
                idProofPhotoPath        TEXT    NOT NULL DEFAULT '',  -- MIGRATION_4_5
                fingerprintTemplate     BLOB,                         -- MIGRATION_5_6
                pendingDeletionMillis   INTEGER                       -- MIGRATION_6_7 (dead field — keep, see Member.cs)
            );
            """);
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS index_members_phone ON members(phone);"); // MIGRATION_2_3
        // Windows performance indexes (no Android counterpart; harmless to the schema contract): name backs the
        // "ORDER BY name ASC" every member list uses, expiryMillis backs status/expiry lookups.
        Exec("CREATE INDEX IF NOT EXISTS index_members_name ON members(name);");
        Exec("CREATE INDEX IF NOT EXISTS index_members_expiryMillis ON members(expiryMillis);");

        // ---------------------------------------------------------------------------
        // attendance_records — created MIGRATION_7_8, globalId column + unique index
        // added MIGRATION_9_10, (memberId,timestampMillis) unique index MIGRATION_8_9.
        // ---------------------------------------------------------------------------
        Exec("""
            CREATE TABLE IF NOT EXISTS attendance_records (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                memberId            TEXT    NOT NULL,
                timestampMillis     INTEGER NOT NULL,
                dayEpoch            INTEGER NOT NULL,
                session             TEXT    NOT NULL,
                globalId            TEXT    NOT NULL DEFAULT ''   -- MIGRATION_9_10
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS index_attendance_records_memberId ON attendance_records(memberId);");
        Exec("CREATE INDEX IF NOT EXISTS index_attendance_records_dayEpoch ON attendance_records(dayEpoch);");
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS index_attendance_records_memberId_timestampMillis ON attendance_records(memberId, timestampMillis);");
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS index_attendance_records_globalId ON attendance_records(globalId);");

        // ---------------------------------------------------------------------------
        // sync_change_log — created MIGRATION_10_11.
        // ---------------------------------------------------------------------------
        Exec("""
            CREATE TABLE IF NOT EXISTS sync_change_log (
                changeId        TEXT    PRIMARY KEY NOT NULL,
                entityType      TEXT    NOT NULL,
                recordId        TEXT    NOT NULL,
                operation       TEXT    NOT NULL,
                originDeviceId  TEXT    NOT NULL,
                seq             INTEGER NOT NULL,
                timestampMillis INTEGER NOT NULL,
                fieldsJson      TEXT
            );
            """);
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS index_sync_change_log_originDeviceId_seq ON sync_change_log(originDeviceId, seq);");
        Exec("CREATE INDEX IF NOT EXISTS index_sync_change_log_recordId ON sync_change_log(recordId);");
        Exec("CREATE INDEX IF NOT EXISTS index_sync_change_log_entityType ON sync_change_log(entityType);");

        // ---------------------------------------------------------------------------
        // archived_members — created MIGRATION_11_12.
        // ---------------------------------------------------------------------------
        Exec("""
            CREATE TABLE IF NOT EXISTS archived_members (
                originalMemberId    TEXT    PRIMARY KEY NOT NULL,
                name                TEXT    NOT NULL,
                phone               TEXT    NOT NULL,
                joinedMillis        INTEGER NOT NULL,
                lastPlan            TEXT    NOT NULL,
                lastFee             REAL    NOT NULL,
                lastStartMillis     INTEGER NOT NULL,
                lastExpiryMillis    INTEGER NOT NULL,
                idProof             TEXT    NOT NULL DEFAULT '',
                archivedAtMillis    INTEGER NOT NULL
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS index_archived_members_phone ON archived_members(phone);");
        Exec("CREATE INDEX IF NOT EXISTS index_archived_members_name ON archived_members(name);");

        tx.Commit();

        void Exec(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();
}
