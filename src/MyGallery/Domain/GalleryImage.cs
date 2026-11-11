using System.IO;

namespace MyGallery.Domain;

/// <summary>Woher das verwendete Datum eines Bildes stammt.</summary>
public enum DateSource
{
    Exif,
    Folder,
    File,
}

/// <summary>Ein Bild der Galerie.</summary>
public sealed class GalleryImage
{
    public required string Path { get; init; }

    /// <summary>Ordner relativ zum Bildordner mit '/' und abschliessendem '/', z. B. "2015_Zinnen/" ("" = Wurzel).</summary>
    public required string RelativeDir { get; init; }

    public long Size { get; init; }

    /// <summary>Letzte Änderung der Datei (Ortszeit).</summary>
    public DateTime Modified { get; init; }

    /// <summary>Pixelgrösse der Datei (vor der EXIF-Drehung).</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>EXIF-Orientation 1..8</summary>
    public int Orientation { get; init; } = 1;

    public DateTime? ExifDate { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }

    /// <summary>Land (ADM0_A3, z. B. "CHE") aus den GPS-Daten, sonst null.</summary>
    public string? Country { get; init; }

    /// <summary>Verwendetes Datum: EXIF-Aufnahmedatum, sonst Datum aus Ordnername, sonst Dateidatum.</summary>
    public DateTime Date { get; init; }
    public DateSource DateSource { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Schlüssel für Favoriten: relativer Ordner + Dateiname (wie favKey in der Android-App, ohne "Data/images/").</summary>
    public string FavKey => RelativeDir + Name;

    /// <summary>Ordnername = Albumname</summary>
    public string FolderName
    {
        get
        {
            var dir = RelativeDir.TrimEnd('/');
            return dir.Length == 0 ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path) ?? "") : dir[(dir.LastIndexOf('/') + 1)..];
        }
    }

    /// <summary>Breite/Höhe so, wie das Bild angezeigt wird (EXIF-Drehung berücksichtigt).</summary>
    public bool IsRotated90 => Orientation is 5 or 6 or 7 or 8;
    public int DisplayWidth => IsRotated90 ? Height : Width;
    public int DisplayHeight => IsRotated90 ? Width : Height;

    public bool HasLocation => Lat is not null && Lon is not null;

    public static string DateSourceLabel(DateSource s) => s switch
    {
        DateSource.Exif => "EXIF-Aufnahmedatum",
        DateSource.Folder => "Ordnername (kein EXIF-Datum)",
        _ => "Dateidatum (kein Aufnahmedatum vorhanden)",
    };

    public static GalleryImage FromRow(Data.ImageRow r, string root)
    {
        var dir = System.IO.Path.GetDirectoryName(r.Path) ?? "";
        var rel = System.IO.Path.GetRelativePath(root, dir).Replace('\\', '/');
        rel = rel == "." ? "" : rel.TrimEnd('/') + "/";
        var modified = new DateTime(r.MTime, DateTimeKind.Utc).ToLocalTime();
        var (date, source) = ImageDates.Resolve(r.ExifDate, rel, modified);
        return new GalleryImage
        {
            Path = r.Path,
            RelativeDir = rel,
            Size = r.Size,
            Modified = modified,
            Width = r.Width,
            Height = r.Height,
            Orientation = r.Orientation,
            ExifDate = r.ExifDate,
            Lat = r.Lat,
            Lon = r.Lon,
            Country = r.Country is { Length: > 1 } c ? c : null,
            Date = date,
            DateSource = source,
        };
    }
}
