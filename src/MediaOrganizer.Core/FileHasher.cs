using System.Security.Cryptography;

namespace MediaOrganizer.Core;

/// <summary>Streaming SHA-256. Files are never loaded fully into memory.</summary>
public static class FileHasher
{
    private const int BufferSize = 1 << 20;

    public static string Sha256(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[BufferSize];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            h.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public static Task<string> Sha256Async(string path, CancellationToken ct = default) =>
        Task.Run(() => Sha256(path, ct), ct);
}
