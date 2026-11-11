using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>
/// Kurze, feste Nummern für Bilder auf der Karte (https://thumbs.local/{id} liefert das Vorschaubild,
/// Klicks auf der Karte melden die Nummer zurück). Gleicher Pfad = gleiche Nummer.
/// </summary>
public static class MapIds
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, int> Ids = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<GalleryImage> Images = [];

    public static int Id(GalleryImage img)
    {
        lock (Lock)
        {
            if (Ids.TryGetValue(img.Path, out var id))
            {
                Images[id] = img;   // neuestes Objekt merken
                return id;
            }
            id = Images.Count;
            Ids[img.Path] = id;
            Images.Add(img);
            return id;
        }
    }

    public static GalleryImage? Get(int id)
    {
        lock (Lock) return id >= 0 && id < Images.Count ? Images[id] : null;
    }
}
