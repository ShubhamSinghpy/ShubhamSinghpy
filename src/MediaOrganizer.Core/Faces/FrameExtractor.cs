using System.Diagnostics;

namespace MediaOrganizer.Core.Faces;

/// <summary>
/// Pulls a few still frames out of a video with ffmpeg so faces can be found in videos. The video is only read;
/// frames exist in memory only and are never written to disk.
/// </summary>
public sealed class FrameExtractor
{
    private readonly string? _ffmpeg;

    public FrameExtractor(string? explicitPath = null) => _ffmpeg = explicitPath ?? Locate();

    public bool IsAvailable => _ffmpeg != null;

    /// <summary>Evenly spaced moments between 8% and 92% of the video (a single middle frame for very short clips).</summary>
    public static IReadOnlyList<double> SamplePoints(double? durationSeconds, int frames)
    {
        frames = Math.Clamp(frames, 1, 30);
        double d = durationSeconds is > 0 ? durationSeconds.Value : 6;
        if (d < 1.5 || frames == 1) return new[] { d / 2 };
        var pts = new List<double>();
        for (int i = 0; i < frames; i++)
            pts.Add(d * (0.08 + 0.84 * (frames == 1 ? 0.5 : i / (double)(frames - 1))));
        return pts;
    }

    public RgbImage? ExtractFrame(string videoPath, double seconds, int maxSide, CancellationToken ct)
    {
        if (_ffmpeg == null) return null;
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "-v", "error", "-nostdin",
                "-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", videoPath,
                "-frames:v", "1",
                "-vf", $"scale='min({maxSide},iw)':-2",
                "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "3", "pipe:1"
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;

            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            var err = p.StandardError.ReadToEndAsync(ct);

            if (!p.WaitForExit(40_000))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return null;
            }
            copy.GetAwaiter().GetResult();
            _ = err.GetAwaiter().GetResult();

            if (ms.Length < 200) return null;
            return ImageLoader.Decode(ms.ToArray(), maxSide);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("Could not extract a video frame.", ex);
            return null;
        }
    }

    private static string? Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rel in new[] { Path.Combine("tools", "ffmpeg.exe"), "ffmpeg.exe" })
        {
            var c = Path.Combine(baseDir, rel);
            if (File.Exists(c)) return c;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var c = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                    if (File.Exists(c)) return c;
                }
                catch { /* invalid PATH entry */ }
            }
        }
        return null;
    }
}
