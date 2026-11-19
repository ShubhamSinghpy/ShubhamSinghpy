using MediaOrganizer.Core;

namespace MediaOrganizer.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MediaOrganizerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string relative, string content)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

public static class TestUtil
{
    public static string Fixture(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", name);

    public static MediaItem ItemFor(string path)
    {
        var fi = new FileInfo(path);
        return new MediaItem
        {
            SourcePath = path,
            SizeBytes = fi.Length,
            FsCreated = fi.CreationTime,
            FsModified = fi.LastWriteTime,
            Kind = FileTypes.Kind(fi.Extension)
        };
    }

    public static MediaItem Plain(string path, MediaCategory cat, DateTime date, string location = PathPlanner.UnknownLocation) =>
        new()
        {
            SourcePath = path,
            Category = cat,
            CaptureDate = date,
            DateSource = DateSource.Original,
            Location = location
        };

    public static string Hash(string path) => FileHasher.Sha256(path);
}
