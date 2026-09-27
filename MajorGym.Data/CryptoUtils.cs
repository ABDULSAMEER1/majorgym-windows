using System.Security.Cryptography;

namespace MajorGym.Data;

/// <summary>
/// Real (not obfuscation/Base64) cryptographic primitives shared by:
///  - at-rest fingerprint template encryption (Windows DPAPI-backed key, never leaves
///    this device — the Windows equivalent of Android's Keystore-backed key), and
///  - the LAN sync channel (key derived from the gym's Sync Code, used to authenticate
///    peers and encrypt every byte exchanged) — ported unchanged, since this half has no
///    Android-specific dependency at all.
///
/// Fingerprint templates embedded in a manual backup file are intentionally NOT
/// portable-encrypted here: they travel as plain Base64 inside the backup JSON — see
/// <see cref="BackupManager"/> — exactly matching the Android app's own design (Stage 1
/// report §4.5), so a restore never depends on device-specific key material that a full
/// data clear or a cross-platform move would wipe out or invalidate.
///
/// All AES use is AES/GCM/NoPadding (authenticated encryption — confidentiality AND
/// tamper detection in one primitive), 256-bit keys, random 12-byte IVs (GCM's
/// recommended size), and a 128-bit authentication tag. Never hashing, never Base64,
/// never a hardcoded key. (Android class doc, preserved — every claim in it still holds
/// on Windows with the substitution noted below.)
///
/// ══════════════════════════════════════════════════════════════════════════════════
/// PLATFORM SUBSTITUTION (Stage 1 report §F.6/§H risk #1, Stage 2 brief §15):
/// Android Keystore (hardware/OS-bound, non-exportable) → Windows DPAPI
/// (<see cref="ProtectedData"/>, CurrentUser scope, OS-bound to this Windows user
/// profile). The Android Keystore key itself is NEVER copied or migrated — that is
/// cryptographically impossible by design on both platforms. Instead, a template
/// arriving on Windows (always as plaintext Base64 from a backup import — see above)
/// is encrypted fresh under this Windows-side key. The wire format below uses a
/// DIFFERENT marker byte (0x02) than Android's (0x01) specifically so a single
/// database can unambiguously tell "Android-Keystore-encrypted bytes" (which this
/// platform cannot decrypt — should never occur here, see decrypt notes) apart from
/// "Windows-DPAPI-encrypted bytes" apart from "legacy unencrypted" bytes.
/// ══════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class CryptoUtils
{
    private const string AesMode = "AES-GCM";
    private const int GcmIvBytes = 12;
    private const int GcmTagBytes = 16; // 128 bits
    private const int KeySizeBytes = 32; // 256 bits

    /// <summary>First byte of anything Android's CryptoUtils encrypted at rest (Android
    /// Keystore-wrapped). Preserved here ONLY so <see cref="DecryptAtRestOrLegacy"/> can
    /// recognize and correctly refuse to silently mis-handle such bytes if they were ever
    /// copied into a Windows database file directly (rather than via the normal backup-JSON
    /// plaintext path) — see the decrypt method's doc comment for what happens in that case.</summary>
    private const byte MarkerAndroidKeystore = 0x01;

    /// <summary>First byte of anything THIS (Windows) CryptoUtils has encrypted at rest.
    /// Deliberately a different value than Android's marker (0x01) — see class doc above.</summary>
    private const byte MarkerWindowsDpapi = 0x02;

    // ---------------- At-rest (Windows DPAPI-protected key, device+user-bound) ----------------

    private static readonly string KeyDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MajorGym", "keys");

    private static readonly string KeyFilePath = Path.Combine(KeyDirectory, "fingerprint_key.dpapi");

    /// <summary>
    /// Lazily creates (first call) or loads (every call after) a 256-bit AES key protected
    /// by Windows DPAPI under the current Windows user account. This mirrors Android's
    /// <c>getOrCreateKeystoreKey()</c> pattern exactly: generate once, persist via an
    /// OS-managed protection mechanism, reuse thereafter — the key's raw bytes never touch
    /// disk in the clear, and (like the Android Keystore key) this key does not travel in
    /// backups and cannot be exported/copied to another machine or user account.
    /// </summary>
    private static byte[] GetOrCreateAtRestKey()
    {
        if (File.Exists(KeyFilePath))
        {
            var protectedBytes = File.ReadAllBytes(KeyFilePath);
            return ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }

        Directory.CreateDirectory(KeyDirectory);
        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        var protectedKey = ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFilePath, protectedKey);
        return key;
    }

    /// <summary>Encrypts <paramref name="plain"/> with the Windows DPAPI-protected local
    /// key. Output: [marker(1)=0x02] [iv(12)] [ciphertext] [tag(16)]. Safe to store directly
    /// in the SQLite BLOB column — decryptable only under this Windows user account on this
    /// machine, matching the Android Keystore key's own non-exportability guarantee.</summary>
    public static byte[] EncryptAtRest(byte[] plain)
    {
        var key = GetOrCreateAtRestKey();
        var iv = RandomNumberGenerator.GetBytes(GcmIvBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[GcmTagBytes];

        using var aes = new AesGcm(key, GcmTagBytes);
        aes.Encrypt(iv, plain, ciphertext, tag);

        var result = new byte[1 + GcmIvBytes + ciphertext.Length + GcmTagBytes];
        result[0] = MarkerWindowsDpapi;
        Buffer.BlockCopy(iv, 0, result, 1, GcmIvBytes);
        Buffer.BlockCopy(ciphertext, 0, result, 1 + GcmIvBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, 1 + GcmIvBytes + ciphertext.Length, GcmTagBytes);
        return result;
    }

    /// <summary>
    /// Reverses <see cref="EncryptAtRest"/>. Mirrors Android's <c>decryptAtRestOrLegacy</c>
    /// contract exactly:
    ///  - If <paramref name="stored"/> carries the Windows marker (0x02): decrypt with the
    ///    local DPAPI-protected key. Returns null only if decryption fails (corrupted data,
    ///    or — like a Keystore key lost to a factory reset — a DPAPI key file that no longer
    ///    unprotects, e.g. after a Windows user profile reset); callers must treat that as
    ///    "template lost", never crash or silently invent data.
    ///  - If <paramref name="stored"/> carries the ANDROID marker (0x01): this byte sequence
    ///    is Android-Keystore-wrapped and is cryptographically NOT decryptable on Windows —
    ///    by design, on both platforms (see class doc). This should never actually be
    ///    encountered by normal operation, because the only path fingerprint templates take
    ///    from Android to Windows is the backup-JSON plaintext-Base64 path (see
    ///    <see cref="BackupManager"/>), which never contains Keystore-wrapped bytes. If this
    ///    case IS hit (e.g. a raw copy of an Android SQLite file), it is treated the same as
    ///    a failed decrypt: return null ("template lost/unreadable here"), never throw.
    ///  - Otherwise: legacy plaintext (pre-encryption row, or freshly-imported/decoded
    ///    Base64 bytes the caller hasn't re-encrypted yet) — returned as-is, exactly as
    ///    Android does, so the caller can transparently migrate it forward on next save.
    /// </summary>
    public static byte[]? DecryptAtRestOrLegacy(byte[] stored)
    {
        if (stored.Length == 0) return stored; // legacy plaintext (empty — defensive)
        if (stored[0] == MarkerAndroidKeystore) return null; // see doc above — not decryptable here
        if (stored[0] != MarkerWindowsDpapi) return stored; // legacy plaintext

        if (stored.Length < 1 + GcmIvBytes + GcmTagBytes) return null;
        try
        {
            var iv = stored[1..(1 + GcmIvBytes)];
            var tagStart = stored.Length - GcmTagBytes;
            var ciphertext = stored[(1 + GcmIvBytes)..tagStart];
            var tag = stored[tagStart..];

            var key = GetOrCreateAtRestKey();
            var plain = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, GcmTagBytes);
            aes.Decrypt(iv, ciphertext, tag, plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---------------- Generic AES-GCM (used for the sync channel) — unchanged from Android ----------------

    /// <summary>Derives a raw 256-bit key from an arbitrary passphrase — used by
    /// SyncManager to turn the shared Sync Code into a channel-encryption key. Same salt
    /// string, same iteration count, same algorithm as Android — this key derivation must
    /// produce byte-identical output on both platforms for the same Sync Code, or a
    /// Windows PC and an Android phone could never establish a shared sync channel.</summary>
    public static byte[] DeriveSyncChannelKey(string syncCode)
    {
        var salt = System.Text.Encoding.UTF8.GetBytes("MajorGym-Sync-Channel-v1");
        return Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(syncCode),
            salt,
            150_000,
            HashAlgorithmName.SHA256,
            32);
    }

    /// <summary>Encrypts <paramref name="plain"/> under <paramref name="key"/>. Output:
    /// [iv(12)] [ciphertext] [tag(16)] — same framing as Android's aesGcmEncrypt (Android
    /// appends the GCM tag to the ciphertext per Java Cipher convention; this method
    /// reproduces the same on-the-wire byte layout for protocol compatibility).</summary>
    public static byte[] AesGcmEncrypt(byte[] plain, byte[] key)
    {
        var iv = RandomNumberGenerator.GetBytes(GcmIvBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[GcmTagBytes];
        using var aes = new AesGcm(key, GcmTagBytes);
        aes.Encrypt(iv, plain, ciphertext, tag);

        var result = new byte[GcmIvBytes + ciphertext.Length + GcmTagBytes];
        Buffer.BlockCopy(iv, 0, result, 0, GcmIvBytes);
        Buffer.BlockCopy(ciphertext, 0, result, GcmIvBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, GcmIvBytes + ciphertext.Length, GcmTagBytes);
        return result;
    }

    /// <summary>Reverses <see cref="AesGcmEncrypt"/>. Throws on any tampering/auth failure
    /// or wrong key — callers (the sync channel) must treat that as "this connection is
    /// not trustworthy" and abort rather than proceed with garbage data.</summary>
    public static byte[] AesGcmDecrypt(byte[] ivAndCiphertextAndTag, byte[] key)
    {
        if (ivAndCiphertextAndTag.Length <= GcmIvBytes + GcmTagBytes)
            throw new ArgumentException("Ciphertext too short", nameof(ivAndCiphertextAndTag));

        var iv = ivAndCiphertextAndTag[..GcmIvBytes];
        var tagStart = ivAndCiphertextAndTag.Length - GcmTagBytes;
        var ciphertext = ivAndCiphertextAndTag[GcmIvBytes..tagStart];
        var tag = ivAndCiphertextAndTag[tagStart..];

        var plain = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, GcmTagBytes);
        aes.Decrypt(iv, ciphertext, tag, plain); // throws CryptographicException on auth failure
        return plain;
    }

    /// <summary>HMAC-SHA256(key, message) — used for the sync handshake's
    /// challenge/response proof-of-code-possession. The code itself is never sent; only
    /// this keyed proof over a random, single-use nonce is.</summary>
    public static byte[] HmacSha256(byte[] key, byte[] message) => HMACSHA256.HashData(key, message);

    public static byte[] RandomBytes(int size) => RandomNumberGenerator.GetBytes(size);

    /// <summary>Constant-time comparison — avoids leaking how many leading bytes of an
    /// HMAC/tag matched via response-time differences.</summary>
    public static bool ConstantTimeEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>
    /// Sanitizes a value (typically a member/record ID from imported/synced data) so it
    /// can never be used to escape an intended directory when building a filename from it
    /// — rejects path separators, ".." traversal, null bytes, and anything else that isn't
    /// a plain safe token. Returns null if <paramref name="raw"/> can't be made safe at
    /// all (caller must skip that record rather than guess a replacement). Ported verbatim
    /// from Android — this is pure string logic with no platform dependency.
    /// </summary>
    public static string? SanitizeFileToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        if (cleaned.Length > 128) return null;
        return cleaned;
    }
}
