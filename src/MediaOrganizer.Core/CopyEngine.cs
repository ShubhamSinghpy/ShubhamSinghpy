using System.Security.Cryptography;

namespace MediaOrganizer.Core;

/// <summary>Pause / resume gate shared between the UI and the copy loop.</summary>
public sealed class PauseControl
{
    private readonly object _lock = new();
    private TaskCompletionSource? _gate;

    public bool IsPaused { get { lock (_lock) return _gate != null; } }

    public void Pause()
    {
        lock (_lock)
        {
            _gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? g;
        lock (_lock)
        {
            g = _gate;
            _gate = null;
        }
        g?.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task? t;
        lock (_lock) t = _gate?.Task;
        return t == null ? Task.CompletedTask : t.WaitAsync(ct);
    }
}

public sealed class DiskFullException : IOException
{
    public DiskFullException(string message) : base(message) { }
}

/// <summary>
/// Copies files into the organized structure. COPY ONLY: sources are opened read-only and are never
/// moved, renamed, modified or deleted. Every copy is written to a temporary ".mo-part" file, verified
/// by SHA-256, and only then renamed to its final name, so an interrupted run can never leave a
/// half-written file that looks complete. Existing files are never overwritten.
/// </summary>
public sealed class CopyEngine
{
    public const string PartExt = ".mo-part";
    private const int BufferSize = 1 << 20;

    public async Task RunAsync(IReadOnlyList<MediaItem> items, Journal journal,
                               IProgress<ProgressInfo>? progress, PauseControl pause, CancellationToken ct)
    {
        var todo = items.Where(i => i.Status != ItemStatus.Verified).ToList();
        long totalBytes = todo.Sum(i => i.SizeBytes);
        long doneBytes = 0;
        int done = 0, failed = 0;

        foreach (var it in todo)
        {
            ct.ThrowIfCancellationRequested();
            await pause.WaitAsync(ct).ConfigureAwait(false);

            progress?.Report(new ProgressInfo
            {
                Stage = pause.IsPaused ? "Paused" : "Copying and verifying",
                Done = done, Total = todo.Count, BytesDone = doneBytes, BytesTotal = totalBytes, Failed = failed,
                Message = it.FileName
            });

            try
            {
                await CopyOneAsync(it, journal, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DiskFullException ex)
            {
                it.Status = ItemStatus.Failed;
                it.Error = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                it.Status = ItemStatus.Failed;
                it.Error = Describe(ex);
                failed++;
                AppLog.Warn($"Copy failed for '{it.SourcePath}'.", ex);
            }

            done++;
            doneBytes += it.SizeBytes;
        }

        progress?.Report(new ProgressInfo
        {
            Stage = "Finished copying",
            Done = done, Total = todo.Count, BytesDone = doneBytes, BytesTotal = totalBytes, Failed = failed,
            Message = failed == 0 ? "All files copied and verified" : $"{failed:N0} file(s) failed"
        });
    }

    private async Task CopyOneAsync(MediaItem it, Journal journal, CancellationToken ct)
    {
        var srcInfo = new FileInfo(it.SourcePath);
        if (!srcInfo.Exists) throw new FileNotFoundException("Source file no longer exists.");

        long length = srcInfo.Length;
        long mtime = srcInfo.LastWriteTimeUtc.Ticks;

        // ---- resume: already copied and verified in an earlier (interrupted) run? -------------
        var rec = journal.Find(it.SourcePath, length, mtime);
        if (rec != null && File.Exists(rec.DestPath) && new FileInfo(rec.DestPath).Length == length)
        {
            MarkVerified(it, rec.DestPath, rec.Sha256, rec.Sha256);
            return;
        }

        var target = it.DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // ---- something already at the target? never overwrite ---------------------------------
        if (File.Exists(target))
        {
            if (new FileInfo(target).Length == length)
            {
                var srcHash = it.Sha256 ?? await FileHasher.Sha256Async(it.SourcePath, ct).ConfigureAwait(false);
                var existingHash = await FileHasher.Sha256Async(target, ct).ConfigureAwait(false);
                if (srcHash == existingHash)
                {
                    // Identical content is already there (e.g. app was closed right after the final rename).
                    MarkVerified(it, target, srcHash, existingHash);
                    journal.Record(new CopyRecord(it.SourcePath, length, mtime, target, existingHash));
                    return;
                }
            }
            target = UniquePath(target);
        }

        var tmp = target + PartExt;
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp); // our own leftover from a crashed run

            string sourceHash;
            using (var src = new FileStream(it.SourcePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.Asynchronous))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
                dst.Flush(true); // force data to disk before verification
                sourceHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            // Preserve original timestamps wherever technically possible.
            try
            {
                File.SetCreationTimeUtc(tmp, srcInfo.CreationTimeUtc);
                File.SetLastWriteTimeUtc(tmp, srcInfo.LastWriteTimeUtc);
                File.SetLastAccessTimeUtc(tmp, srcInfo.LastAccessTimeUtc);
            }
            catch (Exception ex) { AppLog.Warn($"Could not preserve timestamps for '{it.FileName}'.", ex); }

            // ---- verify: size + SHA-256 of what is actually on disk -----------------------------
            if (new FileInfo(tmp).Length != length)
                throw new IOException("Size of the copy does not match the original.");

            var destHash = await FileHasher.Sha256Async(tmp, ct).ConfigureAwait(false);
            if (!string.Equals(sourceHash, destHash, StringComparison.Ordinal))
                throw new IOException("SHA-256 of the copy does not match the original.");

            srcInfo.Refresh();
            if (srcInfo.Length != length || srcInfo.LastWriteTimeUtc.Ticks != mtime)
                throw new IOException("The source file changed while it was being copied.");

            File.Move(tmp, target, overwrite: false);

            it.Sha256 = sourceHash;
            MarkVerified(it, target, sourceHash, destHash);
            journal.Record(new CopyRecord(it.SourcePath, length, mtime, target, sourceHash));
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            TryDelete(tmp);
            throw new DiskFullException("The destination drive is out of space.");
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void MarkVerified(MediaItem it, string destPath, string sourceHash, string destHash)
    {
        it.DestinationPath = destPath;
        it.Sha256 ??= sourceHash;
        it.DestSha256 = destHash;
        it.HashMatch = string.Equals(sourceHash, destHash, StringComparison.Ordinal);
        it.Error = null;
        it.Status = ItemStatus.Verified;
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name}_{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort: only our own temp file */ }
    }

    private static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 112 or 39 || ex.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Permission denied (file or folder is protected or in use).",
        FileNotFoundException => ex.Message,
        DirectoryNotFoundException => "Folder not found: " + ex.Message,
        PathTooLongException => "Path is too long for Windows.",
        _ => ex.Message
    };
}
