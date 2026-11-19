using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MDir = MetadataExtractor.Directory;

namespace MediaOrganizer.Core;

/// <summary>
/// Reads metadata and classifies one file. Strictly read-only: files are opened for reading with
/// shared access and nothing is ever written back.
/// </summary>
public sealed class MetadataService
{
    private readonly FfprobeReader _ffprobe;

    public MetadataService(FfprobeReader? ffprobe = null)
    {
        _ffprobe = ffprobe ?? new FfprobeReader();
    }

    /// <summary>True when videos are being read without ffprobe (limited metadata, no GPS).</summary>
    public bool VideoToolMissing => !_ffprobe.IsAvailable;

    public void Populate(MediaItem item)
    {
        DateTime? original = null;
        DateTime? other = null;

        item.Category = item.Kind switch
        {
            ExtKind.Photo or ExtKind.Raw => MediaCategory.Photo,
            ExtKind.Video => MediaCategory.Video,
            _ => MediaCategory.Unclassified
        };

        try
        {
            if (item.SizeBytes == 0)
            {
                item.Category = MediaCategory.Unclassified;
                item.AddNote("Empty file (0 bytes)");
            }
            else if (item.Kind is ExtKind.Photo or ExtKind.Raw)
            {
                ReadPhoto(item, out original, out other);
            }
            else if (item.Kind == ExtKind.Video)
            {
                ReadVideo(item, out original);
            }
            else
            {
                item.AddNote("Unsupported or unidentified media format");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never crash on one bad file: keep the file, record why, fall back on other date sources.
            item.AddNote("Metadata could not be read: " + ex.Message);
            AppLog.Warn($"Metadata read failed for '{item.SourcePath}'.", ex);
        }

        ResolveDate(item, original, other);
    }

    // ------------------------------------------------------------------ photos
    private static void ReadPhoto(MediaItem item, out DateTime? original, out DateTime? other)
    {
        original = null;
        other = null;

        IReadOnlyList<MDir> dirs;
        try
        {
            using var fs = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.RandomAccess);
            dirs = ImageMetadataReader.ReadMetadata(fs);
        }
        catch (ImageProcessingException ex)
        {
            if (item.Kind == ExtKind.Raw)
            {
                // Known RAW format the library cannot parse: still a photo, date falls back.
                item.AddNote("RAW metadata not supported; fallback date rules applied");
            }
            else
            {
                item.Category = MediaCategory.Unclassified;
                item.AddNote("Unreadable or corrupted image: " + ex.Message);
            }
            return;
        }

        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

        if (sub != null)
        {
            if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var d1)) original = d1;
            if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var d2)) other = d2;
        }
        if (ifd0 != null)
        {
            if (other == null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var d3)) other = d3;
            item.CameraMake = ifd0.GetString(ExifDirectoryBase.TagMake)?.Trim();
            item.CameraModel = ifd0.GetString(ExifDirectoryBase.TagModel)?.Trim();
            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orient)) item.Orientation = orient;
        }

        var loc = gps?.GetGeoLocation();
        if (loc is not null && Iso6709.Validate(loc.Latitude, loc.Longitude, out var lat, out var lon))
        {
            item.Latitude = lat;
            item.Longitude = lon;
        }

        item.Width = FindInt(dirs, "Image Width", "Exif Image Width", "Width");
        item.Height = FindInt(dirs, "Image Height", "Exif Image Height", "Height");

        if (item.Kind == ExtKind.Photo)
        {
            var userComment = sub?.GetDescription(ExifDirectoryBase.TagUserComment);
            bool isShot = ScreenshotDetector.IsScreenshot(
                item.SourcePath,
                hasCameraMake: !string.IsNullOrWhiteSpace(item.CameraMake),
                hasExifCaptureDate: original.HasValue,
                item.Width, item.Height, userComment);
            if (isShot) item.Category = MediaCategory.Screenshot;
        }
    }

    // ------------------------------------------------------------------ videos
    private void ReadVideo(MediaItem item, out DateTime? original)
    {
        original = null;

        if (_ffprobe.IsAvailable)
        {
            var probe = _ffprobe.Probe(item.SourcePath);
            if (probe == null)
            {
                item.Category = MediaCategory.Unclassified;
                item.AddNote("Unreadable or corrupted video (ffprobe could not parse it)");
                return;
            }

            original = probe.CreationDate;
            item.Width = probe.Width;
            item.Height = probe.Height;
            item.DurationSeconds = probe.DurationSeconds;
            item.FrameRate = probe.FrameRate;
            item.Codec = probe.Codec;
            item.CameraMake = probe.Make;
            item.CameraModel = probe.Model;
            if (probe.Latitude.HasValue && probe.Longitude.HasValue &&
                Iso6709.Validate(probe.Latitude.Value, probe.Longitude.Value, out var lat, out var lon))
            {
                item.Latitude = lat;
                item.Longitude = lon;
            }
            return;
        }

        // Fallback without ffprobe: MP4/MOV container dates and size via MetadataExtractor. No GPS.
        try
        {
            using var fs = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.RandomAccess);
            var dirs = ImageMetadataReader.ReadMetadata(fs);

            foreach (var d in dirs)
            {
                if (!(d.Name.Contains("QuickTime", StringComparison.OrdinalIgnoreCase) ||
                      d.Name.Contains("MP4", StringComparison.OrdinalIgnoreCase))) continue;

                foreach (var t in d.Tags)
                {
                    if (!(t.Name.Contains("Created", StringComparison.OrdinalIgnoreCase) ||
                          t.Name.Contains("Creation", StringComparison.OrdinalIgnoreCase))) continue;

                    if (d.GetObject(t.Type) is DateTime dt && dt.Year >= 1971)
                    {
                        var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        original = utc.ToLocalTime();
                        break;
                    }
                }
                if (original.HasValue) break;
            }

            item.Width = FindInt(dirs, "Width");
            item.Height = FindInt(dirs, "Height");
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or NotSupportedException)
        {
            // AVI / MKV / WMV etc.: keep as Video, dates fall back. Not corruption.
        }
    }

    // ------------------------------------------------------------------ helpers
    private static void ResolveDate(MediaItem item, DateTime? original, DateTime? other)
    {
        if (original.HasValue && IsPlausible(original.Value))
        {
            item.CaptureDate = original.Value;
            item.DateSource = DateSource.Original;
            return;
        }
        if (other.HasValue && IsPlausible(other.Value))
        {
            item.CaptureDate = other.Value;
            item.DateSource = DateSource.OtherEmbedded;
            return;
        }

        var fromName = FilenameParser.TryParseDate(item.FileName);
        if (fromName.HasValue)
        {
            item.CaptureDate = fromName.Value;
            item.DateSource = DateSource.FilenamePattern;
            return;
        }

        // Last resort: file system timestamp (earliest of created / modified). Reported as a fallback.
        var created = item.FsCreated == default ? item.FsModified : item.FsCreated;
        var modified = item.FsModified == default ? item.FsCreated : item.FsModified;
        item.CaptureDate = created < modified ? created : modified;
        if (item.CaptureDate == default) item.CaptureDate = DateTime.Now;
        item.DateSource = DateSource.FileSystemFallback;
    }

    private static bool IsPlausible(DateTime dt) => dt.Year >= 1971 && dt <= DateTime.Now.AddDays(2);

    private static int? FindInt(IEnumerable<MDir> dirs, params string[] names)
    {
        var list = dirs as IReadOnlyList<MDir> ?? dirs.ToList();
        foreach (var name in names)
        {
            foreach (var d in list)
            {
                foreach (var t in d.Tags)
                {
                    if (!string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    var o = d.GetObject(t.Type);
                    int? v = o switch
                    {
                        int i => i,
                        uint ui => (int)ui,
                        long l => (int)l,
                        ushort us => us,
                        short s => s,
                        _ => null
                    };
                    if (v is > 0) return v;
                }
            }
        }
        return null;
    }
}
