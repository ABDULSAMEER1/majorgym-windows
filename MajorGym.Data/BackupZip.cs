using System.IO.Compression;
using System.Text;

namespace MajorGym.Data;

/// <summary>Thrown for anything wrong with a backup's ZIP/JSON container — corrupt zip,
/// missing backup.json, invalid JSON, unsupported schema. Callers show this message to
/// the owner and must not modify any app data when it's thrown. Ported 1:1 from Android's
/// <c>BackupFormatException</c>.</summary>
public sealed class BackupFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Minimal, safe ZIP container for a single JSON backup entry. Ported 1:1 from Android's
/// <c>BackupZip</c> object — byte-for-byte compatible: an Android-produced backup.zip must
/// open correctly here, and a Windows-produced one must open correctly on Android
/// (Stage 1 report §6 "Keep — this is the most valuable free compatibility win in the
/// whole project"; Stage 2 brief §16).
///
/// The only entry this ever reads or writes is literally named "backup.json" at the
/// archive root. On read, every other entry in the archive is ignored outright — this
/// never iterates-and-extracts arbitrary entries, so a malicious "../../etc/whatever"
/// entry name is never followed or written anywhere; it's simply not the entry being
/// looked for. (Android class doc, preserved verbatim — same defense, same reasoning.)
/// </summary>
public static class BackupZip
{
    public const string EntryName = "backup.json";

    /// <summary>Compresses <paramref name="json"/> into a new ZIP at <paramref name="destFile"/>
    /// containing just backup.json. Overwrites destFile if it already exists. Uses
    /// standard lossless DEFLATE (.NET's ZipArchive default — same algorithm family as
    /// Java's java.util.zip default), never lossy.</summary>
    public static void Write(string json, string destFile)
    {
        var dir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(destFile)) File.Delete(destFile);
        using var zipStream = new FileStream(destFile, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(EntryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(json);
        entryStream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>True if <paramref name="filePath"/>'s first bytes look like a ZIP (local
    /// file header magic "PK\3\4"). Used to route an imported file to the ZIP or
    /// plain-JSON path regardless of what extension it happens to have.</summary>
    public static bool LooksLikeZip(string filePath)
    {
        if (!File.Exists(filePath) || new FileInfo(filePath).Length < 4) return false;
        using var stream = File.OpenRead(filePath);
        Span<byte> header = stackalloc byte[4];
        var read = stream.Read(header);
        return read == 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;
    }

    /// <summary>
    /// Opens <paramref name="zipFilePath"/> and copies just the backup.json entry into a
    /// fresh temporary file under <paramref name="tempBaseDirectory"/>, returning its path.
    /// Throws <see cref="BackupFormatException"/> if the zip can't be opened or contains
    /// no backup.json. Callers must delete the returned file (and ideally the temp dir)
    /// once done reading it — see <see cref="CleanupTemp"/>.
    /// </summary>
    public static string ExtractJsonToTemp(string tempBaseDirectory, string zipFilePath)
    {
        var tempDir = Path.Combine(tempBaseDirectory, "backup_restore_tmp");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"backup_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json");
        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var entry = archive.Entries.FirstOrDefault(e => SanitizedName(e.FullName) == EntryName)
                ?? throw new BackupFormatException("This ZIP doesn't contain a backup.json file.");
            using var input = entry.Open();
            using var output = File.Create(tempFile);
            input.CopyTo(output);
        }
        catch (BackupFormatException)
        {
            TryDelete(tempFile);
            throw;
        }
        catch (Exception e)
        {
            TryDelete(tempFile);
            throw new BackupFormatException(CorruptMessage(zipFilePath), e);
        }
        return tempFile;
    }

    private const string CorruptBase = "This backup file is corrupted and couldn't be opened.";

    /// <summary>Same message Android shows, plus a specific hint when the ZIP starts correctly
    /// but has no end-of-central-directory record — the signature of a file that was cut
    /// short while being copied or sent (the only way an Android export, which is verified
    /// before it is handed out, ends up unreadable).</summary>
    private static string CorruptMessage(string zipFilePath)
    {
        try
        {
            if (LooksLikeZip(zipFilePath) && !HasEndOfCentralDirectory(zipFilePath))
                return CorruptBase + " The file looks incomplete or damaged — most likely it was cut off while being copied or sent " +
                       $"({new FileInfo(zipFilePath).Length:N0} bytes received). Copy the original backup from the phone again " +
                       "(or export a fresh one) and compare the file sizes.";
        }
        catch { /* fall through to the plain message */ }
        return CorruptBase;
    }

    /// <summary>True if the last 64 KB + 22 bytes contain the ZIP end-of-central-directory
    /// signature (PK\5\6), where a complete archive always ends.</summary>
    public static bool HasEndOfCentralDirectory(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var len = (int)Math.Min(fs.Length, 65557);
        fs.Seek(-len, SeekOrigin.End);
        var buf = new byte[len];
        var read = 0;
        while (read < len) { var n = fs.Read(buf, read, len - read); if (n <= 0) break; read += n; }
        for (var i = read - 22; i >= 0; i--)
            if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) return true;
        return false;
    }

    /// <summary>Opens <paramref name="zipFilePath"/> purely to confirm it's a valid,
    /// readable archive containing backup.json — used right after writing a fresh backup
    /// to make sure it's actually usable before it's ever reported as "successful".
    /// Returns the entry's text so the caller can also validate the JSON itself.</summary>
    public static string ReadAndVerify(string zipFilePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var entry = archive.Entries.FirstOrDefault(e => SanitizedName(e.FullName) == EntryName)
                ?? throw new BackupFormatException("backup.json is missing from the newly created ZIP.");
            using var input = entry.Open();
            using var reader = new StreamReader(input, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (BackupFormatException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new BackupFormatException("The ZIP that was just created could not be reopened.", e);
        }
    }

    /// <summary>Best-effort cleanup of everything under the temp-restore directory. Safe
    /// to call even if nothing was ever extracted.</summary>
    public static void CleanupTemp(string tempBaseDirectory)
    {
        var tempDir = Path.Combine(tempBaseDirectory, "backup_restore_tmp");
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Normalizes a zip entry name and rejects anything that isn't a plain,
    /// same-directory filename — defense in depth against zip-slip style entries, even
    /// though this only ever looks up one specific name. (Android doc comment, preserved.)</summary>
    private static string SanitizedName(string name)
    {
        var normalized = name.Replace('\\', '/').TrimStart('/');
        return normalized.Contains("..") ? "" : normalized;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
