using System.IO;
using System.Windows;
using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>Fortschritt des Einlesens (für die Statuszeile).</summary>
public sealed record ScanStatus(bool Running, string Phase = "", int Done = 0, int Total = 0, string? Error = null)
{
    public static ScanStatus Idle { get; } = new(false);
}

/// <summary>
/// Alle Bilder des Bildordners. Beim Start sofort aus der Datenbank, danach im Hintergrund
/// mit dem Ordner abgeglichen (neue/geänderte Dateien: EXIF lesen, gelöschte entfernen).
/// </summary>
public sealed class GalleryLibrary(GalleryDatabase db, Settings settings)
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff",
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, ImageRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private bool _dbLoaded;
    private bool _started;
    private CancellationTokenSource? _cts;
    private Task _scanTask = Task.CompletedTask;

    /// <summary>Alle Bilder, neueste zuoberst. Wird bei Änderungen als neue Liste ersetzt.</summary>
    public IReadOnlyList<GalleryImage> Images { get; private set; } = [];

    public ScanStatus Status { get; private set; } = ScanStatus.Idle;

    /// <summary>Wird im UI-Thread ausgelöst.</summary>
    public event Action? ImagesChanged;

    /// <summary>Wird im UI-Thread ausgelöst.</summary>
    public event Action? StatusChanged;

    public string Root => settings.ImageFolder;

    /// <summary>Sortierung: älteste zuerst?</summary>
    public bool OldestFirst => settings.OldestFirst;

    /// <summary>Sortierung umstellen (alle Ansichten werden über ImagesChanged neu aufgebaut).</summary>
    public void SetOldestFirst(bool oldestFirst)
    {
        if (settings.OldestFirst == oldestFirst) return;
        settings.OldestFirst = oldestFirst;
        settings.Save();
        Images = Sort(Images);
        ImagesChanged?.Invoke();
    }

    private List<GalleryImage> Sort(IEnumerable<GalleryImage> images) => settings.OldestFirst
        ? images.OrderBy(i => i.Date).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()
        : images.OrderByDescending(i => i.Date).ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void Start()
    {
        if (_started) return;
        _started = true;
        Rescan();
    }

    /// <summary>Anderen Bildordner wählen und neu einlesen.</summary>
    public void SetRoot(string folder)
    {
        settings.ImageFolder = folder;
        settings.Save();
        Rescan();
    }

    /// <summary>Ordner neu abgleichen (ein laufender Scan wird abgebrochen).</summary>
    public async void Rescan()
    {
        _cts?.Cancel();
        try { await _scanTask; } catch (Exception) { }
        var cts = _cts = new CancellationTokenSource();
        _scanTask = Task.Run(() => Scan(cts.Token));
        try
        {
            await _scanTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus(new ScanStatus(false, Error: $"Fehler beim Einlesen: {ex.Message}"));
        }
    }

    private void Scan(CancellationToken ct)
    {
        var root = Path.GetFullPath(settings.ImageFolder);
        if (!_dbLoaded)
        {
            SetStatus(new ScanStatus(true, "Datenbank laden"));
            var all = db.LoadAll();
            lock (_lock) foreach (var r in all) _rows[r.Path] = r;
            _dbLoaded = true;
            Publish(root);
        }
        if (!Directory.Exists(root))
        {
            SetStatus(new ScanStatus(false, Error: $"Bildordner {root} nicht gefunden – bitte in den Einstellungen wählen."));
            return;
        }

        // 1. Ordner durchsuchen
        SetStatus(new ScanStatus(true, "Ordner durchsuchen"));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(f.Extension)) continue;
            seen.Add(f.FullName);
            ImageRow? known;
            lock (_lock) _rows.TryGetValue(f.FullName, out known);
            if (known is null || known.Size != f.Length || known.MTime != f.LastWriteTimeUtc.Ticks) todo.Add(f);
        }

        // 2. gelöschte Bilder entfernen
        List<string> removed;
        lock (_lock) removed = _rows.Keys.Where(k => !seen.Contains(k)).ToList();
        if (removed.Count > 0)
        {
            db.Delete(removed);
            lock (_lock) foreach (var k in removed) _rows.Remove(k);
            Publish(root);
        }

        // 3. neue / geänderte Bilder: EXIF lesen (parallel, in Blöcken)
        const int chunk = 400;
        for (int start = 0; start < todo.Count; start += chunk)
        {
            ct.ThrowIfCancellationRequested();
            SetStatus(new ScanStatus(true, "EXIF-Daten lesen", start, todo.Count));
            var rows = todo.Skip(start).Take(chunk)
                .AsParallel().AsOrdered()
                .WithDegreeOfParallelism(Math.Clamp(Environment.ProcessorCount / 2, 2, 6))
                .WithCancellation(ct)
                .Select(f =>
                {
                    var info = ExifReader.Read(f.FullName);
                    return new ImageRow(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks, info.Width, info.Height,
                                        info.Orientation, info.Taken, info.Lat, info.Lon);
                })
                .ToList();
            db.Upsert(rows);
            lock (_lock) foreach (var r in rows) _rows[r.Path] = r;
            if (start / chunk % 3 == 2 || start + chunk >= todo.Count) Publish(root);
        }

        // 4. Länder aus den GPS-Daten (nur für Bilder, bei denen es noch nicht bestimmt ist)
        AssignCountries(root, ct);

        // 5. Gesichter erkennen und Personen zuordnen (nur neue / geänderte Bilder)
        ScanFaces(root, ct);
        SetStatus(ScanStatus.Idle);
    }

    /// <summary>Hinweis, warum keine Gesichter erkannt werden (fehlende Modelle, anderes Modell), sonst null.</summary>
    public string? FaceScanProblem { get; private set; }

    private void ScanFaces(string root, CancellationToken ct)
    {
        if (FaceAnalyzer.MissingModels() is { } missing)
        {
            FaceScanProblem = $"Gesichtserkennung aus: Modell fehlt ({missing}).";
            return;
        }
        List<GalleryImage> images;
        lock (_lock) images = _rows.Values.Select(r => GalleryImage.FromRow(r, root)).ToList();
        var faces = AppServices.Faces;
        using var analyzer = FaceAnalyzer.TryCreate();
        if (analyzer is null)
        {
            FaceScanProblem = "Gesichtserkennung aus: Modelle konnten nicht geladen werden (siehe log.txt).";
            return;
        }
        if (faces.PrepareScan(analyzer.ModelId, analyzer.Kind, images) is { } problem)
        {
            FaceScanProblem = problem;
            Log.Write(problem);
            return;
        }
        FaceScanProblem = null;
        // neueste zuerst, damit aktuelle Bilder schnell Personen bekommen
        var todo = images.Where(i => !faces.IsDone(i)).OrderByDescending(i => i.Date).ToList();
        if (todo.Count == 0) return;
        Log.Write($"Gesichter erkennen: {todo.Count} Bilder");
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 5 == 0) SetStatus(new ScanStatus(true, "Gesichter erkennen", i, todo.Count));
                List<DetectedFace> detected;
                try
                {
                    detected = analyzer.Analyze(todo[i]);
                }
                catch (Exception ex)
                {
                    Log.Write($"Gesichter {todo[i].Path}: {ex.GetType().Name}: {ex.Message}");
                    detected = [];
                }
                faces.AddImage(todo[i], detected);
                if (i % 300 == 299) faces.ScanProgress();
            }
        }
        finally
        {
            SetStatus(new ScanStatus(true, "Personen zuordnen"));
            faces.FinishScan();
        }
    }

    private void AssignCountries(string root, CancellationToken ct)
    {
        if (settings.CountryRulesVersion != Countries.RulesVersion)
        {
            db.ClearCountries();
            lock (_lock)
            {
                foreach (var k in _rows.Keys.ToList()) _rows[k] = _rows[k] with { Country = null };
            }
            settings.CountryRulesVersion = Countries.RulesVersion;
            settings.Save();
        }
        List<ImageRow> need;
        lock (_lock) need = _rows.Values.Where(r => r.Lat is not null && r.Lon is not null && r.Country is null).ToList();
        if (need.Count == 0) return;

        SetStatus(new ScanStatus(true, "Ländergrenzen laden"));
        var lookup = AppServices.Countries;
        const int chunk = 1000;
        for (int start = 0; start < need.Count; start += chunk)
        {
            ct.ThrowIfCancellationRequested();
            SetStatus(new ScanStatus(true, "Länder zuordnen", start, need.Count));
            var result = need.Skip(start).Take(chunk)
                .AsParallel()
                .WithCancellation(ct)
                .Select(r => (r.Path, Country: lookup.CountryAt(r.Lat!.Value, r.Lon!.Value) ?? "-"))
                .ToList();
            db.UpdateCountries(result);
            lock (_lock)
            {
                foreach (var (path, country) in result)
                {
                    if (_rows.TryGetValue(path, out var r)) _rows[path] = r with { Country = country };
                }
            }
        }
        Publish(root);
    }

    private void Publish(string root)
    {
        List<ImageRow> rows;
        lock (_lock) rows = [.. _rows.Values];
        var images = Sort(rows.Select(r => GalleryImage.FromRow(r, root)));
        OnUi(() =>
        {
            Images = images;
            ImagesChanged?.Invoke();
        });
    }

    private void SetStatus(ScanStatus status) => OnUi(() =>
    {
        Status = status;
        StatusChanged?.Invoke();
    });

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
