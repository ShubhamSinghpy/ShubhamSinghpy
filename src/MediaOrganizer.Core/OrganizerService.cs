using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.Core;

public sealed class AnalysisResult
{
    public OrganizerSettings Settings { get; init; } = new();
    public List<MediaItem> Items { get; init; } = new();
    public int NonMediaSkipped { get; init; }
    public bool VideoToolMissing { get; init; }
    public bool OnlineLocationUnavailable { get; init; }
    public int OnlineLookups { get; init; }
    public DateTime StartedLocal { get; init; } = DateTime.Now;

    /// <summary>Extra verified copies for the optional person-wise collections (empty unless face recognition was used).</summary>
    public List<MediaItem> PersonCopies { get; set; } = new();
}

public sealed class RunResult
{
    public int Verified { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int PersonVerified { get; set; }
    public int PersonFailed { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public string? ReportFolder { get; set; }
    public IReadOnlyList<string> ReportFiles { get; set; } = Array.Empty<string>();
}

/// <summary>Orchestrates analysis (preview) and execution (copy + verify + reports).</summary>
public sealed class OrganizerService
{
    /// <summary>Scans, reads metadata, resolves locations, finds duplicates and plans destinations. Copies nothing.</summary>
    public async Task<AnalysisResult> AnalyzeAsync(OrganizerSettings s, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Validate(s);
        var started = DateTime.Now;
        AppLog.Info("Analysis started.");

        var scan = FileScanner.Enumerate(s, ct, count => progress?.Report(new ProgressInfo
        {
            Stage = "Scanning folders",
            Message = $"{count:N0} files found so far",
            Indeterminate = true
        }));

        var items = scan.Items;
        var meta = new MetadataService();
        int total = items.Count, done = 0;

        await Parallel.ForEachAsync(items,
            new ParallelOptions { MaxDegreeOfParallelism = s.EffectiveThreads, CancellationToken = ct },
            (item, _) =>
            {
                meta.Populate(item);
                int d = Interlocked.Increment(ref done);
                if (d % 25 == 0 || d == total)
                    progress?.Report(new ProgressInfo
                    {
                        Stage = "Reading metadata",
                        Done = d, Total = total,
                        Message = $"{d:N0} of {total:N0} files"
                    });
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        using var cache = AppCache.TryOpenDefault();

        var resolver = new LocationResolver(cache, s.OnlineGeocoding);
        await resolver.ResolveAsync(items, progress, ct).ConfigureAwait(false);

        DuplicateDetector.Detect(items, cache, Math.Min(4, s.EffectiveThreads), progress, ct);
        EventGrouper.Assign(items, s);
        PathPlanner.Plan(items, s);

        AppLog.Info($"Analysis finished: {items.Count} media files, {items.Count(i => i.IsDuplicate)} duplicates.");

        return new AnalysisResult
        {
            Settings = s,
            Items = items,
            NonMediaSkipped = scan.NonMediaSkipped,
            VideoToolMissing = meta.VideoToolMissing && items.Any(i => i.Kind == ExtKind.Video),
            OnlineLocationUnavailable = resolver.OnlineUnavailable,
            OnlineLookups = resolver.OnlineLookups,
            StartedLocal = started
        };
    }

    /// <summary>
    /// OPTIONAL face scan, run only when the user asks for it, on the files they chose. Never part of Analyze.
    /// Everything stays on this PC; the originals are only read.
    /// </summary>
    public async Task<FaceScanSummary> ScanFacesAsync(AnalysisResult analysis, IReadOnlyList<MediaItem> targets,
                                                      FaceOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct,
                                                      FaceEngine? engineOverride = null, PersonRegistry? registryOverride = null)
    {
        var engine = engineOverride ?? FaceEngine.TryCreate(out var error)
            ?? throw new InvalidOperationException(error);
        try
        {
            var registry = registryOverride ?? PersonRegistry.Load();
            using var cache = AppCache.TryOpenDefault();
            var scanner = new FaceScanner(engine, new FrameExtractor(), cache);

            AppLog.Info($"Face scan started on {targets.Count} files (feature enabled by the user).");
            var summary = await scanner.ScanAsync(targets, registry, options, progress, ct).ConfigureAwait(false);

            // Exact duplicates share their original's content, so they share its result too.
            var byPath = analysis.Items.ToDictionary(i => i.SourcePath, StringComparer.OrdinalIgnoreCase);
            foreach (var dup in analysis.Items.Where(i => i.IsDuplicate && i.DuplicateOfPath != null))
            {
                if (byPath.TryGetValue(dup.DuplicateOfPath!, out var orig) && orig.FaceState != FaceScanState.NotScanned)
                    CopyFaceResult(orig, dup);
            }

            RebuildPersonCopies(analysis);
            AppLog.Info($"Face scan finished: {summary.WithPeople} with people, {summary.WithoutPeople} without, {summary.Failed} failed.");
            return summary;
        }
        finally
        {
            if (engineOverride == null) engine.Dispose();
        }
    }

    private static void CopyFaceResult(MediaItem from, MediaItem to)
    {
        to.FaceState = from.FaceState;
        to.FaceCount = from.FaceCount;
        to.AutoUnidentified = from.AutoUnidentified;
        to.FaceError = from.FaceError;
        to.AutoPeople.Clear();
        to.AutoPeople.AddRange(from.AutoPeople);
        to.AddedPeople.Clear();
        foreach (var p in from.AddedPeople) to.AddedPeople.Add(p);
        to.RemovedPeople.Clear();
        foreach (var p in from.RemovedPeople) to.RemovedPeople.Add(p);
        to.NotifyFaceChanged();
    }

    /// <summary>Recomputes which files belong in which person collection (after a scan or a manual correction).</summary>
    public void RebuildPersonCopies(AnalysisResult analysis)
    {
        analysis.PersonCopies = analysis.Settings.CreatePersonFolders
            ? PersonCollections.Plan(analysis.Items, analysis.Settings, analysis.PersonCopies)
            : new List<MediaItem>();
    }

    /// <summary>
    /// Copies every file that is not yet verified. Safe to call again after Cancel, a crash or failures:
    /// files already copied and verified are recognised through the journal and never copied twice.
    /// </summary>
    public async Task<RunResult> ExecuteAsync(AnalysisResult analysis, IProgress<ProgressInfo>? progress,
                                              PauseControl pause, CancellationToken ct)
    {
        var s = analysis.Settings;
        var items = analysis.Items;
        var result = new RunResult();
        var started = DateTime.Now;

        Directory.CreateDirectory(s.DestinationFolder);
        var root = PathPlanner.OutputRoot(s.DestinationFolder);
        Directory.CreateDirectory(root);

        var personCopies = analysis.PersonCopies;
        CheckFreeSpace(items.Concat(personCopies), s.DestinationFolder);
        CleanupPartFiles(root);

        using (var journal = new Journal(Path.Combine(s.DestinationFolder, ".media_organizer", "journal.db")))
        {
            try
            {
                var engine = new CopyEngine();
                await engine.RunAsync(items, journal, progress, pause, ct).ConfigureAwait(false);

                // Optional person-wise collections (only exists when the user used face recognition).
                if (personCopies.Count > 0)
                    await engine.RunAsync(personCopies, journal, progress, pause, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                AppLog.Info("Run cancelled by user.");
            }
            catch (DiskFullException ex)
            {
                result.Error = ex.Message + " Free some space and press Start / Resume to continue.";
                AppLog.Error("Disk full.", ex);
            }
        }

        CleanupPartFiles(root);

        result.Verified = items.Count(i => i.Status == ItemStatus.Verified);
        result.Failed = items.Count(i => i.Status == ItemStatus.Failed);
        result.Pending = items.Count(i => i.Status == ItemStatus.Pending);
        result.PersonVerified = personCopies.Count(i => i.Status == ItemStatus.Verified);
        result.PersonFailed = personCopies.Count(i => i.Status == ItemStatus.Failed);

        // Reports are always written, even for a cancelled or partly failed run.
        try
        {
            var summary = RunSummary.Build(items, s.SourceFolder, s.DestinationFolder, analysis.NonMediaSkipped);
            summary.StartedLocal = started;
            summary.FinishedLocal = DateTime.Now;
            summary.Cancelled = result.Cancelled;

            var folder = Path.Combine(root, "Reports", "Run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            result.ReportFiles = ReportWriter.Write(items, summary, folder, s.ExportExcel, personCopies);
            result.ReportFolder = folder;
        }
        catch (Exception ex)
        {
            AppLog.Error("Reports could not be written.", ex);
            result.Error = (result.Error == null ? "" : result.Error + " ") + "Reports could not be written: " + ex.Message;
        }

        return result;
    }

    private static void Validate(OrganizerSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.SourceFolder) || !Directory.Exists(s.SourceFolder))
            throw new InvalidOperationException("Please choose an existing source folder.");
        if (string.IsNullOrWhiteSpace(s.DestinationFolder))
            throw new InvalidOperationException("Please choose a destination folder.");

        var src = FileScanner.NormalizeDir(s.SourceFolder);
        var dst = FileScanner.NormalizeDir(s.DestinationFolder);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The destination folder must be different from the source folder.");
    }

    private static void CheckFreeSpace(IEnumerable<MediaItem> items, string destination)
    {
        long needed = items.Where(i => i.Status != ItemStatus.Verified).Sum(i => i.SizeBytes);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            const long margin = 200L * 1024 * 1024;
            if (free < needed + margin)
                throw new InvalidOperationException(
                    $"Not enough free space on the destination drive. Needed about {Fmt.Bytes(needed + margin)}, available {Fmt.Bytes(free)}.");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("Free space could not be determined (e.g. network path); continuing.", ex);
        }
    }

    /// <summary>Removes only this app's own unfinished temporary files (*.mo-part) from the output folder.</summary>
    private static void CleanupPartFiles(string root)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*" + CopyEngine.PartExt, SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }
}
