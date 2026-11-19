using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaOrganizer.Core;

public sealed class VideoProbe
{
    public DateTime? CreationDate { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? FrameRate { get; set; }
    public string? Codec { get; set; }
}

/// <summary>Reads video metadata (dates, GPS, codec, resolution, frame rate) with ffprobe. Read-only; never touches the video.</summary>
public sealed class FfprobeReader
{
    private readonly string? _path;

    public FfprobeReader(string? explicitPath = null)
    {
        _path = explicitPath ?? Locate();
    }

    public bool IsAvailable => _path != null;

    /// <summary>Returns null when ffprobe is missing or cannot parse the file (corrupted / unsupported).</summary>
    public VideoProbe? Probe(string videoPath)
    {
        if (_path == null) return null;

        try
        {
            var psi = new ProcessStartInfo(_path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", videoPath })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(45_000))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return null;
            }

            var json = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return null;

            return Parse(json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ffprobe failed for '{videoPath}'.", ex);
            return null;
        }
    }

    public static VideoProbe? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new VideoProbe();

        if (root.TryGetProperty("format", out var fmt))
        {
            AddTags(fmt, tags);
            if (fmt.TryGetProperty("duration", out var dur) &&
                double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                result.DurationSeconds = seconds;
        }

        bool sawVideoStream = false;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in streams.EnumerateArray())
            {
                AddTags(st, tags);
                var type = st.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                if (type != "video" || sawVideoStream) continue;

                // Skip embedded cover art streams.
                if (st.TryGetProperty("disposition", out var disp) &&
                    disp.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1)
                    continue;

                sawVideoStream = true;
                if (st.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) result.Width = w.GetInt32();
                if (st.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) result.Height = h.GetInt32();
                if (st.TryGetProperty("codec_name", out var cn)) result.Codec = cn.GetString();

                var rate = st.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() : null;
                var fps = ParseRate(rate) ?? (st.TryGetProperty("r_frame_rate", out var rfr) ? ParseRate(rfr.GetString()) : null);
                result.FrameRate = fps;
            }
        }

        if (!sawVideoStream && result.DurationSeconds == null) return null; // nothing usable

        // --- creation date: prefer Apple's local-time-with-offset tag, then container creation_time (UTC) ---
        result.CreationDate = ParseApple(tags) ?? ParseUtc(tags, "creation_time");

        // --- GPS ---
        foreach (var key in new[] { "com.apple.quicktime.location.iso6709", "location", "location-eng" })
        {
            if (tags.TryGetValue(key, out var loc) && Iso6709.TryParse(loc, out var lat, out var lon))
            {
                result.Latitude = lat;
                result.Longitude = lon;
                break;
            }
        }

        // --- device ---
        result.Make = FirstTag(tags, "com.apple.quicktime.make", "make", "manufacturer");
        result.Model = FirstTag(tags, "com.apple.quicktime.model", "model");
        return result;
    }

    private static void AddTags(JsonElement el, Dictionary<string, string> into)
    {
        if (!el.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in t.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String && !into.ContainsKey(prop.Name))
                into[prop.Name] = prop.Value.GetString() ?? "";
        }
    }

    private static string? FirstTag(Dictionary<string, string> tags, params string[] keys)
    {
        foreach (var k in keys)
            if (tags.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    private static double? ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return null;
        var parts = rate.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
        {
            var r = n / d;
            return r > 0 ? Math.Round(r, 3) : null;
        }
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) && single > 0 ? single : null;
    }

    private static DateTime? ParseApple(Dictionary<string, string> tags)
    {
        if (!tags.TryGetValue("com.apple.quicktime.creationdate", out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        // "2024-07-14T12:34:56+0530" -> "2024-07-14T12:34:56+05:30"
        var fixedText = Regex.Replace(raw.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
        if (DateTimeOffset.TryParse(fixedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && dto.Year >= 1971)
            return dto.DateTime; // wall-clock time where the video was shot
        return null;
    }

    private static DateTime? ParseUtc(Dictionary<string, string> tags, string key)
    {
        if (!tags.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto) && dto.Year >= 1971)
            return dto.UtcDateTime.ToLocalTime();
        return null;
    }

    private static string? Locate()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "ffprobe.exe" } : new[] { "ffprobe" };
        var baseDir = AppContext.BaseDirectory;

        foreach (var n in names)
        {
            var c1 = Path.Combine(baseDir, "tools", n);
            if (File.Exists(c1)) return c1;
            var c2 = Path.Combine(baseDir, n);
            if (File.Exists(c2)) return c2;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var n in names)
                {
                    try
                    {
                        var c = Path.Combine(dir.Trim('"'), n);
                        if (File.Exists(c)) return c;
                    }
                    catch { /* invalid PATH entry */ }
                }
            }
        }
        return null;
    }
}
