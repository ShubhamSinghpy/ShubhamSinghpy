using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaOrganizer.Core;

/// <summary>Parses ISO 6709 location strings such as "+26.8467+080.9462+123.4/" used in MP4/MOV files.</summary>
public static class Iso6709
{
    private static readonly Regex Rx = new(
        @"^\s*(?<lat>[+-]\d+(?:\.\d+)?)(?<lon>[+-]\d+(?:\.\d+)?)(?<alt>[+-]\d+(?:\.\d+)?)?\s*/?\s*$",
        RegexOptions.Compiled);

    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = Rx.Match(text);
        if (!m.Success) return false;

        if (!double.TryParse(m.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(m.Groups["lon"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return false;

        return Validate(lat, lon, out latitude, out longitude);
    }

    /// <summary>Accepts only real coordinates. (0,0) is treated as "no GPS" because cameras write it when there is no fix.</summary>
    public static bool Validate(double lat, double lon, out double latitude, out double longitude)
    {
        latitude = lat;
        longitude = lon;
        if (double.IsNaN(lat) || double.IsNaN(lon)) return false;
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return false;
        if (lat == 0 && lon == 0) return false;
        return true;
    }
}
