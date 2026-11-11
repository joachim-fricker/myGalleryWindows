using System.IO;
using System.Text.Json;
using MyGallery.Domain;

namespace MyGallery.Data;

/// <summary>
/// Ländergrenzen aus Assets/countries.geojson (Natural Earth 1:50 Mio., gemeinfrei; aus dem Activity Viewer).
/// Properties je Land: id = ADM0_A3 (z. B. CHE), name = deutscher Name.
/// </summary>
public sealed class CountryLookup
{
    private const double MPerDeg = 111_194.93;
    private const double NearM = 3_000;   // Punkte knapp ausserhalb (See, Küste) gehen ans nächste Land

    /// <summary>Polygone als flache Arrays [lon0, lat0, lon1, lat1, ...]; erster Ring aussen, weitere = Löcher.</summary>
    private sealed record Country(string Id, string Name, double MinLon, double MinLat, double MaxLon, double MaxLat,
                                  List<List<double[]>> Polygons);

    private readonly Lazy<List<Country>> _countries;

    public CountryLookup(string geoJsonPath)
    {
        _countries = new Lazy<List<Country>>(() => Parse(File.ReadAllText(geoJsonPath)));
    }

    /// <summary>ADM0_A3 → deutscher Name (alle Länder der Karte).</summary>
    public IReadOnlyDictionary<string, string> Names => _names ??= _countries.Value.ToDictionary(c => c.Id, c => c.Name);

    private Dictionary<string, string>? _names;

    /// <summary>Deutscher Name eines Landes (ADM0_A3), sonst der Code.</summary>
    public string NameOf(string code) => Names.GetValueOrDefault(code, code);

    /// <summary>Land an der Position (ADM0_A3) oder null.</summary>
    public string? CountryAt(double lat, double lon)
    {
        foreach (var o in Countries.Overrides)
            if (o.Contains(lat, lon)) return o.Country;

        var countries = _countries.Value;
        foreach (var c in countries)
            if (InBox(c, lat, lon, 0) && Contains(c, lat, lon)) return c.Id;

        double pad = NearM / MPerDeg / Math.Max(0.2, Math.Cos(lat * Math.PI / 180));
        string? best = null;
        double bestDist = NearM;
        foreach (var c in countries.Where(c => InBox(c, lat, lon, pad)))
        {
            double d = DistanceM(c, lat, lon);
            if (d <= bestDist) { bestDist = d; best = c.Id; }
        }
        return best;
    }

    private static bool InBox(Country c, double lat, double lon, double pad) =>
        lat >= c.MinLat - pad && lat <= c.MaxLat + pad && lon >= c.MinLon - pad && lon <= c.MaxLon + pad;

    private static bool Contains(Country c, double lat, double lon) =>
        c.Polygons.Any(rings => InRing(rings[0], lat, lon) && !rings.Skip(1).Any(r => InRing(r, lat, lon)));

    /// <summary>Ray-Casting (gerade/ungerade Anzahl Schnittpunkte).</summary>
    private static bool InRing(double[] r, double lat, double lon)
    {
        bool inside = false;
        int n = r.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = r[2 * i], yi = r[2 * i + 1], xj = r[2 * j], yj = r[2 * j + 1];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Kürzester Abstand zum Rand des Landes in Metern (lokal eben gerechnet).</summary>
    private static double DistanceM(Country c, double lat, double lon)
    {
        double kx = MPerDeg * Math.Cos(lat * Math.PI / 180);
        double best = double.MaxValue;
        foreach (var rings in c.Polygons)
        foreach (var r in rings)
        {
            for (int i = 0; i < r.Length / 2 - 1; i++)
            {
                double ax = (r[2 * i] - lon) * kx, ay = (r[2 * i + 1] - lat) * MPerDeg;
                double bx = (r[2 * i + 2] - lon) * kx, by = (r[2 * i + 3] - lat) * MPerDeg;
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                double t = len2 == 0 ? 0 : Math.Clamp((-ax * dx - ay * dy) / len2, 0, 1);
                best = Math.Min(best, Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2)));
            }
        }
        return best;
    }

    private static List<Country> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var result = new List<Country>();
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = f.GetProperty("properties");
            double minLon = 180, minLat = 90, maxLon = -180, maxLat = -90;
            var polygons = new List<List<double[]>>();
            foreach (var poly in f.GetProperty("geometry").GetProperty("coordinates").EnumerateArray())   // MultiPolygon
            {
                var rings = new List<double[]>();
                foreach (var ring in poly.EnumerateArray())
                {
                    var pts = ring.EnumerateArray().ToList();
                    var flat = new double[pts.Count * 2];
                    for (int i = 0; i < pts.Count; i++)
                    {
                        double x = pts[i][0].GetDouble(), y = pts[i][1].GetDouble();
                        flat[2 * i] = x;
                        flat[2 * i + 1] = y;
                        minLon = Math.Min(minLon, x); maxLon = Math.Max(maxLon, x);
                        minLat = Math.Min(minLat, y); maxLat = Math.Max(maxLat, y);
                    }
                    rings.Add(flat);
                }
                polygons.Add(rings);
            }
            result.Add(new Country(props.GetProperty("id").GetString()!, props.GetProperty("name").GetString()!,
                                   minLon, minLat, maxLon, maxLat, polygons));
        }
        return result;
    }
}
