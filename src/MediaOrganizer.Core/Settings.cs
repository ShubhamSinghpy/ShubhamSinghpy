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

    /// <summary>0 = automatic.</summary>
    public int Threads { get; set; } = 0;

    public int EffectiveThreads => Threads > 0 ? Threads : Math.Clamp(Environment.ProcessorCount, 2, 8);

    // ---- persistence ------------------------------------------------------
    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaOrganizer");

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
