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
}
