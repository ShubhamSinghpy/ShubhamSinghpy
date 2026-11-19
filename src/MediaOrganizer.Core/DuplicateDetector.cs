namespace MediaOrganizer.Core;

/// <summary>
/// Finds exact duplicates by SHA-256 of file CONTENT (never by name or size alone). Files are only hashed
/// when another file has the same size, which keeps large libraries fast. Duplicates are never deleted.
/// Visually similar (but not byte-identical) media is never treated as a duplicate.
/// </summary>
public static class DuplicateDetector
{
    public static void Detect(IReadOnlyList<MediaItem> items, AppCache? cache, int degreeOfParallelism,
                              IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        foreach (var it in items) { it.IsDuplicate = false; it.DuplicateGroup = 0; it.DuplicateOfPath = null; }

        var candidates = items.Where(i => i.SizeBytes > 0)
                              .GroupBy(i => i.SizeBytes)
                              .Where(g => g.Count() > 1)
                              .SelectMany(g => g)
                              .ToList();

        int done = 0;
        Parallel.ForEach(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degreeOfParallelism), CancellationToken = ct },
            it =>
            {
                try
                {
                    long mtime = File.GetLastWriteTimeUtc(it.SourcePath).Ticks;
                    var hash = cache?.GetHash(it.SourcePath, it.SizeBytes, mtime);
                    if (hash == null)
                    {
                        hash = FileHasher.Sha256(it.SourcePath, ct);
                        cache?.PutHash(it.SourcePath, it.SizeBytes, mtime, hash);
                    }
                    it.Sha256 = hash;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    it.AddNote("Could not hash file: " + ex.Message);
                    AppLog.Warn($"Hashing failed for '{it.SourcePath}'.", ex);
                }

                int d = Interlocked.Increment(ref done);
                if (d % 10 == 0 || d == candidates.Count)
                    progress?.Report(new ProgressInfo
                    {
                        Stage = "Checking for duplicates",
                        Done = d,
                        Total = candidates.Count,
                        Message = $"{d:N0} of {candidates.Count:N0} candidate files hashed"
                    });
            });

        int groupId = 0;
        var groups = candidates.Where(i => i.Sha256 != null)
                               .GroupBy(i => i.Sha256!)
                               .Where(g => g.Count() > 1)
                               .OrderBy(g => g.Min(i => i.SourcePath), StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            groupId++;
            // The "original" is the file with a trustworthy capture date, then the oldest file, then path order.
            var ordered = g.OrderBy(i => i.DateSource == DateSource.Original ? 0 : 1)
                           .ThenBy(i => i.FsModified)
                           .ThenBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                           .ToList();
            var original = ordered[0];
            original.DuplicateGroup = groupId;

            foreach (var dup in ordered.Skip(1))
            {
                dup.IsDuplicate = true;
                dup.DuplicateGroup = groupId;
                dup.DuplicateOfPath = original.SourcePath;
            }
        }
    }
}
