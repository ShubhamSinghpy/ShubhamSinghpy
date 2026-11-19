using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MediaOrganizer.Core.Faces;

/// <summary>Finds the two ONNX model files. They ship next to the exe in a "models" folder (see tools\get-models.ps1).</summary>
public static class FaceModels
{
    public const string DetectorFile = "face_detection_yunet_2023mar.onnx";
    public const string RecognizerFile = "face_recognition_sface_2021dec.onnx";

    /// <summary>Bump when the models or preprocessing change so cached face data is not reused.</summary>
    public const string Version = "yunet2023mar-sface2021dec-v1";

    public static bool TryLocate(out string detector, out string recognizer)
    {
        detector = recognizer = "";
        foreach (var dir in CandidateFolders())
        {
            var d = Path.Combine(dir, DetectorFile);
            var r = Path.Combine(dir, RecognizerFile);
            if (File.Exists(d) && File.Exists(r)) { detector = d; recognizer = r; return true; }
        }
        return false;
    }

    private static IEnumerable<string> CandidateFolders()
    {
        var env = Environment.GetEnvironmentVariable("MEDIAORGANIZER_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        yield return AppContext.BaseDirectory;
    }
}

/// <summary>
/// Local face detection (YuNet) and face embedding (SFace) with ONNX Runtime. Everything runs on this PC: no network
/// access, nothing is uploaded. The two sessions are created only when an engine is constructed, which the app does
/// only after the user has switched face recognition on and started a scan.
/// </summary>
public sealed class FaceEngine : IDisposable
{
    private const int DetSize = 640;
    private static readonly int[] Strides = { 8, 16, 32 };

    private readonly InferenceSession _detector;
    private readonly InferenceSession _recognizer;
    private readonly string _recInputName;
    private readonly string _detInputName;

    /// <summary>SFace expects RGB order (OpenCV swaps channels when it builds the input blob).</summary>
    public bool RecognizerUsesRgb { get; set; } = true;

    public FaceEngine(string detectorPath, string recognizerPath)
    {
        using var opts = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        _detector = new InferenceSession(detectorPath, opts);
        _recognizer = new InferenceSession(recognizerPath, opts);
        _detInputName = _detector.InputMetadata.Keys.First();
        _recInputName = _recognizer.InputMetadata.Keys.First();
    }

    public static FaceEngine? TryCreate(out string? error)
    {
        error = null;
        if (!FaceModels.TryLocate(out var det, out var rec))
        {
            error = "Face models were not found. Run tools\\get-models.ps1 once (about 40 MB), then restart the app.";
            return null;
        }
        try { return new FaceEngine(det, rec); }
        catch (Exception ex)
        {
            error = "The face models could not be loaded: " + ex.Message;
            return null;
        }
    }

    // ================================================================== detection
    public List<DetectedFace> Detect(RgbImage img, float scoreThreshold = 0.85f, float nmsIou = 0.3f, int minFacePx = 24)
    {
        float scale = DetSize / (float)Math.Max(img.Width, img.Height);
        int nw = Math.Max(1, (int)Math.Round(img.Width * scale));
        int nh = Math.Max(1, (int)Math.Round(img.Height * scale));

        var input = new DenseTensor<float>(new[] { 1, 3, DetSize, DetSize });
        FillDetectorInput(img, nw, nh, input.Buffer.Span);

        using var results = _detector.Run(new[] { NamedOnnxValue.CreateFromTensor(_detInputName, input) });
        var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());

        var found = new List<DetectedFace>();
        foreach (int stride in Strides)
        {
            var cls = byName["cls_" + stride];
            var obj = byName["obj_" + stride];
            var bbox = byName["bbox_" + stride];
            var kps = byName["kps_" + stride];
            int cols = DetSize / stride, rows = DetSize / stride;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    float score = MathF.Sqrt(Math.Clamp(cls[idx], 0f, 1f) * Math.Clamp(obj[idx], 0f, 1f));
                    if (score < scoreThreshold) continue;

                    float cx = (c + bbox[idx * 4 + 0]) * stride;
                    float cy = (r + bbox[idx * 4 + 1]) * stride;
                    float w = MathF.Exp(bbox[idx * 4 + 2]) * stride;
                    float h = MathF.Exp(bbox[idx * 4 + 3]) * stride;

                    var lm = new float[10];
                    for (int n = 0; n < 5; n++)
                    {
                        lm[2 * n] = (kps[idx * 10 + 2 * n] + c) * stride / scale;
                        lm[2 * n + 1] = (kps[idx * 10 + 2 * n + 1] + r) * stride / scale;
                    }

                    found.Add(new DetectedFace
                    {
                        X = (cx - w / 2) / scale,
                        Y = (cy - h / 2) / scale,
                        W = w / scale,
                        H = h / scale,
                        Score = score,
                        Landmarks = lm
                    });
                }
            }
        }

        return FaceGeometry.Nms(found, nmsIou).Where(f => Math.Min(f.W, f.H) >= minFacePx).ToList();
    }

    /// <summary>Resizes into the top-left of a 640x640 canvas. YuNet wants BGR order with raw 0-255 values.</summary>
    private static void FillDetectorInput(RgbImage img, int nw, int nh, Span<float> dst)
    {
        const int plane = DetSize * DetSize;
        double sx = img.Width / (double)nw, sy = img.Height / (double)nh;

        for (int y = 0; y < nh; y++)
        {
            double fy = (y + 0.5) * sy - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, img.Height - 1);
            int y1 = Math.Min(y0 + 1, img.Height - 1);
            double wy = Math.Clamp(fy - y0, 0, 1);

            for (int x = 0; x < nw; x++)
            {
                double fx = (x + 0.5) * sx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, img.Width - 1);
                int x1 = Math.Min(x0 + 1, img.Width - 1);
                double wx = Math.Clamp(fx - x0, 0, 1);

                for (int c = 0; c < 3; c++) // c = RGB channel in the source
                {
                    double p = img.Data[(y0 * img.Width + x0) * 3 + c] * (1 - wx) * (1 - wy)
                             + img.Data[(y0 * img.Width + x1) * 3 + c] * wx * (1 - wy)
                             + img.Data[(y1 * img.Width + x0) * 3 + c] * (1 - wx) * wy
                             + img.Data[(y1 * img.Width + x1) * 3 + c] * wx * wy;
                    dst[(2 - c) * plane + y * DetSize + x] = (float)p; // BGR planes
                }
            }
        }
    }

    // ================================================================== recognition
    /// <summary>128-number face fingerprint, length-normalised so a dot product is the cosine similarity.</summary>
    public float[] Embed(RgbImage img, DetectedFace face)
    {
        var aligned = FaceGeometry.AlignCrop(img, face.Landmarks);
        int size = FaceGeometry.AlignSize;
        var input = new DenseTensor<float>(new[] { 1, 3, size, size });
        var span = input.Buffer.Span;
        int plane = size * size;

        for (int i = 0; i < plane; i++)
        {
            byte r = aligned[i * 3], g = aligned[i * 3 + 1], b = aligned[i * 3 + 2];
            if (RecognizerUsesRgb) { span[i] = r; span[plane + i] = g; span[2 * plane + i] = b; }
            else { span[i] = b; span[plane + i] = g; span[2 * plane + i] = r; }
        }

        using var results = _recognizer.Run(new[] { NamedOnnxValue.CreateFromTensor(_recInputName, input) });
        var vec = results.First().AsTensor<float>().ToArray();
        return FaceGeometry.Normalize(vec);
    }

    /// <summary>Detects every face in the picture and fingerprints each one.</summary>
    public List<DetectedFace> DetectAndEmbed(RgbImage img, float scoreThreshold = 0.85f, int minFacePx = 24)
    {
        var faces = Detect(img, scoreThreshold, 0.3f, minFacePx);
        foreach (var f in faces) f.Embedding = Embed(img, f);
        return faces;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }
}
