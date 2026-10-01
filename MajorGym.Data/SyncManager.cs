using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MajorGym.Data.Settings;

namespace MajorGym.Data;

/// <summary>Result of one sync attempt — port of Android's <c>SyncOutcome</c> sealed class.</summary>
public abstract record SyncOutcome
{
    /// <summary><paramref name="RecordCount"/> = how many change-log entries this device actually
    /// learned that it didn't already have.</summary>
    public sealed record Success(string PeerName, int RecordCount) : SyncOutcome;
    public sealed record NoCodeSet : SyncOutcome;
    public sealed record NotFound : SyncOutcome;
    public sealed record Error(string Message) : SyncOutcome;
}

/// <summary>
/// Runs database work on whichever thread owns the shared SQLite connection (the WPF UI thread in
/// this app — see BackupViewModel's threading note). Keeps MajorGym.Data free of any WPF reference.
/// </summary>
public interface IDbThread
{
    Task<T> RunAsync<T>(Func<T> work);
}

/// <summary>
/// Local Wi-Fi/hotspot device sync — no internet, no server, no accounts. Windows port of Android's
/// <c>SyncManager</c>, protocol-identical so a PC and a phone (or two PCs) can sync:
///
///  1. Both devices advertise + browse <c>_majorgym._tcp</c> over mDNS (<see cref="MdnsSession"/>) and
///     filter candidates by the SHA-256 hash of the Sync Code in the TXT record.
///  2. Only the device whose id sorts FIRST dials out; the other only accepts (prevents the crossed-
///     connections deadlock Android documents).
///  3. Over TCP, framed as [4-byte big-endian length][payload]: each side sends a random 32-byte nonce,
///     then HMAC-SHA256(key, peerNonce) where key = PBKDF2(SyncCode). A wrong proof closes the
///     connection before any data is exchanged.
///  4. Everything after that is AES-256-GCM under the same code-derived key.
///  5. Each side sends its version vector; each then sends only the change-log entries the other lacks;
///     each applies what it received via <see cref="Repository.ApplyRemoteChanges"/> (replay-based,
///     idempotent, per-field merge, deterministic tie-break — see Repository).
///
/// All networking is scoped to one bounded attempt: nothing runs in the background between syncs.
/// </summary>
public sealed class SyncManager
{
    private const int NonceBytes = 32;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const int SocketTimeoutMs = 10_000;

    private const string TimedOutMessage = "Timed out waiting for the other phone - try again";

    private readonly Repository _repository;
    private readonly SyncPrefs _prefs;
    private readonly PhotoStore _photoStore;
    private readonly IDbThread _db;

    public SyncManager(Repository repository, SyncPrefs prefs, PhotoStore photoStore, IDbThread db)
    {
        _repository = repository;
        _prefs = prefs;
        _photoStore = photoStore;
        _db = db;
    }

    public async Task<SyncOutcome> RunSyncAsync(Action<string> onStatus, int timeoutMs = 20_000, CancellationToken cancellationToken = default)
    {
        try
        {
            // Give any pre-existing record that predates the change log a synthetic ADD entry BEFORE
            // this device's version vector is computed (Android: backfillPreSyncHistoryIfNeeded).
            await _db.RunAsync(() => { _repository.BackfillPreSyncHistoryIfNeeded(_prefs); return 0; }).ConfigureAwait(false);
            // Windows hardening: re-attach any profile photo whose file is on disk but whose path was
            // lost, so it is logged (and sent) before the version vector below is computed.
            await _db.RunAsync(() => _repository.AdoptOrphanedPhotos(_photoStore)).ConfigureAwait(false);

            var code = _prefs.SyncCode;
            if (string.IsNullOrWhiteSpace(code)) return new SyncOutcome.NoCodeSet();
            var codeHash = Sha256Hex(code);
            var deviceId = _prefs.DeviceId;
            var deviceName = _prefs.DeviceName;

            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var connected = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            var claimed = 0; // 0 = free, 1 = a connection has been claimed
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            MdnsSession? mdns = null;

            try
            {
                onStatus("Waiting for other phones on this Wi-Fi\u2026");

                // Accept an inbound connection from a peer in the background.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token);
                        acceptTimeout.CancelAfter(timeoutMs);
                        var accepted = await listener.AcceptTcpClientAsync(acceptTimeout.Token).ConfigureAwait(false);
                        if (Interlocked.CompareExchange(ref claimed, 1, 0) == 0) connected.TrySetResult(accepted);
                        else accepted.Dispose();
                    }
                    catch { /* timeout or listener closed — expected when nobody connected in time */ }
                });

                var serviceLabel = $"majorgym-{deviceId[..Math.Min(8, deviceId.Length)]}";
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                mdns = new MdnsSession(serviceLabel, port, new Dictionary<string, string>
                {
                    ["id"] = deviceId,
                    ["code"] = codeHash,
                    ["name"] = deviceName
                });

                mdns.PeerResolved += peer =>
                {
                    if (Volatile.Read(ref claimed) == 1) return;
                    peer.Attributes.TryGetValue("code", out var peerCodeHash);
                    peer.Attributes.TryGetValue("id", out var peerId);
                    if (!string.Equals(peerCodeHash, codeHash, StringComparison.Ordinal)) return;
                    if (peerId is null || peerId == deviceId) return;
                    if (!_prefs.CanAdd(peerId)) return;
                    // Only the device that sorts first by ID dials out; the other side only accepts.
                    if (string.CompareOrdinal(deviceId, peerId) >= 0) return;
                    if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return;
                    try
                    {
                        var dialed = ConnectToAny(peer);
                        if (dialed is null) { Interlocked.Exchange(ref claimed, 0); return; }
                        if (!connected.TrySetResult(dialed)) dialed.Dispose();
                    }
                    catch
                    {
                        Interlocked.Exchange(ref claimed, 0);
                    }
                };

                mdns.Start();
                onStatus("Looking for authorized devices\u2026");

                var winner = await Task.WhenAny(connected.Task, Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
                if (winner != connected.Task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new SyncOutcome.NotFound();
                }

                var client = await connected.Task.ConfigureAwait(false);
                onStatus("Connected \u2014 authenticating\u2026");
                return await PerformExchangeAsync(client, code, deviceId, deviceName).ConfigureAwait(false);
            }
            finally
            {
                attemptCts.Cancel();
                try { mdns?.Dispose(); } catch { }
                try { listener.Stop(); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return new SyncOutcome.Error("Sync was cancelled");
        }
        catch (Exception e)
        {
            return new SyncOutcome.Error(string.IsNullOrWhiteSpace(e.Message) ? "Sync failed" : e.Message);
        }
    }

    private static TcpClient? ConnectToAny(MdnsPeer peer)
    {
        foreach (var address in peer.Addresses)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var connect = client.ConnectAsync(address, peer.Port);
                if (connect.Wait(4000) && client.Connected) return client;
            }
            catch { /* try the next address */ }
            client.Dispose();
        }
        return null;
    }

    private async Task<SyncOutcome> PerformExchangeAsync(TcpClient client, string syncCode, string deviceId, string deviceName)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var channelKey = await Task.Run(() => CryptoUtils.DeriveSyncChannelKey(syncCode)).ConfigureAwait(false);

                // --- Mutual challenge/response: prove possession of the real sync code (never
                // sent itself) before anything else happens. ---
                var myNonce = CryptoUtils.RandomBytes(NonceBytes);
                await WriteFrameAsync(stream, myNonce).ConfigureAwait(false);
                var peerNonce = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (peerNonce is null) return new SyncOutcome.Error("Connection closed during authentication");

                await WriteFrameAsync(stream, CryptoUtils.HmacSha256(channelKey, peerNonce)).ConfigureAwait(false);
                var peerProof = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (peerProof is null) return new SyncOutcome.Error("Connection closed during authentication");
                var expectedPeerProof = CryptoUtils.HmacSha256(channelKey, myNonce);
                if (peerProof.Length != expectedPeerProof.Length || !CryptoUtils.ConstantTimeEquals(peerProof, expectedPeerProof))
                {
                    // Wrong/missing sync code on the other end — fail safely, exchange nothing further.
                    return new SyncOutcome.Error("The other device's sync code doesn't match");
                }

                // --- Version-vector exchange: each side tells the other exactly what it already has. ---
                var myVector = await _db.RunAsync(() => _repository.LocalVersionVector()).ConfigureAwait(false);
                var myIdentity = new JsonObject
                {
                    ["deviceId"] = deviceId,
                    ["deviceName"] = deviceName,
                    ["versionVector"] = SyncChangeCodec.EncodeVersionVector(myVector)
                };
                await WriteFrameAsync(stream, CryptoUtils.AesGcmEncrypt(Encoding.UTF8.GetBytes(myIdentity.ToJsonString()), channelKey)).ConfigureAwait(false);

                var identityFrame = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (identityFrame is null) return new SyncOutcome.Error("Connection closed while exchanging device info");
                byte[] identityBytes;
                try { identityBytes = CryptoUtils.AesGcmDecrypt(identityFrame, channelKey); }
                catch (Exception) { return new SyncOutcome.Error("The received data failed integrity verification"); }

                var peerIdentity = JsonNode.Parse(Encoding.UTF8.GetString(identityBytes)) as JsonObject
                                   ?? throw new InvalidDataException("Malformed device info from peer");
                var peerId = (string?)peerIdentity["deviceId"] ?? throw new InvalidDataException("Peer sent no device id");
                var peerName = (string?)peerIdentity["deviceName"] ?? "Unknown device";
                var peerVector = SyncChangeCodec.DecodeVersionVector(peerIdentity["versionVector"] as JsonObject ?? new JsonObject());

                // --- Send only what the peer is missing. ---
                var outgoing = await _db.RunAsync(() => _repository.ChangesMissingForPeer(peerVector)).ConfigureAwait(false);
                var outgoingPayload = new JsonObject { ["changes"] = SyncChangeCodec.EncodeChangeLog(outgoing) };
                await WriteFrameAsync(stream, CryptoUtils.AesGcmEncrypt(Encoding.UTF8.GetBytes(outgoingPayload.ToJsonString()), channelKey)).ConfigureAwait(false);

                var changesFrame = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (changesFrame is null) return new SyncOutcome.Error("Connection closed while exchanging records");
                byte[] changesBytes;
                try { changesBytes = CryptoUtils.AesGcmDecrypt(changesFrame, channelKey); }
                catch (Exception)
                {
                    // GCM auth failure = tampered or corrupted in transit.
                    return new SyncOutcome.Error("The received data failed integrity verification");
                }
                var incomingPayload = JsonNode.Parse(Encoding.UTF8.GetString(changesBytes)) as JsonObject
                                      ?? throw new InvalidDataException("Malformed change batch from peer");
                var incoming = SyncChangeCodec.DecodeChangeLog(incomingPayload["changes"] as JsonArray ?? new JsonArray());

                var applied = await _db.RunAsync(() => _repository.ApplyRemoteChanges(incoming, _photoStore)).ConfigureAwait(false);
                _prefs.RecordSync(peerId, peerName);

                return new SyncOutcome.Success(peerName, applied);
            }
        }
        catch (TimeoutException)
        {
            return new SyncOutcome.Error(TimedOutMessage);
        }
        catch (Exception e)
        {
            return new SyncOutcome.Error(string.IsNullOrWhiteSpace(e.Message) ? "Sync failed" : e.Message);
        }
    }

    /// <summary>[4-byte big-endian length][payload]. Same framing as Android's DataOutputStream.writeInt.
    /// NetworkStream ignores ReadTimeout/WriteTimeout for async calls, so each I/O call carries its own
    /// cancellation token instead; expiry surfaces as <see cref="TimeoutException"/>.</summary>
    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload)
    {
        var header = new byte[4];
        header[0] = (byte)(payload.Length >> 24);
        header[1] = (byte)(payload.Length >> 16);
        header[2] = (byte)(payload.Length >> 8);
        header[3] = (byte)payload.Length;
        using var cts = new CancellationTokenSource(SocketTimeoutMs);
        try
        {
            await stream.WriteAsync(header, cts.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new TimeoutException(); }
    }

    /// <summary>Returns null on a closed/garbled connection or an implausible length (rejected up
    /// front rather than allocated), exactly like Android's readFrame. A stalled peer throws
    /// <see cref="TimeoutException"/>.</summary>
    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream)
    {
        try
        {
            var header = new byte[4];
            if (!await ReadFullyAsync(stream, header).ConfigureAwait(false)) return null;
            var len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (len < 0 || len > MaxFrameBytes) return null;
            var payload = new byte[len];
            return await ReadFullyAsync(stream, payload).ConfigureAwait(false) ? payload : null;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<bool> ReadFullyAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            using var cts = new CancellationTokenSource(SocketTimeoutMs);
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new TimeoutException(); }
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
