using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>
/// Tab „Länder“ (wie CountriesScreen.kt): Weltkarte mit besuchten Ländern (rot) und Liste mit Flagge,
/// Name, Anzahl Bilder und Zeitraum. Besucht = Land aus GPS-EXIF oder manuell eingetragen (Countries.cs).
/// </summary>
public partial class LaenderPage : Page
{
    private Task? _mapInit;
    private bool _mapReady;
    private List<CountryInfo> _all = [];
    private string _visitedKey = "";
    private string? _selected;
    private bool _selectingFromMap;

    public LaenderPage()
    {
        InitializeComponent();
        AppServices.Library.ImagesChanged += () => _ = RefreshAsync();
        DetailGrid.ImageClicked += (images, index) => OpenViewer(images, index);
        Loaded += async (_, _) =>
        {
            _mapInit ??= InitMapAsync();
            await _mapInit;
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

    private async Task InitMapAsync()
    {
        try
        {
            await MapHost.InitAsync(Map);
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Karte konnte nicht geladen werden: {ex.Message}";
            Log.Write($"Länderkarte: {ex}");
        }
        Map.WebMessageReceived += (_, e) =>
        {
            if (MapHost.Parse(e) is not { } m) return;
            if (m.Country is string code) SelectFromMap(code);
            if (m.Cluster is { } ids && Current is { } c)
                ShowDetail(c, ids.Select(MapIds.Get).OfType<GalleryImage>().ToList(), "Gruppe auf der Karte");
            if (m.Photo is int id && MapIds.Get(id) is { } img && Current is { } country)
            {
                var list = country.Images.ToList();
                int index = list.FindIndex(i => string.Equals(i.Path, img.Path, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) OpenViewer(list, index);
            }
        };
        _mapReady = Map.CoreWebView2 is not null;
        await RefreshAsync();
    }

    private CountryInfo? Current => _all.FirstOrDefault(c => c.Code == _selected);

    private async Task RefreshAsync()
    {
        var images = AppServices.Library.Images;
        // Ländergrenzen beim ersten Mal im Hintergrund laden (1,6 MB)
        var names = await Task.Run(() => AppServices.Countries.Names);
        _all = CountryInfo.Build(images, names);
        ApplyFilter();

        int visited = _all.Count(c => c.Visited);
        int withImages = _all.Count(c => c.Count > 0 && c.Visited);
        TitleText.Text = $"Länder ({visited})";
        SummaryText.Text = $"{visited} von {names.Count} Ländern besucht ({100.0 * visited / Math.Max(1, names.Count):N0} %) · " +
                           $"{withImages} mit Bildern, {_all.Count(c => c.Manual)} ohne Bilder eingetragen · " +
                           $"{images.Count(i => i.HasLocation):N0} Bilder mit GPS";

        if (!_mapReady) return;
        var visitedCodes = _all.Where(c => c.Visited).Select(c => c.Code).OrderBy(c => c).ToList();
        var excludedCodes = _all.Where(c => c.Excluded).Select(c => c.Code).OrderBy(c => c).ToList();
        var key = string.Join(",", visitedCodes) + "|" + string.Join(",", excludedCodes);
        if (key != _visitedKey)
        {
            _visitedKey = key;
            await Map.ExecuteScriptAsync(
                $"showCountries({JsonSerializer.Serialize(visitedCodes)}, {JsonSerializer.Serialize(excludedCodes)})");
            if (_selected is not null) await Map.ExecuteScriptAsync($"selectCountry({JsonSerializer.Serialize(_selected)}, false)");
        }
        if (Current is { } cur) ShowCountryInfo(cur.Code, zoom: false, sendPhotos: false);
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var rows = q.Length == 0
            ? _all
            : _all.Where(c => c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var selected = _selected;
        _selectingFromMap = true;
        CountryTable.ItemsSource = rows;
        CountryTable.SelectedItem = rows.FirstOrDefault(r => r.Code == selected);
        _selectingFromMap = false;
    }

    // ------------------------------------------------------------------ Auswahl

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SelectFromMap(string code)
    {
        _selectingFromMap = true;
        var row = (CountryTable.ItemsSource as IEnumerable<CountryInfo>)?.FirstOrDefault(r => r.Code == code);
        CountryTable.SelectedItem = row;   // nicht besuchte Länder sind nicht in der Liste
        if (row is not null) CountryTable.ScrollIntoView(row);
        _selectingFromMap = false;
        ShowCountryInfo(code, zoom: false, sendPhotos: true);
    }

    private void OnCountrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingFromMap || CountryTable.SelectedItem is not CountryInfo row) return;
        ShowCountryInfo(row.Code, zoom: true, sendPhotos: true);
    }

    private void OnCountryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CountryTable.SelectedItem is CountryInfo { Count: > 0 } c) ShowDetail(c, c.Images, null);
    }

    private async void ShowCountryInfo(string code, bool zoom, bool sendPhotos)
    {
        _selected = code;
        var c = Current;
        var name = c?.Name ?? AppServices.Countries.NameOf(code);
        CountryPanel.Visibility = Visibility.Visible;
        CountryTitle.Text = name;
        CountryInfoText.Text = c switch
        {
            null => "nicht besucht",
            { Manual: true } => "als besucht eingetragen, keine Bilder mit GPS",
            { Excluded: true } => $"{c.Count:N0} Bilder · {c.First:dd.MM.yyyy} – {c.Last:dd.MM.yyyy} · zählt nicht als besucht",
            _ => $"{c.Count:N0} Bilder · erstes {c.First:dd.MM.yyyy} · letztes {c.Last:dd.MM.yyyy}",
        };
        ShowImagesButton.Visibility = c is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        ShowImagesButton.Content = c is { Count: > 0 } ? $"Bilder anzeigen ({c.Count:N0})" : "Bilder anzeigen";
        try
        {
            CountryFlag.Source = Countries.FlagUrl(code) is { } url ? new BitmapImage(new Uri(url)) : null;
        }
        catch (Exception)
        {
            CountryFlag.Source = null;
        }

        if (!_mapReady) return;
        await Map.ExecuteScriptAsync($"selectCountry({JsonSerializer.Serialize(code)}, {(zoom ? "true" : "false")})");
        if (sendPhotos) MapHost.ShowPhotos(Map, c?.Images ?? [], fit: false);
    }

    // ------------------------------------------------------------------ Bilder

    private void OnShowImages(object sender, RoutedEventArgs e)
    {
        if (Current is { Count: > 0 } c) ShowDetail(c, c.Images, null);
    }

    private void ShowDetail(CountryInfo country, IReadOnlyList<GalleryImage> images, string? what)
    {
        if (images.Count == 0) return;
        var order = country.Images.Select((img, i) => (img.Path, i))
            .ToDictionary(x => x.Path, x => x.i, StringComparer.OrdinalIgnoreCase);
        var list = images.OrderBy(i => order.GetValueOrDefault(i.Path, int.MaxValue)).ToList();
        DetailTitle.Text = what is null ? country.Name : $"{country.Name} – {what}";
        DetailSubtitle.Text = $"{list.Count:N0} Bilder · {list.Min(i => i.Date):dd.MM.yyyy} – {list.Max(i => i.Date):dd.MM.yyyy}";
        OverviewPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailGrid.SetImages(list, keepPosition: false);
    }

    private void CloseDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e) => CloseDetail();

    private void OpenViewer(IReadOnlyList<GalleryImage> images, int index)
    {
        var viewer = new ViewerWindow(images, index) { Owner = Window.GetWindow(this) };
        viewer.Show();
    }
}
