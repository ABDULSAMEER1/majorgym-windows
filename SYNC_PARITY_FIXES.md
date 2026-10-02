# Windows sync parity fixes (Android SyncManager = golden source)

Wire protocol is UNCHANGED (mDNS `_majorgym._tcp`, TXT id/code/name, 4-byte length framing, nonce + HMAC proof,
AES-256-GCM, version-vector change-log exchange), so Windows<->Android and Windows<->Windows keep interoperating.

## Transport (MdnsSession.cs, SyncManager.cs)
1. Dial loop: the dialing side now keeps retrying every discovered peer until one connects or the window ends
   (Android gets repeated NSD callbacks; Windows used to report a peer once and never retry a failed dial).
2. All of a peer's addresses are dialed in parallel (best first), not one-by-one at 4 s each.
3. Only real LAN adapters are advertised/used (VMware, VirtualBox, Hyper-V internal, WSL, Docker, VPN, Bluetooth skipped);
   peer addresses are ordered: address its packets came from, same-subnet, then the rest; own/169.254 addresses dropped.
4. mDNS goodbye packets now clear the peer's stale port/addresses.
5. Frames are written in slices (stall timeout = no progress for 10 s, not whole-payload 10 s) and send/receive run
   concurrently, so a large photo batch can no longer deadlock or time out.
6. "Found device but couldn't connect" (firewall / Public network profile) is reported as such instead of "not found".

## Data (Repository.cs, SyncChangeCodec.cs)
7. ApplyRemoteChanges applies each record in isolation: one bad record no longer rolls back the whole batch.
8. Phone-number conflicts are resolved like Android's REPLACE upsert (old holder evicted), so the result no longer
   depends on arrival order (previously a re-registered member could be lost for good).
9. Backfill of records without change-log history now runs on every sync (backup restore creates such records).
10. Codec decoding is as lenient as Android's optString/optLong (no more whole-batch failures on odd JSON types).
11. Archive sweep is idempotent (no duplicate ARCHIVED_MEMBER ADD when the archive row arrived by sync);
    delete/archive remove photo files only after the DB transaction commits.

## Settings / UI (LocalSettingsStore.cs, SyncPrefs.cs, SyncViewModel.cs)
12. Settings JSON written atomically with a .bak fallback, one lock per file shared by all instances; DeviceId cached
    so a torn write can never silently mint a new device id (which breaks version vectors).
13. Sync code is normalised exactly like Android (uppercase, letters/digits, max 8); a typed-but-unsaved code is
    saved when Sync Now is pressed.

Not compiled here (no .NET SDK in this environment) - build with the GitHub Actions workflow and test on two PCs
and a phone. First run: allow MajorGym through Windows Firewall on PRIVATE networks.
