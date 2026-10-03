using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace MajorGym.App;

/// <summary>
/// MajorGym.Data's <see cref="MajorGym.Data.QrUtils"/> renders QR codes as
/// <see cref="System.Drawing.Bitmap"/> (chosen in Stage 2/3 specifically so MajorGym.Data
/// has no WPF dependency and stays usable from a future non-UI host, e.g. a CLI sync tool).
/// WPF's own Image control binds to <see cref="System.Windows.Media.ImageSource"/>, not
/// System.Drawing.Bitmap, so every screen that shows a member's QR (Registered, Renewed)
/// needs this one small bridge — encode to an in-memory PNG, then decode that into a
/// BitmapImage. This is the only place in MajorGym.App that touches System.Drawing.
/// </summary>
public static class BitmapImageUtils
{
    public static BitmapImage FromGdiBitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // decode now — stream can be disposed after EndInit
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze(); // safe to hand to any thread/binding once frozen
        return image;
    }

    /// <summary>
    /// Loads a member/ID-proof photo from disk for display. Phase 1: WPF's default
    /// URI-based Image loading (a) caches by path, so replacing a member's photo — which
    /// PhotoStore deliberately writes to the same file every time — kept showing the OLD
    /// picture, and (b) holds the file open, which would make the overwrite/delete on
    /// replace/remove/member-delete fail with a sharing violation. Decoding once into memory
    /// with OnLoad + IgnoreImageCache avoids both. Returns null for a blank/missing/
    /// undecodable file (Android's <c>File(p).exists()</c> guard: show the initials/placeholder
    /// instead of throwing).
    /// </summary>
    public static BitmapImage? LoadFromFile(string? path, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;

            // In-memory cache of already-decoded (frozen) photos. The key includes the file's last-write time and size, so
            // replacing a member's photo (PhotoStore rewrites the SAME path) automatically produces a fresh decode - the
            // "stale picture after replace" problem this method exists to avoid stays solved. Scrolling a list, filtering it
            // while searching, or returning to a screen no longer re-reads and re-decodes every photo from disk.
            var key = (fullPath, decodePixelWidth, info.LastWriteTimeUtc.Ticks, info.Length);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var hit))
                {
                    LruOrder.Remove(hit.Node);
                    LruOrder.AddFirst(hit.Node);
                    return hit.Image;
                }
            }
            var image = DecodeFromFile(fullPath, decodePixelWidth);
            if (image is null) return null;
            lock (CacheLock)
            {
                if (!Cache.ContainsKey(key))
                {
                    var node = new LinkedListNode<(string, int, long, long)>(key);
                    LruOrder.AddFirst(node);
                    Cache[key] = (image, node);
                    while (Cache.Count > MaxCachedPhotos)
                    {
                        var last = LruOrder.Last!;
                        LruOrder.RemoveLast();
                        Cache.Remove(last.Value);
                    }
                }
            }
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private const int MaxCachedPhotos = 400; // 400 x ~40 KB thumbnails = ~16 MB at most
    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string, int, long, long), (BitmapImage Image, LinkedListNode<(string, int, long, long)> Node)> Cache = new();
    private static readonly LinkedList<(string, int, long, long)> LruOrder = new();

    private static BitmapImage? DecodeFromFile(string path, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
