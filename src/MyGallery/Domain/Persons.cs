using MyGallery.Data;

namespace MyGallery.Domain;

/// <summary>Eine Person mit allen Bildern, auf denen sie erkannt wurde (wie PersonGroup in PersonsScreen.kt).</summary>
public sealed class PersonInfo
{
    public required int Id { get; init; }
    public string? Name { get; init; }
    public string DisplayName => Name ?? "Unbenannt";

    /// <summary>Bilder mit sicherer Zuordnung (bestätigt oder automatisch), in der Reihenfolge der Galerie</summary>
    public required IReadOnlyList<GalleryImage> Images { get; init; }

    /// <summary>Gesicht für das Vorschaubild</summary>
    public required FaceRef Cover { get; init; }
    public required GalleryImage CoverImage { get; init; }

    public int? GroupId { get; init; }
    public string? GroupName { get; init; }
    public int Order { get; init; } = int.MaxValue;

    /// <summary>Sicher zugeordnete Gesichter (bestätigt + automatisch)</summary>
    public IReadOnlyList<(GalleryImage Image, FaceRef Face)> Faces { get; init; } = [];

    /// <summary>Vorschläge, die noch geprüft werden müssen (sicherste zuerst)</summary>
    public IReadOnlyList<(GalleryImage Image, FaceRef Face)> Suggestions { get; init; } = [];

    public int AutoCount => Faces.Count(f => f.Face.Status == FaceStatus.Auto);
}

public static class Persons
{
    /// <summary>Unbenannte Gruppen erst ab so vielen Bildern anzeigen (Einzelgesichter sind meist Zufallstreffer).</summary>
    public const int MinImagesUnnamed = 3;

    /// <summary>
    /// Reihenfolge: benannte Personen nach Gruppen (Gruppen-Reihenfolge, darin eigene Reihenfolge, dann Name),
    /// dann unbenannte (nach Anzahl Bilder).
    /// </summary>
    public static List<PersonInfo> Build(IReadOnlyList<GalleryImage> images, FaceSnapshot snap, bool includeSmall = false)
    {
        var main = new Dictionary<int, List<(GalleryImage, FaceRef)>>();
        var sugg = new Dictionary<int, List<(GalleryImage, FaceRef)>>();
        foreach (var img in images)
        {
            if (!snap.FacesByPath.TryGetValue(img.Path, out var faces)) continue;
            foreach (var f in faces)
            {
                bool isNamed = snap.Names.ContainsKey(f.PersonId);
                if (isNamed && f.Status == FaceStatus.Suggested) Add(sugg, f.PersonId, (img, f));
                else if (isNamed && f.Status is FaceStatus.Auto or FaceStatus.Confirmed) Add(main, f.PersonId, (img, f));
                else if (!isNamed && f.Status == FaceStatus.Unnamed) Add(main, f.PersonId, (img, f));
            }
        }
        foreach (var pid in sugg.Keys) if (!main.ContainsKey(pid)) main[pid] = [];

        var all = new List<PersonInfo>();
        foreach (var (pid, list) in main)
        {
            var s = sugg.GetValueOrDefault(pid) ?? [];
            var pool = list.Count > 0 ? list : s;
            if (pool.Count == 0) continue;
            var chosen = snap.Covers.TryGetValue(pid, out var fid) ? pool.FirstOrDefault(x => x.Item2.Id == fid) : default;
            var cover = chosen.Item2 is not null ? chosen
                : pool.FirstOrDefault(x => x.Item2.Status == FaceStatus.Confirmed) is { Item2: not null } conf ? conf
                : pool.Take(30).MaxBy(x => x.Item2.Area);
            string? name = snap.Names.GetValueOrDefault(pid);
            int? group = name is null ? null : snap.GroupOf.TryGetValue(pid, out var g) ? g : FaceStore.MainGroup;
            var person = new PersonInfo
            {
                Id = pid,
                Name = name,
                Images = list.Select(x => x.Item1).Distinct().ToList(),
                Cover = cover.Item2,
                CoverImage = cover.Item1,
                GroupId = group,
                GroupName = group is null ? null : snap.Groups.FirstOrDefault(x => x.Id == group)?.Name,
                Order = snap.Orders.TryGetValue(pid, out var o) ? o : int.MaxValue,
                Faces = list,
                Suggestions = s.OrderByDescending(x => x.Item2.Score).ToList(),
            };
            if (includeSmall || person.Name is not null || person.Images.Count >= MinImagesUnnamed) all.Add(person);
        }

        var groupIndex = snap.Groups.Select((g, i) => (g.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var named = all.Where(p => p.Name is not null)
            .OrderBy(p => p.GroupId is int gid && groupIndex.TryGetValue(gid, out var gi) ? gi : int.MaxValue)
            .ThenBy(p => p.Order)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCulture);
        var unnamed = all.Where(p => p.Name is null).OrderByDescending(p => p.Images.Count);
        return named.Concat(unnamed).ToList();
    }

    private static void Add(Dictionary<int, List<(GalleryImage, FaceRef)>> map, int pid, (GalleryImage, FaceRef) item)
    {
        if (!map.TryGetValue(pid, out var list)) map[pid] = list = [];
        list.Add(item);
    }
}
