using System.IO;
using Microsoft.Data.Sqlite;

namespace MyGallery.Data;

/// <summary>Ein Bild so, wie es in der Datenbank steht (Cache der Datei- und EXIF-Daten).</summary>
public sealed record ImageRow(
    string Path,
    long Size,
    // Letzte Änderung der Datei (UTC-Ticks) – geändert = EXIF neu lesen
    long MTime,
    int Width,
    int Height,
    int Orientation,
    DateTime? ExifDate,
    double? Lat,
    double? Lon,
    // Land (ADM0_A3, z. B. "CHE"), "-" = keins gefunden, null = noch nicht bestimmt
    string? Country = null);

/// <summary>Lokale SQLite-Datenbank (gallery.db).</summary>
public sealed class GalleryDatabase
{
    private readonly string _connectionString;

    public string FilePath { get; }

    public GalleryDatabase(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath }.ToString();
        CreateSchema();
    }

    /// <summary>Verbindung für andere Speicher (z. B. FaceStore) zur selben Datenbank.</summary>
    public SqliteConnection OpenConnection() => Open();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void CreateSchema()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS image (
                path        TEXT    PRIMARY KEY COLLATE NOCASE,
                size        INTEGER NOT NULL,
                mtime       INTEGER NOT NULL,
                width       INTEGER NOT NULL,
                height      INTEGER NOT NULL,
                orientation INTEGER NOT NULL,
                exifDate    INTEGER,
                lat REAL, lon REAL
            );
            CREATE TABLE IF NOT EXISTS favorite (
                key TEXT PRIMARY KEY COLLATE NOCASE
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c, "image", "country", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection c, string table, string column, string type)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public List<ImageRow> LoadAll()
    {
        var list = new List<ImageRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, size, mtime, width, height, orientation, exifDate, lat, lon, country FROM image";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ImageRow(
                r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : new DateTime(r.GetInt64(6), DateTimeKind.Local),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetString(9)));
        }
        return list;
    }

    public void Upsert(IReadOnlyCollection<ImageRow> rows)
    {
        if (rows.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO image (path, size, mtime, width, height, orientation, exifDate, lat, lon, country)
            VALUES ($p, $s, $m, $w, $h, $o, $e, $la, $lo, $c)
            """;
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        var s = cmd.Parameters.Add("$s", SqliteType.Integer);
        var m = cmd.Parameters.Add("$m", SqliteType.Integer);
        var w = cmd.Parameters.Add("$w", SqliteType.Integer);
        var h = cmd.Parameters.Add("$h", SqliteType.Integer);
        var o = cmd.Parameters.Add("$o", SqliteType.Integer);
        var e = cmd.Parameters.Add("$e", SqliteType.Integer);
        var la = cmd.Parameters.Add("$la", SqliteType.Real);
        var lo = cmd.Parameters.Add("$lo", SqliteType.Real);
        var co = cmd.Parameters.Add("$c", SqliteType.Text);
        foreach (var row in rows)
        {
            p.Value = row.Path;
            s.Value = row.Size;
            m.Value = row.MTime;
            w.Value = row.Width;
            h.Value = row.Height;
            o.Value = row.Orientation;
            e.Value = row.ExifDate is DateTime d ? d.Ticks : DBNull.Value;
            la.Value = row.Lat is double a ? a : DBNull.Value;
            lo.Value = row.Lon is double b ? b : DBNull.Value;
            co.Value = (object?)row.Country ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Delete(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM image WHERE path = $p";
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        foreach (var path in paths)
        {
            p.Value = path;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ------------------------------------------------------------------ Länder

    public void UpdateCountries(IReadOnlyCollection<(string Path, string Country)> items)
    {
        if (items.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE image SET country = $c WHERE path = $p";
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        var co = cmd.Parameters.Add("$c", SqliteType.Text);
        foreach (var (path, country) in items)
        {
            p.Value = path;
            co.Value = country;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Alle Länder neu bestimmen lassen (geänderte Regeln/Grenzen).</summary>
    public void ClearCountries()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE image SET country = NULL";
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ Favoriten

    public List<string> LoadFavorites()
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key FROM favorite";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void SetFavorites(IReadOnlyCollection<string> keys, bool favorite)
    {
        if (keys.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = favorite
            ? "INSERT OR IGNORE INTO favorite (key) VALUES ($k)"
            : "DELETE FROM favorite WHERE key = $k";
        var k = cmd.Parameters.Add("$k", SqliteType.Text);
        foreach (var key in keys)
        {
            k.Value = key;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
