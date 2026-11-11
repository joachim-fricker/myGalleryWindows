using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>
/// Vorschaubilder: im Speicher (die zuletzt benutzten) und auf der Festplatte (thumbs\xx\hash.jpg).
/// Kürzere Seite = <see cref="Size"/> Pixel, bereits gedreht.
/// Geladen wird von wenigen eigenen Hintergrund-Threads; die zuletzt angeforderten Bilder (= gerade sichtbar)
/// kommen zuerst dran, abgebrochene Anforderungen werden übersprungen.
/// </summary>
public sealed class ThumbnailCache
{
    public const int Size = 256;
    private const int MemoryCapacity = 400;

    private sealed record Request(string Key, Func<string, BitmapSource?> Load, string What,
                                  TaskCompletionSource<BitmapSource?> Result, CancellationToken Ct);

    private readonly string _folder;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _map = [];
    private readonly LinkedList<(string Key, BitmapSource Image)> _lru = new();
    private readonly object _queueLock = new();
    private readonly Stack<Request> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _requests, _loaded;

    public ThumbnailCache(string folder)
    {
        _folder = folder;
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (int i = 0; i < workers; i++)
        {
            new Thread(Work) { IsBackground = true, Name = $"Thumbs {i + 1}", Priority = ThreadPriority.BelowNormal }.Start();
        }
    }

    public string Folder => _folder;

    /// <summary>Für das Protokoll: angefordert / geladen</summary>
    public (int Requests, int Loaded) Stats => (_requests, _loaded);

    public BitmapSource? TryGetMemory(GalleryImage img) => TryGetMemory(Key(img));

    /// <summary>Für das Protokoll</summary>
    public int QueueLength { get { lock (_queueLock) return _queue.Count; } }

    private BitmapSource? TryGetMemory(string key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    public Task<BitmapSource?> GetAsync(GalleryImage img, CancellationToken ct) =>
        Enqueue(Key(img), key => Load(img, key), img.Path, ct);

    /// <summary>Rundes Gesichts-Vorschaubild (Ausschnitt um das Gesicht).</summary>
    public Task<BitmapSource?> GetFaceAsync(GalleryImage img, FaceRef face, CancellationToken ct) =>
        Enqueue($"face{face.Id}_{Key(img)}", key => LoadFace(img, face, key), img.Path, ct);

    public BitmapSource? TryGetFaceMemory(GalleryImage img, FaceRef face) => TryGetMemory($"face{face.Id}_{Key(img)}");

    private Task<BitmapSource?> Enqueue(string key, Func<string, BitmapSource?> load, string what, CancellationToken ct)
    {
        if (TryGetMemory(key) is { } hit) return Task.FromResult<BitmapSource?>(hit);
        Interlocked.Increment(ref _requests);
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_queueLock) _queue.Push(new Request(key, load, what, tcs, ct));
        _signal.Release();
        return tcs.Task;
    }

    private void Work()
    {
        while (true)
        {
            _signal.Wait();
            Request? r;
            lock (_queueLock) _queue.TryPop(out r);
            if (r is null || r.Ct.IsCancellationRequested) continue;
            try
            {
                var bmp = TryGetMemory(r.Key) ?? r.Load(r.Key);
                if (bmp is not null) Remember(r.Key, bmp);
                Interlocked.Increment(ref _loaded);
                r.Result.TrySetResult(bmp);
            }
            catch (Exception ex)
            {
                Log.Write($"Thumb {r.What}: {ex.GetType().Name}: {ex.Message}");
                r.Result.TrySetResult(null);
            }
        }
    }

    private const int FaceSize = 192;

    /// <summary>Wie FaceAvatars.load: quadratischer Ausschnitt (1,5 × Gesicht) aus dem bis 1024 px grossen Bild.</summary>
    private BitmapSource? LoadFace(GalleryImage img, FaceRef face, string key)
    {
        var file = Path.Combine(_folder, "faces", key[^2..], key + ".jpg");
        if (File.Exists(file) && Decode(file, 0, 0, 1) is { } cached) return cached;

        int w = 0, h = 0;
        if (img.Width > 1024 || img.Height > 1024)
        {
            if (img.Width >= img.Height) w = 1024; else h = 1024;
        }
        var bmp = Decode(img.Path, w, h, img.Orientation);
        if (bmp is null) return null;
        int bw = bmp.PixelWidth, bh = bmp.PixelHeight;
        double fw = (face.R - face.L) * bw, fh = (face.B - face.T) * bh;
        int side = (int)Math.Clamp(Math.Max(fw, fh) * 1.5, 1, Math.Min(bw, bh));
        double cx = (face.L + face.R) / 2 * bw, cy = (face.T + face.B) / 2 * bh;
        int x = (int)Math.Clamp(cx - side / 2.0, 0, bw - side);
        int y = (int)Math.Clamp(cy - side / 2.0, 0, bh - side);
        var crop = new CroppedBitmap(bmp, new System.Windows.Int32Rect(x, y, side, side));
        double scale = (double)FaceSize / side;
        BitmapSource result = scale < 1 ? new TransformedBitmap(crop, new ScaleTransform(scale, scale)) : crop;
        result.Freeze();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(result));
            var tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, file, true);
        }
        catch (Exception)
        {
        }
        return result;
    }

    private readonly SemaphoreSlim _fileGate = new(3);

    /// <summary>Vorschaubild als JPEG-Datei (für die Karte im WebView2); wird bei Bedarf erzeugt.</summary>
    public async Task<byte[]?> GetJpegBytesAsync(GalleryImage img)
    {
        var key = Key(img);
        var file = Path.Combine(_folder, key[..2], key + ".jpg");
        await _fileGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(file)) Load(img, key);
                try { return File.Exists(file) ? File.ReadAllBytes(file) : null; }
                catch (IOException) { return null; }
            });
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Alle Vorschaubilder löschen (Festplatte und Speicher).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Grösse des Caches auf der Festplatte in Bytes.</summary>
    public long DiskSize()
    {
        try
        {
            return Directory.Exists(_folder)
                ? new DirectoryInfo(_folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void Remember(string key, BitmapSource bmp)
    {
        lock (_lock)
        {
            if (_map.ContainsKey(key)) return;
            _map[key] = _lru.AddFirst((key, bmp));
            while (_lru.Count > MemoryCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    private static string Key(GalleryImage img)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{img.Path.ToLowerInvariant()}|{img.Size}|{img.Modified.Ticks}"));
        return Convert.ToHexString(bytes);
    }

    private BitmapSource? Load(GalleryImage img, string key)
    {
        var file = Path.Combine(_folder, key[..2], key + ".jpg");
        if (File.Exists(file))
        {
            var cached = Decode(file, 0, 0, 1);
            if (cached is not null) return cached;
        }

        // Kürzere Seite auf Size dekodieren (WIC skaliert JPEG direkt beim Dekodieren -> schnell)
        int w = 0, h = 0;
        if (img.Width > 0 && img.Height > 0 && Math.Min(img.Width, img.Height) > Size)
        {
            if (img.Width >= img.Height) h = Size; else w = Size;
        }
        else if (img.Width <= 0)
        {
            w = Size;
        }
        var thumb = Decode(img.Path, w, h, img.Orientation);
        if (thumb is null) return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(thumb));
            var tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, file, true);
        }
        catch (Exception ex)
        {
            Log.Write($"Thumb speichern {file}: {ex.GetType().Name}: {ex.Message}");   // Cache ist optional
        }
        return thumb;
    }

    /// <summary>Bild laden (decodeWidth/-Height 0 = Originalgrösse), gemäss EXIF gedreht. Threadsicher (Freeze).</summary>
    public static BitmapSource? Decode(string path, int decodeWidth, int decodeHeight, int orientation)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;   // kein globaler WPF-Bildcache
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
            image.EndInit();
            image.Freeze();
            return Orient(image, orientation);
        }
        catch (Exception ex)
        {
            Log.Write($"Decode {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>EXIF-Orientation anwenden (Drehen/Spiegeln).</summary>
    public static BitmapSource Orient(BitmapSource image, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t is null) return image;
        var result = new TransformedBitmap(image, t);
        result.Freeze();
        return result;
    }
}
