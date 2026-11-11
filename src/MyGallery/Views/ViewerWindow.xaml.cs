using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MyGallery.Data;
using MyGallery.Domain;
using Wpf.Ui.Controls;

namespace MyGallery.Views;

/// <summary>
/// Vollbild-Betrachter: Pfeiltasten blättern, Mausrad/Doppelklick zoomen, Ziehen verschiebt,
/// I = Details, F11 = maximieren, Esc schliesst.
/// </summary>
public partial class ViewerWindow : FluentWindow
{
    private const int MaxDecode = 3840;

    private readonly IReadOnlyList<GalleryImage> _images;
    private int _index;
    private int _loadRequest;
    private Point? _dragStart;
    private Point _panStart;

    public ViewerWindow(IReadOnlyList<GalleryImage> images, int index)
    {
        InitializeComponent();
        WindowBounds.Apply(this, "viewer", widthFraction: 0.7, heightFraction: 0.85);
        _images = images;
        _index = index;
        Loaded += async (_, _) => await ShowCurrentAsync();
        PreviewKeyDown += async (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: await MoveToAsync(_index - 1); break;
                case Key.Right: await MoveToAsync(_index + 1); break;
                case Key.Home: await MoveToAsync(0); break;
                case Key.End: await MoveToAsync(_images.Count - 1); break;
                case Key.I: ToggleInfo(); break;
                case Key.F: ToggleFavorite(); break;
                case Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): Print(); break;
                case Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): Share(); break;
                case Key.F11:
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;
                case Key.Add or Key.OemPlus: ZoomAt(Center(), 1.25); break;
                case Key.Subtract or Key.OemMinus: ZoomAt(Center(), 1 / 1.25); break;
                case Key.D0 or Key.NumPad0: ResetZoom(); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    private async Task MoveToAsync(int index)
    {
        if (index < 0 || index >= _images.Count || index == _index) return;
        _index = index;
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        var img = _images[_index];
        int request = ++_loadRequest;
        ResetZoom();
        Title = $"{img.Name} – {_index + 1:N0} / {_images.Count:N0}";
        AppTitleBar.Title = Title;
        Caption.Text = $"{img.Date:dd.MM.yyyy HH:mm} · {img.FolderName} · {img.Name}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _images.Count - 1;
        if (InfoPanel.Visibility == Visibility.Visible) FillInfo(img);
        UpdateFavButton();

        Picture.Source = AppServices.Thumbs.TryGetMemory(img);   // sofort die Vorschau, dann das grosse Bild
        Busy.Visibility = Visibility.Visible;
        var full = await Task.Run(() => LoadFull(img));
        if (request != _loadRequest) return;
        Busy.Visibility = Visibility.Collapsed;
        if (full is not null) Picture.Source = full;
    }

    private static BitmapSource? LoadFull(GalleryImage img)
    {
        int w = 0, h = 0;
        if (img.Width > 0 && img.Height > 0)
        {
            if (img.Width >= img.Height) w = Math.Min(img.Width, MaxDecode);
            else h = Math.Min(img.Height, MaxDecode);
        }
        return ThumbnailCache.Decode(img.Path, w, h, img.Orientation);
    }

    // ------------------------------------------------------------------ Zoom / Verschieben

    private Point Center() => new(Picture.ActualWidth / 2, Picture.ActualHeight / 2);

    /// <summary>Zoomen um den Punkt p (Koordinaten des Bildes), der dabei an derselben Stelle bleibt.</summary>
    private void ZoomAt(Point p, double factor)
    {
        double s = Zoom.ScaleX;
        double ns = Math.Clamp(s * factor, 1, 12);
        if (ns <= 1.0001)
        {
            ResetZoom();
            return;
        }
        Pan.X += p.X * (s - ns);
        Pan.Y += p.Y * (s - ns);
        Zoom.ScaleX = Zoom.ScaleY = ns;
        Stage.Cursor = Cursors.SizeAll;
    }

    private void ResetZoom()
    {
        Zoom.ScaleX = Zoom.ScaleY = 1;
        Pan.X = Pan.Y = 0;
        Stage.Cursor = null;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(Picture), e.Delta > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    private void OnStageDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (Zoom.ScaleX > 1) ResetZoom();
            else ZoomAt(e.GetPosition(Picture), 2.5);
            return;
        }
        if (Zoom.ScaleX <= 1) return;
        _dragStart = e.GetPosition(Stage);
        _panStart = new Point(Pan.X, Pan.Y);
        Stage.CaptureMouse();
    }

    private void OnStageMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        var pos = e.GetPosition(Stage);
        Pan.X = _panStart.X + pos.X - start.X;
        Pan.Y = _panStart.Y + pos.Y - start.Y;
    }

    private void OnStageUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        Stage.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------------ Details

    private void ToggleInfo()
    {
        bool show = InfoPanel.Visibility != Visibility.Visible;
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) FillInfo(_images[_index]);
    }

    private void FillInfo(GalleryImage img)
    {
        InfoList.Children.Clear();
        Section("Datei");
        Row("Name", img.Name);
        Row("Ordner", img.RelativeDir.Length == 0 ? "(Bildordner)" : img.RelativeDir.TrimEnd('/'));
        Row("Pfad", img.Path);
        Row("Grösse", $"{img.Size / 1024.0 / 1024.0:N1} MB");
        if (img.Width > 0) Row("Abmessungen", $"{img.DisplayWidth:N0} × {img.DisplayHeight:N0} px");

        Section("Datum");
        Row("Verwendet", $"{img.Date:dd.MM.yyyy HH:mm:ss}");
        Row("Quelle", GalleryImage.DateSourceLabel(img.DateSource));
        Row("EXIF-Aufnahmedatum", img.ExifDate is DateTime d ? $"{d:dd.MM.yyyy HH:mm:ss}" : "–");
        var folder = ImageDates.FolderDateFromPath(img.RelativeDir);
        Row("Datum aus Ordnername", folder is { } fd
            ? (fd.Day > 0 ? $"{fd.Day:00}.{fd.Month:00}.{fd.Year}" : fd.Month > 0 ? $"{fd.Month:00}.{fd.Year}" : $"{fd.Year}")
            : "–");
        Row("Dateidatum", $"{img.Modified:dd.MM.yyyy HH:mm:ss}");
        if (img.Orientation != 1) Row("EXIF-Drehung", img.Orientation.ToString(CultureInfo.InvariantCulture));

        var snap = AppServices.Faces.Snapshot;
        if (snap.FacesByPath.TryGetValue(img.Path, out var faces) && faces.Count > 0)
        {
            Section("Personen");
            foreach (var f in faces.OrderBy(f => f.L))
            {
                var name = snap.Names.TryGetValue(f.PersonId, out var n) ? n : "Unbenannt";
                var state = f.Status switch
                {
                    FaceStatus.Confirmed => "bestätigt",
                    FaceStatus.Auto => "automatisch",
                    FaceStatus.Suggested => "Vorschlag – prüfen",
                    _ => "ohne Namen",
                };
                Row(name, state);
            }
        }

        Section("Ort");
        if (img.Lat is double lat && img.Lon is double lon)
        {
            Row("GPS", string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}"));
            if (img.Country is { } code) Row("Land", AppServices.Countries.NameOf(code));
            InfoList.Children.Add(Link("Auf der Karte zeigen", () =>
            {
                if (Application.Current.MainWindow is MainWindow main) main.ShowOnMap(lat, lon);
            }));
            InfoList.Children.Add(Link("In OpenStreetMap öffnen (Browser)", () =>
                Process.Start(new ProcessStartInfo(string.Create(CultureInfo.InvariantCulture,
                    $"https://www.openstreetmap.org/?mlat={lat:F6}&mlon={lon:F6}#map=15/{lat:F6}/{lon:F6}")) { UseShellExecute = true })));
        }
        else
        {
            Row("GPS", "keine GPS-Daten");
        }
    }

    private static System.Windows.Controls.TextBlock Link(string text, Action action)
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text));
        link.Click += (_, _) => action();
        return new System.Windows.Controls.TextBlock(link) { Margin = new Thickness(0, 6, 0, 0) };
    }

    private void Section(string title) =>
        InfoList.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, InfoList.Children.Count == 0 ? 4 : 16, 0, 4),
        });

    private void Row(string label, string value)
    {
        InfoList.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 6, 0, 0),
        });
        InfoList.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    // ------------------------------------------------------------------ Knöpfe

    private async void OnPrev(object sender, RoutedEventArgs e) => await MoveToAsync(_index - 1);

    private async void OnNext(object sender, RoutedEventArgs e) => await MoveToAsync(_index + 1);

    private void OnInfo(object sender, RoutedEventArgs e) => ToggleInfo();

    private void OnFavorite(object sender, RoutedEventArgs e) => ToggleFavorite();

    private void ToggleFavorite()
    {
        AppServices.Favorites.Toggle(_images[_index]);
        UpdateFavButton();
    }

    private void UpdateFavButton()
    {
        bool fav = AppServices.Favorites.Contains(_images[_index]);
        var icon = new SymbolIcon { Symbol = SymbolRegular.Heart24, Filled = fav };
        if (fav) icon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x5A, 0x6E));
        FavButton.Icon = icon;
        FavButton.ToolTip = fav ? "Aus Favoriten entfernen (F)" : "Zu Favoriten (F)";
    }

    // ------------------------------------------------------------------ Teilen / Drucken

    private bool _shareHooked;

    private void OnShare(object sender, RoutedEventArgs e) => Share();

    /// <summary>Windows-Teilen-Dialog (Mail, Nearby Sharing, WhatsApp …) mit der Bilddatei.</summary>
    private void Share()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var manager = global::Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.GetForWindow(hwnd);
            if (!_shareHooked)
            {
                _shareHooked = true;
                manager.DataRequested += async (_, args) =>
                {
                    var deferral = args.Request.GetDeferral();
                    try
                    {
                        var img = _images[_index];
                        args.Request.Data.Properties.Title = img.Name;
                        args.Request.Data.Properties.Description = $"{img.Date:dd.MM.yyyy} · {img.FolderName}";
                        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(img.Path);
                        args.Request.Data.SetStorageItems([file]);
                    }
                    catch (Exception ex)
                    {
                        args.Request.FailWithDisplayText($"Bild kann nicht geteilt werden: {ex.Message}");
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };
            }
            global::Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Write($"Teilen: {ex}");
            System.Windows.MessageBox.Show(this, $"Teilen ist nicht möglich: {ex.Message}", "Teilen");
        }
    }

    private void OnPrint(object sender, RoutedEventArgs e) => Print();

    /// <summary>Bild auf eine Seite drucken (Hoch-/Querformat passend zum Bild, Rand 1 cm).</summary>
    private void Print()
    {
        var img = _images[_index];
        var dialog = new System.Windows.Controls.PrintDialog();
        bool landscape = img.DisplayWidth > img.DisplayHeight;
        try
        {
            dialog.PrintTicket.PageOrientation = landscape
                ? System.Printing.PageOrientation.Landscape
                : System.Printing.PageOrientation.Portrait;
        }
        catch (Exception)
        {
        }
        if (dialog.ShowDialog() != true) return;

        // Druckauflösung: bis 4000 px statt der Bildschirmgrösse
        int w = 0, h = 0;
        if (img.Width > 4000 || img.Height > 4000)
        {
            if (img.Width >= img.Height) w = 4000; else h = 4000;
        }
        var bitmap = ThumbnailCache.Decode(img.Path, w, h, img.Orientation);
        if (bitmap is null) return;

        const double margin = 96 / 2.54;   // 1 cm
        double pageW = dialog.PrintableAreaWidth, pageH = dialog.PrintableAreaHeight;
        var picture = new System.Windows.Controls.Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.Uniform };
        var page = new System.Windows.Controls.Grid { Width = pageW, Height = pageH, Background = System.Windows.Media.Brushes.White };
        picture.Margin = new Thickness(margin);
        page.Children.Add(picture);
        page.Measure(new Size(pageW, pageH));
        page.Arrange(new Rect(0, 0, pageW, pageH));
        page.UpdateLayout();
        dialog.PrintVisual(page, img.Name);
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"/select,\"{_images[_index].Path}\"");
}
