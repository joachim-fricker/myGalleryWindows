using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>Ein gefundenes Gesicht: Box relativ (0..1) zum gedrehten Bild, normierter Vektor, Schärfe.</summary>
public sealed record DetectedFace(float L, float T, float R, float B, float[] Embedding, float Sharpness);

/// <summary>
/// Gesichtserkennung unter Windows (Ersatz für ML Kit + FaceAnalyzer aus Faces.kt):
///  1. SCRFD (InsightFace det_500m.onnx) findet Gesichter + 5 Merkmalspunkte
///  2. 5-Punkt-Ausrichtung auf die ArcFace-Vorlage (112×112), wie in der Android-App
///  3. Gesichtsmodell face_model.onnx (InsightFace w600k_mbf, dasselbe wie auf dem Handy) → Vektor
/// Gleiches Modell + gleiche Ausrichtung = gleiche ModelId → die Daten vom Handy passen dazu.
/// </summary>
public sealed class FaceAnalyzer : IDisposable
{
    public const string EmbedderFile = "face_model.onnx";
    public static readonly string[] DetectorFiles = ["face_detector.onnx", "det_500m.onnx", "det_10g.onnx"];
    private const int AlignVersion = 2;          // wie FaceEmbedder.ALIGN_VERSION
    private const int DecodeMax = 1280;
    private const int MinFacePx = 56;            // kleinere Gesichter (bei 1280er Bild) werden ignoriert
    private const int DetSize = 640;
    private const float DetThreshold = 0.5f;
    private const float NmsThreshold = 0.4f;

    /// <summary>ArcFace-Standardvorlage für 112×112: linkes Auge, rechtes Auge, Nase, linker/rechter Mundwinkel (Bildseite).</summary>
    private static readonly float[] Template =
    [
        38.2946f, 51.6963f,
        73.5318f, 51.5014f,
        56.0252f, 71.7366f,
        41.5493f, 92.3655f,
        70.7299f, 92.2041f,
    ];

    private readonly InferenceSession _det;
    private readonly InferenceSession _emb;
    private readonly string _detInput, _embInput;
    private readonly bool _embNchw;
    private readonly int _embW, _embH;

    public string ModelId { get; }
    public ModelKind Kind => ModelKind.InsightFace;

    /// <summary>Ordner, in denen die Modelle gesucht werden: Assets der App und %LOCALAPPDATA%\MyGallery.</summary>
    private static IEnumerable<string> Folders =>
        [Path.Combine(AppContext.BaseDirectory, "Assets"), AppServices.DataFolder];

    public static string? FindFile(params string[] names) =>
        Folders.SelectMany(f => names.Select(n => Path.Combine(f, n))).FirstOrDefault(File.Exists);

    /// <summary>Beide Modelle vorhanden? Sonst Hinweis, was fehlt.</summary>
    public static string? MissingModels()
    {
        var missing = new List<string>();
        if (FindFile(EmbedderFile) is null) missing.Add(EmbedderFile);
        if (FindFile(DetectorFiles) is null) missing.Add("face_detector.onnx (det_500m.onnx aus buffalo_s)");
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    private FaceAnalyzer(string detPath, string embPath)
    {
        var opts = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        _det = new InferenceSession(detPath, opts);
        _emb = new InferenceSession(embPath, opts);
        _detInput = _det.InputMetadata.Keys.First();
        _embInput = _emb.InputMetadata.Keys.First();
        var shape = _emb.InputMetadata[_embInput].Dimensions;
        _embNchw = shape.Length == 4 && shape[1] == 3;
        int Dim(int v) => v > 0 ? v : 112;
        _embH = _embNchw ? Dim(shape[2]) : Dim(shape[1]);
        _embW = _embNchw ? Dim(shape[3]) : Dim(shape[2]);
        ModelId = $"onnx:{new FileInfo(embPath).Length}:a{AlignVersion}";
    }

    public static FaceAnalyzer? TryCreate()
    {
        var det = FindFile(DetectorFiles);
        var emb = FindFile(EmbedderFile);
        if (det is null || emb is null) return null;
        try
        {
            return new FaceAnalyzer(det, emb);
        }
        catch (Exception ex)
        {
            Log.Write($"Gesichtsmodelle laden: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _det.Dispose();
        _emb.Dispose();
    }

    // ------------------------------------------------------------------ Bild

    /// <summary>Bild (BGRA, gedreht) mit max. 1280 px.</summary>
    private sealed record Pixels(byte[] Data, int Width, int Height)
    {
        /// <summary>Bilinear: (b, g, r) an Position x, y</summary>
        public (float B, float G, float R) Sample(float x, float y)
        {
            if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1) return (0, 0, 0);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
            float fx = x - x0, fy = y - y0;
            float Ch(int c)
            {
                float a = Data[(y0 * Width + x0) * 4 + c], b = Data[(y0 * Width + x1) * 4 + c];
                float d = Data[(y1 * Width + x0) * 4 + c], e = Data[(y1 * Width + x1) * 4 + c];
                return (a + (b - a) * fx) * (1 - fy) + (d + (e - d) * fx) * fy;
            }
            return (Ch(0), Ch(1), Ch(2));
        }
    }

    private static Pixels? Decode(GalleryImage img)
    {
        int w = 0, h = 0;
        if (img.Width > DecodeMax || img.Height > DecodeMax)
        {
            if (img.Width >= img.Height) w = DecodeMax; else h = DecodeMax;
        }
        var bmp = ThumbnailCache.Decode(img.Path, w, h, img.Orientation);
        if (bmp is null) return null;
        var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var data = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(data, stride, 0);
        return new Pixels(data, bgra.PixelWidth, bgra.PixelHeight);
    }

    // ------------------------------------------------------------------ Erkennung

    private sealed record Candidate(float X1, float Y1, float X2, float Y2, float Score, float[] Kps);

    /// <summary>Läuft im Hintergrund-Thread.</summary>
    public List<DetectedFace> Analyze(GalleryImage img)
    {
        var px = Decode(img);
        if (px is null) return [];
        var result = new List<DetectedFace>();
        foreach (var c in Detect(px))
        {
            if (c.X2 - c.X1 < MinFacePx) continue;
            if (!Frontal(c.Kps)) continue;
            var crop = Align(px, c.Kps);
            float sharp = Sharpness(crop);
            var e = Embed(crop);
            result.Add(new DetectedFace(
                Math.Clamp(c.X1 / px.Width, 0, 1), Math.Clamp(c.Y1 / px.Height, 0, 1),
                Math.Clamp(c.X2 / px.Width, 0, 1), Math.Clamp(c.Y2 / px.Height, 0, 1), e, sharp));
        }
        return result;
    }

    /// <summary>Ersatz für die Kopfwinkel von ML Kit: zu seitliche oder zu stark geneigte Gesichter auslassen.</summary>
    private static bool Frontal(float[] k)
    {
        float eyeDist = MathF.Abs(k[2] - k[0]);
        if (eyeDist < 1) return false;
        float eyeMidX = (k[0] + k[2]) / 2, eyeY = (k[1] + k[3]) / 2, mouthY = (k[7] + k[9]) / 2;
        float yaw = MathF.Abs(k[4] - eyeMidX) / eyeDist;              // ~0 frontal, ~0.5 bei ~30°
        float pitch = (k[5] - eyeY) / Math.Max(1, mouthY - eyeY);       // Nase zwischen Augen und Mund
        return yaw <= 0.45f && pitch is > 0.2f and < 0.85f;
    }

    /// <summary>SCRFD: Bild auf 640×640 (Seitenverhältnis bleibt, Rand unten/rechts), Ausgaben je Schrittweite 8/16/32.</summary>
    private List<Candidate> Detect(Pixels px)
    {
        float scale = Math.Min((float)DetSize / px.Width, (float)DetSize / px.Height);
        int nw = (int)(px.Width * scale), nh = (int)(px.Height * scale);
        var input = new DenseTensor<float>(new[] { 1, 3, DetSize, DetSize });
        var buf = input.Buffer.Span;
        const int plane = DetSize * DetSize;
        const float pad = (0 - 127.5f) / 128f;
        for (int y = 0; y < DetSize; y++)
        for (int x = 0; x < DetSize; x++)
        {
            int i = y * DetSize + x;
            if (x >= nw || y >= nh)
            {
                buf[i] = pad; buf[plane + i] = pad; buf[2 * plane + i] = pad;
                continue;
            }
            var (b, g, r) = px.Sample((x + 0.5f) / scale - 0.5f, (y + 0.5f) / scale - 0.5f);
            buf[i] = (r - 127.5f) / 128f;
            buf[plane + i] = (g - 127.5f) / 128f;
            buf[2 * plane + i] = (b - 127.5f) / 128f;
        }

        using var outputs = _det.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_detInput, input) });
        var outs = outputs.Select(o => o.AsTensor<float>().ToArray()).ToList();
        int fmc = outs.Count == 9 ? 3 : outs.Count == 15 ? 5 : 3;
        int[] strides = fmc == 3 ? new[] { 8, 16, 32 } : new[] { 8, 16, 32, 64, 128 };
        bool hasKps = outs.Count == fmc * 3;
        const int anchors = 2;

        var cands = new List<Candidate>();
        for (int s = 0; s < fmc; s++)
        {
            int stride = strides[s];
            var scores = outs[s];
            var boxes = outs[s + fmc];
            var kps = hasKps ? outs[s + fmc * 2] : null;
            int fw = DetSize / stride, fh = DetSize / stride;
            for (int i = 0; i < scores.Length; i++)
            {
                if (scores[i] < DetThreshold) continue;
                int cell = i / anchors;
                float cx = cell % fw * stride, cy = cell / fw * stride;
                if (cell / fw >= fh) continue;
                float x1 = cx - boxes[i * 4] * stride, y1 = cy - boxes[i * 4 + 1] * stride;
                float x2 = cx + boxes[i * 4 + 2] * stride, y2 = cy + boxes[i * 4 + 3] * stride;
                var k = new float[10];
                if (kps is not null)
                    for (int j = 0; j < 5; j++)
                    {
                        k[2 * j] = (cx + kps[i * 10 + 2 * j] * stride) / scale;
                        k[2 * j + 1] = (cy + kps[i * 10 + 2 * j + 1] * stride) / scale;
                    }
                cands.Add(new Candidate(x1 / scale, y1 / scale, x2 / scale, y2 / scale, scores[i], k));
            }
        }
        return hasKps ? Nms(cands) : new List<Candidate>();
    }

    private static List<Candidate> Nms(List<Candidate> cands)
    {
        var keep = new List<Candidate>();
        foreach (var c in cands.OrderByDescending(c => c.Score))
        {
            if (keep.All(k => Iou(k, c) <= NmsThreshold)) keep.Add(c);
        }
        return keep;
    }

    private static float Iou(Candidate a, Candidate b)
    {
        float iw = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1), ih = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (iw <= 0 || ih <= 0) return 0;
        float inter = iw * ih;
        float union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // ------------------------------------------------------------------ Ausrichtung + Vektor

    /// <summary>Ähnlichkeitstransformation der 5 Punkte auf die Vorlage (wie align() in Faces.kt), bilinear.</summary>
    private Pixels Align(Pixels src, float[] p)
    {
        int outW = _embW, outH = _embH;
        float sx = outW / 112f, sy = outH / 112f;
        float mx = 0, my = 0, mu = 0, mv = 0;
        for (int i = 0; i < 5; i++)
        {
            mx += p[2 * i]; my += p[2 * i + 1];
            mu += Template[2 * i] * sx; mv += Template[2 * i + 1] * sy;
        }
        mx /= 5; my /= 5; mu /= 5; mv /= 5;
        float num1 = 0, num2 = 0, den = 0;
        for (int i = 0; i < 5; i++)
        {
            float x = p[2 * i] - mx, y = p[2 * i + 1] - my;
            float u = Template[2 * i] * sx - mu, v = Template[2 * i + 1] * sy - mv;
            num1 += x * u + y * v;
            num2 += x * v - y * u;
            den += x * x + y * y;
        }
        float a = den > 0 ? num1 / den : 1, bb = den > 0 ? num2 / den : 0;
        float tx = mu - (a * mx - bb * my), ty = mv - (bb * mx + a * my);
        float d = a * a + bb * bb;
        var data = new byte[outW * outH * 4];
        for (int v = 0; v < outH; v++)
        for (int u = 0; u < outW; u++)
        {
            // Zielpunkt (u, v) zurück ins Quellbild: Umkehrung von [a -b; b a]
            float du = u + 0.5f - tx, dv = v + 0.5f - ty;
            float x = (a * du + bb * dv) / d - 0.5f, y = (-bb * du + a * dv) / d - 0.5f;
            var (B, G, R) = src.Sample(x, y);
            int o = (v * outW + u) * 4;
            data[o] = (byte)B; data[o + 1] = (byte)G; data[o + 2] = (byte)R; data[o + 3] = 255;
        }
        return new Pixels(data, outW, outH);
    }

    /// <summary>Schärfe als Varianz des Laplace-Filters auf Graustufen (wie sharpness() in Faces.kt).</summary>
    private static float Sharpness(Pixels px)
    {
        int w = px.Width, h = px.Height;
        var g = new float[w * h];
        for (int i = 0; i < g.Length; i++)
            g[i] = 0.299f * px.Data[i * 4 + 2] + 0.587f * px.Data[i * 4 + 1] + 0.114f * px.Data[i * 4];
        double sum = 0, sum2 = 0;
        int n = 0;
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            float l = 4 * g[i] - g[i - 1] - g[i + 1] - g[i - w] - g[i + w];
            sum += l; sum2 += l * l; n++;
        }
        if (n == 0) return 0;
        double mean = sum / n;
        return (float)(sum2 / n - mean * mean);
    }

    /// <summary>ArcFace: RGB, (x-127.5)/127.5, Ausgabe normiert.</summary>
    private float[] Embed(Pixels face)
    {
        int w = _embW, h = _embH;
        var t = _embNchw ? new DenseTensor<float>(new[] { 1, 3, h, w }) : new DenseTensor<float>(new[] { 1, h, w, 3 });
        var buf = t.Buffer.Span;
        int plane = w * h;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x, o = i * 4;
            float r = (face.Data[o + 2] - 127.5f) / 127.5f, g = (face.Data[o + 1] - 127.5f) / 127.5f, b = (face.Data[o] - 127.5f) / 127.5f;
            if (_embNchw) { buf[i] = r; buf[plane + i] = g; buf[2 * plane + i] = b; }
            else { buf[3 * i] = r; buf[3 * i + 1] = g; buf[3 * i + 2] = b; }
        }
        using var outputs = _emb.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_embInput, t) });
        var e = outputs.First().AsTensor<float>().ToArray();
        float n = MathF.Sqrt(e.Sum(v => v * v));
        if (n > 0) for (int i = 0; i < e.Length; i++) e[i] /= n;
        return e;
    }
}
