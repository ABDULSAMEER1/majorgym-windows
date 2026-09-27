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
    private readonly string _filePath;
    private readonly object _lock = new();

    public LocalSettingsStore(string appDataDirectory, string storeName)
    {
        var dir = Path.Combine(appDataDirectory, "settings");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, $"{storeName}.json");
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
        if (!File.Exists(_filePath)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(_filePath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject(); // corrupted settings file — start fresh rather than crash
        }
    }

    private void Save(JsonObject root) => File.WriteAllText(_filePath, root.ToJsonString());
}
