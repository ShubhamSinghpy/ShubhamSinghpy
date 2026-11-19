using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MediaOrganizer.Core;

public enum MediaCategory { Photo, Video, Screenshot, Unclassified }

public enum DateSource
{
    /// <summary>EXIF DateTimeOriginal (photos) or container creation date (videos).</summary>
    Original,
    /// <summary>Another reliable embedded date (EXIF digitized / modified).</summary>
    OtherEmbedded,
    /// <summary>Date parsed from a filename such as IMG_20240714_123456.jpg.</summary>
    FilenamePattern,
    /// <summary>File system timestamp. Always reported as a fallback.</summary>
    FileSystemFallback
}

public enum ItemStatus { Pending, Verified, Failed }

/// <summary>One media file found in the source folder plus everything we learned about it.</summary>
public sealed class MediaItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- identity -------------------------------------------------------
    public string SourcePath { get; init; } = "";
    public string FileName => Path.GetFileName(SourcePath);
    public string Extension => Path.GetExtension(SourcePath).ToLowerInvariant();
    public string Format => Extension.TrimStart('.').ToUpperInvariant();
    public long SizeBytes { get; set; }
    public DateTime FsCreated { get; set; }
    public DateTime FsModified { get; set; }
    public ExtKind Kind { get; set; }

    // ---- classification & dates ----------------------------------------
    public MediaCategory Category { get; set; } = MediaCategory.Unclassified;
    public DateTime CaptureDate { get; set; }
    public DateSource DateSource { get; set; } = DateSource.FileSystemFallback;

    // ---- location -------------------------------------------------------
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
    public string Location { get; set; } = PathPlanner.UnknownLocation;

    // ---- device & media properties --------------------------------------
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public int? Orientation { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? FrameRate { get; set; }
    public string? Codec { get; set; }

    public string Device
    {
        get
        {
            var make = CameraMake?.Trim();
            var model = CameraModel?.Trim();
            if (string.IsNullOrEmpty(make)) return model ?? "";
            if (string.IsNullOrEmpty(model)) return make;
            return model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
        }
    }

    public string Resolution => Width.HasValue && Height.HasValue ? $"{Width}x{Height}" : "";

    // ---- duplicates & events -------------------------------------------
    public string? Sha256 { get; set; }
    public bool IsDuplicate { get; set; }
    public int DuplicateGroup { get; set; }
    public string? DuplicateOfPath { get; set; }
    public string? EventName { get; set; }

    // ---- notes ----------------------------------------------------------
    public string? MetadataNote { get; private set; }
    public void AddNote(string note)
    {
        MetadataNote = string.IsNullOrEmpty(MetadataNote) ? note : MetadataNote + " | " + note;
    }

    // ---- output ---------------------------------------------------------
    private string _destinationPath = "";
    public string DestinationPath
    {
        get => _destinationPath;
        set { if (_destinationPath != value) { _destinationPath = value; Raise(); } }
    }

    private ItemStatus _status = ItemStatus.Pending;
    public ItemStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Raise(); Raise(nameof(StatusText)); } }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (_error != value) { _error = value; Raise(); } }
    }

    public string? DestSha256 { get; set; }
    public bool? HashMatch { get; set; }

    // ---- display helpers (used by the UI and reports) -------------------
    public int Year => CaptureDate.Year;
    public string MonthText => CaptureDate.ToString("MM", CultureInfo.InvariantCulture);
    public string CaptureDateText => CaptureDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string DateSourceText => DateSource switch
    {
        DateSource.Original => "Original",
        DateSource.OtherEmbedded => "Embedded (other)",
        DateSource.FilenamePattern => "Filename pattern",
        _ => "FALLBACK: file system date"
    };

    public string SizeText => Fmt.Bytes(SizeBytes);
    public string DuplicateText => IsDuplicate ? $"Duplicate (group {DuplicateGroup})"
                                 : DuplicateGroup > 0 ? $"Original (group {DuplicateGroup})" : "No";
    public string StatusText => Status switch
    {
        ItemStatus.Verified => "Copied & verified",
        ItemStatus.Failed => "Failed",
        _ => "Pending"
    };

    /// <summary>Which useful metadata fields are missing for this file.</summary>
    public IReadOnlyList<string> MissingFieldList()
    {
        var list = new List<string>();
        if (DateSource != DateSource.Original) list.Add("CaptureDate");
        if (Category is MediaCategory.Photo or MediaCategory.Video)
        {
            if (!HasGps) list.Add("GPS");
            if (string.IsNullOrEmpty(Device)) list.Add("Device");
        }
        return list;
    }
}

public sealed class ProgressInfo
{
    public string Stage { get; init; } = "";
    public string Message { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public bool Indeterminate { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int Failed { get; init; }
    public double Percent => Total > 0 ? Done * 100.0 / Total : 0;
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[i];
    }
}
