using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.Faces;

/// <summary>A registered person: a name plus the face fingerprints computed from 1 to 4 reference photos.</summary>
public sealed class PersonProfile
{
    public string Name { get; set; } = "";
    /// <summary>Base64 face fingerprints (128 numbers each). The reference photos themselves are not stored.</summary>
    public List<string> Embeddings { get; set; } = new();
    /// <summary>File names (not paths) of the reference photos, for display only.</summary>
    public List<string> ReferenceNames { get; set; } = new();

    [JsonIgnore] public int ReferenceCount => Embeddings.Count;
    [JsonIgnore] internal List<float[]>? Vectors { get; set; }

    public List<float[]> GetVectors() => Vectors ??= Embeddings.Select(FaceGeometry.FromBase64).ToList();
}

public enum ReferenceOutcome { Used, NoFace, MultipleFacesUsedLargest, Unreadable, TooSmall }

public sealed record ReferenceResult(string File, ReferenceOutcome Outcome, string Message);

/// <summary>
/// The people the user has registered. Stored only on this PC (%LocalAppData%\MediaOrganizer\people.json) as face
/// fingerprints, never as pictures, and never sent anywhere.
/// </summary>
public sealed class PersonRegistry
{
    public const int MaxReferencePhotos = 4;

    public List<PersonProfile> People { get; set; } = new();

    public static string DefaultPath => Path.Combine(OrganizerSettings.AppDataFolder, "people.json");

    public static PersonRegistry Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PersonRegistry>(File.ReadAllText(path)) ?? new PersonRegistry();
        }
        catch (Exception ex) { AppLog.Warn("Could not load the people list; starting empty.", ex); }
        return new PersonRegistry();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    public PersonProfile? Find(string name) =>
        People.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a person, or replaces the references of an existing one with the same name.</summary>
    public PersonProfile AddOrReplace(string name, IEnumerable<float[]> embeddings, IEnumerable<string> referenceNames)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Please enter a name.");
        var list = embeddings.Take(MaxReferencePhotos).ToList();
        if (list.Count == 0) throw new ArgumentException("At least one reference photo with a clear face is needed.");

        var existing = Find(name);
        if (existing != null) People.Remove(existing);

        var p = new PersonProfile
        {
            Name = name,
            Embeddings = list.Select(FaceGeometry.ToBase64).ToList(),
            ReferenceNames = referenceNames.Take(MaxReferencePhotos).ToList()
        };
        People.Add(p);
        People.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return p;
    }

    public bool Remove(string name)
    {
        var p = Find(name);
        return p != null && People.Remove(p);
    }

    /// <summary>
    /// Builds a person from 1 to 4 reference photos. Each photo should show that person; if several faces are
    /// found, the largest is used (and reported so the user can double-check).
    /// </summary>
    public static (List<float[]> Embeddings, List<ReferenceResult> Results) BuildFromPhotos(
        FaceEngine engine, IEnumerable<string> photoPaths, float scoreThreshold = 0.7f)
    {
        var embeddings = new List<float[]>();
        var results = new List<ReferenceResult>();

        foreach (var path in photoPaths.Take(MaxReferencePhotos))
        {
            var file = Path.GetFileName(path);
            var img = ImageLoader.TryLoad(path, out var error);
            if (img == null) { results.Add(new(file, ReferenceOutcome.Unreadable, error ?? "Could not be read.")); continue; }

            var faces = engine.Detect(img, scoreThreshold, 0.3f, minFacePx: 24);
            if (faces.Count == 0) { results.Add(new(file, ReferenceOutcome.NoFace, "No face found. Use a clear, front-facing photo.")); continue; }

            var best = faces.OrderByDescending(f => f.W * f.H).First();
            if (Math.Min(best.W, best.H) < 60)
            {
                results.Add(new(file, ReferenceOutcome.TooSmall, "The face is too small. Use a closer photo."));
                continue;
            }

            embeddings.Add(engine.Embed(img, best));
            results.Add(faces.Count > 1
                ? new(file, ReferenceOutcome.MultipleFacesUsedLargest, $"{faces.Count} faces found; the largest was used.")
                : new(file, ReferenceOutcome.Used, "Face found."));
        }
        return (embeddings, results);
    }

    /// <summary>Best matching person for a face fingerprint, using the highest similarity over the person's references.</summary>
    public (PersonProfile? Person, float Score) Best(float[] embedding)
    {
        PersonProfile? best = null;
        float bestScore = -1;
        foreach (var p in People)
        {
            foreach (var v in p.GetVectors())
            {
                var s = FaceGeometry.Cosine(embedding, v);
                if (s > bestScore) { bestScore = s; best = p; }
            }
        }
        return (best, bestScore);
    }

    public float Score(PersonProfile person, float[] embedding)
    {
        float best = -1;
        foreach (var v in person.GetVectors()) best = Math.Max(best, FaceGeometry.Cosine(embedding, v));
        return best;
    }
}
