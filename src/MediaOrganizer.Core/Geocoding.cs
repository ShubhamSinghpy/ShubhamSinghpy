using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MediaOrganizer.Core;

/// <summary>Offline location naming: coordinates rounded to ~11 km cells, e.g. GPS_26.8N_80.9E.</summary>
public static class OfflineLocationNamer
{
    public static string Name(double lat, double lon)
    {
        var la = Math.Round(lat, 1, MidpointRounding.AwayFromZero);
        var lo = Math.Round(lon, 1, MidpointRounding.AwayFromZero);
        return "GPS_" + Math.Abs(la).ToString("F1", CultureInfo.InvariantCulture) + (la >= 0 ? "N" : "S") +
               "_" + Math.Abs(lo).ToString("F1", CultureInfo.InvariantCulture) + (lo >= 0 ? "E" : "W");
    }
}

/// <summary>
/// Optional online reverse geocoding using OpenStreetMap Nominatim.
/// Privacy: only rounded coordinates (2 decimals, roughly 1 km) are sent. No images, file names or other metadata.
/// Usage policy compliance: custom User-Agent, at most one request per second, results cached locally.
/// </summary>
public sealed class NominatimClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public NominatimClient(HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProfessionalMediaOrganizer", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(personal-media-organizer)"));
    }

    /// <summary>Returns a locality name, or null when the lookup failed (offline, blocked, no result).</summary>
    public async Task<string?> ReverseAsync(double lat, double lon, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;

            var url = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=10&accept-language=en" +
                      "&lat=" + lat.ToString("F2", CultureInfo.InvariantCulture) +
                      "&lon=" + lon.ToString("F2", CultureInfo.InvariantCulture);

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseName(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // offline / timeout: caller falls back to offline naming
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string? ParseName(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("address", out var addr) || addr.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "city", "town", "village", "municipality", "suburb", "city_district",
                                    "county", "state_district", "state", "country" })
        {
            if (addr.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Assigns a folder-safe location name to every item that has GPS coordinates.</summary>
public sealed class LocationResolver
{
    private readonly AppCache? _cache;
    private readonly bool _online;
    private readonly NominatimClient? _client;
    private int _consecutiveFailures;

    public LocationResolver(AppCache? cache, bool online, NominatimClient? client = null)
    {
        _cache = cache;
        _online = online;
        _client = online ? client ?? new NominatimClient() : null;
    }

    public int OnlineLookups { get; private set; }
    public int CacheHits { get; private set; }
    /// <summary>True when online lookup was requested but the service could not be reached.</summary>
    public bool OnlineUnavailable { get; private set; }

    public async Task ResolveAsync(IEnumerable<MediaItem> items, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var groups = items.Where(i => i.HasGps)
                          .GroupBy(i => Key(i.Latitude!.Value, i.Longitude!.Value))
                          .ToList();
        int done = 0;

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var first = g.First();
            double lat = Math.Round(first.Latitude!.Value, 2, MidpointRounding.AwayFromZero);
            double lon = Math.Round(first.Longitude!.Value, 2, MidpointRounding.AwayFromZero);

            string? name = _cache?.GetPlace(g.Key);
            if (name != null) CacheHits++;

            if (name == null && _online && _client != null && _consecutiveFailures < 3)
            {
                name = await _client.ReverseAsync(lat, lon, ct).ConfigureAwait(false);
                if (name != null)
                {
                    OnlineLookups++;
                    _consecutiveFailures = 0;
                    _cache?.PutPlace(g.Key, name);
                }
                else
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 3) OnlineUnavailable = true;
                }
            }

            name ??= OfflineLocationNamer.Name(first.Latitude!.Value, first.Longitude!.Value);
            var folder = PathPlanner.SanitizeSegment(name);
            foreach (var it in g) it.Location = folder;

            done++;
            if (done % 5 == 0 || done == groups.Count)
                progress?.Report(new ProgressInfo
                {
                    Stage = "Resolving locations",
                    Done = done,
                    Total = groups.Count,
                    Message = $"{done:N0} of {groups.Count:N0} places"
                });
        }
    }

    public static string Key(double lat, double lon) =>
        Math.Round(lat, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture) + "," +
        Math.Round(lon, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);
}
