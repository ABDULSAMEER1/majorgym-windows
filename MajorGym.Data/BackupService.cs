using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MajorGym.Data.Entities;

namespace MajorGym.Data;

/// <summary>Result of a restore attempt. Ported from Android's <c>RestoreOutcome</c>.</summary>
public abstract record RestoreOutcome
{
    public sealed record Success(int MembersRestored, int AttendanceRestored, int ArchivedRestored) : RestoreOutcome;
    public sealed record InvalidBackup(string Reason) : RestoreOutcome;
    public sealed record RestoreFailed(string Reason) : RestoreOutcome;
}

/// <summary>Where backups live on disk. Mirrors Android's Repository helpers:
/// <c>backups/manual/MajorGym_Backup_yyyy-MM-dd_HHmm.zip</c> (kept for Share Backup File)
/// and the single overwritten-each-time <c>backups/safety_backup.zip</c> written before
/// every restore.</summary>
public sealed class LocalBackupStore(string appDataDirectory)
{
    public string ManualBackupsDir
    {
        get { var d = Path.Combine(appDataDirectory, "backups", "manual"); Directory.CreateDirectory(d); return d; }
    }

    public string SafetyBackupFile => Path.Combine(appDataDirectory, "backups", "safety_backup.zip");

    public static string TimestampLabel() => DateTime.Now.ToString("yyyy-MM-dd_HHmm");

    public string NewManualBackupFile(string timestampLabel) =>
        Path.Combine(ManualBackupsDir, $"MajorGym_Backup_{timestampLabel}.zip");

    /// <summary>The newest manual backup this app has produced, or null if none exists.</summary>
    public FileInfo? LatestInternalBackupFile() =>
        new DirectoryInfo(ManualBackupsDir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
}

/// <summary>
/// Windows port of Android's <c>BackupService</c>: create a verified ZIP backup, and
/// validate + restore a ZIP or legacy plain-JSON backup. The restore is split in two so
/// the UI can keep the slow, DB-free part (read, unzip, validate, decode photos) off the UI
/// thread: <see cref="Prepare"/> has no side effects at all, and <see cref="Commit"/> is the
/// only step that touches the database — one atomic transaction, with photo files written
/// only after it has committed.
/// </summary>
public static class BackupService
{
    public const string ExtractTempName = "backup_restore_tmp";

    /// <summary>Generator → Compressor → Validator, exactly Android's createZipBackup: the
    /// ZIP is re-opened and re-validated before it is ever reported as a success, and a
    /// half-written file is deleted on failure.</summary>
    public static string CreateZipBackup(Repository repository, PhotoStore photoStore, string destFile)
    {
        try
        {
            var json = BackupManager.ExportJson(repository.GetAll(), photoStore, repository.GetAllAttendance(), repository.GetArchivedMembers());
            return WriteAndVerify(json, destFile);
        }
        catch (Exception e)
        {
            TryDelete(destFile);
            throw e is BackupFormatException ? e : new BackupFormatException(e.Message, e);
        }
    }

    /// <summary>The DB-free half of <see cref="CreateZipBackup"/>: compress + reopen + validate.
    /// Safe to run on a background thread once the JSON string exists.</summary>
    public static string WriteAndVerify(string json, string destFile)
    {
        try
        {
            BackupZip.Write(json, destFile);
            var verified = BackupZip.ReadAndVerify(destFile);
            var reason = ValidateSchema(verified);
            if (reason is not null) throw new BackupFormatException(reason);
            return destFile;
        }
        catch (Exception e)
        {
            TryDelete(destFile);
            throw e is BackupFormatException ? e : new BackupFormatException(e.Message, e);
        }
    }

    /// <summary>Best-effort recovery point of the CURRENT data, written before a restore.</summary>
    public static void WriteSafetyBackupSnapshot(Repository repository, PhotoStore photoStore, LocalBackupStore store)
    {
        var json = BackupManager.ExportJson(repository.GetAll(), photoStore, repository.GetAllAttendance(), repository.GetArchivedMembers());
        BackupZip.Write(json, store.SafetyBackupFile);
    }

    /// <summary>Reads a ZIP or legacy plain-JSON backup into text. Format is detected from the
    /// file's bytes, not its extension. Never modifies the source file.</summary>
    public static string ReadBackupText(string appDataDirectory, string path)
    {
        if (!File.Exists(path)) throw new BackupFormatException("Unable to open the selected file.");
        try
        {
            if (BackupZip.LooksLikeZip(path))
            {
                var extracted = BackupZip.ExtractJsonToTemp(appDataDirectory, path);
                try { return DecodeUtf8(File.ReadAllBytes(extracted)); }
                finally { try { File.Delete(extracted); } catch { } }
            }
            return DecodeUtf8(File.ReadAllBytes(path));
        }
        finally { BackupZip.CleanupTemp(appDataDirectory); }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0; // tolerate a BOM
        return new UTF8Encoding(false).GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>Port of Android's validateSchema, messages verbatim. Null = valid.</summary>
    public static string? ValidateSchema(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return "This backup file isn't valid JSON and may be corrupted.";
            var app = Js.Str(root, "app") ?? "";
            if (app.Length > 0 && app != "MajorGym") return "This file isn't a MajorGym backup.";
            if (!root.ContainsKey("members")) return "This backup file is missing its member data.";
            if (root["members"] is not JsonArray members) return "This backup file isn't valid JSON and may be corrupted.";
            if (root.ContainsKey("schemaVersion") && !(root["schemaVersion"] is JsonValue sv && sv.TryGetValue<long>(out _)))
                return "This backup file's version marker is invalid.";
            if (members.Count > 0 && members[0] is not JsonObject) return "This backup file's member data is malformed.";
            return null;
        }
        catch (JsonException) { return "This backup file isn't valid JSON and may be corrupted."; }
    }

    /// <summary>Steps 1-5 of a restore, with NO side effects: read, validate file + structure +
    /// schema, decode every record, photo and fingerprint in memory. Returns the parsed backup or
    /// the failure to show.</summary>
    public static (BackupManager.ParsedBackup? Parsed, RestoreOutcome? Failure) Prepare(
        string appDataDirectory, PhotoStore photoStore, string path)
    {
        try
        {
            var json = ReadBackupText(appDataDirectory, path);
            var invalid = ValidateSchema(json);
            if (invalid is not null) return (null, new RestoreOutcome.InvalidBackup(invalid));

            BackupManager.ParsedBackup parsed;
            try { parsed = BackupManager.ParseBackup(json, photoStore); }
            catch (Exception)
            {
                return (null, new RestoreOutcome.InvalidBackup(
                    "This backup file's data couldn't be read. It may be corrupted or from an unsupported version."));
            }
            if (parsed.Members.Count == 0 && parsed.RawMemberCount > 0)
                return (null, new RestoreOutcome.InvalidBackup("None of the records in this backup could be read. It may be corrupted."));
            return (parsed, null);
        }
        catch (BackupFormatException e) { return (null, new RestoreOutcome.InvalidBackup(e.Message)); }
        catch (Exception e) { return (null, new RestoreOutcome.InvalidBackup(string.IsNullOrWhiteSpace(e.Message) ? "This backup file could not be read." : e.Message)); }
    }

    /// <summary>Step 6-8: safety snapshot (best effort), ONE atomic database transaction, then
    /// photo files for the members that were actually applied.</summary>
    public static RestoreOutcome Commit(Repository repository, PhotoStore photoStore, LocalBackupStore store, BackupManager.ParsedBackup parsed)
    {
        try { WriteSafetyBackupSnapshot(repository, photoStore, store); }
        catch (Exception e) { Trace.TraceWarning($"[BackupService] safety snapshot failed (non-fatal): {e.Message}"); }

        // The photo FILES are written after the commit (below), but the sync change-log entries the
        // restore writes must already carry the photo bytes — otherwise every restored member is
        // announced to other devices with an EMPTY photo, which then wins the merge and wipes the
        // picture on both phone and PC. Hand the in-memory bytes to the restore, keyed by final path.
        var pendingPhotos = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pm in parsed.Members)
        {
            if (pm.PhotoBytes is not null && !string.IsNullOrWhiteSpace(pm.Member.PhotoPath)) pendingPhotos[pm.Member.PhotoPath] = pm.PhotoBytes;
            if (pm.IdProofPhotoBytes is not null && !string.IsNullOrWhiteSpace(pm.Member.IdProofPhotoPath)) pendingPhotos[pm.Member.IdProofPhotoPath] = pm.IdProofPhotoBytes;
        }

        RestoreCounts counts;
        try
        {
            counts = repository.RestoreBackup(
                parsed.Members.Select(m => m.Member).ToList(), parsed.Attendance, parsed.Archived,
                path => pendingPhotos.TryGetValue(path, out var bytes) ? bytes : null);
        }
        catch (Exception e)
        {
            return new RestoreOutcome.RestoreFailed(
                "Restore could not be completed. Your existing data has not been changed." +
                (string.IsNullOrWhiteSpace(e.Message) ? "" : $" ({e.Message})"));
        }

        // Committed. Photo files last: a failure here loses one photo, never a member.
        foreach (var pm in parsed.Members.Where(m => counts.AppliedMemberIds.Contains(m.Member.Id)))
        {
            try
            {
                if (pm.PhotoBytes is not null) photoStore.WriteMemberPhoto(pm.Member.Id, pm.PhotoBytes);
                if (pm.IdProofPhotoBytes is not null) photoStore.WriteIdProofPhoto(pm.Member.Id, pm.IdProofPhotoBytes);
            }
            catch (Exception e) { Trace.TraceWarning($"[BackupService] photo write failed for {pm.Member.Id}: {e.Message}"); }
        }
        return new RestoreOutcome.Success(counts.Members, counts.Attendance, counts.Archived);
    }

    /// <summary>Prepare + Commit in one call (Android's importAndRestore).</summary>
    public static RestoreOutcome ImportAndRestore(
        string appDataDirectory, Repository repository, PhotoStore photoStore, LocalBackupStore store, string path)
    {
        var (parsed, failure) = Prepare(appDataDirectory, photoStore, path);
        return failure ?? Commit(repository, photoStore, store, parsed!);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
