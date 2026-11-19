using System.Globalization;

namespace MediaOrganizer.Core;

/// <summary>
/// Decides the destination path of every file. Pure planning: nothing is created or copied here.
/// Layout (each level is configurable):
///   Photos|Videos / Year / Year-Month / Location / Event / file
///   Screenshots / Year / Year-Month / file
///   Duplicates / Photos|Videos|Screenshots / duplicate_file
///   Unclassified / file
/// Files with the same name but different content never overwrite each other: a numeric suffix is added.
/// </summary>
public static class PathPlanner
{
    public const string UnknownLocation = "Unknown_Location";
    public const string DuplicatePrefix = "duplicate_";
    public const string OrganizedRootName = "Organized_Media";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string CategoryFolder(MediaCategory c) => c switch
    {
        MediaCategory.Photo => "Photos",
        MediaCategory.Video => "Videos",
        MediaCategory.Screenshot => "Screenshots",
        _ => "Unclassified"
    };

    /// <summary>The folder that receives all organized output: DestinationFolder\Organized_Media.</summary>
    public static string OutputRoot(string destinationFolder) =>
        Path.Combine(destinationFolder, OrganizedRootName);

    public static void Plan(IEnumerable<MediaItem> items, OrganizerSettings s)
    {
        var root = OutputRoot(s.DestinationFolder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sorted order keeps the plan deterministic, which makes resuming an interrupted job safe.
        foreach (var it in items.OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            string dir;
            string name = it.FileName;

            if (it.IsDuplicate)
            {
                dir = Path.Combine(root, "Duplicates", CategoryFolder(it.Category));
                name = DuplicatePrefix + name;
            }
            else if (it.Category == MediaCategory.Unclassified)
            {
                dir = Path.Combine(root, "Unclassified");
            }
            else
            {
                dir = Path.Combine(root, CategoryFolder(it.Category));
                var d = it.CaptureDate;
                if (s.UseYearFolders) dir = Path.Combine(dir, d.Year.ToString("0000", CultureInfo.InvariantCulture));
                if (s.UseMonthFolders) dir = Path.Combine(dir, d.ToString("yyyy-MM", CultureInfo.InvariantCulture));

                if (it.Category != MediaCategory.Screenshot)
                {
                    if (s.UseLocationFolders) dir = Path.Combine(dir, SanitizeSegment(it.Location));
                    if (!string.IsNullOrEmpty(it.EventName)) dir = Path.Combine(dir, SanitizeSegment(it.EventName));
                }
            }

            var candidate = Path.Combine(dir, name);
            int n = 0;
            while (!used.Add(candidate))
            {
                n++;
                candidate = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(name) + "_" + n.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(name));
            }
            it.DestinationPath = candidate;
        }
    }

    /// <summary>Makes any text safe to use as a single Windows folder name.</summary>
    public static string SanitizeSegment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return UnknownLocation;

        // Fixed Windows-invalid set so results are identical on every platform.
        const string invalid = "<>:\"/\\|?*";
        var chars = text.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');

        if (s.Length == 0) return UnknownLocation;
        if (s.Length > 80) s = s.Substring(0, 80).TrimEnd('.', ' ');
        if (ReservedNames.Contains(s)) s += "_";
        return s;
    }
}
