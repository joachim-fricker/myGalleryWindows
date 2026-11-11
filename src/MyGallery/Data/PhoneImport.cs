using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>
/// Einmaliger Import aus der Android-App (Menü ⋮ → „Export für Windows“, myGallery_export_*.zip):
/// Favoriten, Personen, Gruppen und alle Gesichter (mit Vektoren). Die Bilder werden über den Pfad
/// (Data/images/… → Bildordner) oder – für Kamerabilder – über Dateiname, Grösse und Datum zugeordnet.
/// </summary>
public static class PhoneImport
{
    public sealed record Result(int Images, int ImagesMatched, int Favorites, int Faces, int FacesSkipped,
                                int Persons, int Groups, string Model);

    private const string PhoneRoot = "Data/images/";

    public static Result Run(string zipPath, IReadOnlyList<GalleryImage> library, IProgress<string>? progress)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string Text(string name)
        {
            var e = zip.GetEntry(name);
            if (e is null) return "";
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            return r.ReadToEnd();
        }
        IEnumerable<string[]> Tsv(string name) => Text(name)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t'));

        if (zip.GetEntry("images.tsv") is null)
            throw new InvalidDataException("Das ist kein Export der Android-App (images.tsv fehlt).");

        // ---- 1. Bilder zuordnen: MediaStore-ID → Bild unter Windows
        progress?.Report("Bilder zuordnen …");
        var byPath = library.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var byName = library.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        string root = AppServices.Library.Root.TrimEnd('\\', '/');

        var idToImage = new Dictionary<long, GalleryImage>();
        var keyToImage = new Dictionary<string, GalleryImage>(StringComparer.OrdinalIgnoreCase);   // Android favKey
        int phoneImages = 0;
        foreach (var c in Tsv("images.tsv").Skip(1))   // id, relativePath, name, date, exifDate, dateTaken, width, height, size
        {
            if (c.Length < 9 || !long.TryParse(c[0], out var id)) continue;
            phoneImages++;
            string rel = c[1], name = c[2];
            long size = long.TryParse(c[8], out var sz) ? sz : -1;
            long date = long.TryParse(c[3], out var d) ? d : 0;
            var img = Match(rel, name, size, date, root, byPath, byName);
            if (img is null) continue;
            idToImage[id] = img;
            keyToImage[rel + name] = img;
        }

        // ---- 2. Favoriten
        progress?.Report("Favoriten übernehmen …");
        var favKeys = new List<string>();
        foreach (var line in Text("favorites.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = line.Trim();
            if (keyToImage.TryGetValue(key, out var img)) favKeys.Add(img.FavKey);
            else if (key.StartsWith(PhoneRoot, StringComparison.OrdinalIgnoreCase)) favKeys.Add(key[PhoneRoot.Length..]);
        }
        AppServices.Favorites.AddKeys(favKeys);

        // ---- 3. Gesichter
        progress?.Report("Gesichter übernehmen …");
        var faces = new List<FaceRecord>();
        int skipped = 0;
        var facesEntry = zip.GetEntry("faces/faces.bin");
        if (facesEntry is not null)
        {
            using var s = new BinaryReaderBE(facesEntry.Open());
            int version = s.Int();
            if (version != 2)
                throw new InvalidDataException($"Gesichter-Datei hat Version {version}; bitte in der Android-App zuerst neu scannen lassen.");
            int count = s.Int();
            for (int i = 0; i < count; i++)
            {
                long id = s.Long(), imageId = s.Long();
                float l = s.Float(), t = s.Float(), r = s.Float(), b = s.Float();
                int pid = s.Int();
                int status = s.Int();
                float score = s.Float(), sharp = s.Float();
                int n = s.Int();
                var e = new float[n];
                for (int k = 0; k < n; k++) e[k] = s.Float();
                if (!idToImage.TryGetValue(imageId, out var img)) { skipped++; continue; }
                faces.Add(new FaceRecord
                {
                    Id = id, Path = img.Path, L = l, T = t, R = r, B = b, PersonId = pid,
                    Status = (FaceStatus)Math.Clamp(status, 0, 3), Score = score, Sharpness = sharp, Embedding = e,
                });
            }
        }

        // Personen: id, name, Reihenfolge, other(1/0), Gruppe
        var names = new Dictionary<int, string>();
        var orders = new Dictionary<int, int>();
        var groupOf = new Dictionary<int, int>();
        foreach (var c in Tsv("faces/persons.tsv"))
        {
            if (c.Length < 2 || !int.TryParse(c[0], out var id) || string.IsNullOrWhiteSpace(c[1])) continue;
            names[id] = c[1];
            if (c.Length > 2 && int.TryParse(c[2], out var o)) orders[id] = o;
            if (c.Length > 4 && int.TryParse(c[4], out var g)) groupOf[id] = g;
        }
        var groups = Tsv("faces/groups.tsv")
            .Where(c => c.Length >= 2 && int.TryParse(c[0], out _) && !string.IsNullOrWhiteSpace(c[1]))
            .Select(c => new GroupDef(int.Parse(c[0], CultureInfo.InvariantCulture), c[1]))
            .ToList();
        var covers = new Dictionary<int, long>();
        foreach (var c in Tsv("faces/persons_cover.tsv"))
            if (c.Length >= 2 && int.TryParse(c[0], out var p) && long.TryParse(c[1], out var f)) covers[p] = f;
        var blocked = new Dictionary<long, HashSet<int>>();
        foreach (var c in Tsv("faces/faces_blocked.tsv"))
        {
            if (c.Length < 2 || !long.TryParse(c[0], out var f) || !int.TryParse(c[1], out var p)) continue;
            if (!blocked.TryGetValue(f, out var set)) blocked[f] = set = [];
            set.Add(p);
        }
        // Bereits auf dem Handy gescannte Bilder (auch ohne Gesichter) – müssen unter Windows nicht neu gescannt werden
        var done = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Tsv("faces/faces_done.tsv"))
        {
            if (c.Length >= 2 && long.TryParse(c[0], out var id) && idToImage.TryGetValue(id, out var img))
                done[img.Path] = img.Modified.ToUniversalTime().Ticks;
        }
        var model = Tsv("faces/model.txt").FirstOrDefault() ?? [];
        string modelId = model.Length > 0 ? model[0] : "";
        var kind = ModelKind.FromName(model.Length > 1 ? model[1] : null);

        progress?.Report("Speichern …");
        AppServices.Faces.ReplaceAll(faces, names, orders, groupOf, groups, covers, blocked, done, modelId, kind);

        return new Result(phoneImages, idToImage.Count, favKeys.Count, faces.Count, skipped, names.Count,
                          groups.Count, kind.Label);
    }

    /// <summary>Bild unter Windows zu einem Bild vom Handy.</summary>
    private static GalleryImage? Match(string rel, string name, long size, long dateMs, string root,
                                       Dictionary<string, GalleryImage> byPath,
                                       Dictionary<string, List<GalleryImage>> byName)
    {
        // 1. gleicher Pfad: Data/images/<x>/name → <Bildordner>\<x>\name
        if (rel.StartsWith(PhoneRoot, StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(root, rel[PhoneRoot.Length..].Replace('/', '\\'), name);
            if (byPath.TryGetValue(path, out var img)) return img;
        }
        // 2. über den Dateinamen (Kamerabilder, verschobene Bilder)
        if (!byName.TryGetValue(name, out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0];
        var sameSize = candidates.Where(c => c.Size == size).ToList();
        if (sameSize.Count == 1) return sameSize[0];
        var pool = sameSize.Count > 0 ? sameSize : candidates;
        if (dateMs <= 0) return null;
        var date = DateTimeOffset.FromUnixTimeMilliseconds(dateMs).LocalDateTime;
        var best = pool.MinBy(c => Math.Abs((c.Date - date).TotalMinutes));
        return best is not null && Math.Abs((best.Date - date).TotalMinutes) < 2 ? best : null;
    }

    /// <summary>Liest Java-DataOutputStream (Big Endian).</summary>
    private sealed class BinaryReaderBE(Stream s) : IDisposable
    {
        private readonly BufferedStream _s = new(s, 1 << 16);
        private readonly byte[] _buf = new byte[8];

        private ReadOnlySpan<byte> Read(int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(_buf, got, n - got);
                if (r <= 0) throw new EndOfStreamException("Gesichter-Datei ist unvollständig.");
                got += r;
            }
            return _buf.AsSpan(0, n);
        }

        public int Int() => BinaryPrimitives.ReadInt32BigEndian(Read(4));
        public long Long() => BinaryPrimitives.ReadInt64BigEndian(Read(8));
        public float Float() => BinaryPrimitives.ReadSingleBigEndian(Read(4));
        public void Dispose() => _s.Dispose();
    }
}
