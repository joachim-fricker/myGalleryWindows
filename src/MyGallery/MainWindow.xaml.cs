using System.Globalization;
using MyGallery.Data;
using MyGallery.Domain;
using MyGallery.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MyGallery;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Beim ersten Start gross, danach wie zuletzt verlassen
        WindowBounds.Apply(this, "mainwindow", widthFraction: 0.85, heightFraction: 0.9);

        // Hell/Dunkel automatisch an Windows anpassen
        SystemThemeWatcher.Watch(this);

        AlbenItem.Click += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, AlbenItem)) AlbenPage.Request(new AlbenRequest());
        };
        // Unterpunkte lösen nicht zuverlässig Click aus: zusätzlich auf die Auswahl im Menü reagieren
        RootNavigation.SelectionChanged += (_, _) =>
        {
            if (RootNavigation.SelectedItem is NavigationViewItem item && Requests.TryGetValue(item, out var request))
                AlbenPage.Request(request);
        };
        AppServices.Library.ImagesChanged += BuildAlbumMenu;
        // Jahre nur zeigen, wenn „Alben“ aufgeklappt ist (sie sind eigene Menüpunkte, siehe BuildAlbumMenu)
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(NavigationViewItem.IsExpandedProperty, typeof(NavigationViewItem))
            .AddValueChanged(AlbenItem, (_, _) => UpdateYearVisibility());

        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(AlleBilderPage));
            AppServices.Library.Start();
            _ = Task.Run(AppServices.Faces.EnsureLoaded);
            Watchdog.Start(Dispatcher);
        };
    }

    /// <summary>Aufnahmeort im Tab Karte zeigen (aus dem Bildbetrachter).</summary>
    public void ShowOnMap(double lat, double lon)
    {
        Activate();
        RootNavigation.Navigate(typeof(KartePage));
        KartePage.ShowLocation(lat, lon);
    }

    // ------------------------------------------------------------------ Alben im Menü

    private string _albumSignature = "";
    private readonly List<NavigationViewItem> _yearItems = [];
    private static readonly Dictionary<NavigationViewItem, AlbenRequest> Requests = [];

    /// <summary>
    /// Alben → Favoriten; Jahre (aufklappbar) → Ordner mit Unterordnern.
    /// Nur neu aufbauen, wenn sich Ordner oder Sortierung geändert haben.
    /// </summary>
    private void BuildAlbumMenu()
    {
        var lib = AppServices.Library;
        var years = Albums.Build(lib.Images, lib.OldestFirst);
        var signature = lib.OldestFirst + "|" +
                        string.Join("|", years.SelectMany(y => y.Albums.Select(a => $"{y.Year}:{a.RelativeDir}")));
        if (signature == _albumSignature) return;
        _albumSignature = signature;

        // WPF-UI zeigt nur zwei Menü-Ebenen an. Darum: Alben → Favoriten, und jedes Jahr als eigener
        // Menüpunkt (direkt unter „Alben“) mit seinen Ordnern; Unterordner eingerückt auf derselben Ebene.
        var expanded = _yearItems.Where(i => i.IsExpanded && i.Tag is int).Select(i => (int)i.Tag).ToHashSet();
        var menu = RootNavigation.MenuItems;
        foreach (var old in _yearItems) menu.Remove(old);
        _yearItems.Clear();

        Requests.Clear();
        Requests[AlbenItem] = new AlbenRequest();
        AlbenItem.MenuItems.Clear();
        AlbenItem.MenuItems.Add(CreateItem("Favoriten", SymbolRegular.Heart24, new AlbenRequest(Favorites: true)));

        int insertAt = menu.IndexOf(AlbenItem) + 1;
        foreach (var y in years)
        {
            var yearItem = CreateItem(y.Year > 0 ? y.Year.ToString(CultureInfo.InvariantCulture) : "Ohne Datum",
                                      SymbolRegular.CalendarLtr24, new AlbenRequest(Year: y.Year));
            yearItem.Tag = y.Year;
            yearItem.ToolTip = $"{y.Albums.Count:N0} Alben · {y.ImageCount:N0} Bilder";
            foreach (var node in Albums.Tree(y)) AddFolderItems(yearItem, node, 0);
            yearItem.IsExpanded = expanded.Contains(y.Year);
            // eingerückt wie ein Unterpunkt von „Alben“
            yearItem.Margin = new System.Windows.Thickness(YearIndent, 0, 0, 0);
            menu.Insert(insertAt++, yearItem);
            _yearItems.Add(yearItem);
        }
        UpdateYearVisibility();
    }

    private const double YearIndent = 24;

    private void UpdateYearVisibility()
    {
        var visibility = AlbenItem.IsExpanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        foreach (var item in _yearItems) item.Visibility = visibility;
    }

    private static void AddFolderItems(NavigationViewItem parent, FolderNode node, int depth)
    {
        var request = node.Album is not null
            ? new AlbenRequest(Dir: node.Dir)
            : new AlbenRequest(Dir: node.Dir, Subfolders: true, Name: node.Name);
        var item = CreateItem(node.Name, null, request);
        if (depth > 0)
        {
            item.Content = new System.Windows.Controls.TextBlock
            {
                Text = "↳ " + node.Name,
                Margin = new System.Windows.Thickness(depth * 14, 0, 0, 0),
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            };
        }
        item.ToolTip = node.Children.Count > 0
            ? $"{node.Dir.TrimEnd('/')} · {node.ImageCount:N0} Bilder inkl. Unterordner"
            : $"{node.Dir.TrimEnd('/')} · {node.ImageCount:N0} Bilder";
        parent.MenuItems.Add(item);
        foreach (var child in node.Children) AddFolderItems(parent, child, depth + 1);
    }

    private static NavigationViewItem CreateItem(string text, SymbolRegular? icon, AlbenRequest request)
    {
        var item = new NavigationViewItem
        {
            Content = text,
            TargetPageType = typeof(AlbenPage),
            NavigationCacheMode = NavigationCacheMode.Enabled,
        };
        if (icon is SymbolRegular symbol) item.Icon = new SymbolIcon { Symbol = symbol };
        // Click ist ein Routed Event: der Klick auf einen Ordner kommt auch beim Jahr an -> nur eigene Klicks
        item.Click += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, item)) return;
            AlbenPage.Request(request);
        };
        Requests[item] = request;
        return item;
    }
}
