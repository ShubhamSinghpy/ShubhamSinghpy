using System.Text.Json;

namespace MediaOrganizer.Core.Faces;

public sealed class FaceOptions
{
    /// <summary>Minimum detector confidence for something to count as a face.</summary>
    public float DetectThreshold { get; set; } = 0.85f;
    /// <summary>
    /// Minimum cosine similarity to call a face "this registered person". SFace's published value is 0.363; 0.40 is the
    /// default here because different people were seen as high as 0.35 in testing, and a wrong name is worse than a miss.
    /// </summary>
    public float MatchThreshold { get; set; } = 0.40f;
    public int VideoFrames { get; set; } = 8;
    public int MinFacePx { get; set; } = 24;
    public int Threads { get; set; } = 3;
}

public sealed class FaceScanSummary
{
    public int Requested { get; set; }
    public int Scanned { get; set; }
    public int FromCache { get; set; }
    public int Failed { get; set; }
    public int NotScannable { get; set; }
    public int WithPeople { get; set; }
    public int WithoutPeople { get; set; }
    public int Recognized { get; set; }
    public int UnidentifiedOnly { get; set; }
    public bool Cancelled { get; set; }
    public string? Note { get; set; }
}

/// <summary>One face stored in the local cache. Only the fingerprint and position are kept, never the picture.</summary>
public sealed class StoredFace
{
    public int Frame { get; set; }
    public float Score { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public string Emb { get; set; } = "";
}

public sealed class StoredScan
{
    public List<StoredFace> Faces { get; set; } = new();
}

/// <summary>
/// Optional face scan. Runs only when called, only on the files it is given, and writes nothing except the local
/// cache of face fingerprints. Source files are opened read-only.
/// </summary>
public sealed class FaceScanner
{
    private readonly FaceEngine _engine;
    private readonly FrameExtractor? _frames;
    private readonly AppCache? _cache;

    public FaceScanner(FaceEngine engine, FrameExtractor? frames, AppCache? cache)
    {
        _engine = engine;
        _frames = frames;
        _cache = cache;
    }

    /// <summary>Cache key for stored scan results: changes when the models or detection settings change.</summary>
    public static string CacheKey(MediaItem it, FaceOptions o) => Opts(it, o);

    private static string Opts(MediaItem it, FaceOptions o) =>
        it.Category == MediaCategory.Video
            ? $"{FaceModels.Version}|video{o.VideoFrames}|d{o.DetectThreshold:0.00}|m{o.MinFacePx}"
            : $"{FaceModels.Version}|image|d{o.DetectThreshold:0.00}|m{o.MinFacePx}";

    public async Task<FaceScanSummary> ScanAsync(IReadOnlyList<MediaItem> items, PersonRegistry registry, FaceOptions o,
                                                 IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var summary = new FaceScanSummary { Requested = items.Count };
        int done = 0, fromCache = 0, scanned = 0, failed = 0, notScannable = 0;

        try
        {
            await Parallel.ForEachAsync(items,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Threads), CancellationToken = ct },
                (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var outcome = ScanOne(item, registry, o, token);
                    switch (outcome)
                    {
                        case ScanOutcome.Cached: Interlocked.Increment(ref fromCache); break;
                        case ScanOutcome.Scanned: Interlocked.Increment(ref scanned); break;
                        case ScanOutcome.Failed: Interlocked.Increment(ref failed); break;
                        case ScanOutcome.NotScannable: Interlocked.Increment(ref notScannable); break;
                    }
                    int d = Interlocked.Increment(ref done);
                    if (d % 5 == 0 || d == items.Count)
                        progress?.Report(new ProgressInfo
                        {
                            Stage = "Scanning faces",
                            Done = d, Total = items.Count,
                            Message = $"{d:N0} of {items.Count:N0} files"
                        });
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }

        summary.Scanned = scanned;
        summary.FromCache = fromCache;
        summary.Failed = failed;
        summary.NotScannable = notScannable;
        Tally(items, summary);
        return summary;
    }

    public static void Tally(IEnumerable<MediaItem> items, FaceScanSummary s)
    {
        foreach (var i in items)
        {
            if (!i.FaceScanned) continue;
            if (i.HasPeople) s.WithPeople++; else s.WithoutPeople++;
            if (i.HasRecognizedPeople) s.Recognized++;
            else if (i.HasUnidentifiedPeople) s.UnidentifiedOnly++;
        }
    }

    private enum ScanOutcome { Cached, Scanned, Failed, NotScannable }

    private ScanOutcome ScanOne(MediaItem item, PersonRegistry registry, FaceOptions o, CancellationToken ct)
    {
        if (item.Category == MediaCategory.Unclassified)
        {
            item.FaceState = FaceScanState.Unsupported;
            item.FaceError = "Unclassified files are not scanned.";
            item.NotifyFaceChanged();
            return ScanOutcome.NotScannable;
        }

        FileInfo fi;
        try
        {
            fi = new FileInfo(item.SourcePath);
            if (!fi.Exists) throw new FileNotFoundException("Source file no longer exists.");
        }
        catch (Exception ex)
        {
            Fail(item, ex.Message);
            return ScanOutcome.Failed;
        }

        long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks;
        var opts = Opts(item, o);
        bool cached = false;
        StoredScan? scan = null;

        var json = _cache?.GetFaceScan(item.SourcePath, size, mtime, opts);
        if (json != null)
        {
            try { scan = JsonSerializer.Deserialize<StoredScan>(json); cached = scan != null; }
            catch (JsonException) { scan = null; }
        }

        if (scan == null)
        {
            string? error;
            (scan, error) = item.Category == MediaCategory.Video
                ? ScanVideo(item, o, ct)
                : ScanImage(item, o);

            if (scan == null)
            {
                if (error != null && error.StartsWith("NOTSCANNABLE:", StringComparison.Ordinal))
                {
                    item.FaceState = FaceScanState.Unsupported;
                    item.FaceError = error.Substring("NOTSCANNABLE:".Length);
                    item.NotifyFaceChanged();
                    return ScanOutcome.NotScannable;
                }
                Fail(item, error ?? "Could not be scanned.");
                return ScanOutcome.Failed;
            }
            _cache?.PutFaceScan(item.SourcePath, size, mtime, opts, JsonSerializer.Serialize(scan));
        }

        FaceAssigner.Apply(item, scan, registry, o.MatchThreshold);
        FaceCorrections.LoadInto(item, _cache);
        item.NotifyFaceChanged();
        return cached ? ScanOutcome.Cached : ScanOutcome.Scanned;
    }

    private static void Fail(MediaItem item, string message)
    {
        item.FaceState = FaceScanState.Failed;
        item.FaceError = message;
        item.NotifyFaceChanged();
        AppLog.Warn($"Face scan failed for '{item.FileName}': {message}");
    }

    private (StoredScan?, string?) ScanImage(MediaItem item, FaceOptions o)
    {
        var img = ImageLoader.TryLoad(item.SourcePath, out var error);
        if (img == null) return (null, error);
        try { return (Detect(img, 0, o), null); }
        catch (Exception ex) { return (null, "Face detection failed: " + ex.Message); }
    }

    private (StoredScan?, string?) ScanVideo(MediaItem item, FaceOptions o, CancellationToken ct)
    {
        if (_frames == null || !_frames.IsAvailable)
            return (null, "NOTSCANNABLE:ffmpeg was not found, so videos cannot be scanned. Run tools\\get-tools.ps1.");

        var scan = new StoredScan();
        int decoded = 0, idx = 0;
        foreach (var t in FrameExtractor.SamplePoints(item.DurationSeconds, o.VideoFrames))
        {
            ct.ThrowIfCancellationRequested();
            var frame = _frames.ExtractFrame(item.SourcePath, t, ImageLoader.DefaultMaxSide, ct);
            if (frame != null)
            {
                decoded++;
                try { scan.Faces.AddRange(Detect(frame, idx, o).Faces); }
                catch (Exception ex) { return (null, "Face detection failed: " + ex.Message); }
            }
            idx++;
        }
        return decoded == 0 ? (null, "No frames could be read from this video.") : (scan, null);
    }

    private StoredScan Detect(RgbImage img, int frameIndex, FaceOptions o)
    {
        var scan = new StoredScan();
        foreach (var f in _engine.DetectAndEmbed(img, o.DetectThreshold, o.MinFacePx))
            scan.Faces.Add(new StoredFace
            {
                Frame = frameIndex, Score = f.Score, X = f.X, Y = f.Y, W = f.W, H = f.H,
                Emb = FaceGeometry.ToBase64(f.Embedding!)
            });
        return scan;
    }
}

/// <summary>Turns stored face fingerprints into "who is in this file", using the registered people.</summary>
public static class FaceAssigner
{
    public static void Apply(MediaItem item, StoredScan scan, PersonRegistry registry, float threshold)
    {
        item.AutoPeople.Clear();
        item.FaceError = null;
        item.FaceState = FaceScanState.Scanned;

        int maxFaces = 0, maxUnmatched = 0;
        var names = new List<string>();

        foreach (var frame in scan.Faces.GroupBy(f => f.Frame))
        {
            var faces = frame.ToList();
            maxFaces = Math.Max(maxFaces, faces.Count);

            // All (face, person) pairs above the threshold, best first. Each face and each person is used at most once per frame.
            var pairs = new List<(int Face, PersonProfile Person, float Score)>();
            var vectors = faces.Select(f => FaceGeometry.FromBase64(f.Emb)).ToList();
            for (int i = 0; i < vectors.Count; i++)
                foreach (var p in registry.People)
                {
                    var s = registry.Score(p, vectors[i]);
                    if (s >= threshold) pairs.Add((i, p, s));
                }

            var usedFaces = new HashSet<int>();
            var usedPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in pairs.OrderByDescending(x => x.Score))
            {
                if (usedFaces.Contains(pr.Face) || usedPeople.Contains(pr.Person.Name)) continue;
                usedFaces.Add(pr.Face);
                usedPeople.Add(pr.Person.Name);
                if (!names.Contains(pr.Person.Name, StringComparer.OrdinalIgnoreCase)) names.Add(pr.Person.Name);
            }
            maxUnmatched = Math.Max(maxUnmatched, faces.Count - usedFaces.Count);
        }

        item.FaceCount = maxFaces;
        item.AutoUnidentified = maxUnmatched;
        item.AutoPeople.AddRange(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Manual review: add a person to a file or remove a wrong match. Saved locally and kept across runs.</summary>
public static class FaceCorrections
{
    public static void LoadInto(MediaItem item, AppCache? cache)
    {
        item.AddedPeople.Clear();
        item.RemovedPeople.Clear();
        if (cache == null) return;
        try
        {
            var fi = new FileInfo(item.SourcePath);
            if (!fi.Exists) return;
            var ov = cache.GetFaceOverride(item.SourcePath, fi.Length, fi.LastWriteTimeUtc.Ticks);
            if (ov == null) return;
            foreach (var n in Split(ov.Value.Added)) item.AddedPeople.Add(n);
            foreach (var n in Split(ov.Value.Removed)) item.RemovedPeople.Add(n);
        }
        catch (Exception ex) { AppLog.Warn("Could not load manual face corrections.", ex); }
    }

    /// <summary>Says this person is in the file (also undoes an earlier removal).</summary>
    public static void AddPerson(MediaItem item, string name, AppCache? cache)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        item.RemovedPeople.Remove(name);
        if (!item.AutoPeople.Contains(name, StringComparer.OrdinalIgnoreCase)) item.AddedPeople.Add(name);
        Save(item, cache);
    }

    /// <summary>Says this person is NOT in the file (rejects an automatic match or undoes a manual addition).</summary>
    public static void RemovePerson(MediaItem item, string name, AppCache? cache)
    {
        name = name.Trim();
        item.AddedPeople.Remove(name);
        if (item.AutoPeople.Contains(name, StringComparer.OrdinalIgnoreCase)) item.RemovedPeople.Add(name);
        Save(item, cache);
    }

    /// <summary>Drops a deleted person from a file's results (and its corrections).</summary>
    public static void ForgetPerson(MediaItem item, string name, AppCache? cache)
    {
        item.AutoPeople.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        item.AddedPeople.Remove(name);
        item.RemovedPeople.Remove(name);
        Save(item, cache);
    }

    private static void Save(MediaItem item, AppCache? cache)
    {
        item.NotifyFaceChanged();
        if (cache == null) return;
        try
        {
            var fi = new FileInfo(item.SourcePath);
            if (!fi.Exists) return;
            cache.PutFaceOverride(item.SourcePath, fi.Length, fi.LastWriteTimeUtc.Ticks,
                string.Join(";", item.AddedPeople), string.Join(";", item.RemovedPeople));
        }
        catch (Exception ex) { AppLog.Warn("Could not save manual face corrections.", ex); }
    }

    private static IEnumerable<string> Split(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Plans the person-wise collections: People\&lt;Name&gt;\&lt;Photos|Videos|Screenshots&gt;\file. These are extra,
/// verified copies made from the original files (copy-only, like everything else in the app).
/// </summary>
public static class PersonCollections
{
    public const string RootName = "People";
    public const string UnidentifiedFolder = "_Unidentified_People";

    public static List<MediaItem> Plan(IEnumerable<MediaItem> items, OrganizerSettings s, IReadOnlyList<MediaItem>? previous = null)
    {
        var root = Path.Combine(PathPlanner.OutputRoot(s.DestinationFolder), RootName);
        var copies = new List<MediaItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var old = previous?.ToDictionary(p => p.JournalKey, p => p, StringComparer.OrdinalIgnoreCase)
                  ?? new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in items.OrderBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            // Exact duplicates already live in Duplicates\; their original provides the person copy.
            if (it.IsDuplicate || it.Category == MediaCategory.Unclassified) continue;

            var folders = it.People.ToList();
            if (s.CollectUnidentifiedPeople && it.HasUnidentifiedPeople && folders.Count == 0)
                folders.Add(UnidentifiedFolder);

            foreach (var person in folders)
            {
                var dir = Path.Combine(root, PathPlanner.SanitizeSegment(person), PathPlanner.CategoryFolder(it.Category));
                var name = it.FileName;
                var candidate = Path.Combine(dir, name);
                int n = 0;
                while (!used.Add(candidate))
                {
                    n++;
                    candidate = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "_" + n + Path.GetExtension(name));
                }

                var key = "person:" + person + "|" + it.SourcePath;
                var copy = new MediaItem
                {
                    SourcePath = it.SourcePath,
                    SizeBytes = it.SizeBytes,
                    FsCreated = it.FsCreated,
                    FsModified = it.FsModified,
                    Kind = it.Kind,
                    Category = it.Category,
                    CaptureDate = it.CaptureDate,
                    DateSource = it.DateSource,
                    Sha256 = it.Sha256,
                    JournalKeyOverride = key,
                    DestinationPath = candidate
                };
                copy.AutoPeople.Add(person);

                // Carry over "already copied and verified" so a re-plan after a correction does not redo finished work.
                if (old.TryGetValue(key, out var prev) && prev.Status == ItemStatus.Verified &&
                    string.Equals(prev.DestinationPath, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    copy.Status = ItemStatus.Verified;
                    copy.DestSha256 = prev.DestSha256;
                    copy.HashMatch = prev.HashMatch;
                }
                copies.Add(copy);
            }
        }
        return copies;
    }

    /// <summary>Person name a collection copy belongs to (or the unidentified bucket).</summary>
    public static string PersonOf(MediaItem copy) => copy.AutoPeople.FirstOrDefault() ?? "";
}
