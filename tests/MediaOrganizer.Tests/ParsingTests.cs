using MediaOrganizer.Core;

namespace MediaOrganizer.Tests;

public class FilenameParserTests
{
    [Theory]
    [InlineData("IMG_20240714_123456.jpg", 2024, 7, 14, 12, 34, 56)]
    [InlineData("IMG-20240714-WA0001.jpg", 2024, 7, 14, 0, 0, 0)]
    [InlineData("Screenshot_20240102-030405.png", 2024, 1, 2, 3, 4, 5)]
    [InlineData("VID_20231225_181500.mp4", 2023, 12, 25, 18, 15, 0)]
    [InlineData("Screenshot 2024-07-14 at 12.34.56.png", 2024, 7, 14, 0, 0, 0)]
    public void Parses_known_patterns(string name, int y, int mo, int d, int h, int mi, int s)
    {
        var dt = FilenameParser.TryParseDate(name);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), dt);
    }

    [Theory]
    [InlineData("IMG_1234.jpg")]
    [InlineData("IMG_20230230_101010.jpg")]   // impossible date
    [InlineData("photo.jpg")]
    [InlineData("1720950000000.jpg")]          // epoch milliseconds are not dates
    public void Rejects_non_dates(string name) => Assert.Null(FilenameParser.TryParseDate(name));

    [Theory]
    [InlineData("Screenshot_20240714.png", true)]
    [InlineData("Screenshot (12).png", true)]
    [InlineData("Screen Shot 2024-07-14 at 1.02.03 PM.png", true)]
    [InlineData("IMG_0042.PNG", false)]
    [InlineData("DSC_0001.JPG", false)]
    public void Detects_screenshot_names(string name, bool expected) =>
        Assert.Equal(expected, FilenameParser.LooksLikeScreenshotName(name));
}

public class ScreenshotDetectorTests
{
    [Fact]
    public void Camera_make_always_means_photo() =>
        Assert.False(ScreenshotDetector.IsScreenshot("/x/Screenshot_1.png", true, false, 1170, 2532, null));

    [Fact]
    public void Ios_user_comment_marks_screenshot() =>
        Assert.True(ScreenshotDetector.IsScreenshot("/x/IMG_1.jpg", false, false, 100, 100, "Screenshot"));

    [Fact]
    public void Png_with_phone_resolution_and_no_exif_is_screenshot() =>
        Assert.True(ScreenshotDetector.IsScreenshot("/x/IMG_0042.PNG", false, false, 1170, 2532, null));

    [Fact]
    public void Jpeg_with_phone_resolution_alone_is_not_screenshot() =>
        Assert.False(ScreenshotDetector.IsScreenshot("/x/IMG_0042.jpg", false, false, 1170, 2532, null));

    [Fact]
    public void Ordinary_sized_png_is_not_screenshot() =>
        Assert.False(ScreenshotDetector.IsScreenshot("/x/drawing.png", false, false, 800, 600, null));

    [Fact]
    public void Png_with_exif_capture_date_is_not_screenshot() =>
        Assert.False(ScreenshotDetector.IsScreenshot("/x/IMG_9.png", false, true, 1170, 2532, null));
}

public class Iso6709Tests
{
    [Fact]
    public void Parses_with_altitude()
    {
        Assert.True(Iso6709.TryParse("+26.8467+080.9462+123.400/", out var lat, out var lon));
        Assert.Equal(26.8467, lat, 4);
        Assert.Equal(80.9462, lon, 4);
    }

    [Fact]
    public void Parses_southern_and_western_hemispheres()
    {
        Assert.True(Iso6709.TryParse("-33.8688+151.2093/", out var lat, out var lon));
        Assert.Equal(-33.8688, lat, 4);
        Assert.Equal(151.2093, lon, 4);
    }

    [Theory]
    [InlineData("+00.0000+000.0000/")]   // no GPS fix
    [InlineData("+95.0000+010.0000/")]   // out of range
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_invalid(string? text) => Assert.False(Iso6709.TryParse(text, out _, out _));
}

public class FfprobeParseTests
{
    private const string Json = """
    {
      "streams": [
        { "codec_type": "audio", "codec_name": "aac" },
        { "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160,
          "avg_frame_rate": "30000/1001", "r_frame_rate": "30000/1001" }
      ],
      "format": {
        "duration": "12.480000",
        "tags": {
          "creation_time": "2024-07-14T05:00:56.000000Z",
          "com.apple.quicktime.creationdate": "2024-07-14T10:30:56+0530",
          "com.apple.quicktime.location.ISO6709": "+26.8467+080.9462+123.400/",
          "com.apple.quicktime.make": "Apple",
          "com.apple.quicktime.model": "iPhone 13"
        }
      }
    }
    """;

    [Fact]
    public void Reads_all_fields()
    {
        var p = FfprobeReader.Parse(Json);
        Assert.NotNull(p);
        Assert.Equal(new DateTime(2024, 7, 14, 10, 30, 56), p!.CreationDate);   // local wall-clock time of capture
        Assert.Equal(26.8467, p.Latitude!.Value, 4);
        Assert.Equal(80.9462, p.Longitude!.Value, 4);
        Assert.Equal(3840, p.Width);
        Assert.Equal(2160, p.Height);
        Assert.Equal("hevc", p.Codec);
        Assert.Equal(29.97, p.FrameRate!.Value, 2);
        Assert.Equal(12.48, p.DurationSeconds!.Value, 2);
        Assert.Equal("Apple", p.Make);
        Assert.Equal("iPhone 13", p.Model);
    }

    [Fact]
    public void Video_without_tags_has_no_gps_and_no_date()
    {
        var p = FfprobeReader.Parse("""{"streams":[{"codec_type":"video","width":640,"height":480}],"format":{"duration":"3"}}""");
        Assert.NotNull(p);
        Assert.Null(p!.Latitude);
        Assert.Null(p.CreationDate);
    }
}
