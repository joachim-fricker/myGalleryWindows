using System.Globalization;
using System.Text.RegularExpressions;

namespace MyGallery.Domain;

/// <summary>Datumslogik wie in der Android-App (Grouping.kt).</summary>
public static partial class ImageDates
{
    /// <summary>Jahr, Monat (0 = unbekannt), Tag (0 = unbekannt) aus einem Ordnernamen.</summary>
    public readonly record struct FolderDate(int Year, int Month, int Day);

    [GeneratedRegex(@"^((?:19|20)\d{2})(?:[-_. ]?(\d{2}))?(?:[-_. ]?(\d{2}))?(?!\d)")]
    private static partial Regex DatePrefix();

    [GeneratedRegex(@"^((?:19|20)\d{2})(?!\d)")]
    private static partial Regex YearPrefix();

    /// <summary>Ordnerdatum aus "2015_Zinnen", "2015-07_Nepal", "20150712 Ausflug" – vom innersten Ordner nach aussen.</summary>
    public static FolderDate? FolderDateFromPath(string relativeDir)
    {
        foreach (var seg in Enumerable.Reverse(relativeDir.TrimEnd('/').Split('/')))
        {
            var m = DatePrefix().Match(seg);
            if (!m.Success) continue;
            int year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int month = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            int day = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            if (month is < 1 or > 12) { month = 0; day = 0; }
            if (day is < 1 or > 31) day = 0;
            if (month > 0 && day > DateTime.DaysInMonth(year, month)) day = 0;
            return new FolderDate(year, month, day);
        }
        return null;
    }

    /// <summary>Jahr aus dem Ordnerpfad, z. B. "2015_Zinnen/" -> 2015 (Ordnername muss mit dem Jahr beginnen).</summary>
    public static int? YearFromPath(string relativeDir)
    {
        foreach (var seg in Enumerable.Reverse(relativeDir.TrimEnd('/').Split('/')))
        {
            var m = YearPrefix().Match(seg);
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>
    /// Verwendetes Datum: EXIF > Ordnername > Dateidatum.
    /// Beim Ordnernamen wird das Dateidatum genommen, wenn es im passenden Jahr/Monat/Tag liegt (genauer),
    /// sonst der Anfang des Zeitraums um 12:00.
    /// </summary>
    public static (DateTime Date, DateSource Source) Resolve(DateTime? exif, string relativeDir, DateTime modified)
    {
        if (exif is DateTime e) return (e, DateSource.Exif);
        if (FolderDateFromPath(relativeDir) is FolderDate fd)
        {
            bool fits = modified.Year == fd.Year && (fd.Month == 0 || modified.Month == fd.Month) &&
                        (fd.Day == 0 || modified.Day == fd.Day);
            if (fits) return (modified, DateSource.Folder);
            return (new DateTime(fd.Year, fd.Month > 0 ? fd.Month : 1, fd.Day > 0 ? fd.Day : 1, 12, 0, 0, DateTimeKind.Local),
                    DateSource.Folder);
        }
        return (modified, DateSource.File);
    }

    /// <summary>"November 2026"</summary>
    public static string MonthLabel(DateTime d)
    {
        var s = d.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return s.Length > 0 ? char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..] : s;
    }
}
