using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace MediaOrganizer.Core;

public sealed class RunSummary
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public DateTime StartedLocal { get; set; }
    public DateTime FinishedLocal { get; set; }
    public int TotalMediaFiles { get; set; }
    public int NonMediaSkipped { get; set; }
    public int Photos { get; set; }
    public int Videos { get; set; }
    public int Screenshots { get; set; }
    public int Unclassified { get; set; }
    public int Duplicates { get; set; }
    public int WithoutGps { get; set; }
    public int WithoutCaptureDate { get; set; }
    public int MetadataIssues { get; set; }
    public int Verified { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public long TotalBytes { get; set; }
    public bool Cancelled { get; set; }

    public static RunSummary Build(IReadOnlyList<MediaItem> items, string source, string dest, int nonMedia)
    {
        return new RunSummary
        {
            Source = source,
            Destination = dest,
            TotalMediaFiles = items.Count,
            NonMediaSkipped = nonMedia,
            Photos = items.Count(i => i.Category == MediaCategory.Photo),
            Videos = items.Count(i => i.Category == MediaCategory.Video),
            Screenshots = items.Count(i => i.Category == MediaCategory.Screenshot),
            Unclassified = items.Count(i => i.Category == MediaCategory.Unclassified),
            Duplicates = items.Count(i => i.IsDuplicate),
            WithoutGps = items.Count(i => i.Category is MediaCategory.Photo or MediaCategory.Video && !i.HasGps),
            WithoutCaptureDate = items.Count(i => i.DateSource != DateSource.Original),
            MetadataIssues = items.Count(i => !string.IsNullOrEmpty(i.MetadataNote)),
            Verified = items.Count(i => i.Status == ItemStatus.Verified),
            Failed = items.Count(i => i.Status == ItemStatus.Failed),
            Pending = items.Count(i => i.Status == ItemStatus.Pending),
            TotalBytes = items.Sum(i => i.SizeBytes)
        };
    }
}

public sealed record ReportTable(string Name, string[] Header, List<string[]> Rows);

/// <summary>Writes the CSV reports (and optionally one Excel workbook with the same tables).</summary>
public static class ReportWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> Write(IReadOnlyList<MediaItem> items, RunSummary summary,
                                              string folder, bool excel, IReadOnlyList<MediaItem>? personCopies = null)
    {
        Directory.CreateDirectory(folder);
        var tables = BuildTables(items, summary, personCopies);
        var files = new List<string>();

        foreach (var t in tables)
        {
            var path = Path.Combine(folder, t.Name + ".csv");
            WriteCsv(path, t);
            files.Add(path);
        }

        if (excel)
        {
            try
            {
                var xlsx = Path.Combine(folder, "Media_Organizer_Reports.xlsx");
                WriteExcel(xlsx, tables);
                files.Add(xlsx);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Excel report could not be written; CSV reports are complete.", ex);
            }
        }
        return files;
    }

    public static List<ReportTable> BuildTables(IReadOnlyList<MediaItem> items, RunSummary summary,
                                                IReadOnlyList<MediaItem>? personCopies = null)
    {
        var tables = new List<ReportTable>();

        // 1. Main organization report (fields required by the SRS first, extras after).
        tables.Add(new ReportTable("Organization_Report",
            new[]
            {
                "Original Filename", "Source Path", "Destination Path", "Media Category", "Capture Date",
                "Location", "GPS Coordinates", "Device Information", "Resolution", "File Size",
                "Duplicate Status", "Processing Status",
                "Date Source", "Event", "Format", "Duration (s)", "Frame Rate", "Codec", "Error / Notes"
            },
            items.OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase).Select(i => new[]
            {
                i.FileName, i.SourcePath, i.DestinationPath, i.Category.ToString(), i.CaptureDateText,
                i.HasGps ? i.Location : PathPlanner.UnknownLocation, Gps(i), i.Device, i.Resolution,
                i.SizeBytes.ToString(Inv),
                i.IsDuplicate ? $"Yes - duplicate group {i.DuplicateGroup}" : i.DuplicateGroup > 0 ? $"No - original of group {i.DuplicateGroup}" : "No",
                i.Status == ItemStatus.Verified ? "Success" : i.Status == ItemStatus.Failed ? "Failed" : "Not processed",
                i.DateSourceText, i.EventName ?? "", i.Format,
                i.DurationSeconds?.ToString("0.##", Inv) ?? "", i.FrameRate?.ToString("0.###", Inv) ?? "", i.Codec ?? "",
                Join(i.Error, i.MetadataNote)
            }).ToList()));

        // 2. Duplicate report.
        tables.Add(new ReportTable("Duplicate_Report",
            new[] { "Duplicate Group", "Role", "Original Filename", "Source Path", "Destination Path", "Duplicate Of (source path)", "File Size", "SHA-256" },
            items.Where(i => i.DuplicateGroup > 0)
                 .OrderBy(i => i.DuplicateGroup).ThenBy(i => i.IsDuplicate).ThenBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                 .Select(i => new[]
                 {
                     i.DuplicateGroup.ToString(Inv), i.IsDuplicate ? "Duplicate" : "Original", i.FileName, i.SourcePath,
                     i.DestinationPath, i.DuplicateOfPath ?? "", i.SizeBytes.ToString(Inv), i.Sha256 ?? ""
                 }).ToList()));

        // 3. Missing metadata report.
        tables.Add(new ReportTable("Missing_Metadata_Report",
            new[] { "Original Filename", "Source Path", "Media Category", "Missing Fields", "Date Used", "Date Source", "Notes" },
            items.Where(i => i.MissingFieldList().Count > 0 || !string.IsNullOrEmpty(i.MetadataNote))
                 .OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                 .Select(i => new[]
                 {
                     i.FileName, i.SourcePath, i.Category.ToString(), string.Join("; ", i.MissingFieldList()),
                     i.CaptureDateText, i.DateSourceText, i.MetadataNote ?? ""
                 }).ToList()));

        // 4. Failed files report.
        tables.Add(new ReportTable("Failed_Files_Report",
            new[] { "Original Filename", "Source Path", "Intended Destination", "Error" },
            items.Where(i => i.Status == ItemStatus.Failed)
                 .OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                 .Select(i => new[] { i.FileName, i.SourcePath, i.DestinationPath, i.Error ?? "" }).ToList()));

        // 5. Processing summary.
        var rows = new List<string[]>
        {
            new[] { "Source folder", summary.Source },
            new[] { "Destination folder", summary.Destination },
            new[] { "Started", summary.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv) },
            new[] { "Finished", summary.FinishedLocal.ToString("yyyy-MM-dd HH:mm:ss", Inv) },
            new[] { "Run was cancelled", summary.Cancelled ? "Yes" : "No" },
            new[] { "Media files found", summary.TotalMediaFiles.ToString(Inv) },
            new[] { "Non-media files skipped", summary.NonMediaSkipped.ToString(Inv) },
            new[] { "Total size", Fmt.Bytes(summary.TotalBytes) },
            new[] { "Photos", summary.Photos.ToString(Inv) },
            new[] { "Videos", summary.Videos.ToString(Inv) },
            new[] { "Screenshots", summary.Screenshots.ToString(Inv) },
            new[] { "Unclassified", summary.Unclassified.ToString(Inv) },
            new[] { "Exact duplicates (kept, prefixed duplicate_)", summary.Duplicates.ToString(Inv) },
            new[] { "Photos/videos without GPS", summary.WithoutGps.ToString(Inv) },
            new[] { "Files without original capture date", summary.WithoutCaptureDate.ToString(Inv) },
            new[] { "Files with metadata notes", summary.MetadataIssues.ToString(Inv) },
            new[] { "Copied and verified", summary.Verified.ToString(Inv) },
            new[] { "Failed", summary.Failed.ToString(Inv) },
            new[] { "Not processed", summary.Pending.ToString(Inv) },
            new[] { "Original files modified, moved or deleted", "0 (copy-only; sources are opened read-only)" }
        };
        tables.Add(new ReportTable("Processing_Summary", new[] { "Item", "Value" }, rows));

        // 6. Verification report.
        tables.Add(new ReportTable("Verification_Report",
            new[] { "Source Path", "Destination Path", "Source SHA-256", "Copy SHA-256", "Result" },
            items.Where(i => i.Status != ItemStatus.Pending)
                 .OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                 .Select(i => new[]
                 {
                     i.SourcePath, i.DestinationPath, i.Sha256 ?? "", i.DestSha256 ?? "",
                     i.Status == ItemStatus.Verified && i.HashMatch == true ? "MATCH" :
                     i.Status == ItemStatus.Failed ? "FAILED: " + (i.Error ?? "") : "NOT VERIFIED"
                 }).ToList()));

        // 7. People report (only when the optional face recognition feature was used).
        if (items.Any(i => i.FaceScanned || i.FaceState != FaceScanState.NotScanned))
        {
            var copiesBySource = (personCopies ?? Array.Empty<MediaItem>())
                .GroupBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            tables.Add(new ReportTable("People_Report",
                new[]
                {
                    "Original Filename", "Source Path", "Media Category", "Face Category", "Faces Detected",
                    "Recognized People", "Unidentified Faces", "Manually Corrected", "Person Collection Copies", "Copy Status", "Notes"
                },
                items.Where(i => i.FaceState != FaceScanState.NotScanned || i.People.Count > 0)
                     .OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .Select(i =>
                     {
                         copiesBySource.TryGetValue(i.SourcePath, out var cp);
                         cp ??= new List<MediaItem>();
                         return new[]
                         {
                             i.FileName, i.SourcePath, i.Category.ToString(), i.FaceCategoryText, i.FaceCount.ToString(Inv),
                             i.PeopleText, i.UnidentifiedFaces.ToString(Inv),
                             i.AddedPeople.Count + i.RemovedPeople.Count > 0
                                 ? "Yes (added: " + string.Join("; ", i.AddedPeople) + " / removed: " + string.Join("; ", i.RemovedPeople) + ")"
                                 : "No",
                             string.Join(" | ", cp.Select(c => c.DestinationPath)),
                             cp.Count == 0 ? "" : cp.All(c => c.Status == ItemStatus.Verified) ? "Verified"
                                 : cp.Any(c => c.Status == ItemStatus.Failed) ? "Failed" : "Not copied",
                             i.FaceError ?? ""
                         };
                     }).ToList()));
        }

        return tables;
    }

    private static string Gps(MediaItem i) =>
        i.HasGps ? i.Latitude!.Value.ToString("0.000000", Inv) + ", " + i.Longitude!.Value.ToString("0.000000", Inv) : "";

    private static string Join(string? a, string? b) =>
        string.Join(" | ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static void WriteCsv(string path, ReportTable t)
    {
        // UTF-8 with BOM so Excel opens non-English place names correctly.
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(",", t.Header.Select(Escape)));
        foreach (var row in t.Rows) w.WriteLine(string.Join(",", row.Select(Escape)));
    }

    public static string Escape(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static void WriteExcel(string path, List<ReportTable> tables)
    {
        using var wb = new XLWorkbook();
        foreach (var t in tables)
        {
            var ws = wb.Worksheets.Add(t.Name.Replace('_', ' ').Substring(0, Math.Min(31, t.Name.Length)));
            for (int c = 0; c < t.Header.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = t.Header[c];
                cell.Style.Font.Bold = true;
            }
            for (int r = 0; r < t.Rows.Count; r++)
                for (int c = 0; c < t.Rows[r].Length; c++)
                    ws.Cell(r + 2, c + 1).Value = t.Rows[r][c];

            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, Math.Min(t.Rows.Count + 1, 200));
        }
        wb.SaveAs(path);
    }
}
