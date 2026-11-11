namespace MyGallery.Domain;

/// <summary>Ein Album = ein Ordner. Bilder in der eingestellten Sortierung.</summary>
public sealed record Album(string RelativeDir, string Name, IReadOnlyList<GalleryImage> Images, int Year)
{
    public GalleryImage Cover => Images[0];
}

/// <summary>Alle Alben eines Jahres.</summary>
public sealed record YearAlbums(int Year, IReadOnlyList<Album> Albums)
{
    public int ImageCount => Albums.Sum(a => a.Images.Count);
}

public static class Albums
{
    /// <summary>
    /// Jeder Ordner = ein Album (wie buildAlbums in Grouping.kt). Jahr = Jahr am Anfang des Ordnernamens/Pfads
    /// (z. B. "2015_Zinnen"), sonst das Jahr mit den meisten Aufnahmen.
    /// Ergebnis: Jahre und darin Alben neueste zuerst (oder älteste zuerst).
    /// </summary>
    /// <param name="images">Bilder in der gewünschten Reihenfolge</param>
    public static List<YearAlbums> Build(IReadOnlyList<GalleryImage> images, bool oldestFirst = false)
    {
        var albums = new List<Album>();
        foreach (var group in images.GroupBy(i => i.RelativeDir, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            int year = ImageDates.YearFromPath(group.Key)
                       ?? list.GroupBy(i => i.Date.Year).OrderByDescending(g => g.Count()).First().Key;
            albums.Add(new Album(group.Key, list[0].FolderName, list, year));
        }
        var years = albums.GroupBy(a => a.Year);
        return (oldestFirst ? years.OrderBy(g => g.Key) : years.OrderByDescending(g => g.Key))
            .Select(g => new YearAlbums(g.Key, (oldestFirst
                ? g.OrderBy(a => a.Images.Min(i => i.Date))
                : g.OrderByDescending(a => a.Images.Max(i => i.Date))).ToList()))
            .ToList();
    }

    /// <summary>
    /// Ordnerbaum eines Jahres fürs Menü: Unterordner als Kinder. Ordner ohne eigene Bilder fassen ihre
    /// Unterordner zusammen. Der Jahresordner (z. B. "2013/") wird übersprungen, das Jahr steht ja schon darüber.
    /// </summary>
    public static List<FolderNode> Tree(YearAlbums year)
    {
        var root = new FolderNode("", "");
        foreach (var album in year.Albums)
        {
            var node = root;
            var dir = album.RelativeDir.TrimEnd('/');
            if (dir.Length > 0)
            {
                string path = "";
                foreach (var seg in dir.Split('/'))
                {
                    path += seg + "/";
                    node = node.Child(seg, path);
                }
            }
            node.Album = album;
        }
        root.Count();

        // Den Jahresordner selbst (z. B. "2013/") überspringen, die Jahreszahl steht schon darüber
        var yearName = year.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var top = root;
        while (top.Album is null && top.Children.Count == 1 && top.Children[0].Name == yearName) top = top.Children[0];
        var result = new List<FolderNode>();
        if (top.Album is not null)
        {
            // Bilder direkt im obersten Ordner (z. B. "2026/") als eigener Eintrag
            result.Add(new FolderNode(top.Album.Name, top.Dir) { Album = top.Album, ImageCount = top.Album.Images.Count });
        }
        result.AddRange(top.Children);
        return result;
    }
}

/// <summary>Ein Ordner im Menübaum. Album = null: Ordner ohne eigene Bilder (zeigt die Bilder aller Unterordner).</summary>
public sealed class FolderNode(string name, string dir)
{
    public string Name { get; } = name;

    /// <summary>Relativer Pfad mit abschliessendem '/'</summary>
    public string Dir { get; } = dir;

    public Album? Album { get; set; }
    public List<FolderNode> Children { get; } = [];

    /// <summary>Bilder inkl. Unterordner</summary>
    public int ImageCount { get; set; }

    public FolderNode Child(string name, string dir)
    {
        var c = Children.FirstOrDefault(x => string.Equals(x.Dir, dir, StringComparison.OrdinalIgnoreCase));
        if (c is null)
        {
            c = new FolderNode(name, dir);
            Children.Add(c);
        }
        return c;
    }

    public int Count() => ImageCount = (Album?.Images.Count ?? 0) + Children.Sum(c => c.Count());
}
