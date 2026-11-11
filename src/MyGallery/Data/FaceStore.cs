using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace MyGallery.Data;

/*
 * Personen-Erkennung wie in der Android-App (Faces.kt, Version 2):
 *  Zuordnung über BESTÄTIGTE Beispiele jeder benannten Person (k-nächste-Nachbarn):
 *    sicher   -> automatisch (Auto)
 *    unsicher -> Vorschlag (Suggested), der Benutzer bestätigt/lehnt ab
 *    sonst    -> unbenannte Gruppe (Unnamed), damit neue Personen entdeckt werden
 */

public enum FaceStatus
{
    Unnamed = 0,
    Auto = 1,
    Suggested = 2,
    Confirmed = 3,
}

/// <summary>Schwellen je Modell (Kosinus-Ähnlichkeit), wie ModelKind in Faces.kt.</summary>
public sealed record ModelKind(string Name, string Label, float AutoT, float SuggestT, float ClusterT, float Margin)
{
    public static readonly ModelKind InsightFace = new("INSIGHTFACE", "InsightFace w600k_mbf", 0.45f, 0.28f, 0.45f, 0.08f);
    public static readonly ModelKind MobileFaceNet = new("MOBILEFACENET", "MobileFaceNet", 0.70f, 0.55f, 0.62f, 0.06f);

    public static ModelKind FromName(string? name) => name == MobileFaceNet.Name ? MobileFaceNet : InsightFace;
}

/// <summary>Ein Gesicht ohne Vektor – für die Oberfläche. Box relativ (0..1) zum (gedrehten) Bild.</summary>
public sealed record FaceRef(long Id, string Path, int PersonId, float L, float T, float R, float B, FaceStatus Status, float Score)
{
    public float Area => (R - L) * (B - T);
}

/// <summary>Eine Personen-Gruppe (z. B. "Haupt", "VB").</summary>
public sealed record GroupDef(int Id, string Name);

/// <summary>Unveränderlicher Stand für die Oberfläche.</summary>
public sealed class FaceSnapshot
{
    public IReadOnlyDictionary<int, string> Names { get; init; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, int> Orders { get; init; } = new Dictionary<int, int>();
    public IReadOnlyList<GroupDef> Groups { get; init; } = [];
    public IReadOnlyDictionary<int, int> GroupOf { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, long> Covers { get; init; } = new Dictionary<int, long>();
    public IReadOnlyDictionary<string, List<FaceRef>> FacesByPath { get; init; } = new Dictionary<string, List<FaceRef>>();
    public string ModelLabel { get; init; } = "";
    public int FaceCount { get; init; }
}

/// <summary>Gesicht mit Vektor (nur intern).</summary>
public sealed class FaceRecord
{
    public long Id;
    public required string Path;
    public float L, T, R, B;
    public int PersonId;
    public FaceStatus Status;
    public float Score;
    public float Sharpness;
    public required float[] Embedding;
}

/// <summary>
/// Gesichter-Datenbank (Port von FaceDb aus Faces.kt). Alles im Speicher, gespeichert in gallery.db.
/// Zugriff nur über die Methoden (intern gesperrt); Änderungen melden <see cref="Changed"/> im UI-Thread.
/// </summary>
public sealed class FaceStore
{
    public const float SharpnessMin = 35f;
    private const int MaxAnchors = 80;
    /// <summary>Gruppe "Haupt": immer sichtbar, Standard für neu benannte Personen</summary>
    public const int MainGroup = 1;
    private static readonly string[] DefaultGroups = ["Haupt", "Alcina Familie", "Ulli", "VB", "andere"];

    private readonly GalleryDatabase _db;
    private readonly object _lock = new();
    private bool _loaded;

    private readonly List<FaceRecord> _faces = [];
    private readonly Dictionary<long, FaceRecord> _byId = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly Dictionary<int, int> _orders = [];
    private readonly List<GroupDef> _groups = [];
    private readonly Dictionary<int, int> _groupOf = [];
    private readonly Dictionary<int, long> _covers = [];
    private readonly Dictionary<long, HashSet<int>> _blocked = [];
    private readonly Dictionary<int, float[]> _sums = [];
    /// <summary>Bereits gescannte Bilder: Pfad → Änderungszeit (UTC-Ticks)</summary>
    private readonly Dictionary<string, long> _done = new(StringComparer.OrdinalIgnoreCase);
    private List<Anchors> _anchors = [];
    private long _nextFaceId = 1;
    private int _nextPersonId = 1;

    public ModelKind Kind { get; private set; } = ModelKind.InsightFace;
    public string ModelId { get; private set; } = "";

    /// <summary>Letzter Stand für die Oberfläche</summary>
    public FaceSnapshot Snapshot { get; private set; } = new();

    /// <summary>Daten haben sich geändert (im UI-Thread).</summary>
    public event Action? Changed;

    public FaceStore(GalleryDatabase db)
    {
        _db = db;
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS face (
                id INTEGER PRIMARY KEY, path TEXT NOT NULL COLLATE NOCASE,
                l REAL, t REAL, r REAL, b REAL,
                person INTEGER, status INTEGER, score REAL, sharp REAL, emb BLOB
            );
            CREATE INDEX IF NOT EXISTS ix_face_path ON face(path);
            CREATE TABLE IF NOT EXISTS person (id INTEGER PRIMARY KEY, name TEXT, ord INTEGER, grp INTEGER);
            CREATE TABLE IF NOT EXISTS pgroup (id INTEGER PRIMARY KEY, name TEXT, ord INTEGER);
            CREATE TABLE IF NOT EXISTS cover (person INTEGER PRIMARY KEY, face INTEGER);
            CREATE TABLE IF NOT EXISTS blocked (face INTEGER, person INTEGER, PRIMARY KEY (face, person));
            CREATE TABLE IF NOT EXISTS face_done (path TEXT PRIMARY KEY COLLATE NOCASE, mtime INTEGER);
            CREATE TABLE IF NOT EXISTS face_meta (key TEXT PRIMARY KEY, value TEXT);
            """;
        cmd.ExecuteNonQuery();
    }

    public int FaceCount { get { lock (_lock) return _faces.Count; } }

    // ------------------------------------------------------------------ Laden / Speichern

    /// <summary>Aus der Datenbank laden (einmal, im Hintergrund aufrufen).</summary>
    public void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            using var c = _db.OpenConnection();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, path, l, t, r, b, person, status, score, sharp, emb FROM face";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var bytes = (byte[])rd.GetValue(10);
                    var f = new FaceRecord
                    {
                        Id = rd.GetInt64(0), Path = rd.GetString(1),
                        L = rd.GetFloat(2), T = rd.GetFloat(3), R = rd.GetFloat(4), B = rd.GetFloat(5),
                        PersonId = rd.GetInt32(6), Status = (FaceStatus)rd.GetInt32(7),
                        Score = rd.GetFloat(8), Sharpness = rd.GetFloat(9),
                        Embedding = MemoryMarshal.Cast<byte, float>(bytes).ToArray(),
                    };
                    _faces.Add(f);
                    _byId[f.Id] = f;
                }
            }
            Read(c, "SELECT id, name, ord, grp FROM person", rd =>
            {
                int id = rd.GetInt32(0);
                _names[id] = rd.GetString(1);
                if (!rd.IsDBNull(2)) _orders[id] = rd.GetInt32(2);
                if (!rd.IsDBNull(3)) _groupOf[id] = rd.GetInt32(3);
            });
            Read(c, "SELECT id, name FROM pgroup ORDER BY ord", rd => _groups.Add(new GroupDef(rd.GetInt32(0), rd.GetString(1))));
            Read(c, "SELECT person, face FROM cover", rd => _covers[rd.GetInt32(0)] = rd.GetInt64(1));
            Read(c, "SELECT face, person FROM blocked", rd => Blocked(rd.GetInt64(0)).Add(rd.GetInt32(1)));
            Read(c, "SELECT path, mtime FROM face_done", rd => _done[rd.GetString(0)] = rd.GetInt64(1));
            Read(c, "SELECT key, value FROM face_meta", rd =>
            {
                if (rd.GetString(0) == "modelId") ModelId = rd.GetString(1);
                if (rd.GetString(0) == "kind") Kind = ModelKind.FromName(rd.GetString(1));
            });
            EnsureGroups();
            UpdateCounters();
            RebuildSums();
            RefreshAnchors();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
    }

    private static void Read(SqliteConnection c, string sql, Action<SqliteDataReader> row)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) row(rd);
    }

    private void EnsureGroups()
    {
        if (_groups.Count == 0)
        {
            for (int i = 0; i < DefaultGroups.Length; i++) _groups.Add(new GroupDef(i + 1, DefaultGroups[i]));
        }
        if (_groups.All(g => g.Id != MainGroup)) _groups.Insert(0, new GroupDef(MainGroup, "Haupt"));
        foreach (var pid in _names.Keys)
        {
            if (!_groupOf.TryGetValue(pid, out var g) || _groups.All(x => x.Id != g)) _groupOf[pid] = MainGroup;
        }
    }

    private void UpdateCounters()
    {
        _nextFaceId = (_faces.Count == 0 ? 0 : _faces.Max(f => f.Id)) + 1;
        _nextPersonId = Math.Max(_faces.Count == 0 ? 0 : _faces.Max(f => f.PersonId),
                                 _names.Count == 0 ? 0 : _names.Keys.Max()) + 1;
    }

    /// <summary>Personen, Gruppen, Titelbilder, Ablehnungen und alle Zuordnungen speichern.</summary>
    private void Save()
    {
        using var c = _db.OpenConnection();
        using var tx = c.BeginTransaction();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Exec("DELETE FROM person; DELETE FROM pgroup; DELETE FROM cover; DELETE FROM blocked; DELETE FROM face_meta;");
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO person (id, name, ord, grp) VALUES ($i, $n, $o, $g)";
            var i = cmd.Parameters.Add("$i", SqliteType.Integer);
            var n = cmd.Parameters.Add("$n", SqliteType.Text);
            var o = cmd.Parameters.Add("$o", SqliteType.Integer);
            var g = cmd.Parameters.Add("$g", SqliteType.Integer);
            foreach (var (pid, name) in _names)
            {
                i.Value = pid; n.Value = name;
                o.Value = _orders.TryGetValue(pid, out var ord) ? ord : DBNull.Value;
                g.Value = _groupOf.TryGetValue(pid, out var grp) ? grp : MainGroup;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO pgroup (id, name, ord) VALUES ($i, $n, $o)";
            var i = cmd.Parameters.Add("$i", SqliteType.Integer);
            var n = cmd.Parameters.Add("$n", SqliteType.Text);
            var o = cmd.Parameters.Add("$o", SqliteType.Integer);
            for (int k = 0; k < _groups.Count; k++)
            {
                i.Value = _groups[k].Id; n.Value = _groups[k].Name; o.Value = k;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO cover (person, face) VALUES ($p, $f)";
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            var f = cmd.Parameters.Add("$f", SqliteType.Integer);
            foreach (var (pid, fid) in _covers) { p.Value = pid; f.Value = fid; cmd.ExecuteNonQuery(); }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO blocked (face, person) VALUES ($f, $p)";
            var f = cmd.Parameters.Add("$f", SqliteType.Integer);
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            foreach (var (fid, set) in _blocked)
                foreach (var pid in set) { f.Value = fid; p.Value = pid; cmd.ExecuteNonQuery(); }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO face_meta (key, value) VALUES ('modelId', $m), ('kind', $k)";
            cmd.Parameters.AddWithValue("$m", ModelId);
            cmd.Parameters.AddWithValue("$k", Kind.Name);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE face SET person = $p, status = $s, score = $sc WHERE id = $id";
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            var st = cmd.Parameters.Add("$s", SqliteType.Integer);
            var sc = cmd.Parameters.Add("$sc", SqliteType.Real);
            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var face in _faces)
            {
                p.Value = face.PersonId; st.Value = (int)face.Status; sc.Value = face.Score; id.Value = face.Id;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static void InsertFaces(SqliteConnection c, SqliteTransaction tx, IEnumerable<FaceRecord> faces)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO face (id, path, l, t, r, b, person, status, score, sharp, emb)
            VALUES ($id, $path, $l, $t, $r, $b, $p, $s, $sc, $sh, $e)
            """;
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        var l = cmd.Parameters.Add("$l", SqliteType.Real);
        var t = cmd.Parameters.Add("$t", SqliteType.Real);
        var r = cmd.Parameters.Add("$r", SqliteType.Real);
        var b = cmd.Parameters.Add("$b", SqliteType.Real);
        var p = cmd.Parameters.Add("$p", SqliteType.Integer);
        var s = cmd.Parameters.Add("$s", SqliteType.Integer);
        var sc = cmd.Parameters.Add("$sc", SqliteType.Real);
        var sh = cmd.Parameters.Add("$sh", SqliteType.Real);
        var e = cmd.Parameters.Add("$e", SqliteType.Blob);
        foreach (var f in faces)
        {
            id.Value = f.Id; path.Value = f.Path;
            l.Value = f.L; t.Value = f.T; r.Value = f.R; b.Value = f.B;
            p.Value = f.PersonId; s.Value = (int)f.Status; sc.Value = f.Score; sh.Value = f.Sharpness;
            e.Value = MemoryMarshal.AsBytes(f.Embedding.AsSpan()).ToArray();
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ Import vom Handy

    /// <summary>Daten aus dem Export der Android-App: ersetzt alle bisherigen Gesichter und Personen.</summary>
    public void ReplaceAll(IReadOnlyList<FaceRecord> faces, IReadOnlyDictionary<int, string> names,
                           IReadOnlyDictionary<int, int> orders, IReadOnlyDictionary<int, int> groupOf,
                           IReadOnlyList<GroupDef> groups, IReadOnlyDictionary<int, long> covers,
                           IReadOnlyDictionary<long, HashSet<int>> blocked, IReadOnlyDictionary<string, long> done,
                           string modelId, ModelKind kind)
    {
        lock (_lock)
        {
            _loaded = true;
            _faces.Clear(); _byId.Clear(); _names.Clear(); _orders.Clear(); _groups.Clear();
            _groupOf.Clear(); _covers.Clear(); _blocked.Clear(); _done.Clear();
            foreach (var kv in done) _done[kv.Key] = kv.Value;
            foreach (var f in faces) { _faces.Add(f); _byId[f.Id] = f; }
            foreach (var kv in names) _names[kv.Key] = kv.Value;
            foreach (var kv in orders) _orders[kv.Key] = kv.Value;
            foreach (var kv in groupOf) _groupOf[kv.Key] = kv.Value;
            _groups.AddRange(groups);
            foreach (var kv in covers) if (_byId.ContainsKey(kv.Value)) _covers[kv.Key] = kv.Value;
            foreach (var kv in blocked) if (_byId.ContainsKey(kv.Key)) _blocked[kv.Key] = [.. kv.Value];
            ModelId = modelId;
            Kind = kind;
            EnsureGroups();
            UpdateCounters();

            using (var c = _db.OpenConnection())
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM face; DELETE FROM face_done;";
                    cmd.ExecuteNonQuery();
                }
                InsertFaces(c, tx, _faces);
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO face_done (path, mtime) VALUES ($p, $m)";
                    var p = cmd.Parameters.Add("$p", SqliteType.Text);
                    var m = cmd.Parameters.Add("$m", SqliteType.Integer);
                    foreach (var (path, mtime) in done) { p.Value = path; m.Value = mtime; cmd.ExecuteNonQuery(); }
                }
                tx.Commit();
            }
            Save();
            RebuildSums();
            RefreshAnchors();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ Scannen unter Windows

    /// <summary>Änderungszeit, mit der der Gesichter-Scan arbeitet (wie dateModified auf Android).</summary>
    public static long MTime(Domain.GalleryImage img) => img.Modified.ToUniversalTime().Ticks;

    public bool IsDone(Domain.GalleryImage img)
    {
        lock (_lock) return _done.TryGetValue(img.Path, out var m) && m == MTime(img);
    }

    /// <summary>
    /// Vor dem Scan: Modell prüfen (anderes Modell = Vektoren passen nicht zusammen) und
    /// gelöschte oder geänderte Bilder entfernen. Liefert eine Fehlermeldung oder null.
    /// </summary>
    public string? PrepareScan(string modelId, ModelKind kind, IReadOnlyCollection<Domain.GalleryImage> images)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_faces.Count > 0 && ModelId.Length > 0 && ModelId != modelId)
                return $"Die vorhandenen Gesichter stammen von einem anderen Modell ({ModelId}, hier {modelId}).";
            if (ModelId != modelId || Kind != kind)
            {
                ModelId = modelId;
                Kind = kind;
            }
            var mod = images.ToDictionary(i => i.Path, MTime, StringComparer.OrdinalIgnoreCase);
            var stale = _done.Where(d => !mod.TryGetValue(d.Key, out var m) || m != d.Value).Select(d => d.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // auch Gesichter ohne done-Eintrag, deren Bild es nicht mehr gibt
            foreach (var f in _faces) if (!mod.ContainsKey(f.Path)) stale.Add(f.Path);
            if (stale.Count == 0) return null;
            RemoveFacesOf(stale);
            foreach (var p in stale) _done.Remove(p);
            using var c = _db.OpenConnection();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM face WHERE path = $p; DELETE FROM face_done WHERE path = $p;";
            var par = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var p in stale) { par.Value = p; cmd.ExecuteNonQuery(); }
            tx.Commit();
            RebuildSums();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
        return null;
    }

    private void RemoveFacesOf(HashSet<string> paths)
    {
        var removed = _faces.Where(f => paths.Contains(f.Path)).ToList();
        if (removed.Count == 0) return;
        _faces.RemoveAll(f => paths.Contains(f.Path));
        foreach (var f in removed)
        {
            _byId.Remove(f.Id);
            _blocked.Remove(f.Id);
        }
        foreach (var (pid, fid) in _covers.ToList()) if (!_byId.ContainsKey(fid)) _covers.Remove(pid);
    }

    /// <summary>Gesichter eines Bildes einordnen und speichern (wie addImage in Faces.kt).</summary>
    public void AddImage(Domain.GalleryImage img, IReadOnlyList<DetectedFace> detected)
    {
        lock (_lock)
        {
            RemoveFacesOf(new HashSet<string>([img.Path], StringComparer.OrdinalIgnoreCase));
            var added = new List<FaceRecord>();
            foreach (var d in detected)
            {
                var rec = new FaceRecord
                {
                    Id = _nextFaceId++, Path = img.Path, L = d.L, T = d.T, R = d.R, B = d.B,
                    PersonId = -1, Status = FaceStatus.Unnamed, Score = 0, Sharpness = d.Sharpness, Embedding = d.Embedding,
                };
                Classify(rec);
                _faces.Add(rec);
                _byId[rec.Id] = rec;
                added.Add(rec);
            }
            long mtime = MTime(img);
            _done[img.Path] = mtime;
            using var c = _db.OpenConnection();
            using var tx = c.BeginTransaction();
            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM face WHERE path = $p";
                del.Parameters.AddWithValue("$p", img.Path);
                del.ExecuteNonQuery();
            }
            InsertFaces(c, tx, added);
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO face_done (path, mtime) VALUES ($p, $m)";
                cmd.Parameters.AddWithValue("$p", img.Path);
                cmd.Parameters.AddWithValue("$m", mtime);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Während des Scans: Beispiele auffrischen und den Stand anzeigen.</summary>
    public void ScanProgress()
    {
        lock (_lock)
        {
            RefreshAnchors();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
    }

    /// <summary>Nach dem Scan: alles neu einordnen und speichern.</summary>
    public void FinishScan()
    {
        lock (_lock)
        {
            ReclassifyAll();
            Save();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ Zuordnung

    private sealed record Anchors(int Pid, List<float[]> Vecs, float[] Centroid);

    private static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1f;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a.AsSpan());
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b.AsSpan());
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        float s = System.Numerics.Vector.Dot(acc, Vector<float>.One);
        for (int i = va.Length * Vector<float>.Count; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static float[] Normalized(float[] v)
    {
        float n = MathF.Sqrt(Dot(v, v));
        return n > 0 ? v.Select(x => x / n).ToArray() : v;
    }

    private HashSet<int> Blocked(long faceId)
    {
        if (!_blocked.TryGetValue(faceId, out var set)) _blocked[faceId] = set = [];
        return set;
    }

    /// <summary>Beispiele jeder benannten Person: bestätigte Gesichter, bei wenigen zusätzlich die sichersten automatischen.</summary>
    private void RefreshAnchors()
    {
        _anchors = _faces.Where(f => _names.ContainsKey(f.PersonId))
            .GroupBy(f => f.PersonId)
            .Select(g =>
            {
                var confirmed = g.Where(f => f.Status == FaceStatus.Confirmed).OrderByDescending(f => f.Id).Take(MaxAnchors).ToList();
                var chosen = confirmed.Count >= 5 ? confirmed : confirmed.Concat(
                    g.Where(f => f.Status == FaceStatus.Auto).OrderByDescending(f => f.Score).Take(MaxAnchors - confirmed.Count)).ToList();
                if (chosen.Count == 0) return null;
                var vecs = chosen.Select(f => f.Embedding).ToList();
                int dim = vecs[0].Length;
                var sum = new float[dim];
                foreach (var v in vecs) if (v.Length == dim) for (int i = 0; i < dim; i++) sum[i] += v[i];
                return new Anchors(g.Key, vecs, Normalized(sum));
            })
            .OfType<Anchors>()
            .ToList();
    }

    /// <summary>Beste benannte Person: (personId, score, score der zweitbesten).</summary>
    private (int Pid, float Score, float Second)? BestNamed(FaceRecord f)
    {
        if (_anchors.Count == 0) return null;
        _blocked.TryGetValue(f.Id, out var notAllowed);
        var scored = _anchors
            .Where(a => notAllowed?.Contains(a.Pid) != true)
            .OrderByDescending(a => Dot(a.Centroid, f.Embedding))
            .Take(3)
            .Select(a =>
            {
                var sims = a.Vecs.Where(v => !ReferenceEquals(v, f.Embedding)).Select(v => Dot(v, f.Embedding))
                    .OrderByDescending(x => x).Take(3).ToList();
                return (a.Pid, Score: sims.Count == 0 ? -1f : sims.Average());
            })
            .OrderByDescending(x => x.Score)
            .ToList();
        if (scored.Count == 0) return null;
        return (scored[0].Pid, scored[0].Score, scored.Count > 1 ? scored[1].Score : 0f);
    }

    /// <summary>Gesicht einordnen: automatisch / Vorschlag / unbenannte Gruppe.</summary>
    private void Classify(FaceRecord f)
    {
        if (BestNamed(f) is { } best)
        {
            var (pid, score, second) = best;
            if (score >= Kind.AutoT && score - second >= Kind.Margin && f.Sharpness >= SharpnessMin)
            {
                f.PersonId = pid; f.Status = FaceStatus.Auto; f.Score = score; return;
            }
            if (score >= Kind.SuggestT)
            {
                f.PersonId = pid; f.Status = FaceStatus.Suggested; f.Score = score; return;
            }
        }
        ToUnnamed(f);
    }

    private void ToUnnamed(FaceRecord f)
    {
        f.Status = FaceStatus.Unnamed;
        f.Score = 0f;
        f.PersonId = ClusterAssign(f);
        AddToSum(f.PersonId, f.Embedding);
    }

    /// <summary>Unbenannte Gruppe mit ähnlichstem Durchschnitt, sonst neue Gruppe.</summary>
    private int ClusterAssign(FaceRecord f)
    {
        _blocked.TryGetValue(f.Id, out var notAllowed);
        int best = -1;
        float bestSim = Kind.ClusterT;
        foreach (var (pid, sum) in _sums)
        {
            if (_names.ContainsKey(pid) || notAllowed?.Contains(pid) == true || sum.Length != f.Embedding.Length) continue;
            float n = Dot(sum, sum);
            if (n <= 0f) continue;
            float sim = Dot(sum, f.Embedding) / MathF.Sqrt(n);
            if (sim > bestSim) { bestSim = sim; best = pid; }
        }
        return best >= 0 ? best : _nextPersonId++;
    }

    /// <summary>Alle nicht bestätigten Gesichter neu einordnen (nach Bestätigungen, Namen, Zusammenführen).</summary>
    private void ReclassifyAll()
    {
        RefreshAnchors();
        RebuildSums();
        foreach (var f in _faces)
        {
            switch (f.Status)
            {
                case FaceStatus.Confirmed:
                    break;
                case FaceStatus.Auto or FaceStatus.Suggested:
                    Classify(f);
                    break;
                case FaceStatus.Unnamed:
                    if (BestNamed(f) is not { } best) break;
                    var (pid, score, second) = best;
                    if (score >= Kind.AutoT && score - second >= Kind.Margin && f.Sharpness >= SharpnessMin)
                    {
                        f.PersonId = pid; f.Status = FaceStatus.Auto; f.Score = score;
                    }
                    else if (score >= Kind.SuggestT)
                    {
                        f.PersonId = pid; f.Status = FaceStatus.Suggested; f.Score = score;
                    }
                    break;
            }
        }
        RebuildSums();
    }

    private void AddToSum(int pid, float[] e)
    {
        if (pid < 0 || _names.ContainsKey(pid)) return;
        if (!_sums.TryGetValue(pid, out var s)) _sums[pid] = s = new float[e.Length];
        if (s.Length == e.Length) for (int i = 0; i < s.Length; i++) s[i] += e[i];
    }

    private void RebuildSums()
    {
        _sums.Clear();
        foreach (var f in _faces) if (f.Status == FaceStatus.Unnamed) AddToSum(f.PersonId, f.Embedding);
    }

    // ------------------------------------------------------------------ Aktionen des Benutzers

    /// <summary>
    /// Änderung ausführen und speichern (im Hintergrund). Mit reclassify werden danach alle Vorschläge
    /// neu berechnet, damit die App aus den Bestätigungen lernt.
    /// </summary>
    public Task EditAsync(Action edit, bool reclassify = false) => Task.Run(() =>
    {
        lock (_lock)
        {
            edit();
            RebuildSums();
            Save();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
        if (!reclassify) return;
        lock (_lock)
        {
            ReclassifyAll();
            Save();
            Snapshot = MakeSnapshot();
        }
        RaiseChanged();
    });

    /// <summary>Gesichter gehören sicher zu personId (benannt -> bestätigt, unbenannt -> Gruppenmitglied).</summary>
    public void Confirm(IEnumerable<long> faceIds, int personId)
    {
        foreach (var id in faceIds)
        {
            if (!_byId.TryGetValue(id, out var f)) continue;
            if (f.PersonId >= 0 && f.PersonId != personId)
            {
                Blocked(f.Id).Add(f.PersonId);
                if (_covers.TryGetValue(f.PersonId, out var c) && c == f.Id) _covers.Remove(f.PersonId);
            }
            if (_blocked.TryGetValue(f.Id, out var set)) set.Remove(personId);
            f.PersonId = personId;
            f.Status = _names.ContainsKey(personId) ? FaceStatus.Confirmed : FaceStatus.Unnamed;
            f.Score = 1f;
        }
        RebuildSums();
    }

    /// <summary>Gesichter gehören NICHT zu ihrer aktuellen Person.</summary>
    public void Reject(IEnumerable<long> faceIds)
    {
        foreach (var id in faceIds)
        {
            if (!_byId.TryGetValue(id, out var f)) continue;
            if (f.PersonId >= 0)
            {
                Blocked(f.Id).Add(f.PersonId);
                if (_covers.TryGetValue(f.PersonId, out var c) && c == f.Id) _covers.Remove(f.PersonId);
            }
            RebuildSums();
            ToUnnamed(f);
        }
        RebuildSums();
    }

    public void Rename(int personId, string name)
    {
        bool wasNamed = _names.ContainsKey(personId);
        if (string.IsNullOrWhiteSpace(name))
        {
            _names.Remove(personId); _orders.Remove(personId); _groupOf.Remove(personId);
            foreach (var f in _faces.Where(f => f.PersonId == personId)) { f.Status = FaceStatus.Unnamed; f.Score = 0f; }
        }
        else
        {
            _names[personId] = name.Trim();
            if (!_orders.ContainsKey(personId)) _orders[personId] = (_orders.Count == 0 ? -1 : _orders.Values.Max()) + 1;
            if (!_groupOf.ContainsKey(personId)) _groupOf[personId] = MainGroup;
            if (!wasNamed)
            {
                // Gruppe bekommt einen Namen: Gesichter gelten als automatisch, das Titelbild als bestätigt
                var own = _faces.Where(f => f.PersonId == personId).ToList();
                long? coverId = _covers.TryGetValue(personId, out var c) ? c : own.MaxBy(f => f.R - f.L)?.Id;
                foreach (var f in own)
                {
                    f.Status = f.Id == coverId ? FaceStatus.Confirmed : FaceStatus.Auto;
                    f.Score = 1f;
                }
            }
        }
    }

    /// <summary>Neue (leere) Person anlegen; mit Name, falls angegeben. Liefert die neue ID.</summary>
    public int CreatePerson(string name)
    {
        int pid = _nextPersonId++;
        if (!string.IsNullOrWhiteSpace(name)) Rename(pid, name);
        return pid;
    }

    public void SetGroup(int personId, int groupId)
    {
        if (_names.ContainsKey(personId) && _groups.Any(g => g.Id == groupId)) _groupOf[personId] = groupId;
    }

    public int CreateGroup(string name)
    {
        int id = (_groups.Count == 0 ? 0 : _groups.Max(g => g.Id)) + 1;
        _groups.Add(new GroupDef(id, name.Trim()));
        return id;
    }

    /// <summary>
    /// Ergebnis aus "Gruppen bearbeiten" übernehmen: Reihenfolge = Listenreihenfolge,
    /// id &lt;= 0 = neue Gruppe, fehlende Gruppen werden gelöscht (Personen -> "Haupt").
    /// </summary>
    public void ApplyGroups(IReadOnlyList<GroupDef> edited)
    {
        var keep = edited.Where(e => e.Id > 0).Select(e => e.Id).ToHashSet();
        foreach (var g in _groups.ToList()) if (!keep.Contains(g.Id) && g.Id != MainGroup) DeleteGroup(g.Id);
        var result = new List<GroupDef>();
        foreach (var e in edited)
        {
            var name = e.Name.Trim();
            if (name.Length == 0) continue;
            if (e.Id > 0)
            {
                if (_groups.Any(g => g.Id == e.Id)) result.Add(new GroupDef(e.Id, name));
            }
            else
            {
                int id = Math.Max(_groups.Count == 0 ? 0 : _groups.Max(g => g.Id), result.Count == 0 ? 0 : result.Max(g => g.Id)) + 1;
                result.Add(new GroupDef(id, name));
            }
        }
        if (result.All(g => g.Id != MainGroup))
            result.Insert(0, _groups.FirstOrDefault(g => g.Id == MainGroup) ?? new GroupDef(MainGroup, "Haupt"));
        _groups.Clear();
        _groups.AddRange(result);
    }

    /// <summary>Gruppe löschen (nicht "Haupt"); ihre Personen kommen nach "Haupt".</summary>
    public void DeleteGroup(int groupId)
    {
        if (groupId == MainGroup) return;
        _groups.RemoveAll(g => g.Id == groupId);
        foreach (var (pid, g) in _groupOf.ToList()) if (g == groupId) _groupOf[pid] = MainGroup;
    }

    /// <summary>Neue Reihenfolge der benannten Personen.</summary>
    public void SetOrder(IReadOnlyList<int> ids)
    {
        for (int i = 0; i < ids.Count; i++) _orders[ids[i]] = i;
    }

    /// <summary>Titelbild festlegen (gilt bei benannten Personen auch als Bestätigung).</summary>
    public void SetCover(int personId, long faceId)
    {
        _covers[personId] = faceId;
        if (_names.ContainsKey(personId) && _byId.TryGetValue(faceId, out var f) && f.PersonId == personId)
            f.Status = FaceStatus.Confirmed;
    }

    /// <summary>Alle Gesichter von from zu into verschieben.</summary>
    public void Merge(int from, int into)
    {
        if (from == into) return;
        foreach (var f in _faces) if (f.PersonId == from) f.PersonId = into;
        if (!_names.ContainsKey(into))
        {
            if (_names.TryGetValue(from, out var n)) _names[into] = n;
            if (_orders.TryGetValue(from, out var o)) _orders[into] = o;
            if (_groupOf.TryGetValue(from, out var g)) _groupOf[into] = g;
        }
        if (!_covers.ContainsKey(into) && _covers.TryGetValue(from, out var cv)) _covers[into] = cv;
        _names.Remove(from); _orders.Remove(from); _groupOf.Remove(from); _covers.Remove(from);
        if (_names.ContainsKey(into))
        {
            foreach (var f in _faces.Where(f => f.PersonId == into && f.Status == FaceStatus.Unnamed))
            {
                f.Status = FaceStatus.Auto; f.Score = 1f;
            }
        }
    }

    // ------------------------------------------------------------------ Stand für die Oberfläche

    private FaceSnapshot MakeSnapshot() => new()
    {
        Names = new Dictionary<int, string>(_names),
        Orders = new Dictionary<int, int>(_orders),
        Groups = _groups.ToList(),
        GroupOf = new Dictionary<int, int>(_groupOf),
        Covers = new Dictionary<int, long>(_covers),
        FacesByPath = _faces
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                          g => g.Select(f => new FaceRef(f.Id, f.Path, f.PersonId, f.L, f.T, f.R, f.B, f.Status, f.Score)).ToList(),
                          StringComparer.OrdinalIgnoreCase),
        ModelLabel = Kind.Label,
        FaceCount = _faces.Count,
    };

    private void RaiseChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        d.InvokeAsync(() => Changed?.Invoke());
    }
}
