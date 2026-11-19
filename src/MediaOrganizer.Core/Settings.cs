using System.Text.Json;

namespace MediaOrganizer.Core;

/// <summary>User-configurable options. Persisted as JSON in %LocalAppData%\MediaOrganizer.</summary>
public sealed class OrganizerSettings
{
    public string SourceFolder { get; set; } = "";
    public string DestinationFolder { get; set; } = "";

    // Folder hierarchy: Category / Year / Year-Month / Location / Event
    public bool UseYearFolders { get; set; } = true;
    public bool UseMonthFolders { get; set; } = true;
    public bool UseLocationFolders { get; set; } = true;
    public bool GroupEvents { get; set; } = true;

    // Event grouping thresholds
    public double EventGapHours { get; set; } = 8;
    public double EventDistanceKm { get; set; } = 50;
    public int MinEventItems { get; set; } = 10;

    /// <summary>
    /// When true, readable place names (city / locality) are fetched from OpenStreetMap Nominatim.
    /// Only rounded coordinates are sent - never photos, filenames or other metadata.
    /// When false, or when offline, folders are named from coordinates (e.g. GPS_26.8N_80.9E).
    /// </summary>
    public bool OnlineGeocoding { get; set; } = true;

    /// <summary>Copy non-media files (documents, etc.) into Unclassified. Off by default.</summary>
    public bool IncludeNonMediaFiles { get; set; } = false;

    public bool ExportExcel { get; set; } = true;

    // ---- optional face recognition. OFF by default: nothing is loaded or scanned unless the user turns it on. ----
    public bool FaceRecognitionEnabled { get; set; } = false;
    /// <summary>Create People\&lt;Name&gt; collections of verified copies for every registered person.</summary>
    public bool CreatePersonFolders { get; set; } = true;
    /// <summary>Also collect media that only shows unidentified people in People\_Unidentified_People.</summary>
    public bool CollectUnidentifiedPeople { get; set; } = false;
    public double FaceMatchThreshold { get; set; } = 0.40;
    public int FaceVideoFrames { get; set; } = 8;

    /// <summary>0 = automatic.</summary>
    public int Threads { get; set; } = 0;

    public int EffectiveThreads => Threads > 0 ? Threads : Math.Clamp(Environment.ProcessorCount, 2, 8);

    // ---- persistence ------------------------------------------------------
    /// <summary>%LocalAppData%\MediaOrganizer, or the folder in MEDIAORGANIZER_DATA (used by tests to stay isolated).</summary>
    public static string AppDataFolder =>
        Environment.GetEnvironmentVariable("MEDIAORGANIZER_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaOrganizer");

    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public static OrganizerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<OrganizerSettings>(File.ReadAllText(SettingsPath));
                if (s != null) return s;
            }
        }
        catch (Exception ex) { AppLog.Warn("Could not load settings; using defaults.", ex); }
        return new OrganizerSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Warn("Could not save settings.", ex); }
    }
}
