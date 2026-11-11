using System.Globalization;

namespace MyGallery.Domain;

/// <summary>
/// Regeln für die Länder (wie VisitedCountries.kt in der Android-App und Countries.cs im Activity Viewer).
/// Codes = ADM0_A3 aus countries.geojson (z. B. "CHE").
/// </summary>
public static class Countries
{
    /// <summary>Länder, die ohne (GPS-)Bilder als besucht gelten. GBR = ganzes Vereinigtes Königreich.</summary>
    public static readonly HashSet<string> ManualVisited =
    [
        "CAN", "USA", "MEX", "TUN", "MAR", "GBR", "IRL", "NLD", "BEL", "AND",
        "HUN", "DNK", "SWE", "CUB", "ECU", "BOL", "PER", "FIN", "DMA",
        "MCO", "VAT",   // Monaco, Vatikanstadt
        "LUX",          // Luxemburg
    ];

    /// <summary>Länder, die trotz Bildern nicht als besucht gelten (Bilder nicht von einer eigenen Reise).</summary>
    /// (Früher Indien: das waren Bilder aus Bhadrapur/Nepal, jetzt über Overrides richtig zugeordnet.)
    public static readonly HashSet<string> Excluded = [];

    /// <summary>Gebiet, in dem das Land fest vorgegeben ist (Grenzdaten dort zu ungenau).</summary>
    public sealed record Override(double MinLat, double MaxLat, double MinLon, double MaxLon, string Country)
    {
        public bool Contains(double lat, double lon) => lat >= MinLat && lat <= MaxLat && lon >= MinLon && lon <= MaxLon;
    }

    public static readonly Override[] Overrides =
    [
        // Khumbu-Tal bis Everest Base Camp: Die Grenze in den Natural-Earth-Daten verläuft
        // zu weit südlich, das Basislager läge sonst in China.
        new(27.80, 28.02, 86.60, 86.88, "NPL"),
        // Bhadrapur (Ostnepal, Grenzfluss Mechi): die groben Grenzdaten legen den Ort nach Indien
        new(26.50, 26.60, 88.05, 88.115, "NPL"),
        // Mosel bei Schengen/Perl: Aufnahmen vom deutschen Ufer landen sonst in Luxemburg
        new(49.580, 49.596, 6.369, 6.385, "DEU"),
    ];

    /// <summary>Erhöhen, wenn sich die Regeln oder Grenzdaten ändern: dann wird das Land aller Bilder neu bestimmt.</summary>
    public const int RulesVersion = 3;

    private static readonly Lazy<Dictionary<string, string>> Alpha2 = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                map.TryAdd(region.ThreeLetterISORegionName, region.TwoLetterISORegionName);
            }
            catch (ArgumentException)
            {
            }
        }
        // Natural Earth weicht bei einigen Codes von ISO ab
        map.TryAdd("KOS", "XK");
        map.TryAdd("SDS", "SS");
        map.TryAdd("PSX", "PS");
        return map;
    });

    /// <summary>ISO Alpha-2 (z. B. "CH") für eine Flagge, sonst null.</summary>
    public static string? IsoAlpha2(string adm0a3) => Alpha2.Value.GetValueOrDefault(adm0a3);

    /// <summary>Flaggenbild (wird aus dem Internet geladen, wie die Kartenkacheln).</summary>
    public static string? FlagUrl(string adm0a3) =>
        IsoAlpha2(adm0a3) is { } a2 ? $"https://flagcdn.com/w40/{a2.ToLowerInvariant()}.png" : null;
}

/// <summary>Ein Land in der Liste.</summary>
public sealed class CountryInfo
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? FlagUrl => Countries.FlagUrl(Code);

    /// <summary>Bilder aus diesem Land, in der eingestellten Sortierung (leer bei manuell eingetragenen)</summary>
    public IReadOnlyList<GalleryImage> Images { get; init; } = [];

    public bool Manual { get; init; }
    public bool Excluded { get; init; }
    public bool Visited => !Excluded;

    public int Count => Images.Count;
    public DateTime? First => Images.Count == 0 ? null : Images.Min(i => i.Date);
    public DateTime? Last => Images.Count == 0 ? null : Images.Max(i => i.Date);

    public string Period => Manual ? "ohne Bilder eingetragen"
        : Excluded ? $"{First:MM.yyyy} – {Last:MM.yyyy} (zählt nicht)"
        : First?.Year == Last?.Year && First?.Month == Last?.Month ? $"{First:MM.yyyy}"
        : $"{First:MM.yyyy} – {Last:MM.yyyy}";

    /// <summary>
    /// Länderliste: Länder aus den GPS-Daten der Bilder + manuell eingetragene (ohne Bilder).
    /// Ausgeschlossene Länder bleiben mit ihren Bildern drin, zählen aber nicht als besucht.
    /// </summary>
    public static List<CountryInfo> Build(IReadOnlyList<GalleryImage> images, IReadOnlyDictionary<string, string> names)
    {
        var fromImages = images
            .Where(i => i.Country is not null)
            .GroupBy(i => i.Country!)
            .Select(g => new CountryInfo
            {
                Code = g.Key,
                Name = names.GetValueOrDefault(g.Key, g.Key),
                Images = g.ToList(),
                Excluded = Countries.Excluded.Contains(g.Key),
            })
            .ToList();
        var known = fromImages.Select(c => c.Code).ToHashSet();
        var manual = Countries.ManualVisited.Where(c => !known.Contains(c)).Select(c => new CountryInfo
        {
            Code = c,
            Name = names.GetValueOrDefault(c, c),
            Manual = true,
        });
        return fromImages.Concat(manual).OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }
}
