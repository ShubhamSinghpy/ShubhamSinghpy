namespace MediaOrganizer.Core;

public sealed class ScanResult
{
    public List<MediaItem> Items { get; } = new();
    public int NonMediaSkipped { get; set; }
}

/// <summary>Walks the source folder (read-only) and creates a <see cref="MediaItem"/> for every media file.</summary>
public static class FileScanner
{
    public static ScanResult Enumerate(OrganizerSettings s, CancellationToken ct, Action<int>? onCount = null)
    {
        var result = new ScanResult();
        var src = NormalizeDir(s.SourceFolder);
        var dest = string.IsNullOrWhiteSpace(s.DestinationFolder) ? null : NormalizeDir(s.DestinationFolder);

        // If the destination is inside the source we must not re-scan our own output.
        string? skipPrefix = null;
        if (dest != null && dest.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            skipPrefix = dest + Path.DirectorySeparatorChar;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,          // permission problems on a sub-folder must not abort the scan
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System   // skips $RECYCLE.BIN, System Volume Information
        };

        int seen = 0;
        foreach (var fi in new DirectoryInfo(src).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            if (seen % 500 == 0) onCount?.Invoke(seen);

            if (skipPrefix != null && fi.FullName.StartsWith(skipPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (fi.Extension.Equals(CopyEngine.PartExt, StringComparison.OrdinalIgnoreCase)) continue;

            var kind = FileTypes.Kind(fi.Extension);
            if (kind == ExtKind.Other && !s.IncludeNonMediaFiles)
            {
                result.NonMediaSkipped++;
                continue;
            }

            result.Items.Add(new MediaItem
            {
                SourcePath = fi.FullName,
                SizeBytes = fi.Length,
                FsCreated = fi.CreationTime,
                FsModified = fi.LastWriteTime,
                Kind = kind
            });
        }

        onCount?.Invoke(seen);
        return result;
    }

    public static string NormalizeDir(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
}
