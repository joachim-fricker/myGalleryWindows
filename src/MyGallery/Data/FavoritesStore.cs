using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>
/// Favoriten (wie in der Android-App nur in myGallery gespeichert).
/// Schlüssel = relativer Pfad + Dateiname, damit sie auch nach einem Neu-Einlesen erhalten bleiben.
/// </summary>
public sealed class FavoritesStore
{
    private readonly GalleryDatabase _db;
    private readonly HashSet<string> _keys;

    public FavoritesStore(GalleryDatabase db)
    {
        _db = db;
        _keys = new HashSet<string>(db.LoadFavorites(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Favoriten haben sich geändert (wird immer im UI-Thread ausgelöst).</summary>
    public event Action? Changed;

    public int Count => _keys.Count;

    public bool Contains(GalleryImage img) => _keys.Contains(img.FavKey);

    public void Toggle(GalleryImage img) => Set([img], !Contains(img));

    public void Set(IReadOnlyCollection<GalleryImage> images, bool favorite)
    {
        var keys = images.Select(i => i.FavKey).Where(k => favorite ? !_keys.Contains(k) : _keys.Contains(k)).ToList();
        if (keys.Count == 0) return;
        foreach (var k in keys)
        {
            if (favorite) _keys.Add(k); else _keys.Remove(k);
        }
        _db.SetFavorites(keys, favorite);
        RaiseChanged();
    }

    /// <summary>Für den Import vom Handy: Schlüssel direkt hinzufügen.</summary>
    public void AddKeys(IReadOnlyCollection<string> keys)
    {
        var add = keys.Where(k => !_keys.Contains(k)).ToList();
        if (add.Count == 0) return;
        foreach (var k in add) _keys.Add(k);
        _db.SetFavorites(add, true);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Changed?.Invoke();
        else dispatcher.InvokeAsync(() => Changed?.Invoke());
    }

    /// <summary>Alle Favoriten aus einer Bilderliste (Reihenfolge bleibt).</summary>
    public List<GalleryImage> Filter(IEnumerable<GalleryImage> images) => images.Where(Contains).ToList();
}
