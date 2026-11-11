using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Überschrift in der Albenübersicht ("Favoriten" oder ein Jahr).</summary>
public sealed class SectionRow(string title, string subtitle, int? year, bool open)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    /// <summary>Jahr (aufklappbar) oder null (Favoriten)</summary>
    public int? Year { get; } = year;
    public bool Open { get; } = open;
    public string Chevron => Year is null ? "" : Open ? "" : "";
    public Cursor Cursor => Year is null ? Cursors.Arrow : Cursors.Hand;

    /// <summary>Schlüssel zum Auf-/Zuklappen (Personen-Gruppen)</summary>
    public string? Key { get; init; }
}

/// <summary>Was im Tab Alben gezeigt werden soll (aus dem Menü): Übersicht, Favoriten, ein Jahr oder ein Album.</summary>
/// <param name="Subfolders">Dir ist ein Ordner ohne eigene Bilder: alle Bilder der Unterordner zeigen</param>
public sealed record AlbenRequest(bool Favorites = false, int? Year = null, string? Dir = null, bool Subfolders = false, string? Name = null);

/// <summary>Eine Karte: Album oder Favoriten (Album = null).</summary>
public sealed class AlbumCard(string title, string subtitle, GalleryImage? cover, Album? album)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public GalleryImage? Cover { get; } = cover;
    public Album? Album { get; } = album;
    public bool IsFavorites => Album is null;
}

/// <summary>Eine Zeile Album-Karten.</summary>
public sealed class AlbumCardRow(IReadOnlyList<AlbumCard> cards, double cellSize)
{
    public IReadOnlyList<AlbumCard> Cards { get; } = cards;
    public double CellSize { get; } = cellSize;
}

/// <summary>
/// Tab „Alben“ (wie AlbumsScreen.kt): oben Favoriten, dann pro Jahr (aufklappbar) alle Alben/Ordner.
/// Klick auf eine Karte zeigt die Bilder; in den Favoriten mit Mehrfachauswahl zum Entfernen.
/// </summary>
public partial class AlbenPage : Page
{
    public const double SectionHeight = 64;
    private const double TargetCell = 170;

    private List<YearAlbums> _years = [];
    private readonly HashSet<int> _collapsed = [];
    private double _lastWidth;

    /// <summary>Gerade geöffnet: Album, Favoriten (true) oder nichts.</summary>
    private Album? _openAlbum;
    private bool _openFavorites;
    /// <summary>Geöffneter Ordner mit Unterordnern (Dir, Name)</summary>
    private (string Dir, string Name)? _openFolder;

    private static AlbenPage? _instance;
    private static AlbenRequest? _pending;
    private Dictionary<int, double> _yearTop = [];

    /// <summary>Anzeige aus dem Menü anfordern (wird ausgeführt, sobald die Seite und die Alben bereit sind).</summary>
    public static void Request(AlbenRequest request)
    {
        Log.Write($"Alben-Anfrage: {request}");
        _pending = request;
        _instance?.ApplyPending();
    }

    private void ApplyPending()
    {
        if (_pending is not { } r || !IsLoaded) return;
        if (_years.Count == 0 && AppServices.Library.Images.Count > 0) OnImagesChanged();
        if (_years.Count == 0) return;
        _pending = null;
        if (r.Favorites)
        {
            ShowFavorites(keepPosition: false);
        }
        else if (r.Dir is not null && r.Subfolders)
        {
            ShowFolder(r.Dir, r.Name ?? r.Dir.TrimEnd('/'), keepPosition: false);
        }
        else if (r.Dir is not null)
        {
            var album = _years.SelectMany(y => y.Albums)
                .FirstOrDefault(a => string.Equals(a.RelativeDir, r.Dir, StringComparison.OrdinalIgnoreCase));
            if (album is not null) ShowAlbum(album, keepPosition: false);
            else Log.Write($"Album nicht gefunden: {r.Dir}");
        }
        else
        {
            CloseDetail();
            if (r.Year is int year)
            {
                if (_collapsed.Remove(year)) RebuildRows(keepOffset: true);
                if (_yearTop.TryGetValue(year, out var top) && Scroller is { } sv)
                {
                    Dispatcher.InvokeAsync(() => sv.ScrollToVerticalOffset(top), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }
    }

    public AlbenPage()
    {
        InitializeComponent();
        _instance = this;
        var lib = AppServices.Library;
        lib.ImagesChanged += OnImagesChanged;
        AppServices.Favorites.Changed += OnFavoritesChanged;
        DetailGrid.ImageClicked += OpenViewer;
        DetailGrid.SelectionChanged += UpdateSelectionBar;
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && Math.Abs(Rows.ActualWidth - _lastWidth) > 1) RebuildRows(keepOffset: true);
        };
        Loaded += (_, _) =>
        {
            if (_years.Count == 0 && lib.Images.Count > 0) OnImagesChanged();
            ApplyPending();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DetailPanel.Visibility != Visibility.Visible) return;
            if (DetailGrid.Selected.Count > 0) DetailGrid.ClearSelection();
            else CloseDetail();
            e.Handled = true;
        };
        // Maus-Zurück-Taste
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1 && DetailPanel.Visibility == Visibility.Visible)
            {
                CloseDetail();
                e.Handled = true;
            }
        };
    }

    // ------------------------------------------------------------------ Übersicht

    private void OnImagesChanged()
    {
        _years = Albums.Build(AppServices.Library.Images, AppServices.Library.OldestFirst);
        UpdateSortButtons();
        TitleText.Text = $"Alben ({_years.Sum(y => y.Albums.Count):N0})";
        RebuildRows(keepOffset: true);
        if (_openFavorites) ShowFavorites(keepPosition: true);
        else if (_openFolder is { } folder) ShowFolder(folder.Dir, folder.Name, keepPosition: true);
        else if (_openAlbum is { } open)
        {
            var album = _years.SelectMany(y => y.Albums)
                .FirstOrDefault(a => string.Equals(a.RelativeDir, open.RelativeDir, StringComparison.OrdinalIgnoreCase));
            if (album is null) CloseDetail(); else ShowAlbum(album, keepPosition: true);
        }
        ApplyPending();
    }

    private void OnFavoritesChanged()
    {
        RebuildRows(keepOffset: true);
        if (_openFavorites) ShowFavorites(keepPosition: true);
    }

    private void RebuildRows(bool keepOffset)
    {
        double width = Rows.ActualWidth - SystemParameters.VerticalScrollBarWidth - 10;
        if (width <= 0) return;
        _lastWidth = Rows.ActualWidth;
        int cols = Math.Max(1, (int)(width / (TargetCell + AlbumRowView.Gap)));
        double cell = Math.Floor(width / cols) - AlbumRowView.Gap;

        var rows = new List<object>();
        var yearTop = new Dictionary<int, double>();
        double cardRowHeight = cell + AlbumRowView.TextHeight + AlbumRowView.Gap;
        double top = SectionHeight + cardRowHeight;   // Favoriten-Überschrift + Karte
        var favorites = AppServices.Favorites.Filter(AppServices.Library.Images);
        rows.Add(new SectionRow("Favoriten", "", null, true));
        rows.Add(new AlbumCardRow(
            [new AlbumCard("Favoriten",
                           favorites.Count == 0 ? "Herz im Vollbild anklicken" : $"{favorites.Count:N0} Bilder",
                           favorites.FirstOrDefault(), null)],
            cell));

        foreach (var y in _years)
        {
            bool open = !_collapsed.Contains(y.Year);
            yearTop[y.Year] = top;
            top += SectionHeight;
            rows.Add(new SectionRow(y.Year > 0 ? y.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Ohne Datum",
                                    $"{y.Albums.Count:N0} Alben · {y.ImageCount:N0} Bilder", y.Year, open));
            if (!open) continue;
            var cards = y.Albums.Select(a => new AlbumCard(a.Name, $"{a.Images.Count:N0} Bilder", a.Cover, a)).ToList();
            for (int i = 0; i < cards.Count; i += cols)
            {
                rows.Add(new AlbumCardRow(cards.Skip(i).Take(cols).ToList(), cell));
                top += cardRowHeight;
            }
        }

        _yearTop = yearTop;
        var sv = Scroller;
        double offset = keepOffset && sv is not null ? sv.VerticalOffset : 0;
        Rows.ItemsSource = rows;
        if (sv is not null && offset > 0)
        {
            Dispatcher.InvokeAsync(() => sv.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private ScrollViewer? Scroller
    {
        get
        {
            Rows.ApplyTemplate();
            return Rows.Template.FindName("PART_Scroller", Rows) as ScrollViewer;
        }
    }

    private void OnRowsClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { Tag: AlbumCard card })
            {
                if (card.IsFavorites) ShowFavorites(keepPosition: false);
                else ShowAlbum(card.Album!, keepPosition: false);
                e.Handled = true;
                return;
            }
            if (d is FrameworkElement { DataContext: SectionRow { Year: int year } })
            {
                if (!_collapsed.Remove(year)) _collapsed.Add(year);
                RebuildRows(keepOffset: true);
                e.Handled = true;
                return;
            }
            if (d == Rows) return;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
    }

    // ------------------------------------------------------------------ Detail

    private void ShowAlbum(Album album, bool keepPosition)
    {
        _openAlbum = album;
        _openFavorites = false;
        _openFolder = null;
        DetailGrid.AllowSelection = false;
        DetailTitle.Text = album.Name;
        DetailSubtitle.Text = $"{album.Images.Count:N0} Bilder · {(album.RelativeDir.Length == 0 ? AppServices.Library.Root : album.RelativeDir.TrimEnd('/'))}";
        DetailEmpty.Visibility = Visibility.Collapsed;
        ShowDetail(album.Images, keepPosition);
    }

    /// <summary>Ordner mit allen Unterordnern (z. B. "2013/Sabbactical/").</summary>
    private void ShowFolder(string dir, string name, bool keepPosition)
    {
        _openAlbum = null;
        _openFavorites = false;
        _openFolder = (dir, name);
        DetailGrid.AllowSelection = false;
        var list = AppServices.Library.Images
            .Where(i => i.RelativeDir.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DetailTitle.Text = name;
        DetailSubtitle.Text = $"{list.Count:N0} Bilder inkl. Unterordner · {dir.TrimEnd('/')}";
        DetailEmpty.Visibility = Visibility.Collapsed;
        ShowDetail(list, keepPosition);
    }

    private void ShowFavorites(bool keepPosition)
    {
        _openAlbum = null;
        _openFolder = null;
        _openFavorites = true;
        DetailGrid.AllowSelection = true;
        var list = AppServices.Favorites.Filter(AppServices.Library.Images);
        DetailTitle.Text = "Favoriten";
        DetailEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowDetail(list, keepPosition);
        UpdateSelectionBar();
    }

    private void ShowDetail(IReadOnlyList<GalleryImage> images, bool keepPosition)
    {
        bool wasOpen = DetailPanel.Visibility == Visibility.Visible;
        OverviewPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        if (!wasOpen) DetailGrid.ClearSelection();
        DetailGrid.SetImages(images, keepPosition && wasOpen);
        UpdateSelectionBar();
    }

    private void CloseDetail()
    {
        _openAlbum = null;
        _openFolder = null;
        _openFavorites = false;
        DetailGrid.ClearSelection();
        DetailPanel.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Visible;
    }

    private void UpdateSelectionBar()
    {
        if (!_openFavorites)
        {
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }
        int n = DetailGrid.Selected.Count, total = DetailGrid.Images.Count;
        SelectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.Content = n == total ? "Keine" : "Alle";
        DetailSubtitle.Text = n > 0
            ? $"{n:N0} ausgewählt"
            : $"{total:N0} Bilder · Strg+Klick = auswählen";
    }

    private void OnBack(object sender, RoutedEventArgs e) => CloseDetail();

    private void OnToggleSort(object sender, RoutedEventArgs e) =>
        AppServices.Library.SetOldestFirst(!AppServices.Library.OldestFirst);

    private void UpdateSortButtons()
    {
        var tip = AppServices.Library.OldestFirst
            ? "Sortierung: älteste zuerst (klicken = neueste zuerst)"
            : "Sortierung: neueste zuerst (klicken = älteste zuerst)";
        SortButton.ToolTip = tip;
        DetailSortButton.ToolTip = tip;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (DetailGrid.Selected.Count == DetailGrid.Images.Count) DetailGrid.ClearSelection();
        else DetailGrid.SelectAll();
    }

    private void OnRemoveFavorites(object sender, RoutedEventArgs e)
    {
        var selected = DetailGrid.Selected.ToList();
        DetailGrid.ClearSelection();
        AppServices.Favorites.Set(selected, false);
    }

    private void OpenViewer(IReadOnlyList<GalleryImage> images, int index)
    {
        var viewer = new ViewerWindow(images, index) { Owner = Window.GetWindow(this) };
        viewer.Show();
    }
}

/// <summary>Eine Zeile Album-Karten (Vorschaubild, Name, Anzahl). Kinder werden beim Recycling wiederverwendet.</summary>
public sealed class AlbumRowView : Panel
{
    public const double Gap = 12;
    public const double TextHeight = 44;

    private sealed class Card : StackPanel
    {
        public readonly ThumbImage Thumb = new();
        public readonly Grid Picture = new() { ClipToBounds = true };
        public readonly TextBlock Heart = new()
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x6E)),
            IsHitTestVisible = false,
        };
        public readonly TextBlock Title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 2, 0) };
        public readonly TextBlock Subtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 0) };

        public Card()
        {
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;
            Picture.SetResourceReference(Panel.BackgroundProperty, "ControlFillColorSecondaryBrush");
            Picture.Children.Add(Thumb);
            Picture.Children.Add(Heart);
            Children.Add(Picture);
            Title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            Subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Children.Add(Title);
            Children.Add(Subtitle);
        }
    }

    public AlbumRowView()
    {
        Background = Brushes.Transparent;
        DataContextChanged += (_, _) => Sync();
    }

    private AlbumCardRow? Row => DataContext as AlbumCardRow;

    private void Sync()
    {
        var row = Row;
        int n = row?.Cards.Count ?? 0;
        while (Children.Count < n) Children.Add(new Card());
        while (Children.Count > n) Children.RemoveAt(Children.Count - 1);
        for (int i = 0; i < n; i++)
        {
            var card = (Card)Children[i];
            var data = row!.Cards[i];
            card.Tag = data;
            card.Thumb.Item = data.Cover;
            card.Picture.Width = card.Picture.Height = row.CellSize;
            card.Title.Text = data.Title;
            card.Subtitle.Text = data.Subtitle;
            card.ToolTip = data.Album is { } a && a.RelativeDir.Length > 0 ? a.RelativeDir.TrimEnd('/') : null;
            // Favoriten: Herz (gross ohne Bilder, klein in der Ecke mit Titelbild)
            card.Heart.Visibility = data.IsFavorites ? Visibility.Visible : Visibility.Collapsed;
            bool empty = data.Cover is null;
            card.Heart.FontSize = empty ? 48 : 24;
            card.Heart.HorizontalAlignment = empty ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            card.Heart.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            card.Heart.Margin = empty ? new Thickness(0) : new Thickness(0, 0, 8, 8);
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var row = Row;
        if (row is null) return new Size(0, 0);
        var size = new Size(row.CellSize, row.CellSize + TextHeight);
        foreach (UIElement child in Children) child.Measure(size);
        return new Size(Children.Count * (row.CellSize + Gap), row.CellSize + TextHeight + Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var row = Row;
        if (row is null) return finalSize;
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(i * (row.CellSize + Gap), Gap / 2, row.CellSize, row.CellSize + TextHeight));
        }
        return finalSize;
    }
}
