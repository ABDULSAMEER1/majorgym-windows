namespace MajorGym.Data;

/// <summary>
/// Central place every backup/restore/import/sync file write goes through. Ported 1:1
/// from Android's <c>FileSafety</c> object. Two layers of defense, not just "strip a few
/// characters":
///  1. <see cref="CryptoUtils.SanitizeFileToken"/> reduces the untrusted id to a safe
///     allow-listed token before it ever becomes part of a path.
///  2. <see cref="ResolveWithin"/> then re-verifies, via fully-resolved paths, that the
///     file it built still resolves inside the intended directory — so even a bug in step
///     1, an unexpected filesystem quirk, or a symlink/junction already present in the
///     directory can never result in a write outside it.
/// (Android class doc, preserved verbatim.)
/// </summary>
public static class FileSafety
{
    public sealed class UnsafePathException(string message) : IOException(message);

    /// <summary>Builds "dir\&lt;sanitized id&gt;.&lt;extension&gt;" and guarantees the
    /// result is really inside <paramref name="dir"/>. Throws <see cref="UnsafePathException"/>
    /// rather than silently substituting a different name — callers must skip/reject that
    /// record, never guess.</summary>
    public static string ResolveWithin(string dir, string rawId, string extension)
    {
        var safeId = CryptoUtils.SanitizeFileToken(rawId)
            ?? throw new UnsafePathException("Invalid record id in backup/sync data.");
        Directory.CreateDirectory(dir);
        var candidate = Path.Combine(dir, $"{safeId}.{extension}");

        var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFull = Path.GetFullPath(candidate);
        var candidateParent = Path.GetDirectoryName(candidateFull)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.Equals(candidateParent, dirFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException("Resolved path escaped the intended directory.");
        }
        return candidateFull;
    }
}
