using MediaOrganizer.Core;

namespace MediaOrganizer.Tests;

/// <summary>Original-file integrity, copy verification, resume and report tests (SRS section 5 / 5.1).</summary>
public class CopyEngineTests
{
    private static MediaItem Item(string src, string dest)
    {
        var it = TestUtil.ItemFor(src);
        it.Category = MediaCategory.Photo;
        it.DestinationPath = dest;
        return it;
    }

    [Fact]
    public async Task Copies_verifies_and_leaves_original_untouched()
    {
        using var t = new TempDir();
        var src = t.File("src/a.jpg", "hello media world");
        var before = TestUtil.Hash(src);
        var beforeTime = File.GetLastWriteTimeUtc(src);
        var dest = Path.Combine(t.Path, "out", "Photos", "a.jpg");

        var it = Item(src, dest);
        using var journal = new Journal(Path.Combine(t.Path, "journal.db"));
        await new CopyEngine().RunAsync(new[] { it }, journal, null, new PauseControl(), CancellationToken.None);

        Assert.Equal(ItemStatus.Verified, it.Status);
        Assert.True(it.HashMatch);
        Assert.Equal(before, TestUtil.Hash(dest));                       // copy == original
        Assert.Equal(before, TestUtil.Hash(src));                        // original unchanged
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(src));
        Assert.Equal(new FileInfo(src).Length, new FileInfo(dest).Length);
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(dest));        // timestamp preserved
        Assert.Empty(Directory.GetFiles(t.Path, "*" + CopyEngine.PartExt, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_different_file()
    {
        using var t = new TempDir();
        var src = t.File("src/a.jpg", "NEW CONTENT");
        var dest = t.File("out/a.jpg", "OLD CONTENT THAT MUST SURVIVE");

        var it = Item(src, dest);
        using var journal = new Journal(Path.Combine(t.Path, "journal.db"));
        await new CopyEngine().RunAsync(new[] { it }, journal, null, new PauseControl(), CancellationToken.None);

        Assert.Equal("OLD CONTENT THAT MUST SURVIVE", File.ReadAllText(dest));
        Assert.EndsWith("a_1.jpg", it.DestinationPath);
        Assert.Equal("NEW CONTENT", File.ReadAllText(it.DestinationPath));
    }

    [Fact]
    public async Task Resume_does_not_copy_verified_files_again()
    {
        using var t = new TempDir();
        var src = t.File("src/a.jpg", "resume me");
        var dest = Path.Combine(t.Path, "out", "a.jpg");
        using var journal = new Journal(Path.Combine(t.Path, "journal.db"));

        var first = Item(src, dest);
        await new CopyEngine().RunAsync(new[] { first }, journal, null, new PauseControl(), CancellationToken.None);

        // Simulate a restart: fresh item objects, same plan.
        var second = Item(src, dest);
        await new CopyEngine().RunAsync(new[] { second }, journal, null, new PauseControl(), CancellationToken.None);

        Assert.Equal(ItemStatus.Verified, second.Status);
        Assert.Equal(dest, second.DestinationPath);
        Assert.Single(Directory.GetFiles(Path.Combine(t.Path, "out")));   // no a_1.jpg
    }

    [Fact]
    public async Task Missing_source_is_recorded_as_failed_not_thrown()
    {
        using var t = new TempDir();
        var it = new MediaItem { SourcePath = Path.Combine(t.Path, "gone.jpg"), DestinationPath = Path.Combine(t.Path, "out", "gone.jpg") };
        using var journal = new Journal(Path.Combine(t.Path, "journal.db"));

        await new CopyEngine().RunAsync(new[] { it }, journal, null, new PauseControl(), CancellationToken.None);

        Assert.Equal(ItemStatus.Failed, it.Status);
        Assert.False(string.IsNullOrEmpty(it.Error));
    }

    [Fact]
    public async Task Cancel_stops_cleanly_and_leaves_no_partial_files()
    {
        using var t = new TempDir();
        var src = t.File("src/a.jpg", "cancel me");
        var it = Item(src, Path.Combine(t.Path, "out", "a.jpg"));
        using var journal = new Journal(Path.Combine(t.Path, "journal.db"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CopyEngine().RunAsync(new[] { it }, journal, null, new PauseControl(), cts.Token));

        Assert.True(File.Exists(src));
        Assert.Equal(ItemStatus.Pending, it.Status);
    }

    [Fact]
    public async Task Pause_blocks_until_resumed()
    {
        using var t = new TempDir();
        var src = t.File("src/a.jpg", "pause me");
        var it = Item(src, Path.Combine(t.Path, "out", "a.jpg"));
        using var journal = new Journal(Path.Combine(t.Path, "journal.db"));

        var pause = new PauseControl();
        pause.Pause();
        var run = new CopyEngine().RunAsync(new[] { it }, journal, null, pause, CancellationToken.None);

        await Task.Delay(300);
        Assert.False(run.IsCompleted);
        Assert.Equal(ItemStatus.Pending, it.Status);

        pause.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ItemStatus.Verified, it.Status);
    }
}

public class ReportTests
{
    [Fact]
    public void Csv_escapes_commas_quotes_and_newlines()
    {
        Assert.Equal("plain", ReportWriter.Escape("plain"));
        Assert.Equal("\"a,b\"", ReportWriter.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ReportWriter.Escape("say \"hi\""));
        Assert.Equal("\"line1\nline2\"", ReportWriter.Escape("line1\nline2"));
    }

    [Fact]
    public void Writes_all_required_reports_as_csv_and_excel()
    {
        using var t = new TempDir();
        var a = TestUtil.Plain("/src/a.jpg", MediaCategory.Photo, new DateTime(2024, 7, 14), "Lucknow");
        a.Status = ItemStatus.Verified; a.HashMatch = true; a.Sha256 = "aa"; a.DestSha256 = "aa"; a.DestinationPath = "/out/a.jpg";
        var b = TestUtil.Plain("/src/b.jpg", MediaCategory.Photo, new DateTime(2024, 7, 14));
        b.Status = ItemStatus.Failed; b.Error = "Permission denied";
        var items = new List<MediaItem> { a, b };
        var summary = RunSummary.Build(items, "/src", "/out", 3);

        var files = ReportWriter.Write(items, summary, Path.Combine(t.Path, "reports"), excel: true);

        foreach (var name in new[] { "Organization_Report", "Duplicate_Report", "Missing_Metadata_Report",
                                     "Failed_Files_Report", "Processing_Summary", "Verification_Report" })
            Assert.Contains(files, f => Path.GetFileName(f) == name + ".csv");
        Assert.Contains(files, f => f.EndsWith(".xlsx"));

        var failed = File.ReadAllText(files.First(f => f.EndsWith("Failed_Files_Report.csv")));
        Assert.Contains("Permission denied", failed);
    }
}

/// <summary>End-to-end: analyse real fixture files, copy them, prove the originals are untouched.</summary>
public class EndToEndTests
{
    [Fact]
    public async Task Full_run_organizes_verifies_and_preserves_originals()
    {
        using var t = new TempDir();
        var src = Path.Combine(t.Path, "source");
        Directory.CreateDirectory(src);
        foreach (var f in new[] { "gps_photo.jpg", "nogps_photo.jpg", "IMG_0042.PNG", "corrupt.jpg" })
            File.Copy(TestUtil.Fixture(f), Path.Combine(src, f));
        File.Copy(TestUtil.Fixture("gps_photo.jpg"), Path.Combine(src, "copy_of_gps.jpg"));   // exact duplicate
        File.WriteAllText(Path.Combine(src, "notes.txt"), "not media");

        var originalHashes = Directory.GetFiles(src).ToDictionary(f => f, TestUtil.Hash);

        var settings = new OrganizerSettings
        {
            SourceFolder = src,
            DestinationFolder = Path.Combine(t.Path, "dest"),
            OnlineGeocoding = false,
            GroupEvents = false,
            ExportExcel = false
        };

        var svc = new OrganizerService();
        var analysis = await svc.AnalyzeAsync(settings, null, CancellationToken.None);

        Assert.Equal(5, analysis.Items.Count);           // notes.txt skipped
        Assert.Equal(1, analysis.NonMediaSkipped);
        Assert.Equal(1, analysis.Items.Count(i => i.IsDuplicate));
        Assert.Equal(1, analysis.Items.Count(i => i.Category == MediaCategory.Screenshot));
        Assert.Equal(1, analysis.Items.Count(i => i.Category == MediaCategory.Unclassified));

        var result = await svc.ExecuteAsync(analysis, null, new PauseControl(), CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.Equal(5, result.Verified);
        Assert.Equal(0, result.Failed);
        Assert.NotNull(result.ReportFolder);

        var root = PathPlanner.OutputRoot(settings.DestinationFolder);
        Assert.True(File.Exists(Path.Combine(root, "Photos", "2024", "2024-07", "GPS_26.8N_80.9E", "gps_photo.jpg")) ||
                    File.Exists(Path.Combine(root, "Photos", "2024", "2024-07", "GPS_26.8N_80.9E", "copy_of_gps.jpg")));
        Assert.True(File.Exists(Path.Combine(root, "Photos", "2023", "2023-12", PathPlanner.UnknownLocation, "nogps_photo.jpg")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "Duplicates", "Photos"), "duplicate_*"));
        Assert.True(File.Exists(Path.Combine(root, "Unclassified", "corrupt.jpg")));

        // Original file integrity: nothing deleted, nothing modified.
        foreach (var (path, hash) in originalHashes)
        {
            Assert.True(File.Exists(path));
            Assert.Equal(hash, TestUtil.Hash(path));
        }

        // Every copy matches its source byte for byte.
        foreach (var it in analysis.Items)
            Assert.Equal(TestUtil.Hash(it.SourcePath), TestUtil.Hash(it.DestinationPath));
    }

    [Fact]
    public async Task Rejects_destination_equal_to_source()
    {
        using var t = new TempDir();
        var s = new OrganizerSettings { SourceFolder = t.Path, DestinationFolder = t.Path };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrganizerService().AnalyzeAsync(s, null, CancellationToken.None));
    }
}
