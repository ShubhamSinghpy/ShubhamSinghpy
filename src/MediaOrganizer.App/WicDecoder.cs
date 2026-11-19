using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.App;

/// <summary>
/// Reads pictures through the Windows Imaging Component, which is how HEIC/HEIF photos (iPhone) are opened when the
/// Microsoft "HEIF Image Extensions" codec is installed. Files are opened read-only and only decoded into memory.
/// </summary>
public static class WicDecoder
{
    public static RgbImage? Decode(string path, int maxSide)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var decoder = BitmapDecoder.Create(fs,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;

        BitmapSource frame = decoder.Frames[0];
        int longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest > maxSide)
        {
            double s = (double)maxSide / longest;
            frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
        }

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var data = new byte[w * h * 3];
        converted.CopyPixels(data, w * 3, 0);
        return new RgbImage(w, h, data);
    }
}
