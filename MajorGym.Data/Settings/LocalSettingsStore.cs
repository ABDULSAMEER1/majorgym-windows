using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MajorGym.Data.Settings;

/// <summary>
/// Windows equivalent of an Android <c>SharedPreferences</c> file: a small, private,
/// local key-value store, one JSON file per named store — matching Android's pattern of
/// one SharedPreferences file per settings feature (Stage 2 brief §21: "the storage
/// technology may differ from Android. The logical settings and semantics must remain the
/// same"). Not a database table, not a registry entry — a plain JSON file under the app's
/// data directory, which is the most literal Windows analog of "a small private
/// preferences file this app owns and nothing else touches".
///
/// Every read re-reads the file and every write rewrites it whole — Android's
/// SharedPreferences is similarly just a small in-memory-cached flat file under the hood,
/// and these settings files are tiny (a handful of booleans/strings/a short list) and
/// written infrequently (a toggle flip, a backup completing), so there is no meaningful
/// performance reason to add a caching layer Android's own implementation doesn't
/// meaningfully skip either.
/// </summary>
public sealed class LocalSettingsStore
{
    // One lock PER FILE, shared by every LocalSettingsStore instance pointing at it. Several classes create
    // their own instance for the same store name; with a lock per instance two of them could rewrite the file
    // at the same moment and corrupt it — and a corrupted sync-settings file made the device silently generate
    // a NEW device id, which breaks the version-vector sync (the old id's change history no longer matches).
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _filePath;
    private readonly object _lock;

    public LocalSettingsStore(string appDataDirectory, string storeName)
    {
        var dir = Path.Combine(appDataDirectory, "settings");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, $"{storeName}.json");
        _lock = FileLocks.GetOrAdd(_filePath, _ => new object());
    }

    public bool GetBool(string key, bool defaultValue)
    {
        lock (_lock)
        {
            var root = Load();
            return (bool?)root[key] ?? defaultValue;
        }
    }

    public void SetBool(string key, bool value) => Set(key, JsonValue.Create(value));

    public string? GetString(string key, string? defaultValue = null)
    {
        lock (_lock)
        {
            var root = Load();
            return (string?)root[key] ?? defaultValue;
        }
    }

    public void SetString(string key, string value) => Set(key, JsonValue.Create(value));

    public long GetLong(string key, long defaultValue)
    {
        lock (_lock)
        {
            var root = Load();
            return (long?)root[key] ?? defaultValue;
        }
    }

    public void SetLong(string key, long value) => Set(key, JsonValue.Create(value));

    private void Set(string key, JsonValue value)
    {
        lock (_lock)
        {
            var root = Load();
            root[key] = value;
            Save(root);
        }
    }

    private JsonObject Load()
    {
        // Main file first; if it is unreadable/corrupt fall back to the last known-good copy written by Save.
        return TryRead(_filePath) ?? TryRead(_filePath + ".bak") ?? new JsonObject();
    }

    private static JsonObject? TryRead(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (IOException) { Thread.Sleep(30); } // transient (antivirus / indexer holding the file) — retry
            catch (UnauthorizedAccessException) { Thread.Sleep(30); }
            catch (System.Text.Json.JsonException) { return null; } // corrupt content — not worth retrying
        }
        return null;
    }

    /// <summary>Write-to-temp-then-replace, so a crash or power cut mid-write can never leave a half-written
    /// (and therefore unparseable) settings file; the previous good copy is kept as <c>.bak</c>.</summary>
    private void Save(JsonObject root)
    {
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString());
        try { if (File.Exists(_filePath)) File.Copy(_filePath, _filePath + ".bak", overwrite: true); }
        catch (IOException) { /* the backup copy is best-effort */ }
        File.Move(tmp, _filePath, overwrite: true);
    }
}
