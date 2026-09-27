using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MajorGym.Data;

/// <summary>
/// Windows equivalent of the photo-storage half of Android's Repository (Stage 2 brief
/// §19). Android stores member/ID-proof photos as JPEG files under the app's private
/// storage at <c>filesDir/photos/&lt;memberId&gt;.jpg</c> and
/// <c>filesDir/id_photos/&lt;memberId&gt;.jpg</c>; this class reproduces the same
/// per-member-id naming convention and the same downscale/compress behavior (max 1600px
/// long side, JPEG quality 85) under a Windows app-data directory (recommended:
/// %LOCALAPPDATA%\MajorGym\photos and \id_photos — the caller supplies the base directory
/// so the exact root can be decided alongside AppDatabase's own directory choice).
///
/// Preserves Android's logical relationship: memberId → photo, memberId → ID proof — see
/// Stage 1 report §2.3 and Stage 2 brief §19.
/// </summary>
public sealed class PhotoStore
{
    private readonly string _photosDir;
    private readonly string _idPhotosDir;

    private const int MaxDimension = 1600;
    private const long JpegQuality = 85L;

    public PhotoStore(string appDataDirectory)
    {
        _photosDir = Path.Combine(appDataDirectory, "photos");
        _idPhotosDir = Path.Combine(appDataDirectory, "id_photos");
        Directory.CreateDirectory(_photosDir);
        Directory.CreateDirectory(_idPhotosDir);
    }

    /// <summary>Copies/compresses the picked photo (a file path, e.g. from a Windows file
    /// picker or webcam capture) into permanent storage — downscaled and JPEG-compressed
    /// the same way Android's compressInto does. Always compresses fresh from the source
    /// file, never re-compresses an already-saved file, so repeated edits can't
    /// progressively degrade it. Returns null if the image couldn't be decoded, instead of
    /// throwing (Android parity: Android returns "" — null is this codebase's equivalent
    /// "no path" sentinel for Member.PhotoPath, which is nullable, unlike IdProofPhotoPath
    /// which uses "").</summary>
    public string? SaveMemberPhoto(string memberId, string sourceFilePath)
    {
        var dest = SafePhotoFile(_photosDir, memberId);
        if (dest is null) return null;
        return CompressInto(sourceFilePath, dest) ? dest : null;
    }

    /// <summary>Same as <see cref="SaveMemberPhoto"/> but for the ID-proof photo slot.
    /// Returns "" (Android's own "no photo" convention for IdProofPhotoPath) rather than
    /// null if the image couldn't be decoded.</summary>
    public string SaveIdProofPhoto(string memberId, string sourceFilePath)
    {
        var dest = SafePhotoFile(_idPhotosDir, memberId);
        if (dest is null) return "";
        return CompressInto(sourceFilePath, dest) ? dest : "";
    }

    /// <summary>Writes already-decoded photo bytes (from a backup import or a sync
    /// payload — see BackupManager / SyncChangeCodec) directly to the member photo slot,
    /// WITHOUT re-compressing — the bytes already went through compression once, on
    /// whichever device originally captured the photo; re-compressing a second time on
    /// every restore/sync would be a lossy generation loss Android's own BackupManager
    /// doesn't inflict either.</summary>
    public string? WriteMemberPhoto(string memberId, byte[] jpegBytes)
    {
        var dest = SafePhotoFile(_photosDir, memberId);
        if (dest is null) return null;
        File.WriteAllBytes(dest, jpegBytes);
        return dest;
    }

    public string? WriteIdProofPhoto(string memberId, byte[] jpegBytes)
    {
        var dest = SafePhotoFile(_idPhotosDir, memberId);
        if (dest is null) return null;
        File.WriteAllBytes(dest, jpegBytes);
        return dest;
    }

    /// <summary>Removes a member's photo file, if any (safe no-op if there isn't one).</summary>
    public void DeletePhoto(string memberId)
    {
        var f = SafePhotoFile(_photosDir, memberId);
        if (f is not null && File.Exists(f)) File.Delete(f);
    }

    /// <summary>Removes a member's ID proof photo file, if any (safe no-op if there isn't one).</summary>
    public void DeleteIdProofPhoto(string memberId)
    {
        var f = SafePhotoFile(_idPhotosDir, memberId);
        if (f is not null && File.Exists(f)) File.Delete(f);
    }

    /// <summary>Reads a stored photo's raw bytes (used by SyncChangeCodec.EncodeMember
    /// when building a Base64 change-log/sync payload).</summary>
    public byte[]? ReadBytes(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>Same path-traversal defense as backup/sync import, applied consistently
    /// here too even though memberId is normally an app-generated UUID — defense in depth
    /// costs nothing and means this helper is safe to reuse verbatim if a future caller
    /// ever feeds it an externally-supplied id. (Android doc comment, preserved.)</summary>
    private static string? SafePhotoFile(string dir, string memberId)
    {
        try
        {
            return FileSafety.ResolveWithin(dir, memberId, "jpg");
        }
        catch (FileSafety.UnsafePathException)
        {
            return null;
        }
    }

    private static bool CompressInto(string sourceFilePath, string destPath)
    {
        if (!File.Exists(sourceFilePath)) return false;
        try
        {
            using var original = new Bitmap(sourceFilePath);
            var longSide = Math.Max(original.Width, original.Height);
            var scale = longSide > MaxDimension ? MaxDimension / (float)longSide : 1f;

            using Bitmap toSave = scale < 1f
                ? ResizeBitmap(original, (int)(original.Width * scale), (int)(original.Height * scale))
                : original;

            var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            toSave.Save(destPath, jpegEncoder, encoderParams);
            return true;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException)
        {
            // OutOfMemoryException is System.Drawing's actual "not a valid image" signal —
            // matches Android's BitmapFactory.decodeStream returning null on undecodable input.
            return false;
        }
    }

    private static Bitmap ResizeBitmap(Bitmap original, int width, int height)
    {
        var resized = new Bitmap(width, height);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(original, 0, 0, width, height);
        return resized;
    }
}
