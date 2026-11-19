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

public enum FaceScanState { NotScanned, Scanned, Failed, Unsupported }

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

    // ---- optional face recognition (all defaults mean "not scanned") ------
    public FaceScanState FaceState { get; set; } = FaceScanState.NotScanned;
    /// <summary>Most faces seen in one image / one sampled video frame.</summary>
    public int FaceCount { get; set; }
    /// <summary>Faces that matched no registered person (before manual corrections).</summary>
    public int AutoUnidentified { get; set; }
    public string? FaceError { get; set; }
    /// <summary>People recognised automatically.</summary>
    public List<string> AutoPeople { get; } = new();
    /// <summary>People the user added by hand.</summary>
    public HashSet<string> AddedPeople { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Automatic matches the user rejected.</summary>
    public HashSet<string> RemovedPeople { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Final list of people for this file: automatic matches minus rejected plus manual additions.</summary>
    public IReadOnlyList<string> People
    {
        get
        {
            var list = AutoPeople.Where(p => !RemovedPeople.Contains(p)).ToList();
            foreach (var a in AddedPeople)
                if (!list.Contains(a, StringComparer.OrdinalIgnoreCase)) list.Add(a);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }

    public int UnidentifiedFaces
    {
        get
        {
            int removedAuto = AutoPeople.Count(p => RemovedPeople.Contains(p));
            int addedManual = AddedPeople.Count(p => !AutoPeople.Contains(p, StringComparer.OrdinalIgnoreCase));
            return Math.Max(0, AutoUnidentified + removedAuto - addedManual);
        }
    }

    public bool HasRecognizedPeople => People.Count > 0;
    public bool HasUnidentifiedPeople => FaceState == FaceScanState.Scanned && UnidentifiedFaces > 0;
    public bool HasPeople => HasRecognizedPeople || (FaceState == FaceScanState.Scanned && FaceCount > 0);
    public bool FaceScanned => FaceState == FaceScanState.Scanned || People.Count > 0;

    public string PeopleText => string.Join(", ", People);

    public string FaceCategoryText
    {
        get
        {
            if (FaceState == FaceScanState.Failed && People.Count == 0) return "Scan failed";
            if (FaceState == FaceScanState.Unsupported && People.Count == 0) return "Not scannable";
            if (!FaceScanned) return "Not scanned";
            if (!HasPeople) return "No people";
            if (HasRecognizedPeople && HasUnidentifiedPeople) return "Recognized + unidentified";
            return HasRecognizedPeople ? "Recognized people" : "Unidentified people";
        }
    }

    /// <summary>Tells the UI that any face-related value changed.</summary>
    public void NotifyFaceChanged()
    {
        foreach (var n in new[] { nameof(FaceState), nameof(FaceCount), nameof(People), nameof(PeopleText),
                                  nameof(UnidentifiedFaces), nameof(FaceCategoryText), nameof(HasPeople) })
            Raise(n);
    }

    /// <summary>Clears every face result for this file (used when the user deletes all face data).</summary>
    public void ResetFaces()
    {
        FaceState = FaceScanState.NotScanned;
        FaceCount = 0;
        AutoUnidentified = 0;
        FaceError = null;
        AutoPeople.Clear();
        AddedPeople.Clear();
        RemovedPeople.Clear();
        NotifyFaceChanged();
    }

    /// <summary>Key used by the copy journal. Person-collection copies use their own key so they never clash with the main copy.</summary>
    public string? JournalKeyOverride { get; init; }
    public string JournalKey => JournalKeyOverride ?? SourcePath;

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
