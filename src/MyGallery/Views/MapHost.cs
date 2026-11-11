using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Lädt die Leaflet-Karte (Assets/map.html) in ein WebView2 und liefert die Vorschaubilder dazu.</summary>
public static class MapHost
{
    /// <summary>Nachricht von map.html</summary>
    public sealed record Message(int? Photo, List<int>? Cluster, int? InView, string? Country);

    public static async Task InitAsync(WebView2 webView)
    {
        var env = await AppServices.WebViewEnvironment;
        await webView.EnsureCoreWebView2Async(env);
        var core = webView.CoreWebView2;
        // map.html über eine https-Adresse ausliefern (Kartenserver verlangen einen Referer)
        core.SetVirtualHostNameToFolderMapping(
            "app.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);

        // Vorschaubilder: https://thumbs.local/{id} → JPEG aus dem Vorschaubild-Cache
        core.AddWebResourceRequestedFilter("https://thumbs.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var uri = new Uri(e.Request.Uri);
                if (int.TryParse(uri.AbsolutePath.Trim('/'), out var id) && MapIds.Get(id) is { } img &&
                    await AppServices.Thumbs.GetJpegBytesAsync(img) is { } bytes)
                {
                    e.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryStream(bytes), 200, "OK", "Content-Type: image/jpeg\r\nCache-Control: max-age=86400");
                }
                else
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Karte Vorschaubild: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        };

        var loaded = new TaskCompletionSource();
        webView.NavigationCompleted += (_, _) => loaded.TrySetResult();
        webView.Source = new Uri("https://app.local/map.html");
        await loaded.Task;
    }

    /// <summary>Bilder mit GPS als [[id, lat, lon], ...] an die Karte schicken.</summary>
    public static void ShowPhotos(WebView2 webView, IEnumerable<GalleryImage> images, bool fit)
    {
        var photos = images
            .Where(i => i.Lat is not null && i.Lon is not null)
            .Select(i => new object[] { MapIds.Id(i), Math.Round(i.Lat!.Value, 6), Math.Round(i.Lon!.Value, 6) })
            .ToList();
        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { photos, fit }));
    }

    public static Message? Parse(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            int? Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(v.GetDouble()) : null;
            List<int>? cluster = root.TryGetProperty("cluster", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => (int)Math.Round(x.GetDouble())).ToList()
                : null;
            string? country = root.TryGetProperty("country", out var co) ? co.GetString() : null;
            return new Message(Int("photo"), cluster, Int("inView"), country);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Ergebnis von ExecuteScriptAsync("idsInView()") → Bilder</summary>
    public static List<GalleryImage> ImagesFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(x => MapIds.Get((int)Math.Round(x.GetDouble())))
                .OfType<GalleryImage>()
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}
