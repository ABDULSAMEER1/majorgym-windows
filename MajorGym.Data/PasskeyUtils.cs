using System.Security.Cryptography;
using System.Text;

namespace MajorGym.Data;

/// <summary>
/// Generates and hashes member passkeys. Ported 1:1 from Android's <c>PasskeyUtils</c>
/// object — same alphabet (no I/O digits/letters to avoid confusion with 1/0), same
/// PBKDF2 parameters (120,000 iterations, HMAC-SHA256, 256-bit key, 16-byte salt), same
/// "base64(salt):base64(hash)" storage format. This last point matters: <c>Member.PasswordHash</c>
/// strings are portable data that can arrive via an Android backup import, so the stored
/// format and KDF parameters must match exactly or an Android-issued passkey would fail to
/// verify once the member's data is on Windows.
///
/// The plaintext passkey is only ever held in memory long enough to display it once to the
/// owner — it is never written to disk or the database. Only <see cref="Hash"/> output is
/// persisted, in <c>Member.PasswordHash</c>. (Android doc comment, preserved.)
/// </summary>
public static class PasskeyUtils
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I/O to avoid confusion with 1/0
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string All = Upper + Lower + Digits;

    private const int Pbkdf2Iterations = 120_000;
    private const int KeyLengthBits = 256;
    private const int SaltLengthBytes = 16;

    /// <summary>Generates an 8-character (by default) passkey guaranteed to contain upper,
    /// lower, and digit characters.</summary>
    public static string Generate(int length = 8)
    {
        if (length < 8) throw new ArgumentException("Passkey must be at least 8 characters", nameof(length));

        var chars = new char[length];
        // Guarantee at least one of each required character class first.
        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        for (var i = 3; i < length; i++)
        {
            chars[i] = All[RandomNumberGenerator.GetInt32(All.Length)];
        }
        // Shuffle so the guaranteed characters aren't always in the same position.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    /// <summary>Hashes <paramref name="passkey"/> with a random salt, returning
    /// "base64(salt):base64(hash)" for storage.</summary>
    public static string Hash(string passkey)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        var digest = Pbkdf2(passkey, salt);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(digest);
    }

    /// <summary>Verifies <paramref name="passkey"/> against a previously stored hash from
    /// <see cref="Hash"/>.</summary>
    public static bool Verify(string passkey, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Pbkdf2(passkey, salt);
        // NOTE (documented, minor deviation): Android compares with `ByteArray.contentEquals`,
        // a non-constant-time comparison. Using FixedTimeEquals here returns the identical
        // true/false result for every input — no behavioral or compatibility difference —
        // while closing a timing side-channel. This is a defensive substitution of an
        // equivalent primitive, not a business-rule or data-format change.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string passkey, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passkey),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyLengthBits / 8);
}
