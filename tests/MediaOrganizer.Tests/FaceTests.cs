using System.Text.Json;
using MediaOrganizer.Core;
using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.Tests;

/// <summary>Runs only when the optional face models have been downloaded (tools\get-models.ps1).</summary>
public sealed class FactIfModelsAttribute : FactAttribute
{
    public FactIfModelsAttribute()
    {
        if (!FaceModels.TryLocate(out _, out _)) Skip = "Face models not downloaded (run tools\\get-models.ps1).";
    }
}

internal static class FaceTestUtil
{
    public static float[] Vec(params float[] seedParts)
    {
        // A deterministic unit vector that is "close" for equal seeds and nearly orthogonal for different ones.
        var v = new float[128];
        foreach (var s in seedParts) v[(int)s % 128] += 1f;
        return FaceGeometry.Normalize(v);
    }

    public static PersonRegistry Registry(params (string Name, float[] Vec)[] people)
    {
        var r = new PersonRegistry();
        foreach (var (n, v) in people) r.AddOrReplace(n, new[] { v }, new[] { n + ".jpg" });
        return r;
    }

    public static StoredScan Scan(params (int Frame, float[] Vec)[] faces) => new()
    {
        Faces = faces.Select(f => new StoredFace { Frame = f.Frame, Score = 0.9f, W = 80, H = 80, Emb = FaceGeometry.ToBase64(f.Vec) }).ToList()
    };

    public static MediaItem Photo(string path = "/src/a.jpg") =>
        TestUtil.Plain(path, MediaCategory.Photo, new DateTime(2024, 7, 14));
}

public class FaceGeometryTests
{
    [Fact]
    public void Nms_keeps_best_of_overlapping_boxes_and_separate_ones()
    {
        var a = new DetectedFace { X = 0, Y = 0, W = 100, H = 100, Score = 0.9f };
        var b = new DetectedFace { X = 5, Y = 5, W = 100, H = 100, Score = 0.8f };   // overlaps a
        var c = new DetectedFace { X = 300, Y = 0, W = 100, H = 100, Score = 0.7f }; // far away
        var kept = FaceGeometry.Nms(new[] { b, c, a }, 0.3f);
        Assert.Equal(2, kept.Count);
        Assert.Contains(a, kept);
        Assert.Contains(c, kept);
    }

    [Fact]
    public void Similarity_transform_recovers_a_known_rotation_scale_and_shift()
    {
        // dst = rotate 30 degrees, scale 0.5, shift (10, -4) applied to src
        double ang = 30 * Math.PI / 180, s = 0.5;
        double a = s * Math.Cos(ang), b = s * Math.Sin(ang);
        var src = new float[] { 10, 20, 60, 22, 35, 50, 18, 80, 55, 82 };
        var dst = new float[10];
        for (int i = 0; i < 5; i++)
        {
            dst[2 * i] = (float)(a * src[2 * i] - b * src[2 * i + 1] + 10);
            dst[2 * i + 1] = (float)(b * src[2 * i] + a * src[2 * i + 1] - 4);
        }
        var (ea, eb, etx, ety) = FaceGeometry.EstimateSimilarity(src, dst);
        Assert.Equal(a, ea, 3);
        Assert.Equal(b, eb, 3);
        Assert.Equal(10, etx, 2);
        Assert.Equal(-4, ety, 2);
    }

    [Fact]
    public void AlignCrop_produces_a_112_square_and_maps_the_eyes_onto_the_template()
    {
        // A white dot at each of two "eye" positions on a black picture should land on the template eye positions.
        var img = new RgbImage(300, 300, new byte[300 * 300 * 3]);
        void Dot(int cx, int cy) { for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++) { int i = ((cy + y) * 300 + cx + x) * 3; img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = 255; } }
        // Landmarks placed at 2x the template, shifted: x = 2*t + 20, y = 2*t + 30
        var lm = new float[10];
        for (int i = 0; i < 5; i++) { lm[2 * i] = 2 * FaceGeometry.AlignTemplate[2 * i] + 20; lm[2 * i + 1] = 2 * FaceGeometry.AlignTemplate[2 * i + 1] + 30; }
        Dot((int)lm[0], (int)lm[1]);
        Dot((int)lm[2], (int)lm[3]);

        var crop = FaceGeometry.AlignCrop(img, lm);
        Assert.Equal(112 * 112 * 3, crop.Length);

        int ex = (int)Math.Round(FaceGeometry.AlignTemplate[0]), ey = (int)Math.Round(FaceGeometry.AlignTemplate[1]);
        Assert.True(crop[(ey * 112 + ex) * 3] > 150, "left eye dot should be bright at the template position");
        Assert.True(crop[(5 * 112 + 5) * 3] < 30, "far corner stays dark");
    }

    [Fact]
    public void Embeddings_normalize_round_trip_and_compare()
    {
        var v = FaceGeometry.Normalize(new float[] { 3, 4, 0 });
        Assert.Equal(1.0, FaceGeometry.Cosine(v, v), 5);
        Assert.Equal(0.6, v[0], 5);
        Assert.Equal(v, FaceGeometry.FromBase64(FaceGeometry.ToBase64(v)));
        Assert.Equal(0.0, FaceGeometry.Cosine(FaceGeometry.Normalize(new float[] { 1, 0 }), FaceGeometry.Normalize(new float[] { 0, 1 })), 5);
    }
}

public class ImageOpsTests
{
    // 2 wide x 3 tall: pixels numbered 1..6 in the red channel
    private static RgbImage Sample()
    {
        var d = new byte[2 * 3 * 3];
        for (int i = 0; i < 6; i++) d[i * 3] = (byte)(i + 1);
        return new RgbImage(2, 3, d);
    }

    private static string Reds(RgbImage img) =>
        string.Join(",", Enumerable.Range(0, img.Width * img.Height).Select(i => img.Data[i * 3]));

    [Theory]
    [InlineData(1, 2, 3, "1,2,3,4,5,6")]
    [InlineData(2, 2, 3, "2,1,4,3,6,5")]          // mirror
    [InlineData(3, 2, 3, "6,5,4,3,2,1")]          // rotate 180
    [InlineData(4, 2, 3, "5,6,3,4,1,2")]          // flip vertical
    [InlineData(5, 3, 2, "1,3,5,2,4,6")]          // transpose
    [InlineData(6, 3, 2, "5,3,1,6,4,2")]          // rotate 90 clockwise
    [InlineData(7, 3, 2, "6,4,2,5,3,1")]          // transverse
    [InlineData(8, 3, 2, "2,4,6,1,3,5")]          // rotate 270 clockwise
    public void Orient_applies_each_exif_orientation(int orientation, int w, int h, string expected)
    {
        var r = ImageOps.Orient(Sample(), orientation);
        Assert.Equal((w, h), (r.Width, r.Height));
        Assert.Equal(expected, Reds(r));
    }

    [Fact]
    public void Downscale_limits_the_longest_side_and_never_enlarges()
    {
        var big = new RgbImage(400, 200, Enumerable.Repeat((byte)100, 400 * 200 * 3).ToArray());
        var small = ImageOps.Downscale(big, 100);
        Assert.Equal((100, 50), (small.Width, small.Height));
        Assert.All(small.Data, b => Assert.Equal(100, b));
        Assert.Same(big, ImageOps.Downscale(big, 1000));
    }
}

public class ImageLoaderTests
{
    [Fact]
    public void Decodes_a_real_jpeg_and_png_and_reports_corrupt_files_without_crashing()
    {
        var jpg = ImageLoader.TryLoad(TestUtil.Fixture("gps_photo.jpg"), out var e1);
        Assert.NotNull(jpg);
        Assert.Null(e1);
        Assert.True(jpg!.Width > 0 && jpg.Height > 0);

        var png = ImageLoader.TryLoad(TestUtil.Fixture("IMG_0042.PNG"), out _);
        Assert.NotNull(png);

        Assert.Null(ImageLoader.TryLoad(TestUtil.Fixture("corrupt.jpg"), out var err));
        Assert.False(string.IsNullOrEmpty(err));
        Assert.Null(ImageLoader.TryLoad(TestUtil.Fixture("empty.jpg"), out _));
    }

    [Fact]
    public void Heic_without_a_platform_decoder_gives_a_clear_message()
    {
        using var t = new TempDir();
        var p = t.File("x.heic", "not really heic");
        var saved = ImageLoader.PlatformDecoder;
        ImageLoader.PlatformDecoder = null;
        try
        {
            Assert.Null(ImageLoader.TryLoad(p, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }
        finally { ImageLoader.PlatformDecoder = saved; }
    }
}

public class FrameSamplingTests
{
    [Fact]
    public void Sample_points_are_spread_inside_the_video()
    {
        var pts = FrameExtractor.SamplePoints(100, 5);
        Assert.Equal(5, pts.Count);
        Assert.True(pts[0] >= 7.9 && pts[^1] <= 92.1);
        Assert.Equal(pts.OrderBy(x => x), pts);
        Assert.Single(FrameExtractor.SamplePoints(1.0, 8));          // very short clip -> one middle frame
        Assert.Equal(8, FrameExtractor.SamplePoints(null, 8).Count);   // unknown duration still samples
    }
}

public class PersonRegistryTests
{
    [Fact]
    public void Add_replace_remove_and_persist()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "people.json");
        var reg = new PersonRegistry();
        reg.AddOrReplace("Asha", new[] { FaceTestUtil.Vec(1) }, new[] { "a.jpg" });
        reg.AddOrReplace("Ravi", new[] { FaceTestUtil.Vec(2), FaceTestUtil.Vec(3) }, new[] { "r1.jpg", "r2.jpg" });
        reg.AddOrReplace("asha", new[] { FaceTestUtil.Vec(4) }, new[] { "new.jpg" });   // same name (any case) replaces
        Assert.Equal(2, reg.People.Count);
        Assert.Equal(2, reg.Find("RAVI")!.ReferenceCount);
        Assert.Equal("asha", reg.Find("Asha")!.Name);

        reg.Save(path);
        var loaded = PersonRegistry.Load(path);
        Assert.Equal(2, loaded.People.Count);
        Assert.Equal(FaceTestUtil.Vec(4), loaded.Find("asha")!.GetVectors()[0]);

        Assert.True(loaded.Remove("ravi"));
        Assert.False(loaded.Remove("ravi"));
        Assert.Single(loaded.People);
    }

    [Fact]
    public void Rejects_empty_names_no_references_and_caps_at_four_references()
    {
        var reg = new PersonRegistry();
        Assert.Throws<ArgumentException>(() => reg.AddOrReplace("  ", new[] { FaceTestUtil.Vec(1) }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => reg.AddOrReplace("Asha", Array.Empty<float[]>(), Array.Empty<string>()));
        var p = reg.AddOrReplace("Asha", Enumerable.Range(1, 6).Select(i => FaceTestUtil.Vec(i)), Array.Empty<string>());
        Assert.Equal(PersonRegistry.MaxReferencePhotos, p.ReferenceCount);
    }

    [Fact]
    public void Best_uses_the_closest_reference_of_any_person()
    {
        var reg = FaceTestUtil.Registry(("Asha", FaceTestUtil.Vec(1)), ("Ravi", FaceTestUtil.Vec(2)));
        var (p, s) = reg.Best(FaceTestUtil.Vec(2));
        Assert.Equal("Ravi", p!.Name);
        Assert.Equal(1.0, s, 4);
    }

    [Fact]
    public void Missing_or_corrupt_file_loads_as_empty()
    {
        using var t = new TempDir();
        Assert.Empty(PersonRegistry.Load(System.IO.Path.Combine(t.Path, "none.json")).People);
        Assert.Empty(PersonRegistry.Load(t.File("bad.json", "{ not json")).People);
    }
}

public class FaceAssignerTests
{
    private static readonly float[] Asha = FaceTestUtil.Vec(1);
    private static readonly float[] Ravi = FaceTestUtil.Vec(2);
    private static readonly float[] Stranger = FaceTestUtil.Vec(50);

    [Fact]
    public void Photo_with_no_faces_is_scanned_and_has_no_people()
    {
        var it = FaceTestUtil.Photo();
        FaceAssigner.Apply(it, new StoredScan(), FaceTestUtil.Registry(("Asha", Asha)), 0.4f);
        Assert.Equal(FaceScanState.Scanned, it.FaceState);
        Assert.False(it.HasPeople);
        Assert.Equal("No people", it.FaceCategoryText);
    }

    [Fact]
    public void Several_registered_people_in_one_photo_are_all_found()
    {
        var it = FaceTestUtil.Photo();
        FaceAssigner.Apply(it, FaceTestUtil.Scan((0, Asha), (0, Ravi)), FaceTestUtil.Registry(("Asha", Asha), ("Ravi", Ravi)), 0.4f);
        Assert.Equal(new[] { "Asha", "Ravi" }, it.People);
        Assert.Equal(2, it.FaceCount);
        Assert.Equal(0, it.UnidentifiedFaces);
        Assert.Equal("Recognized people", it.FaceCategoryText);
    }

    [Fact]
    public void Unknown_faces_are_counted_as_unidentified_and_mixed_is_reported()
    {
        var onlyStranger = FaceTestUtil.Photo();
        FaceAssigner.Apply(onlyStranger, FaceTestUtil.Scan((0, Stranger)), FaceTestUtil.Registry(("Asha", Asha)), 0.4f);
        Assert.True(onlyStranger.HasPeople);
        Assert.False(onlyStranger.HasRecognizedPeople);
        Assert.Equal(1, onlyStranger.UnidentifiedFaces);
        Assert.Equal("Unidentified people", onlyStranger.FaceCategoryText);

        var mixed = FaceTestUtil.Photo();
        FaceAssigner.Apply(mixed, FaceTestUtil.Scan((0, Asha), (0, Stranger)), FaceTestUtil.Registry(("Asha", Asha)), 0.4f);
        Assert.Equal("Recognized + unidentified", mixed.FaceCategoryText);
        Assert.Equal(1, mixed.UnidentifiedFaces);
    }

    [Fact]
    public void One_person_is_used_once_per_frame_and_each_face_once()
    {
        // Two faces that both look like Asha: only the better one becomes Asha, the other stays unidentified.
        var it = FaceTestUtil.Photo();
        FaceAssigner.Apply(it, FaceTestUtil.Scan((0, Asha), (0, Asha)), FaceTestUtil.Registry(("Asha", Asha)), 0.4f);
        Assert.Equal(new[] { "Asha" }, it.People);
        Assert.Equal(1, it.UnidentifiedFaces);
    }

    [Fact]
    public void Strictness_threshold_controls_matches()
    {
        var near = FaceGeometry.Normalize(Asha.Zip(FaceTestUtil.Vec(77), (a, b) => a + 0.9f * b).ToArray());   // somewhat similar
        var reg = FaceTestUtil.Registry(("Asha", Asha));
        double sim = FaceGeometry.Cosine(near, Asha);
        Assert.InRange(sim, 0.5, 0.95);

        var loose = FaceTestUtil.Photo(); FaceAssigner.Apply(loose, FaceTestUtil.Scan((0, near)), reg, (float)(sim - 0.05));
        var strict = FaceTestUtil.Photo(); FaceAssigner.Apply(strict, FaceTestUtil.Scan((0, near)), reg, (float)(sim + 0.05));
        Assert.Equal(new[] { "Asha" }, loose.People);
        Assert.Empty(strict.People);
    }

    [Fact]
    public void Video_people_are_the_union_over_frames_and_unidentified_is_the_worst_frame()
    {
        var it = TestUtil.Plain("/src/v.mp4", MediaCategory.Video, DateTime.Now);
        var scan = FaceTestUtil.Scan((0, Asha), (1, Ravi), (1, Stranger), (2, Stranger));
        FaceAssigner.Apply(it, scan, FaceTestUtil.Registry(("Asha", Asha), ("Ravi", Ravi)), 0.4f);
        Assert.Equal(new[] { "Asha", "Ravi" }, it.People);
        Assert.Equal(2, it.FaceCount);          // frame 1 has two faces
        Assert.Equal(1, it.AutoUnidentified);
    }
}

public class FaceCorrectionTests
{
    private static MediaItem ScannedWith(params string[] people)
    {
        var it = FaceTestUtil.Photo();
        it.FaceState = FaceScanState.Scanned;
        it.FaceCount = people.Length;
        it.AutoPeople.AddRange(people);
        return it;
    }

    [Fact]
    public void Removing_a_wrong_match_and_adding_a_missed_person()
    {
        var it = ScannedWith("Asha");
        FaceCorrections.RemovePerson(it, "Asha", null);
        Assert.Empty(it.People);
        Assert.Equal(1, it.UnidentifiedFaces);                // the face is still there, just not Asha

        FaceCorrections.AddPerson(it, "Ravi", null);
        Assert.Equal(new[] { "Ravi" }, it.People);
        Assert.Equal(0, it.UnidentifiedFaces);

        FaceCorrections.AddPerson(it, "Asha", null);           // undo the removal
        Assert.Equal(new[] { "Asha", "Ravi" }, it.People);
        Assert.Empty(it.RemovedPeople);
    }

    [Fact]
    public void Corrections_survive_a_rescan_through_the_cache()
    {
        using var t = new TempDir();
        using var cache = new AppCache(System.IO.Path.Combine(t.Path, "c.db"));
        var file = t.File("p.jpg", "x");
        var it = ScannedWith("Asha");
        var real = TestUtil.Plain(file, MediaCategory.Photo, DateTime.Now);
        real.FaceState = FaceScanState.Scanned; real.FaceCount = 1; real.AutoPeople.Add("Asha");

        FaceCorrections.RemovePerson(real, "Asha", cache);
        FaceCorrections.AddPerson(real, "Ravi", cache);

        var again = TestUtil.Plain(file, MediaCategory.Photo, DateTime.Now);
        again.FaceState = FaceScanState.Scanned; again.FaceCount = 1; again.AutoPeople.Add("Asha");
        FaceCorrections.LoadInto(again, cache);
        Assert.Equal(new[] { "Ravi" }, again.People);
        Assert.NotNull(it);
    }

    [Fact]
    public void Forgetting_a_deleted_person_clears_every_trace()
    {
        var it = ScannedWith("Asha");
        FaceCorrections.AddPerson(it, "Ravi", null);
        FaceCorrections.ForgetPerson(it, "Asha", null);
        FaceCorrections.ForgetPerson(it, "Ravi", null);
        Assert.Empty(it.People);
        Assert.Empty(it.AutoPeople);
    }

    [Fact]
    public void Clearing_face_data_empties_the_cache_tables()
    {
        using var t = new TempDir();
        using var cache = new AppCache(System.IO.Path.Combine(t.Path, "c.db"));
        cache.PutFaceScan("/a.jpg", 1, 2, "k", "{}");
        cache.PutFaceOverride("/a.jpg", 1, 2, "Asha", "");
        Assert.NotNull(cache.GetFaceScan("/a.jpg", 1, 2, "k"));
        cache.ClearFaceData();
        Assert.Null(cache.GetFaceScan("/a.jpg", 1, 2, "k"));
        Assert.Null(cache.GetFaceOverride("/a.jpg", 1, 2));
    }
}

public class PersonCollectionTests
{
    private static OrganizerSettings Settings(string dest) => new() { DestinationFolder = dest, SourceFolder = "/src" };

    private static MediaItem Item(string path, MediaCategory cat, params string[] people)
    {
        var it = TestUtil.Plain(path, cat, new DateTime(2024, 7, 14));
        it.FaceState = FaceScanState.Scanned;
        it.FaceCount = people.Length;
        it.AutoPeople.AddRange(people);
        return it;
    }

    [Fact]
    public void Each_recognized_person_gets_a_copy_in_their_own_folder_by_category()
    {
        var items = new[]
        {
            Item("/src/a.jpg", MediaCategory.Photo, "Asha", "Ravi"),
            Item("/src/b.mp4", MediaCategory.Video, "Asha"),
            Item("/src/c.png", MediaCategory.Screenshot, "Ravi"),
            Item("/src/d.jpg", MediaCategory.Photo)                         // nobody -> no copy
        };
        var copies = PersonCollections.Plan(items, Settings("/dest"));
        var dests = copies.Select(c => c.DestinationPath.Replace('\\', '/').Replace("/dest/Organized_Media/People/", "")).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Asha/Photos/a.jpg", "Asha/Videos/b.mp4", "Ravi/Photos/a.jpg", "Ravi/Screenshots/c.png" }, dests);
        Assert.Equal(copies.Count, copies.Select(c => c.JournalKey).Distinct().Count());     // never share a journal key
        Assert.All(copies, c => Assert.NotEqual(c.SourcePath, c.JournalKey));
    }

    [Fact]
    public void Duplicates_and_unclassified_are_skipped_and_name_clashes_get_a_suffix()
    {
        var dup = Item("/src/dup.jpg", MediaCategory.Photo, "Asha"); dup.IsDuplicate = true;
        var unc = Item("/src/u.jpg", MediaCategory.Unclassified, "Asha");
        var one = Item("/src/x/IMG_1.jpg", MediaCategory.Photo, "Asha");
        var two = Item("/src/y/IMG_1.jpg", MediaCategory.Photo, "Asha");
        var copies = PersonCollections.Plan(new[] { dup, unc, one, two }, Settings("/dest"));
        Assert.Equal(2, copies.Count);
        Assert.Equal(2, copies.Select(c => c.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Unidentified_collection_is_optional_and_only_for_files_without_recognized_people()
    {
        var s = Settings("/dest");
        var stranger = Item("/src/s.jpg", MediaCategory.Photo); stranger.AutoUnidentified = 1; stranger.FaceCount = 1;
        var known = Item("/src/k.jpg", MediaCategory.Photo, "Asha"); known.AutoUnidentified = 1;

        Assert.Single(PersonCollections.Plan(new[] { stranger, known }, s));
        s.CollectUnidentifiedPeople = true;
        var copies = PersonCollections.Plan(new[] { stranger, known }, s);
        Assert.Equal(2, copies.Count);
        Assert.Contains(copies, c => c.DestinationPath.Contains(PersonCollections.UnidentifiedFolder));
    }

    [Fact]
    public void Unsafe_person_names_become_safe_folder_names()
    {
        var copies = PersonCollections.Plan(new[] { Item("/src/a.jpg", MediaCategory.Photo, "A/B: C?") }, Settings("/dest"));
        var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(copies[0].DestinationPath))!);
        Assert.DoesNotContain(folder, new[] { "" });
        Assert.All(new[] { '/', ':', '?' }, ch => Assert.DoesNotContain(ch, folder));
    }

    [Fact]
    public void Replanning_keeps_finished_copies_marked_verified()
    {
        var items = new[] { Item("/src/a.jpg", MediaCategory.Photo, "Asha") };
        var first = PersonCollections.Plan(items, Settings("/dest"));
        first[0].Status = ItemStatus.Verified; first[0].DestSha256 = "abc"; first[0].HashMatch = true;
        var again = PersonCollections.Plan(items, Settings("/dest"), first);
        Assert.Equal(ItemStatus.Verified, again[0].Status);
    }
}

public class FaceEndToEndTests
{
    private static async Task<(AnalysisResult A, OrganizerService Svc, string Src, string Dest, TempDir T)> Prepare()
    {
        var t = new TempDir();
        var src = System.IO.Path.Combine(t.Path, "source");
        Directory.CreateDirectory(src);
        foreach (var f in new[] { "gps_photo.jpg", "nogps_photo.jpg", "plain_no_exif.jpg", "IMG_0042.PNG" })
            File.Copy(TestUtil.Fixture(f), System.IO.Path.Combine(src, f));

        var settings = new OrganizerSettings
        {
            SourceFolder = src, DestinationFolder = System.IO.Path.Combine(t.Path, "dest"),
            OnlineGeocoding = false, GroupEvents = false, ExportExcel = false
        };
        var svc = new OrganizerService();
        var a = await svc.AnalyzeAsync(settings, null, CancellationToken.None);
        return (a, svc, src, settings.DestinationFolder, t);
    }

    [Fact]
    public async Task Normal_run_with_face_recognition_off_touches_nothing_face_related()
    {
        var (a, svc, _, dest, t) = await Prepare();
        using (t)
        {
            Assert.All(a.Items, i => Assert.Equal(FaceScanState.NotScanned, i.FaceState));
            Assert.Empty(a.PersonCopies);

            var result = await svc.ExecuteAsync(a, null, new PauseControl(), CancellationToken.None);
            Assert.Equal(4, result.Verified);
            Assert.Equal(0, result.PersonVerified);
            Assert.False(Directory.Exists(System.IO.Path.Combine(PathPlanner.OutputRoot(dest), "People")));
            Assert.DoesNotContain(result.ReportFiles, f => f.Contains("People_Report"));
            Assert.False(File.Exists(PersonRegistry.DefaultPath));
        }
    }

    [Fact]
    public async Task Person_collections_are_verified_copies_and_originals_stay_untouched()
    {
        var (a, svc, src, dest, t) = await Prepare();
        using (t)
        {
            var before = Directory.GetFiles(src).ToDictionary(f => f, TestUtil.Hash);

            var gps = a.Items.Single(i => i.FileName == "gps_photo.jpg");
            gps.FaceState = FaceScanState.Scanned; gps.FaceCount = 2; gps.AutoUnidentified = 1; gps.AutoPeople.Add("Asha");
            var plain = a.Items.Single(i => i.FileName == "plain_no_exif.jpg");
            plain.FaceState = FaceScanState.Scanned; plain.FaceCount = 0;

            svc.RebuildPersonCopies(a);
            Assert.Single(a.PersonCopies);

            var result = await svc.ExecuteAsync(a, null, new PauseControl(), CancellationToken.None);
            Assert.Equal(4, result.Verified);
            Assert.Equal(1, result.PersonVerified);
            Assert.Equal(0, result.PersonFailed);

            var copy = System.IO.Path.Combine(PathPlanner.OutputRoot(dest), "People", "Asha", "Photos", "gps_photo.jpg");
            Assert.True(File.Exists(copy));
            Assert.Equal(TestUtil.Hash(gps.SourcePath), TestUtil.Hash(copy));

            // The normal organized copy is still there too, and sources are byte-identical.
            Assert.True(File.Exists(gps.DestinationPath));
            foreach (var (p, h) in before) Assert.Equal(h, TestUtil.Hash(p));

            // Report exists and lists the recognized person.
            var report = result.ReportFiles.Single(f => f.EndsWith("People_Report.csv"));
            var text = File.ReadAllText(report);
            Assert.Contains("Asha", text);
            Assert.Contains("Recognized + unidentified", text);
            Assert.Contains("No people", text);
        }
    }

    [Fact]
    public async Task Resuming_does_not_copy_person_files_again_and_a_new_person_adds_only_the_new_copy()
    {
        var (a, svc, _, dest, t) = await Prepare();
        using (t)
        {
            var gps = a.Items.Single(i => i.FileName == "gps_photo.jpg");
            gps.FaceState = FaceScanState.Scanned; gps.FaceCount = 1; gps.AutoPeople.Add("Asha");
            svc.RebuildPersonCopies(a);
            await svc.ExecuteAsync(a, null, new PauseControl(), CancellationToken.None);

            var folder = System.IO.Path.Combine(PathPlanner.OutputRoot(dest), "People");
            var firstWrite = File.GetLastWriteTimeUtc(System.IO.Path.Combine(folder, "Asha", "Photos", "gps_photo.jpg"));

            // User adds a second person to the same photo (manual correction) and presses Start again.
            FaceCorrections.AddPerson(gps, "Ravi", null);
            svc.RebuildPersonCopies(a);
            Assert.Equal(1, a.PersonCopies.Count(c => c.Status == ItemStatus.Verified));   // Asha's carried over
            Assert.Equal(1, a.PersonCopies.Count(c => c.Status == ItemStatus.Pending));    // only Ravi's is new

            var result = await svc.ExecuteAsync(a, null, new PauseControl(), CancellationToken.None);
            Assert.Equal(2, result.PersonVerified);
            Assert.True(File.Exists(System.IO.Path.Combine(folder, "Ravi", "Photos", "gps_photo.jpg")));
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(System.IO.Path.Combine(folder, "Asha", "Photos", "gps_photo.jpg")));
            Assert.Equal(2, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length);   // no _1 duplicates
        }
    }

    [Fact]
    public async Task Turning_person_folders_off_plans_no_copies()
    {
        var (a, svc, _, _, t) = await Prepare();
        using (t)
        {
            var gps = a.Items.Single(i => i.FileName == "gps_photo.jpg");
            gps.FaceState = FaceScanState.Scanned; gps.FaceCount = 1; gps.AutoPeople.Add("Asha");
            a.Settings.CreatePersonFolders = false;
            svc.RebuildPersonCopies(a);
            Assert.Empty(a.PersonCopies);
        }
    }

    [Fact]
    public async Task Scanner_reads_stored_results_matches_people_and_propagates_to_duplicates()
    {
        // Uses the real models only to construct the engine; the stored results mean no pixels are analysed.
        if (!FaceModels.TryLocate(out var det, out var rec)) return;

        var (a, svc, src, _, t) = await Prepare();
        using (t)
        {
            var asha = FaceTestUtil.Vec(1);
            var item = a.Items.Single(i => i.FileName == "gps_photo.jpg");
            var dupe = a.Items.Single(i => i.FileName == "nogps_photo.jpg");
            dupe.IsDuplicate = true; dupe.DuplicateOfPath = item.SourcePath;      // pretend: exact duplicate of gps_photo

            var opts = new FaceOptions();
            var fi = new FileInfo(item.SourcePath);
            using (var cache = AppCache.TryOpenDefault()!)
                cache.PutFaceScan(item.SourcePath, fi.Length, fi.LastWriteTimeUtc.Ticks, FaceScanner.CacheKey(item, opts),
                    JsonSerializer.Serialize(FaceTestUtil.Scan((0, asha), (0, FaceTestUtil.Vec(60)))));

            using var engine = new FaceEngine(det, rec);
            var registry = FaceTestUtil.Registry(("Asha", asha));
            var summary = await svc.ScanFacesAsync(a, new[] { item }, opts, null, CancellationToken.None, engine, registry);

            Assert.Equal(1, summary.FromCache);
            Assert.Equal(new[] { "Asha" }, item.People);
            Assert.Equal(1, item.UnidentifiedFaces);
            Assert.Equal(new[] { "Asha" }, dupe.People);                          // inherited
            Assert.Equal(FaceScanState.NotScanned, a.Items.First(i => i.FileName == "IMG_0042.PNG").FaceState);   // out of scope: untouched
            Assert.Single(a.PersonCopies);                                         // duplicate skipped, original copied
        }
    }

    [Fact]
    public async Task Unclassified_files_are_not_scannable_and_are_reported_not_crashed()
    {
        if (!FaceModels.TryLocate(out var det, out var rec)) return;
        using var t = new TempDir();
        var bad = TestUtil.Plain(t.File("corrupt.jpg", "xx"), MediaCategory.Unclassified, DateTime.Now);
        using var engine = new FaceEngine(det, rec);
        var summary = await new FaceScanner(engine, null, null).ScanAsync(new[] { bad }, new PersonRegistry(), new FaceOptions(), null, CancellationToken.None);
        Assert.Equal(1, summary.NotScannable);
        Assert.Equal(FaceScanState.Unsupported, bad.FaceState);
    }

    [Fact]
    public async Task Cancelling_a_scan_stops_cleanly()
    {
        if (!FaceModels.TryLocate(out var det, out var rec)) return;
        using var t = new TempDir();
        var items = Enumerable.Range(0, 5).Select(i =>
        {
            var it = TestUtil.Plain(t.File($"p{i}.jpg", "x"), MediaCategory.Photo, DateTime.Now);
            return it;
        }).ToList();
        using var engine = new FaceEngine(det, rec);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var summary = await new FaceScanner(engine, null, null).ScanAsync(items, new PersonRegistry(), new FaceOptions(), null, cts.Token);
        Assert.True(summary.Cancelled);
    }
}

public class FaceEngineTests
{
    [FactIfModels]
    public void A_picture_without_a_face_finds_none_and_embeddings_are_unit_length()
    {
        FaceModels.TryLocate(out var det, out var rec);
        using var engine = new FaceEngine(det, rec);

        var rnd = new Random(7);
        var noise = new byte[320 * 240 * 3];
        rnd.NextBytes(noise);
        var flat = new RgbImage(320, 240, Enumerable.Repeat((byte)128, 320 * 240 * 3).ToArray());
        Assert.Empty(engine.Detect(flat));
        Assert.Empty(engine.Detect(new RgbImage(320, 240, noise)));

        // Embedding a made-up face region still yields a valid 128-number unit vector.
        var face = new DetectedFace { X = 60, Y = 40, W = 120, H = 120, Score = 1, Landmarks = FaceGeometry.AlignTemplate.Select((v, i) => v + (i % 2 == 0 ? 60 : 40)).ToArray() };
        var emb = engine.Embed(flat, face);
        Assert.Equal(128, emb.Length);
        Assert.Equal(1.0, Math.Sqrt(emb.Sum(x => x * x)), 3);
    }

    [FactIfModels]
    public void Model_loading_failure_is_reported_not_thrown()
    {
        var saved = Environment.GetEnvironmentVariable("MEDIAORGANIZER_MODELS");
        using var t = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(t.Path, FaceModels.DetectorFile), "not a model");
        File.WriteAllText(System.IO.Path.Combine(t.Path, FaceModels.RecognizerFile), "not a model");
        try
        {
            Environment.SetEnvironmentVariable("MEDIAORGANIZER_MODELS", t.Path);
            var engine = FaceEngine.TryCreate(out var error);
            Assert.Null(engine);
            Assert.False(string.IsNullOrEmpty(error));
        }
        finally { Environment.SetEnvironmentVariable("MEDIAORGANIZER_MODELS", saved); }
    }
}
