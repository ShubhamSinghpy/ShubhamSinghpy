using System.Globalization;

namespace MediaOrganizer.Core;

/// <summary>
/// Groups photos and videos into events using capture-time gaps (and optionally GPS distance).
/// Event folders get neutral, date-based names (Event_2024-07-14); the app never guesses
/// "Wedding" or "Birthday". Only files with a reliable capture date take part.
/// </summary>
public static class EventGrouper
{
    public static void Assign(IEnumerable<MediaItem> items, OrganizerSettings s)
    {
        var all = items.ToList();
        foreach (var it in all) it.EventName = null;
        if (!s.GroupEvents) return;

        var list = all.Where(i => !i.IsDuplicate &&
                                  i.Category is MediaCategory.Photo or MediaCategory.Video &&
                                  i.DateSource is DateSource.Original or DateSource.OtherEmbedded)
                      .OrderBy(i => i.CaptureDate)
                      .ThenBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                      .ToList();

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new List<MediaItem>();

        foreach (var it in list)
        {
            if (current.Count > 0 && IsBreak(current[^1], it, s))
            {
                Flush(current, s, usedNames);
                current = new List<MediaItem>();
            }
            current.Add(it);
        }
        Flush(current, s, usedNames);
    }

    private static bool IsBreak(MediaItem prev, MediaItem next, OrganizerSettings s)
    {
        if ((next.CaptureDate - prev.CaptureDate).TotalHours > s.EventGapHours) return true;
        if (s.EventDistanceKm > 0 && prev.HasGps && next.HasGps &&
            HaversineKm(prev.Latitude!.Value, prev.Longitude!.Value, next.Latitude!.Value, next.Longitude!.Value) > s.EventDistanceKm)
            return true;
        return false;
    }

    private static void Flush(List<MediaItem> group, OrganizerSettings s, HashSet<string> usedNames)
    {
        if (group.Count == 0 || group.Count < s.MinEventItems) return;

        var start = group[0].CaptureDate.Date;
        var end = group[^1].CaptureDate.Date;
        var name = start == end
            ? "Event_" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "Event_" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_to_" +
              end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var unique = name;
        int n = 1;
        while (!usedNames.Add(unique)) { n++; unique = name + "_" + n.ToString(CultureInfo.InvariantCulture); }

        foreach (var it in group) it.EventName = unique;
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
