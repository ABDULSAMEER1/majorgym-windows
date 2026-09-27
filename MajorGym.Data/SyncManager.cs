using MajorGym.Data.Settings;

namespace MajorGym.Data;

/// <summary>
/// LAN Device Sync — FOUNDATION ONLY (Stage 2 brief §22: "Do not fully implement the Sync
/// UI in Stage 2... the architecture must leave room for the existing NSD/mDNS/DNS-SD, TCP,
/// AES-GCM, shared Sync Code, version vectors, SyncChangeLogEntry... must NOT silently
/// replace the synchronization model with simple 'latest database wins'").
///
/// What Stage 2 DOES provide, real and working: the change-log data model
/// (<see cref="Entities.SyncChangeLogEntry"/>), the wire-format encode/decode logic
/// (<see cref="SyncChangeCodec"/>), the crypto primitives the channel will use
/// (<see cref="CryptoUtils.DeriveSyncChannelKey"/>, <see cref="CryptoUtils.AesGcmEncrypt"/>/
/// <see cref="CryptoUtils.AesGcmDecrypt"/>, <see cref="CryptoUtils.HmacSha256"/>), the
/// version-vector-producing groundwork in <see cref="Repository"/> (every save/delete
/// already writes a properly-sequenced change-log entry), and this device's persisted
/// sync identity (<see cref="SyncPrefs"/>). What Stage 2 explicitly does NOT provide: the
/// actual network transport — mDNS/DNS-SD peer discovery and the TCP listener/connection
/// that would carry <see cref="SyncChangeCodec"/>'s encoded payloads between a Windows PC
/// and an Android phone (or another Windows PC).
///
/// This split is deliberate, not an oversight: everything listed as "provided" above is
/// pure, portable logic that <see cref="Repository"/> already depends on for its own
/// change-log-on-every-save behavior, regardless of whether any network transport exists
/// yet. The network transport itself is real I/O-heavy, hardware/network-environment-
/// dependent work (Windows Firewall/network-profile behavior for mDNS discovery is a
/// documented risk area — Stage 1 report §H risk #9) that belongs in its own focused
/// later-stage implementation rather than being rushed alongside the database/fingerprint
/// foundation this stage is scoped to.
///
/// A future Stage 3 SyncManager fills in EstablishConnectionAsync/ExchangeChangesAsync
/// below using a real mDNS library (e.g. Zeroconf) for discovery and TcpListener/TcpClient
/// for the connection — the shape of those methods is sketched here so the intended design
/// is visible and reviewable now, not invented from scratch later.
/// </summary>
public sealed class SyncManager
{
    private readonly Repository _repository;
    private readonly SyncPrefs _syncPrefs;

    public SyncManager(Repository repository, SyncPrefs syncPrefs)
    {
        _repository = repository;
        _syncPrefs = syncPrefs;
    }

    /// <summary>Not yet implemented — see class doc. Intended shape: advertise/browse for
    /// other MajorGym instances on the LAN via mDNS/DNS-SD (a future Stage 3 dependency,
    /// e.g. Zeroconf), matching Android's NsdManager service-discovery role.</summary>
    public Task<IReadOnlyList<string>> DiscoverPeersAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "LAN peer discovery is a Stage 3 concern (Stage 2 brief §22) — the change-log " +
            "data model and wire-format codec it will use are already in place (see " +
            "SyncChangeCodec, Entities.SyncChangeLogEntry).");

    /// <summary>Not yet implemented — see class doc. Intended shape: connect to
    /// <paramref name="peerAddress"/> over TCP, perform the challenge/response
    /// proof-of-Sync-Code-possession handshake (HMAC-SHA256 over a random nonce, key from
    /// <see cref="CryptoUtils.DeriveSyncChannelKey"/> — never sending the Sync Code
    /// itself), then exchange version vectors and missing <see cref="Entities.SyncChangeLogEntry"/>
    /// batches via <see cref="SyncChangeCodec"/>, matching Android's SyncManager protocol
    /// exactly so a Windows PC and an Android phone (or two Windows PCs) can sync.</summary>
    public Task ExchangeChangesAsync(string peerAddress, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "The sync network transport is a Stage 3 concern (Stage 2 brief §22) — see class doc.");
}
