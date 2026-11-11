using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace MyGallery.Data;

/// <summary>Liest Bildgrösse, Drehung, Aufnahmedatum und GPS über WIC (ohne das Bild zu dekodieren).</summary>
public static class ExifReader
{
    public sealed record Info(int Width, int Height, int Orientation, DateTime? Taken, double? Lat, double? Lon);

    public static Info Read(string path)
    {
        int width = 0, height = 0, orientation = 1;
        DateTime? taken = null;
        double? lat = null, lon = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                                           BitmapCacheOption.None);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            if (frame.Metadata is BitmapMetadata md)
            {
                // JPEG: /app1/ifd/…, TIFF/HEIC u.a.: /ifd/…
                foreach (var root in new[] { "/app1/ifd", "/ifd" })
                {
                    if (Query(md, root + "/{ushort=274}") is ushort o) orientation = o;
                    taken ??= ParseDate(Query(md, root + "/exif/{ushort=36867}"))
                              ?? ParseDate(Query(md, root + "/exif/{ushort=36868}"));
                    var la = Degrees(Query(md, root + "/gps/{ushort=2}"));
                    var lo = Degrees(Query(md, root + "/gps/{ushort=4}"));
                    if (lat is null && la is double a && lo is double b && (a != 0 || b != 0))
                    {
                        if (Query(md, root + "/gps/{ushort=1}") as string == "S") a = -a;
                        if (Query(md, root + "/gps/{ushort=3}") as string == "W") b = -b;
                        lat = a;
                        lon = b;
                    }
                    if (taken is not null || lat is not null) break;
                }
            }
        }
        catch (Exception)
        {
            // keine oder unlesbare Metadaten
        }
        if (orientation is < 1 or > 8) orientation = 1;
        return new Info(width, height, orientation, taken, lat, lon);
    }

    private static object? Query(BitmapMetadata md, string query)
    {
        try
        {
            return md.ContainsQuery(query) ? md.GetQuery(query) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? ParseDate(object? value) =>
        value is string text &&
        DateTime.TryParseExact(text.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out var t) && t.Year > 1900
            ? DateTime.SpecifyKind(t, DateTimeKind.Local)
            : null;

    /// <summary>EXIF-Grad/Minuten/Sekunden (je ein Bruch: unten Zähler, oben Nenner) in Dezimalgrad.</summary>
    private static double? Degrees(object? value)
    {
        if (value is not ulong[] { Length: 3 } parts) return null;
        static double Rational(ulong v)
        {
            uint numerator = (uint)(v & 0xFFFFFFFF);
            uint denominator = (uint)(v >> 32);
            return denominator == 0 ? 0 : (double)numerator / denominator;
        }
        return Rational(parts[0]) + Rational(parts[1]) / 60 + Rational(parts[2]) / 3600;
    }
}
