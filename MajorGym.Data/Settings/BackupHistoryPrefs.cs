using System.Text.Json.Nodes;
using MajorGym.Data;

namespace MajorGym.Data.Settings;

/// <summary>
/// A lightweight log of WHEN a backup was taken (date/time only). Windows port of
/// Android's <c>BackupHistoryPrefs</c>. Deliberately NOT a second copy of the backup
/// itself: no member data, photos, membership plans, attendance, ID proofs, or
/// fingerprint data is ever written here, only a timestamp per entry — the real backup
/// file (with all of that content) is still produced by <see cref="BackupManager.ExportJson"/>
/// / <see cref="BackupZip.Write"/>. This class only notes that it happened and when.
/// (Android doc comment, preserved.)
/// </summary>
public sealed class BackupHistoryPrefs
{
    private const string KeyEntries = "entries";
    private const long RetentionMonths = 3L; // Backup History is kept for the latest 3 months only.

    private readonly LocalSettingsStore _store;

    public BackupHistoryPrefs(string appDataDirectory) =>
        _store = new LocalSettingsStore(appDataDirectory, "majorgym_backup_history");

    /// <summary>Call right after a real backup file has actually been written to disk.
    /// Appends "now", then immediately prunes anything past the retention window so the
    /// stored list never grows unbounded.</summary>
    public void RecordBackupTaken()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var updated = RawEntries().Append(now).ToList();
        Save(Prune(updated));
    }

    /// <summary>Backup timestamps within the retention window, newest first.</summary>
    public List<long> Entries() => Prune(RawEntries()).OrderByDescending(x => x).ToList();

    private static List<long> Prune(List<long> list)
    {
        var cutoff = DateUtils.AddMonthsMillis(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), -RetentionMonths);
        return list.Where(x => x >= cutoff).ToList();
    }

    private List<long> RawEntries()
    {
        var raw = _store.GetString(KeyEntries);
        if (raw is null) return new List<long>();
        var arr = JsonNode.Parse(raw) as JsonArray ?? new JsonArray();
        return arr.Select(n => (long)(n ?? 0L)).ToList();
    }

    private void Save(List<long> list)
    {
        var arr = new JsonArray();
        foreach (var v in list) arr.Add(v);
        _store.SetString(KeyEntries, arr.ToJsonString());
    }
}
