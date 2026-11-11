using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Monats-Überschrift im Raster.</summary>
public sealed class HeaderRow(string label)
{
    public string Label { get; } = label;
}

/// <summary>Eine Zeile Vorschaubilder im Raster.</summary>
public sealed class ThumbRow(IReadOnlyList<GalleryImage> images, double cellSize, ImageGrid owner)
{
    public IReadOnlyList<GalleryImage> Images { get; } = images;
    public double CellSize { get; } = cellSize;
    public ImageGrid Owner { get; } = owner;
}

/// <summary>
/// Virtualisiertes Bilderraster (wie ImageGrid.kt): Zeilen mit Vorschaubildern, optional mit Monats-Überschriften.
/// Die Spaltenzahl passt sich der Breite an; die Scrollposition bleibt bei Änderungen erhalten.
/// </summary>
public partial class ImageGrid : UserControl
{
    public const double HeaderHeight = 44;
    private const double Margin2 = 4;          // 2 px Rand rundum je Zelle
    private const double TargetCell = 150;

    private readonly record struct RowInfo(double Top, GalleryImage? First);

    private IReadOnlyList<GalleryImage> _images = [];
    private List<RowInfo> _rowInfo = [];
    private List<(int Year, double Top)> _years = [];
    private double _totalHeight;
    private bool _loggedScroll;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private double _lastWidth;
    private bool _dirty;
    private GalleryImage? _pendingAnchor;
    private bool _pendingTop;
    private ScrollViewer? _scroller;
    private readonly HashSet<GalleryImage> _selected = new(ReferenceEqualityComparer.Instance);

    /// <summary>Klick auf ein Bild: Liste und Index.</summary>
    public event Action<IReadOnlyList<GalleryImage>, int>? ImageClicked;

    /// <summary>Monats-Überschriften anzeigen (Standard: ja).</summary>
    public bool MonthHeaders { get; set; } = true;

    /// <summary>Mehrfachauswahl erlaubt: Strg+Klick startet die Auswahl, danach wählt jeder Klick an/ab.</summary>
    public bool AllowSelection { get; set; }

    /// <summary>Auswahl hat sich geändert.</summary>
    public event Action? SelectionChanged;

    public IReadOnlyCollection<GalleryImage> Selected => _selected;

    public bool IsSelected(GalleryImage img) => _selected.Contains(img);

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        RefreshCells();
        SelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        foreach (var img in _images) _selected.Add(img);
        RefreshCells();
        SelectionChanged?.Invoke();
    }

    /// <summary>Herz/Auswahl der sichtbaren Zellen neu zeichnen.</summary>
    public void RefreshCells()
    {
        foreach (var row in FindChildren<ThumbRowView>(Rows)) row.RefreshOverlays();
    }

    public ImageGrid()
    {
        InitializeComponent();
        Data.AppServices.Favorites.Changed += RefreshCells;
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && _dirty) Rebuild(_pendingAnchor);
            else if (e.WidthChanged && Math.Abs(e.NewSize.Width - _lastWidth) > 1) Rebuild(FirstVisibleImage());
            else if (e.HeightChanged) PlaceYearMarks();
        };
        // ScrollChanged ist ein Routed Event: kommt vom ScrollViewer im Template hier an
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            Bubble.BeginAnimation(OpacityProperty, fade);
            YearMarks.BeginAnimation(OpacityProperty, fade);
        };
    }

    // ------------------------------------------------------------------ Jahr an der Scrollbar

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _rowInfo.Count == 0 || e.OriginalSource is not ScrollViewer sv) return;
        double scrollable = sv.ExtentHeight - sv.ViewportHeight;
        if (scrollable <= 0) return;

        var img = ImageAt(sv.VerticalOffset);
        if (img is null) return;
        if (!_loggedScroll)
        {
            _loggedScroll = true;
            Data.Log.Write($"Erstes Scrollen: {ImageDates.MonthLabel(img.Date)}, Höhe {ActualHeight:F0}");
        }
        var label = ImageDates.MonthLabel(img.Date);
        if (BubbleText.Text != label) BubbleText.Text = label;
        double frac = Math.Clamp(sv.VerticalOffset / scrollable, 0, 1);
        double bubbleHeight = Bubble.ActualHeight > 0 ? Bubble.ActualHeight : 30;
        BubbleShift.Y = frac * Math.Max(0, ActualHeight - bubbleHeight - 8);

        Bubble.BeginAnimation(OpacityProperty, null);
        YearMarks.BeginAnimation(OpacityProperty, null);
        Bubble.Opacity = 1;
        YearMarks.Opacity = 1;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Jahreszahlen am rechten Rand, an der Stelle, an der das Jahr beginnt.</summary>
    private void PlaceYearMarks()
    {
        YearMarks.Children.Clear();
        if (_years.Count < 2 || _totalHeight <= 0 || ActualHeight <= 0) return;
        const double markHeight = 18;
        double lastY = double.NegativeInfinity;
        foreach (var (year, top) in _years)
        {
            double y = top / _totalHeight * (ActualHeight - markHeight);
            if (y - lastY < markHeight + 2) continue;   // nicht überlappen
            lastY = y;
            var text = new TextBlock
            {
                Text = year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var border = new Border { Child = text, CornerRadius = new CornerRadius(4) };
            border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            Canvas.SetRight(border, SystemParameters.VerticalScrollBarWidth + 6);
            Canvas.SetTop(border, y);
            YearMarks.Children.Add(border);
        }
    }

    /// <summary>Erstes Bild der Zeile an Position y (oder danach).</summary>
    private GalleryImage? ImageAt(double y)
    {
        if (_rowInfo.Count == 0) return null;
        int lo = 0, hi = _rowInfo.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rowInfo[mid].Top <= y + 1) lo = mid; else hi = mid - 1;
        }
        for (int i = lo; i < _rowInfo.Count; i++)
        {
            if (_rowInfo[i].First is { } first) return first;
        }
        return null;
    }

    public IReadOnlyList<GalleryImage> Images => _images;

    /// <summary>Neue Bilderliste anzeigen; das oberste sichtbare Bild bleibt (wenn noch vorhanden) oben.</summary>
    public void SetImages(IReadOnlyList<GalleryImage> images, bool keepPosition = true)
    {
        var anchor = keepPosition ? FirstVisibleImage() : null;
        _images = images;
        // Auswahl auf noch vorhandene Bilder beschränken
        if (_selected.Count > 0)
        {
            var paths = new HashSet<string>(_selected.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var stillThere = images.Where(i => paths.Contains(i.Path)).ToList();
            _selected.Clear();
            foreach (var i in stillThere) _selected.Add(i);
            SelectionChanged?.Invoke();
        }
        _pendingAnchor = anchor;
        _pendingTop = !keepPosition;
        Rebuild(anchor);
    }

    private ScrollViewer? Scroller
    {
        get
        {
            if (_scroller is null)
            {
                Rows.ApplyTemplate();
                _scroller = Rows.Template.FindName("PART_Scroller", Rows) as ScrollViewer ?? FindChild<ScrollViewer>(Rows);
            }
            return _scroller;
        }
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            else foreach (var c in FindChildren<T>(child)) yield return c;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private void Rebuild(GalleryImage? anchor)
    {
        double width = ActualWidth - SystemParameters.VerticalScrollBarWidth - 2;
        if (width <= 0)
        {
            _dirty = true;   // nachholen, sobald sichtbar
            return;
        }
        _dirty = false;
        _lastWidth = ActualWidth;
        int cols = Math.Max(1, (int)(width / (TargetCell + Margin2)));
        double cell = Math.Floor(width / cols) - Margin2;
        double rowHeight = cell + Margin2;

        var rows = new List<object>();
        var info = new List<RowInfo>();
        double top = 0;
        int currentMonth = int.MinValue;
        int currentYear = int.MinValue;
        var years = new List<(int Year, double Top)>();
        var current = new List<GalleryImage>(cols);

        void Flush()
        {
            if (current.Count == 0) return;
            rows.Add(new ThumbRow(current, cell, this));
            info.Add(new RowInfo(top, current[0]));
            top += rowHeight;
            current = new List<GalleryImage>(cols);
        }

        foreach (var img in _images)
        {
            if (MonthHeaders)
            {
                int month = img.Date.Year * 100 + img.Date.Month;
                if (month != currentMonth)
                {
                    Flush();
                    currentMonth = month;
                    if (img.Date.Year != currentYear)
                    {
                        currentYear = img.Date.Year;
                        years.Add((currentYear, top));
                    }
                    rows.Add(new HeaderRow(ImageDates.MonthLabel(img.Date)));
                    info.Add(new RowInfo(top, null));
                    top += HeaderHeight;
                }
            }
            current.Add(img);
            if (current.Count == cols) Flush();
        }
        Flush();

        _rowInfo = info;
        _years = years;
        _totalHeight = top;
        PlaceYearMarks();
        Data.Log.Write($"Raster: {_images.Count} Bilder, {rows.Count} Zeilen, {cols} Spalten, Zelle {cell:F0}, Breite {width:F0}");
        Rows.ItemsSource = rows;
        if (_pendingTop)
        {
            _pendingTop = false;
            Scroller?.ScrollToTop();
        }

        if (anchor is null) return;
        int index = -1;
        for (int i = 0; i < info.Count; i++)
        {
            if (rows[i] is ThumbRow tr && tr.Images.Any(x => string.Equals(x.Path, anchor.Path, StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return;
        // Überschrift direkt darüber mit anzeigen
        double offset = index > 0 && rows[index - 1] is HeaderRow ? info[index - 1].Top : info[index].Top;
        Dispatcher.InvokeAsync(() => Scroller?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    /// <summary>Erstes Bild der obersten sichtbaren Zeile.</summary>
    private GalleryImage? FirstVisibleImage()
    {
        if (_rowInfo.Count == 0 || Scroller is not { } sv) return null;
        double y = sv.VerticalOffset;
        if (y <= 0) return null;   // ganz oben bleiben
        int lo = 0, hi = _rowInfo.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rowInfo[mid].Top <= y) lo = mid; else hi = mid - 1;
        }
        for (int i = lo; i < _rowInfo.Count; i++)
        {
            if (_rowInfo[i].First is { } first) return first;
        }
        return null;
    }

    /// <summary>Einträge fürs Kontextmenü (Rechtsklick auf ein Bild); null = kein Menü.</summary>
    public Func<GalleryImage, IReadOnlyList<(string Header, Action Action)>>? ContextActions { get; set; }

    private void OnRowsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ContextActions is null) return;
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not ThumbImage)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not ThumbImage { Item: { } img } thumb) return;
        var actions = ContextActions(img);
        if (actions.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = thumb };
        foreach (var (header, action) in actions)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnRowsClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not ThumbImage)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not ThumbImage { Item: { } img }) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (AllowSelection && (ctrl || _selected.Count > 0))
        {
            if (!_selected.Remove(img)) _selected.Add(img);
            RefreshCells();
            SelectionChanged?.Invoke();
            e.Handled = true;
            return;
        }
        int index = -1;
        for (int i = 0; i < _images.Count; i++)
        {
            if (ReferenceEquals(_images[i], img)) { index = i; break; }
        }
        if (index >= 0) ImageClicked?.Invoke(_images, index);
    }
}

/// <summary>
/// Eine Zeile Vorschaubilder: feste Grösse (Zelle + Rand), Kinder werden beim Recycling wiederverwendet.
/// Jede Zelle: Vorschaubild, Herz (Favorit), Auswahl-Markierung.
/// </summary>
public sealed class ThumbRowView : Panel
{
    private const double Gap = 4;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Teile einer Zelle</summary>
    private sealed class Cell : Grid
    {
        public readonly ThumbImage Thumb = new();
        public readonly TextBlock Heart;
        public readonly Border SelectedOverlay;

        public Cell()
        {
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            SetResourceReference(BackgroundProperty, "ControlFillColorSecondaryBrush");
            Children.Add(Thumb);
            Heart = new TextBlock
            {
                Text = "\uEB52",   // Herz gefüllt
                FontFamily = IconFont,
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 5),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8 },
            };
            Children.Add(Heart);
            var check = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4),
                Child = new TextBlock
                {
                    Text = "\uE73E",   // Häkchen
                    FontFamily = IconFont,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ((TextBlock)check.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
            var tint = new Border { Opacity = 0.25 };
            tint.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            SelectedOverlay = new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var overlayGrid = new Grid();
            overlayGrid.Children.Add(tint);
            overlayGrid.Children.Add(check);
            SelectedOverlay.Child = overlayGrid;
            Children.Add(SelectedOverlay);
        }
    }

    public ThumbRowView()
    {
        Background = Brushes.Transparent;
        DataContextChanged += (_, _) => Sync();
    }

    private ThumbRow? Row => DataContext as ThumbRow;

    private void Sync()
    {
        var row = Row;
        int n = row?.Images.Count ?? 0;
        while (Children.Count < n) Children.Add(new Cell());
        while (Children.Count > n) Children.RemoveAt(Children.Count - 1);
        for (int i = 0; i < n; i++)
        {
            var cell = (Cell)Children[i];
            var img = row!.Images[i];
            cell.Thumb.Item = img;
            cell.ToolTip = img.Name;
        }
        RefreshOverlays();
        InvalidateMeasure();
    }

    /// <summary>Herz und Auswahl gemäss aktuellem Stand anzeigen.</summary>
    public void RefreshOverlays()
    {
        var row = Row;
        if (row is null) return;
        var favorites = Data.AppServices.Favorites;
        for (int i = 0; i < Children.Count && i < row.Images.Count; i++)
        {
            var cell = (Cell)Children[i];
            var img = row.Images[i];
            bool selected = row.Owner.IsSelected(img);
            cell.Heart.Visibility = favorites.Contains(img) ? Visibility.Visible : Visibility.Collapsed;
            cell.SelectedOverlay.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            cell.Thumb.Margin = selected ? new Thickness(8) : new Thickness(0);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var row = Row;
        if (row is null) return new Size(0, 0);
        var cell = new Size(row.CellSize, row.CellSize);
        foreach (UIElement child in Children) child.Measure(cell);
        return new Size(Children.Count * (row.CellSize + Gap), row.CellSize + Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var row = Row;
        if (row is null) return finalSize;
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(i * (row.CellSize + Gap) + Gap / 2, Gap / 2, row.CellSize, row.CellSize));
        }
        return finalSize;
    }
}
