using System.Net;
using MediaOrganizer.Core;

namespace MediaOrganizer.Tests;

public class MetadataServiceTests
{
    private readonly MetadataService _svc = new();

    private MediaItem Read(string fixture)
    {
        var it = TestUtil.ItemFor(TestUtil.Fixture(fixture));
        _svc.Populate(it);
        return it;
    }

    [Fact]
    public void Photo_with_gps_and_capture_date()
    {
        var it = Read("gps_photo.jpg");
        Assert.Equal(MediaCategory.Photo, it.Category);
        Assert.Equal(DateSource.Original, it.DateSource);
        Assert.Equal(new DateTime(2024, 7, 14, 10, 30, 0), it.CaptureDate);
        Assert.True(it.HasGps);
        Assert.Equal(26.8467, it.Latitude!.Value, 3);
        Assert.Equal(80.9462, it.Longitude!.Value, 3);
        Assert.Equal("Apple iPhone 13", it.Device);
        Assert.Equal(64, it.Width);
        Assert.Equal(48, it.Height);
    }

    [Fact]
    public void Photo_without_gps_keeps_unknown_location()
    {
        var it = Read("nogps_photo.jpg");
        Assert.Equal(MediaCategory.Photo, it.Category);
        Assert.False(it.HasGps);
        Assert.Equal(PathPlanner.UnknownLocation, it.Location);
        Assert.Equal("Canon EOS 80D", it.Device);
    }

    [Fact]
    public void Missing_exif_date_uses_marked_fallback()
    {
        var it = Read("plain_no_exif.jpg");
        Assert.Equal(MediaCategory.Photo, it.Category);
        Assert.Equal(DateSource.FileSystemFallback, it.DateSource);
        Assert.Contains("CaptureDate", it.MissingFieldList());
    }

    [Fact]
    public void Exif_screenshot_comment_is_screenshot() =>
        Assert.Equal(MediaCategory.Screenshot, Read("exif_screenshot.jpg").Category);

    [Fact]
    public void Phone_sized_png_without_exif_is_screenshot() =>
        Assert.Equal(MediaCategory.Screenshot, Read("IMG_0042.PNG").Category);

    [Fact]
    public void Ordinary_png_is_a_photo_not_a_screenshot() =>
        Assert.Equal(MediaCategory.Photo, Read("drawing.png").Category);

    [Fact]
    public void Corrupted_image_is_unclassified_without_crashing()
    {
        var it = Read("corrupt.jpg");
        Assert.Equal(MediaCategory.Unclassified, it.Category);
        Assert.False(string.IsNullOrEmpty(it.MetadataNote));
    }

    [Fact]
    public void Empty_file_is_unclassified()
    {
        var it = Read("empty.jpg");
        Assert.Equal(MediaCategory.Unclassified, it.Category);
    }
}

public class PathPlannerTests
{
    private static OrganizerSettings Settings() => new() { DestinationFolder = Path.Combine(Path.GetTempPath(), "out") };

    private static string Expected(OrganizerSettings s, params string[] parts) =>
        Path.Combine(new[] { PathPlanner.OutputRoot(s.DestinationFolder) }.Concat(parts).ToArray());

    [Fact]
    public void Photo_goes_to_category_year_month_location()
    {
        var s = Settings();
        var it = TestUtil.Plain("/src/a.jpg", MediaCategory.Photo, new DateTime(2024, 7, 14), "Lucknow");
        PathPlanner.Plan(new[] { it }, s);
        Assert.Equal(Expected(s, "Photos", "2024", "2024-07", "Lucknow", "a.jpg"), it.DestinationPath);
    }

    [Fact]
    public void Photo_without_location_uses_Unknown_Location()
    {
        var s = Settings();
        var it = TestUtil.Plain("/src/a.jpg", MediaCategory.Photo, new DateTime(2024, 8, 1));
        PathPlanner.Plan(new[] { it }, s);
        Assert.Equal(Expected(s, "Photos", "2024", "2024-08", "Unknown_Location", "a.jpg"), it.DestinationPath);
    }

    [Fact]
    public void Screenshot_has_no_location_folder()
    {
        var s = Settings();
        var it = TestUtil.Plain("/src/s.png", MediaCategory.Screenshot, new DateTime(2025, 1, 2), "Lucknow");
        PathPlanner.Plan(new[] { it }, s);
        Assert.Equal(Expected(s, "Screenshots", "2025", "2025-01", "s.png"), it.DestinationPath);
    }

    [Fact]
    public void Duplicate_gets_prefix_and_duplicates_folder()
    {
        var s = Settings();
        var it = TestUtil.Plain("/src/a.jpg", MediaCategory.Photo, new DateTime(2024, 7, 14), "Lucknow");
        it.IsDuplicate = true;
        PathPlanner.Plan(new[] { it }, s);
        Assert.Equal(Expected(s, "Duplicates", "Photos", "duplicate_a.jpg"), it.DestinationPath);
    }

    [Fact]
    public void Same_name_different_files_never_collide()
    {
        var s = Settings();
        var a = TestUtil.Plain("/src1/IMG_1.jpg", MediaCategory.Photo, new DateTime(2024, 7, 14), "Lucknow");
        var b = TestUtil.Plain("/src2/IMG_1.jpg", MediaCategory.Photo, new DateTime(2024, 7, 20), "Lucknow");
        PathPlanner.Plan(new[] { a, b }, s);
        Assert.NotEqual(a.DestinationPath, b.DestinationPath);
        Assert.EndsWith("IMG_1.jpg", a.DestinationPath);
        Assert.EndsWith("IMG_1_1.jpg", b.DestinationPath);
    }

    [Fact]
    public void Hierarchy_levels_are_configurable()
    {
        var s = Settings();
        s.UseMonthFolders = false;
        s.UseLocationFolders = false;
        var it = TestUtil.Plain("/src/a.jpg", MediaCategory.Video, new DateTime(2024, 7, 14), "Lucknow");
        PathPlanner.Plan(new[] { it }, s);
        Assert.Equal(Expected(s, "Videos", "2024", "a.jpg"), it.DestinationPath);
    }

    [Theory]
    [InlineData("Lucknow", "Lucknow")]
    [InlineData("A/B:C?", "A_B_C_")]
    [InlineData("CON", "CON_")]
    [InlineData("  ", "Unknown_Location")]
    [InlineData(null, "Unknown_Location")]
    public void Sanitizes_folder_names(string? input, string expected) =>
        Assert.Equal(expected, PathPlanner.SanitizeSegment(input));
}

public class EventGrouperTests
{
    private static List<MediaItem> Burst(DateTime start, int count, string prefix) =>
        Enumerable.Range(0, count)
                  .Select(i => TestUtil.Plain($"/src/{prefix}{i:000}.jpg", MediaCategory.Photo, start.AddMinutes(i * 10)))
                  .ToList();

    [Fact]
    public void Splits_on_time_gap_and_names_events_neutrally()
    {
        var items = Burst(new DateTime(2024, 7, 14, 9, 0, 0), 12, "a").Concat(Burst(new DateTime(2024, 7, 15, 9, 0, 0), 12, "b")).ToList();
        EventGrouper.Assign(items, new OrganizerSettings { GroupEvents = true, MinEventItems = 10, EventGapHours = 8 });

        Assert.All(items.Take(12), i => Assert.Equal("Event_2024-07-14", i.EventName));
        Assert.All(items.Skip(12), i => Assert.Equal("Event_2024-07-15", i.EventName));
    }

    [Fact]
    public void Small_groups_do_not_become_events()
    {
        var items = Burst(new DateTime(2024, 7, 14, 9, 0, 0), 3, "a");
        EventGrouper.Assign(items, new OrganizerSettings { GroupEvents = true, MinEventItems = 10 });
        Assert.All(items, i => Assert.Null(i.EventName));
    }

    [Fact]
    public void Disabled_grouping_assigns_nothing()
    {
        var items = Burst(new DateTime(2024, 7, 14, 9, 0, 0), 12, "a");
        EventGrouper.Assign(items, new OrganizerSettings { GroupEvents = false });
        Assert.All(items, i => Assert.Null(i.EventName));
    }

    [Fact]
    public void Files_with_only_fallback_dates_are_not_grouped()
    {
        var items = Burst(new DateTime(2024, 7, 14, 9, 0, 0), 12, "a");
        foreach (var i in items) i.DateSource = DateSource.FileSystemFallback;
        EventGrouper.Assign(items, new OrganizerSettings { GroupEvents = true, MinEventItems = 10 });
        Assert.All(items, i => Assert.Null(i.EventName));
    }

    [Fact]
    public void Large_distance_splits_an_event()
    {
        var items = Burst(new DateTime(2024, 7, 14, 9, 0, 0), 20, "a");
        foreach (var i in items.Take(10)) { i.Latitude = 26.85; i.Longitude = 80.95; }      // Lucknow
        foreach (var i in items.Skip(10)) { i.Latitude = 28.61; i.Longitude = 77.21; }      // Delhi
        EventGrouper.Assign(items, new OrganizerSettings { GroupEvents = true, MinEventItems = 10, EventDistanceKm = 50 });
        Assert.NotEqual(items[0].EventName, items[19].EventName);
        Assert.NotNull(items[0].EventName);
        Assert.NotNull(items[19].EventName);
    }
}

public class DuplicateDetectorTests
{
    [Fact]
    public void Finds_exact_duplicates_by_content_regardless_of_name_and_keeps_all()
    {
        using var t = new TempDir();
        var a = TestUtil.ItemFor(t.File("a/IMG_1.jpg", "AAAAAAAA"));
        var b = TestUtil.ItemFor(t.File("b/renamed.jpg", "AAAAAAAA"));       // same content, different name
        var c = TestUtil.ItemFor(t.File("c/IMG_1.jpg", "BBBBBBBB"));         // same name and size, different content
        var d = TestUtil.ItemFor(t.File("d/other.jpg", "CCC"));              // unique size
        var items = new List<MediaItem> { a, b, c, d };

        DuplicateDetector.Detect(items, null, 2, null, CancellationToken.None);

        Assert.Equal(1, items.Count(i => i.IsDuplicate));
        Assert.Equal(a.DuplicateGroup, b.DuplicateGroup);
        Assert.NotEqual(0, a.DuplicateGroup);
        Assert.Equal(0, c.DuplicateGroup);
        Assert.Equal(0, d.DuplicateGroup);
        Assert.All(items, i => Assert.True(File.Exists(i.SourcePath)));      // nothing deleted
    }
}

public class LocationResolverTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public int Calls;
        public FakeHandler(Func<HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_respond());
        }
    }

    private static MediaItem Gps(double lat, double lon)
    {
        var it = TestUtil.Plain("/src/a.jpg", MediaCategory.Photo, DateTime.Now);
        it.Latitude = lat;
        it.Longitude = lon;
        return it;
    }

    [Fact]
    public async Task Offline_uses_coordinate_names()
    {
        var it = Gps(26.8467, 80.9462);
        await new LocationResolver(null, false).ResolveAsync(new[] { it }, null, CancellationToken.None);
        Assert.Equal("GPS_26.8N_80.9E", it.Location);
    }

    [Fact]
    public async Task Online_uses_place_name_and_caches_it()
    {
        using var tmp = new TempDir();
        using var cache = new AppCache(Path.Combine(tmp.Path, "cache.db"));
        var handler = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"address":{"city":"Lucknow","state":"Uttar Pradesh"}}""")
        });

        var it = Gps(26.8467, 80.9462);
        using (var client = new NominatimClient(handler))
            await new LocationResolver(cache, true, client).ResolveAsync(new[] { it }, null, CancellationToken.None);
        Assert.Equal("Lucknow", it.Location);
        Assert.Equal(1, handler.Calls);

        // Second run: answered from the cache, no network call at all.
        var it2 = Gps(26.8470, 80.9460);
        using (var client2 = new NominatimClient(handler))
            await new LocationResolver(cache, true, client2).ResolveAsync(new[] { it2 }, null, CancellationToken.None);
        Assert.Equal("Lucknow", it2.Location);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Online_failure_falls_back_to_offline_name()
    {
        var handler = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var it = Gps(26.8467, 80.9462);
        using var client = new NominatimClient(handler);
        await new LocationResolver(null, true, client).ResolveAsync(new[] { it }, null, CancellationToken.None);
        Assert.Equal("GPS_26.8N_80.9E", it.Location);
    }

    [Theory]
    [InlineData("Shimla ( Rural )", "Shimla")]
    [InlineData("Shimla", "Shimla")]
    [InlineData("Sudhagad Taluka", "Sudhagad")]
    [InlineData("Lakhimpur", "Lakhimpur")]
    [InlineData("Kosya Kutauli Tehsil", "Kosya Kutauli")]
    [InlineData("Rural", "Rural")]
    [InlineData("Uttar Pradesh", "Uttar Pradesh")]
    public void Normalizes_place_names(string input, string expected) =>
        Assert.Equal(expected, PlaceNames.Normalize(input));

    [Fact]
    public async Task Nearby_cells_with_variant_names_share_one_folder()
    {
        var names = new Queue<string>(new[] { "Shimla", "Shimla ( Rural )" });
        var handler = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"address\":{\"city\":\"" + names.Dequeue() + "\"}}")
        });
        var a = Gps(31.10, 77.17);
        var b = Gps(31.20, 77.30);
        using var client = new NominatimClient(handler);
        await new LocationResolver(null, true, client).ResolveAsync(new[] { a, b }, null, CancellationToken.None);
        Assert.Equal("Shimla", a.Location);
        Assert.Equal("Shimla", b.Location);
    }

    [Fact]
    public void Parses_nominatim_address_preferring_city()
    {
        Assert.Equal("Sitapur", NominatimClient.ParseName("""{"address":{"county":"X","town":"Sitapur","state":"UP"}}"""));
        Assert.Equal("Uttar Pradesh", NominatimClient.ParseName("""{"address":{"state":"Uttar Pradesh","country":"India"}}"""));
        Assert.Null(NominatimClient.ParseName("""{"error":"Unable to geocode"}"""));
    }
}
