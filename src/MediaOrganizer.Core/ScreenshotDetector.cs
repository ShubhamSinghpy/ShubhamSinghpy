namespace MediaOrganizer.Core;

/// <summary>
/// Decides whether an image is a screenshot. Ordinary photos are never classified as screenshots just
/// because their dimensions look similar: a dimension match alone is not enough. We combine
/// file name, folder name, EXIF hints and image characteristics.
/// </summary>
public static class ScreenshotDetector
{
    // Common screen resolutions stored as (shortSide, longSide) so orientation does not matter.
    private static readonly HashSet<(int, int)> ScreenSizes = new()
    {
        // iPhone
        (640, 1136), (750, 1334), (828, 1792), (1080, 1920), (1125, 2436), (1170, 2532), (1179, 2556),
        (1206, 2622), (1242, 2208), (1242, 2688), (1284, 2778), (1290, 2796), (1320, 2868),
        // iPad
        (1536, 2048), (1620, 2160), (1640, 2360), (1668, 2224), (1668, 2388), (2048, 2732), (2064, 2752),
        // Android
        (720, 1280), (720, 1520), (720, 1600), (1080, 2160), (1080, 2280), (1080, 2340), (1080, 2400),
        (1440, 2560), (1440, 2960), (1440, 3040), (1440, 3120), (1440, 3200), (1344, 2992),
        // Desktop / laptop
        (768, 1366), (864, 1536), (900, 1440), (1050, 1680), (1080, 1920), (1200, 1920), (1440, 2560),
        (1440, 3440), (1600, 2560), (1800, 2880), (1964, 3024), (2160, 3840), (2234, 3456), (1117, 1728)
    };

    public static bool IsScreenshot(
        string fullPath,
        bool hasCameraMake,
        bool hasExifCaptureDate,
        int? width,
        int? height,
        string? exifUserComment)
    {
        // A camera make means a real camera/phone camera produced this image.
        if (hasCameraMake) return false;

        // iOS writes UserComment = "Screenshot" into screenshots.
        if (!string.IsNullOrWhiteSpace(exifUserComment) &&
            exifUserComment.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            return true;

        var name = Path.GetFileName(fullPath);
        if (FilenameParser.LooksLikeScreenshotName(name)) return true;
        if (FilenameParser.InScreenshotFolder(fullPath)) return true;

        // Weakest signal: PNG, no EXIF capture date, no camera, and an exact screen resolution.
        var ext = Path.GetExtension(fullPath);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !hasExifCaptureDate && width.HasValue && height.HasValue &&
            IsScreenSize(width.Value, height.Value))
            return true;

        return false;
    }

    public static bool IsScreenSize(int w, int h) =>
        ScreenSizes.Contains((Math.Min(w, h), Math.Max(w, h)));
}
