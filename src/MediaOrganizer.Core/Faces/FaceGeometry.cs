namespace MediaOrganizer.Core.Faces;

public sealed class DetectedFace
{
    public float X { get; init; }
    public float Y { get; init; }
    public float W { get; init; }
    public float H { get; init; }
    public float Score { get; init; }
    /// <summary>Five landmarks as x,y pairs: right eye, left eye, nose tip, right mouth corner, left mouth corner.</summary>
    public float[] Landmarks { get; init; } = new float[10];
    public float[]? Embedding { get; set; }
}

/// <summary>Pure math used by the face pipeline: overlap, non-max suppression, alignment and similarity.</summary>
public static class FaceGeometry
{
    /// <summary>Standard 112x112 template used by SFace / ArcFace-style recognisers.</summary>
    public static readonly float[] AlignTemplate =
    {
        38.2946f, 51.6963f,
        73.5318f, 51.5014f,
        56.0252f, 71.7366f,
        41.5493f, 92.3655f,
        70.7299f, 92.2041f
    };

    public const int AlignSize = 112;

    public static float Iou(DetectedFace a, DetectedFace b)
    {
        float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Keeps the best-scoring face of every group of overlapping boxes.</summary>
    public static List<DetectedFace> Nms(IEnumerable<DetectedFace> faces, float iouThreshold)
    {
        var sorted = faces.OrderByDescending(f => f.Score).ToList();
        var kept = new List<DetectedFace>();
        foreach (var f in sorted)
            if (kept.All(k => Iou(k, f) <= iouThreshold)) kept.Add(f);
        return kept;
    }

    /// <summary>
    /// Least-squares similarity transform (rotation + uniform scale + shift) that maps the detected landmarks onto
    /// the template. Returns a, b, tx, ty with: u = a*x - b*y + tx, v = b*x + a*y + ty.
    /// </summary>
    public static (double a, double b, double tx, double ty) EstimateSimilarity(float[] src, float[] dst)
    {
        int n = src.Length / 2;
        double msx = 0, msy = 0, mdx = 0, mdy = 0;
        for (int i = 0; i < n; i++) { msx += src[2 * i]; msy += src[2 * i + 1]; mdx += dst[2 * i]; mdy += dst[2 * i + 1]; }
        msx /= n; msy /= n; mdx /= n; mdy /= n;

        double num1 = 0, num2 = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double sx = src[2 * i] - msx, sy = src[2 * i + 1] - msy;
            double dx = dst[2 * i] - mdx, dy = dst[2 * i + 1] - mdy;
            num1 += sx * dx + sy * dy;
            num2 += sx * dy - sy * dx;
            den += sx * sx + sy * sy;
        }
        if (den < 1e-9) return (1, 0, mdx - msx, mdy - msy);

        double a = num1 / den, b = num2 / den;
        double tx = mdx - (a * msx - b * msy);
        double ty = mdy - (b * msx + a * msy);
        return (a, b, tx, ty);
    }

    /// <summary>Warps the face into the 112x112 template frame (bilinear, black outside the picture). Output is RGB.</summary>
    public static byte[] AlignCrop(RgbImage img, float[] landmarks)
    {
        var (a, b, tx, ty) = EstimateSimilarity(landmarks, AlignTemplate);
        double det = a * a + b * b;
        var output = new byte[AlignSize * AlignSize * 3];
        if (det < 1e-12) return output;

        for (int v = 0; v < AlignSize; v++)
        {
            for (int u = 0; u < AlignSize; u++)
            {
                // inverse map: [x,y] = R^-1 * ([u,v] - t)
                double pu = u - tx, pv = v - ty;
                double x = (a * pu + b * pv) / det;
                double y = (-b * pu + a * pv) / det;
                Sample(img, x, y, output, (v * AlignSize + u) * 3);
            }
        }
        return output;
    }

    private static void Sample(RgbImage img, double x, double y, byte[] dst, int o)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 3; c++)
        {
            double p00 = Px(img, x0, y0, c), p10 = Px(img, x0 + 1, y0, c);
            double p01 = Px(img, x0, y0 + 1, c), p11 = Px(img, x0 + 1, y0 + 1, c);
            double v = p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy) + p01 * (1 - fx) * fy + p11 * fx * fy;
            dst[o + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
        }
    }

    private static double Px(RgbImage img, int x, int y, int c) =>
        x < 0 || y < 0 || x >= img.Width || y >= img.Height ? 0 : img.Data[(y * img.Width + x) * 3 + c];

    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var f in v) sum += f * f;
        var norm = (float)Math.Sqrt(sum);
        if (norm < 1e-9f) return v;
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = v[i] / norm;
        return r;
    }

    /// <summary>Cosine similarity of two L2-normalised vectors (so just the dot product).</summary>
    public static float Cosine(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double s = 0;
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return (float)s;
    }

    public static string ToBase64(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[] FromBase64(string s)
    {
        var bytes = Convert.FromBase64String(s);
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
        return v;
    }
}
