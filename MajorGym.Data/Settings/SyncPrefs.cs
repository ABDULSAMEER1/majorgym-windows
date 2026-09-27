using System.Text.Json.Nodes;

namespace MajorGym.Data.Settings;

/// <summary>Ported 1:1 from Android's <c>PairedDevice</c> data class.</summary>
public sealed record PairedDevice(string Id, string Name, long LastSyncedMillis);

/// <summary>
/// Stores this device's sync identity, the shared "sync code" for this gym's device
/// circle, and the list of devices it has successfully synced with. Windows port of
/// Android's <c>SyncPrefs</c>. Any number of devices may join the circle — the only
/// requirements are having the app, the exact matching sync code, and passing the
/// authentication check (see the future SyncManager network layer — Stage 2 brief §22).
///
/// Nothing here is ever transmitted in the clear: only a derived key/hash of the sync
/// code is ever sent over the network (see <see cref="CryptoUtils.DeriveSyncChannelKey"/>),
/// so the code itself never leaves the device. (Android doc comment, preserved.)
/// </summary>
public sealed class SyncPrefs
{
    private const string KeyDeviceId = "device_id";
    private const string KeyDeviceName = "device_name";
    private const string KeySyncCode = "sync_code";
    private const string KeyPaired = "paired_devices";
    private const string KeyBackfilledSyncHistory = "backfilled_sync_history";

    private readonly LocalSettingsStore _store;

    public SyncPrefs(string appDataDirectory) =>
        _store = new LocalSettingsStore(appDataDirectory, "majorgym_sync");

    public string DeviceId
    {
        get
        {
            var existing = _store.GetString(KeyDeviceId);
            if (existing is not null) return existing;
            var fresh = Guid.NewGuid().ToString();
            _store.SetString(KeyDeviceId, fresh);
            return fresh;
        }
    }

    public string DeviceName
    {
        get => _store.GetString(KeyDeviceName) ?? Environment.MachineName;
        set => _store.SetString(KeyDeviceName, value);
    }

    public string? SyncCode
    {
        get => _store.GetString(KeySyncCode);
        set => _store.SetString(KeySyncCode, value ?? "");
    }

    /// <summary>One-time completion flag for a future backfill-pre-sync-history routine
    /// (Android: <c>Repository.backfillPreSyncHistoryIfNeeded</c>, a later-stage concern
    /// once the sync network layer itself is implemented — see Stage 2 brief §22). Kept
    /// here now so the settings shape is ready for it.</summary>
    public bool HasBackfilledSyncHistory
    {
        get => _store.GetBool(KeyBackfilledSyncHistory, false);
        set => _store.SetBool(KeyBackfilledSyncHistory, value);
    }

    public List<PairedDevice> PairedDevices()
    {
        var raw = _store.GetString(KeyPaired);
        if (raw is null) return new List<PairedDevice>();
        var arr = JsonNode.Parse(raw) as JsonArray ?? new JsonArray();
        var result = new List<PairedDevice>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            result.Add(new PairedDevice((string?)o["id"] ?? "", (string?)o["name"] ?? "", (long?)o["last"] ?? 0L));
        }
        return result;
    }

    /// <summary>No device-count cap: any device with a matching sync code may join the
    /// circle (the actual gate is the code-hash check in the sync network layer, which
    /// this does not affect). Kept as a method rather than removed outright so future
    /// SyncManager call sites don't need to change. (Android doc comment, preserved.)</summary>
    public bool CanAdd(string id) => true;

    public void RecordSync(string id, string name)
    {
        var updated = PairedDevices().Where(d => d.Id != id).ToList();
        updated.Add(new PairedDevice(id, name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        var arr = new JsonArray();
        foreach (var d in updated)
        {
            arr.Add(new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["last"] = d.LastSyncedMillis });
        }
        _store.SetString(KeyPaired, arr.ToJsonString());
    }
}
