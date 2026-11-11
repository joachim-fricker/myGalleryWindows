using System.IO;
using Microsoft.Web.WebView2.Core;

namespace MyGallery.Data;

/// <summary>Zentrale Objekte der App (einfacher Ersatz für Dependency Injection).</summary>
public static class AppServices
{
    /// <summary>%LOCALAPPDATA%\MyGallery</summary>
    public static string DataFolder { get; } = CreateDataFolder();

    public static Settings Settings { get; } = Settings.Load(Path.Combine(DataFolder, "settings.json"));

    public static GalleryDatabase Db { get; } = new(Path.Combine(DataFolder, "gallery.db"));

    public static FavoritesStore Favorites { get; } = new(Db);

    /// <summary>Gesichter und Personen</summary>
    public static FaceStore Faces { get; } = new(Db);

    public static ThumbnailCache Thumbs { get; } = new(Path.Combine(DataFolder, "thumbs"));

    public static GalleryLibrary Library { get; } = new(Db, Settings);

    /// <summary>Ländergrenzen (wird beim ersten Gebrauch geladen).</summary>
    public static CountryLookup Countries { get; } =
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "countries.geojson"));

    private static Task<CoreWebView2Environment>? _webViewEnvironment;

    /// <summary>Gemeinsame WebView2-Umgebung für alle Karten (Daten unter DataFolder\WebView2).</summary>
    public static Task<CoreWebView2Environment> WebViewEnvironment =>
        _webViewEnvironment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder, "WebView2"));

    private static string CreateDataFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyGallery");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
