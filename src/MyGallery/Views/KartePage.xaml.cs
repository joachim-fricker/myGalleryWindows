using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>
/// Tab „Karte“ (wie MapScreen.kt): alle Bilder mit GPS auf OpenStreetMap, nahe beieinander als Gruppe mit
/// Vorschaubild und Anzahl. Klick auf eine Gruppe oder „Bilder im Ausschnitt“ zeigt die Bilder.
/// </summary>
public partial class KartePage : Page
{
    private static KartePage? _instance;
    private static (double Lat, double Lon)? _pendingLocation;

    private Task? _mapInit;
    private bool _mapReady;
    private int _sentCount = -1;

    public KartePage()
    {
        InitializeComponent();
        _instance = this;
        AppServices.Library.ImagesChanged += SendPhotos;
        DetailGrid.ImageClicked += (images, index) => OpenViewer(images, index);
        Loaded += async (_, _) =>
        {
            _mapInit ??= InitMapAsync();
            await _mapInit;
            ApplyPending();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DetailPanel.Visibility == Visibility.Visible)
            {
                CloseDetail();
                e.Handled = true;
            }
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1 && DetailPanel.Visibility == Visibility.Visible)
            {
                CloseDetail();
                e.Handled = true;
            }
        };
    }

    /// <summary>Aufnahmeort auf der Karte zeigen (aus dem Bildbetrachter).</summary>
    public static void ShowLocation(double lat, double lon)
    {
        _pendingLocation = (lat, lon);
        _instance?.ApplyPending();
    }

    private async void ApplyPending()
    {
        if (_pendingLocation is not { } p || !_mapReady) return;
        _pendingLocation = null;
        CloseDetail();
        await Map.ExecuteScriptAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"showLocation({p.Lat}, {p.Lon})"));
    }

    private async Task InitMapAsync()
    {
        try
        {
            await MapHost.InitAsync(Map);
        }
        catch (Exception ex)
        {
            TitleText.Text = $"Karte konnte nicht geladen werden: {ex.Message}";
            Log.Write($"Karte: {ex}");
            return;
        }
        Map.WebMessageReceived += async (_, e) =>
        {
            if (MapHost.Parse(e) is not { } m) return;
            if (m.InView is int n) InViewButton.Content = $"Bilder im Ausschnitt ({n:N0})";
            if (m.Cluster is { } ids) ShowImages(ids.Select(MapIds.Get).OfType<GalleryImage>(), "Gruppe auf der Karte");
            if (m.Photo is int id && MapIds.Get(id) is { } img)
            {
                // Bildbetrachter mit allen Bildern im Ausschnitt (zum Blättern)
                var inView = Sort(MapHost.ImagesFromJson(await Map.ExecuteScriptAsync("idsInView()")));
                int index = inView.FindIndex(i => string.Equals(i.Path, img.Path, StringComparison.OrdinalIgnoreCase));
                if (index < 0) { inView = [img]; index = 0; }
                OpenViewer(inView, index);
            }
        };
        _mapReady = true;
        SendPhotos();
    }

    private void SendPhotos()
    {
        if (!_mapReady) return;
        var withGps = AppServices.Library.Images.Where(i => i.HasLocation).ToList();
        if (withGps.Count == _sentCount) return;   // während des Einlesens nicht bei jeder Kleinigkeit neu zeichnen
        bool first = _sentCount < 0;
        _sentCount = withGps.Count;
        TitleText.Text = $"Karte ({withGps.Count:N0} Bilder mit GPS)";
        MapHost.ShowPhotos(Map, withGps, fit: first && _pendingLocation is null);
    }

    /// <summary>Bilder in der eingestellten Reihenfolge der Galerie.</summary>
    private static List<GalleryImage> Sort(IEnumerable<GalleryImage> images)
    {
        var order = AppServices.Library.Images
            .Select((img, i) => (img.Path, i))
            .ToDictionary(x => x.Path, x => x.i, StringComparer.OrdinalIgnoreCase);
        return images.OrderBy(i => order.GetValueOrDefault(i.Path, int.MaxValue)).ToList();
    }

    private void ShowImages(IEnumerable<GalleryImage> images, string title)
    {
        var list = Sort(images);
        if (list.Count == 0) return;
        DetailTitle.Text = $"{title}: {list.Count:N0} Bilder";
        var first = list.Min(i => i.Date);
        var last = list.Max(i => i.Date);
        DetailSubtitle.Text = first.Date == last.Date ? $"{first:dd.MM.yyyy}" : $"{first:dd.MM.yyyy} – {last:dd.MM.yyyy}";
        MapPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailGrid.SetImages(list, keepPosition: false);
    }

    private void CloseDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        MapPanel.Visibility = Visibility.Visible;
    }

    private async void OnInView(object sender, RoutedEventArgs e)
    {
        if (!_mapReady) return;
        var images = MapHost.ImagesFromJson(await Map.ExecuteScriptAsync("idsInView()"));
        ShowImages(images, "Im Ausschnitt");
    }

    private void OnBack(object sender, RoutedEventArgs e) => CloseDetail();

    private void OpenViewer(IReadOnlyList<GalleryImage> images, int index)
    {
        var viewer = new ViewerWindow(images, index) { Owner = Window.GetWindow(this) };
        viewer.Show();
    }
}
