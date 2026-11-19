using SkiaSharp;

namespace MediaOrganizer.Core.Faces;

/// <summary>A decoded picture: 8-bit RGB, row-major, 3 bytes per pixel.</summary>
public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public RgbImage(int width, int height, byte[] data)
    {
        if (data.Length != width * height * 3) throw new ArgumentException("Pixel buffer has the wrong size.");
        Width = width;
        Height = height;
        Data = data;
    }
}

/// <summary>Plain pixel operations used to prepare pictures for face analysis.</summary>
public static class ImageOps
{
    /// <summary>Applies an EXIF orientation (1-8) so the picture is upright. 1 or anything unknown returns the input.</summary>
    public static RgbImage Orient(RgbImage src, int orientation)
    {
        if (orientation is < 2 or > 8) return src;

        int w = src.Width, h = src.Height;
        bool swap = orientation >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var dst = new byte[dw * dh * 3];

        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = w - 1 - x; sy = y; break;              // mirror
                    case 3: sx = w - 1 - x; sy = h - 1 - y; break;      // rotate 180
                    case 4: sx = x; sy = h - 1 - y; break;              // flip vertical
                    case 5: sx = y; sy = x; break;                      // transpose
                    case 6: sx = y; sy = h - 1 - x; break;              // rotate 90 clockwise
                    case 7: sx = w - 1 - y; sy = h - 1 - x; break;      // transverse
                    default: sx = w - 1 - y; sy = x; break;             // 8: rotate 270 clockwise
                }
                int si = (sy * w + sx) * 3, di = (y * dw + x) * 3;
                dst[di] = src.Data[si]; dst[di + 1] = src.Data[si + 1]; dst[di + 2] = src.Data[si + 2];
            }
        }
        return new RgbImage(dw, dh, dst);
    }

    /// <summary>Shrinks so the longest side is at most <paramref name="maxSide"/> (area averaging). Never enlarges.</summary>
    public static RgbImage Downscale(RgbImage src, int maxSide)
    {
        int longest = Math.Max(src.Width, src.Height);
        if (longest <= maxSide) return src;

        double s = (double)maxSide / longest;
        int nw = Math.Max(1, (int)Math.Round(src.Width * s));
        int nh = Math.Max(1, (int)Math.Round(src.Height * s));
        var dst = new byte[nw * nh * 3];

        for (int y = 0; y < nh; y++)
        {
            int y0 = (int)(y / s), y1 = Math.Min(src.Height, Math.Max(y0 + 1, (int)((y + 1) / s)));
            for (int x = 0; x < nw; x++)
            {
                int x0 = (int)(x / s), x1 = Math.Min(src.Width, Math.Max(x0 + 1, (int)((x + 1) / s)));
                long r = 0, g = 0, b = 0;
                int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * src.Width + xx) * 3;
                        r += src.Data[i]; g += src.Data[i + 1]; b += src.Data[i + 2];
                        n++;
                    }
                int d = (y * nw + x) * 3;
                dst[d] = (byte)(r / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(b / n);
            }
        }
        return new RgbImage(nw, nh, dst);
    }
}

/// <summary>
/// Decodes pictures for face analysis. Files are only ever opened for reading, and results live in memory only.
/// JPEG, PNG, BMP, GIF and WebP are decoded here (SkiaSharp). Formats it cannot read (HEIC/HEIF) are handed to
/// <see cref="PlatformDecoder"/>, which the Windows app provides through the built-in Windows Imaging Component.
/// </summary>
public static class ImageLoader
{
    public const int DefaultMaxSide = 1600;

    /// <summary>Optional decoder supplied by the host (path, maxSide) -> upright image, or null when it cannot decode the file.</summary>
    public static Func<string, int, RgbImage?>? PlatformDecoder { get; set; }

    private static readonly HashSet<string> PlatformFirst = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".tif", ".tiff" };

    public static RgbImage? TryLoad(string path, out string? error, int maxSide = DefaultMaxSide)
    {
        error = null;
        var ext = Path.GetExtension(path);

        if (PlatformFirst.Contains(ext) && PlatformDecoder != null)
        {
            var viaPlatform = TryPlatform(path, maxSide, out error);
            if (viaPlatform != null) return viaPlatform;
            if (ext.StartsWith(".he", StringComparison.OrdinalIgnoreCase))
            {
                error ??= "HEIC/HEIF pictures need the Windows 'HEIF Image Extensions' codec.";
                return null;
            }
            error = null; // TIFF: fall through to the built-in decoder
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Decode(stream, maxSide);
        }
        catch (Exception ex)
        {
            var viaPlatform = TryPlatform(path, maxSide, out _);
            if (viaPlatform != null) return viaPlatform;
            error = "Picture could not be decoded: " + ex.Message;
            return null;
        }
    }

    public static RgbImage Decode(byte[] bytes, int maxSide = DefaultMaxSide)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        return Decode(ms, maxSide);
    }

    public static RgbImage Decode(Stream stream, int maxSide = DefaultMaxSide)
    {
        using var data = SKData.Create(stream) ?? throw new InvalidDataException("Empty picture.");
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unknown or damaged picture format.");

        // Let the decoder shrink JPEGs while decoding (1/2, 1/4, 1/8) - much faster for big photos.
        var size = codec.Info.Size;
        foreach (var denom in new[] { 8, 4, 2 })
        {
            var scaled = codec.GetScaledDimensions(1f / denom);
            if (Math.Max(scaled.Width, scaled.Height) >= maxSide) { size = scaled; break; }
        }

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("Picture could not be decoded.");
        var px = bmp.GetPixelSpan(); // RGBA

        var rgb = new byte[bmp.Width * bmp.Height * 3];
        for (int i = 0, j = 0; i + 3 < px.Length && j + 2 < rgb.Length; i += 4, j += 3)
        {
            rgb[j] = px[i]; rgb[j + 1] = px[i + 1]; rgb[j + 2] = px[i + 2];
        }

        var img = new RgbImage(bmp.Width, bmp.Height, rgb);
        img = ImageOps.Orient(img, (int)codec.EncodedOrigin);
        return ImageOps.Downscale(img, maxSide);
    }

    private static RgbImage? TryPlatform(string path, int maxSide, out string? error)
    {
        error = null;
        var decoder = PlatformDecoder;
        if (decoder == null) return null;
        try { return decoder(path, maxSide); }
        catch (Exception ex)
        {
            error = "Picture could not be decoded: " + ex.Message;
            return null;
        }
    }
}
