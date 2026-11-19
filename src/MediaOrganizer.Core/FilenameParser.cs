using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaOrganizer.Core;

/// <summary>Extracts dates and screenshot hints from file names.</summary>
public static class FilenameParser
{
    // Matches 20240714, 2024-07-14, 2024_07_14, optionally followed by a time (123456 / 12-34-56 / 12.34.56).
    // Digits on either side are rejected so long numbers are never mis-read as dates.
    private static readonly Regex DateRx = new(
        @"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])" +
        @"(?:[-_ T.]?(?<h>[01]\d|2[0-3])[-_.:]?(?<mi>[0-5]\d)[-_.:]?(?<s>[0-5]\d))?(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex ScreenshotWordRx = new(
        @"screen[\s_\-]?shot", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ScreenshotStartRx = new(
        @"^(simulator[\s_\-]*)?(screen[\s_\-]?capture|snip(ping)?([\s_\-]?tool)?|snipaste)([\s_\-\d(]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses a capture date out of a file name, or returns null when none is present.</summary>
    public static DateTime? TryParseDate(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var m = DateRx.Match(name);
        if (!m.Success) return null;

        try
        {
            int y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            int mo = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            int d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            int h = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
            int mi = m.Groups["mi"].Success ? int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture) : 0;
            int s = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;

            var dt = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
            if (y < 1990 || dt > DateTime.Now.AddDays(1)) return null;
            return dt;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // e.g. 2023-02-30
        }
    }

    /// <summary>True when the file name looks like a screenshot (iPhone/Android/Windows/macOS conventions).</summary>
    public static bool LooksLikeScreenshotName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return ScreenshotWordRx.IsMatch(name) || ScreenshotStartRx.IsMatch(name);
    }

    /// <summary>True when the file sits in a folder called "Screenshots" (or similar).</summary>
    public static bool InScreenshotFolder(string fullPath)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "");
        return ScreenshotWordRx.IsMatch(dir) || dir.Contains("screenshot", StringComparison.OrdinalIgnoreCase);
    }
}
