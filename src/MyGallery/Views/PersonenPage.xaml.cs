using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Eine Zeile Personen-Karten.</summary>
public sealed class PersonCardRow(IReadOnlyList<PersonInfo> persons, double cellSize)
{
    public IReadOnlyList<PersonInfo> Persons { get; } = persons;
    public double CellSize { get; } = cellSize;
}

/// <summary>
/// Tab „Personen“ (wie PersonsScreen.kt): benannte Personen nach Gruppen („Haupt“ immer sichtbar, weitere
/// aufklappbar), dann „Ohne Namen“. Klick zeigt die Bilder der Person; dort umbenennen, Gruppe, zusammenführen
/// und mit Strg+Klick ausgewählte Bilder einer anderen Person zuordnen oder entfernen.
/// </summary>
public partial class PersonenPage : Page
{
    private const double TargetCell = 130;
    private const string UnnamedKey = "unnamed";

    private List<PersonInfo> _persons = [];
    private readonly HashSet<string> _open = ["g1"];   // "Haupt" (Gruppe 1) ist immer offen
    private double _lastWidth;
    private int? _openPerson;

    public PersonenPage()
    {
        InitializeComponent();
        AppServices.Library.ImagesChanged += Refresh;
        AppServices.Faces.Changed += Refresh;
        DetailGrid.AllowSelection = true;
        DetailGrid.ImageClicked += (images, index) =>
        {
            var viewer = new ViewerWindow(images, index) { Owner = Window.GetWindow(this) };
            viewer.Show();
        };
        DetailGrid.SelectionChanged += UpdateBars;
        DetailGrid.ContextActions = PersonImageActions;
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && Math.Abs(Rows.ActualWidth - _lastWidth) > 1) RebuildRows();
        };
        Loaded += (_, _) => Refresh();
        SearchGrid.ImageClicked += (images, index) =>
        {
            var viewer = new ViewerWindow(images, index) { Owner = Window.GetWindow(this) };
            viewer.Show();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible)
            {
                CloseSearch();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && ReviewPanel.Visibility == Visibility.Visible)
            {
                CloseReview();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Escape || DetailPanel.Visibility != Visibility.Visible) return;
            if (DetailGrid.Selected.Count > 0) DetailGrid.ClearSelection(); else CloseDetail();
            e.Handled = true;
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

    private FaceSnapshot Snap => AppServices.Faces.Snapshot;

    private void Refresh()
    {
        if (!IsLoaded) return;
        _persons = Persons.Build(AppServices.Library.Images, Snap);
        int named = _persons.Count(p => p.Name is not null);
        TitleText.Text = $"Personen ({named})";
        SubtitleText.Text = (Snap.FaceCount == 0 ? ""
            : $"{Snap.FaceCount:N0} Gesichter · {_persons.Count - named} unbenannte Gruppen · Modell {Snap.ModelLabel}")
            + (AppServices.Library.FaceScanProblem is { } problem ? $" · {problem}" : "");
        if (SearchPanel.Visibility == Visibility.Visible) RefreshSearch();
        EmptyText.Visibility = Snap.FaceCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildRows();
        if (_openPerson is int pid) ShowPerson(pid, keepPosition: true);
    }

    // ------------------------------------------------------------------ Übersicht

    private void RebuildRows()
    {
        double width = Rows.ActualWidth - SystemParameters.VerticalScrollBarWidth - 10;
        if (width <= 0) return;
        _lastWidth = Rows.ActualWidth;
        int cols = Math.Max(1, (int)(width / (TargetCell + PersonRowView.Gap)));
        double cell = Math.Floor(width / cols) - PersonRowView.Gap;

        var rows = new List<object>();
        void Section(string key, string title, List<PersonInfo> persons, bool collapsible)
        {
            if (persons.Count == 0 && key != "g1") return;
            bool open = !collapsible || _open.Contains(key);
            rows.Add(new SectionRow(title, $"{persons.Count} Personen", collapsible ? 0 : null, open) { Key = key });
            if (!open) return;
            for (int i = 0; i < persons.Count; i += cols) rows.Add(new PersonCardRow(persons.Skip(i).Take(cols).ToList(), cell));
        }
        foreach (var g in Snap.Groups)
        {
            var members = _persons.Where(p => p.Name is not null && (p.GroupId ?? FaceStore.MainGroup) == g.Id).ToList();
            Section($"g{g.Id}", g.Name, members, collapsible: g.Id != FaceStore.MainGroup);
        }
        Section(UnnamedKey, "Ohne Namen", _persons.Where(p => p.Name is null).ToList(), collapsible: true);

        var sv = Scroller;
        double offset = sv?.VerticalOffset ?? 0;
        Rows.ItemsSource = rows;
        if (sv is not null && offset > 0)
            Dispatcher.InvokeAsync(() => sv.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
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
        while (d is not null && d != Rows)
        {
            if (d is FrameworkElement { Tag: PersonInfo person })
            {
                ShowPerson(person.Id, keepPosition: false);
                e.Handled = true;
                return;
            }
            if (d is FrameworkElement { DataContext: SectionRow { Year: not null, Key: { } key } })
            {
                if (!_open.Remove(key)) _open.Add(key);
                RebuildRows();
                e.Handled = true;
                return;
            }
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
    }

    // ------------------------------------------------------------------ Eine Person

    private PersonInfo? Current => _openPerson is int pid
        ? Persons.Build(AppServices.Library.Images, Snap, includeSmall: true).FirstOrDefault(p => p.Id == pid)
        : null;

    private void ShowPerson(int personId, bool keepPosition)
    {
        // beim Prüfen und Suchen nicht umschalten
        if (ReviewPanel.Visibility == Visibility.Visible || SearchPanel.Visibility == Visibility.Visible) return;
        var person = Persons.Build(AppServices.Library.Images, Snap, includeSmall: true).FirstOrDefault(p => p.Id == personId);
        if (person is null)
        {
            CloseDetail();
            return;
        }
        bool wasOpen = DetailPanel.Visibility == Visibility.Visible && _openPerson == personId;
        _openPerson = personId;
        OverviewPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailAvatar.Person = person;
        DetailTitle.Text = person.DisplayName;
        GroupButton.Visibility = person.Name is null ? Visibility.Collapsed : Visibility.Visible;
        if (!wasOpen) DetailGrid.ClearSelection();
        DetailGrid.SetImages(person.Images, keepPosition && wasOpen);
        UpdateBars();
    }

    private void CloseDetail()
    {
        _openPerson = null;
        DetailGrid.ClearSelection();
        DetailPanel.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Visible;
    }

    private void UpdateBars()
    {
        if (_openPerson is null) return;
        int n = DetailGrid.Selected.Count, total = DetailGrid.Images.Count;
        bool selecting = n > 0;
        SelectionBar.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        PersonBar.Visibility = selecting ? Visibility.Collapsed : Visibility.Visible;
        SelectAllButton.Content = n == total ? "Keine" : "Alle";
        SortButton.ToolTip = AppServices.Library.OldestFirst
            ? "Sortierung: älteste zuerst (klicken = neueste zuerst)"
            : "Sortierung: neueste zuerst (klicken = älteste zuerst)";
        CoverButton.Visibility = n == 1 ? Visibility.Visible : Visibility.Collapsed;
        var person = Current;
        string group = person?.GroupName is { } g ? $" · Gruppe {g}" : "";
        int suggestions = person?.Suggestions.Count ?? 0;
        ReviewButton.Content = $"{suggestions:N0} Vorschläge prüfen";
        ReviewButton.Visibility = suggestions > 0 && person?.Name is not null ? Visibility.Visible : Visibility.Collapsed;
        ReviewAutoButton.Visibility = person is { Name: not null, AutoCount: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SearchWithButton.Visibility = person?.Name is not null ? Visibility.Visible : Visibility.Collapsed;
        DetailSubtitle.Text = selecting
            ? $"{n:N0} ausgewählt"
            : $"{total:N0} Bilder{group} · Rechtsklick = Titelbild/zuordnen · Strg+Klick = auswählen";
    }

    /// <summary>Gesichter dieser Person auf den ausgewählten Bildern.</summary>
    private List<long> SelectedFaceIds()
    {
        if (_openPerson is not int pid) return [];
        return DetailGrid.Selected
            .SelectMany(img => Snap.FacesByPath.TryGetValue(img.Path, out var faces) ? faces : [])
            .Where(f => f.PersonId == pid)
            .Select(f => f.Id)
            .ToList();
    }

    private void OnBack(object sender, RoutedEventArgs e) => CloseDetail();

    // ------------------------------------------------------------------ Personen suchen

    private readonly HashSet<int> _searchSelected = [];
    private bool _searchFilling;
    private bool _searchFromPerson;

    private void OnOpenSearch(object sender, RoutedEventArgs e) => OpenSearch(null);

    private void OnSearchWith(object sender, RoutedEventArgs e)
    {
        if (Current is { Name: not null } p) OpenSearch(p.Id);
    }

    private void OpenSearch(int? withPerson)
    {
        _searchFromPerson = withPerson is not null;
        if (withPerson is int pid) { _searchSelected.Clear(); _searchSelected.Add(pid); }
        OverviewPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        RefreshSearch();
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        SearchGrid.SetImages([], keepPosition: false);
        if (_searchFromPerson && _openPerson is int pid) ShowPerson(pid, keepPosition: true);
        else
        {
            _openPerson = null;
            OverviewPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnSearchBack(object sender, RoutedEventArgs e) => CloseSearch();

    private void OnSearchOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_searchFilling) RefreshSearch();
    }

    /// <summary>Wie PersonSearch.kt: Treffer, Zähler je Person, Land/Jahr mit Anzahl.</summary>
    private void RefreshSearch()
    {
        var snap = Snap;
        var images = AppServices.Library.Images;
        var named = _persons.Where(p => p.Name is not null).ToList();
        _searchSelected.RemoveWhere(id => named.All(p => p.Id != id));

        // benannte Personen pro Bild (nur sichere Zuordnungen)
        var perImage = new List<(GalleryImage Image, HashSet<int> Ids)>();
        foreach (var img in images)
        {
            if (!snap.FacesByPath.TryGetValue(img.Path, out var faces)) continue;
            var ids = faces.Where(f => snap.Names.ContainsKey(f.PersonId) && f.Status is FaceStatus.Auto or FaceStatus.Confirmed)
                           .Select(f => f.PersonId).ToHashSet();
            if (ids.Count > 0) perImage.Add((img, ids));
        }
        bool onlyThese = OnlyTheseSwitch.IsChecked == true;
        var hits = _searchSelected.Count == 0 ? new List<GalleryImage>() : perImage
            .Where(x => x.Ids.IsSupersetOf(_searchSelected) && (!onlyThese || x.Ids.Count == _searchSelected.Count))
            .Select(x => x.Image).ToList();

        // Zähler: wie viele Bilder es gäbe, wenn die Person dazukäme
        var counts = new Dictionary<int, int>();
        foreach (var (_, ids) in perImage)
        {
            if (!ids.IsSupersetOf(_searchSelected)) continue;
            foreach (var p in ids) if (!_searchSelected.Contains(p)) counts[p] = counts.GetValueOrDefault(p) + 1;
        }

        // Land / Jahr
        string? country = (CountryBox.SelectedItem as ComboBoxItem)?.Tag as string;
        int? year = (YearBox.SelectedItem as ComboBoxItem)?.Tag as int?;
        bool OkCountry(GalleryImage i) => country is null || i.Country == country;
        bool OkYear(GalleryImage i) => year is null || i.Date.Year == year;
        var countries = hits.Where(OkYear).Where(i => i.Country is not null).GroupBy(i => i.Country!)
            .Select(g => (Code: g.Key, Name: AppServices.Countries.NameOf(g.Key), Count: g.Count()))
            .OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        var years = hits.Where(OkCountry).GroupBy(i => i.Date.Year)
            .Select(g => (Year: g.Key, Count: g.Count())).OrderByDescending(x => x.Year).ToList();
        if (country is not null && countries.All(c => c.Code != country)) country = null;
        if (year is not null && years.All(y => y.Year != year)) year = null;
        var results = hits.Where(i => OkCountry(i) && OkYear(i)).ToList();

        _searchFilling = true;
        CountryBox.Items.Clear();
        CountryBox.Items.Add(new ComboBoxItem { Content = "Alle Länder", Tag = null });
        foreach (var c in countries) CountryBox.Items.Add(new ComboBoxItem { Content = $"{c.Name} ({c.Count:N0})", Tag = c.Code });
        CountryBox.SelectedItem = CountryBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, country)) ?? CountryBox.Items[0];
        YearBox.Items.Clear();
        YearBox.Items.Add(new ComboBoxItem { Content = "Alle Jahre", Tag = null });
        foreach (var y in years) YearBox.Items.Add(new ComboBoxItem { Content = $"{y.Year} ({y.Count:N0})", Tag = y.Year });
        YearBox.SelectedItem = YearBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, year)) ?? YearBox.Items[0];

        // Personenliste nach Gruppen
        SearchPersons.Children.Clear();
        foreach (var g in snap.Groups)
        {
            var members = named.Where(p => (p.GroupId ?? FaceStore.MainGroup) == g.Id).ToList();
            if (members.Count == 0) continue;
            SearchPersons.Children.Add(new TextBlock
            {
                Text = g.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2),
            });
            foreach (var p in members)
            {
                bool selected = _searchSelected.Contains(p.Id);
                int n = _searchSelected.Count == 0 ? p.Images.Count : counts.GetValueOrDefault(p.Id);
                var box = new CheckBox
                {
                    Content = selected ? p.DisplayName : $"{p.DisplayName}  ({n:N0})",
                    IsChecked = selected,
                    IsEnabled = selected || n > 0,
                    Tag = p.Id,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                box.Click += (_, _) =>
                {
                    if (!_searchSelected.Remove(p.Id)) _searchSelected.Add(p.Id);
                    RefreshSearch();
                };
                SearchPersons.Children.Add(box);
            }
        }
        _searchFilling = false;

        var names = named.Where(p => _searchSelected.Contains(p.Id)).Select(p => p.DisplayName).ToList();
        SearchTitle.Text = names.Count == 0 ? "Personen suchen" : string.Join(" + ", names);
        var extra = new List<string> { $"{results.Count:N0} Bilder" };
        if (country is not null) extra.Add(AppServices.Countries.NameOf(country));
        if (year is not null) extra.Add(year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (onlyThese) extra.Add("nur diese Personen");
        int noCountry = hits.Count(i => OkYear(i) && i.Country is null);
        if (country is null && noCountry > 0 && names.Count > 0) extra.Add($"{noCountry:N0} ohne Land (kein GPS)");
        SearchSubtitle.Text = names.Count == 0 ? "Personen links anklicken" : string.Join(" · ", extra);
        SearchEmpty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmpty.Text = names.Count == 0 ? "Links eine oder mehrere Personen anklicken." : "Keine Bilder mit dieser Auswahl.";
        SearchGrid.SetImages(results, keepPosition: false);
    }

    // ------------------------------------------------------------------ Gruppen / Reihenfolge

    private async void OnEditGroups(object sender, RoutedEventArgs e)
    {
        var edited = await Dialogs.EditGroupsAsync(Snap.Groups);
        if (edited is null) return;
        var faces = AppServices.Faces;
        await faces.EditAsync(() => faces.ApplyGroups(edited));
    }

    private async void OnEditOrder(object sender, RoutedEventArgs e)
    {
        var result = await Dialogs.EditOrderAsync(Snap.Groups, _persons);
        if (result is not { } r || r.Order.Count == 0) return;
        var faces = AppServices.Faces;
        await faces.EditAsync(() => faces.SetOrder(r.Order));
    }

    // ------------------------------------------------------------------ Prüfen ("Ist das …?")

    private const int ReviewBatch = 40;
    private bool _reviewAll;
    private readonly HashSet<long> _reviewHandled = [];
    private readonly HashSet<long> _reviewRejected = [];
    private List<(GalleryImage Image, FaceRef Face)> _reviewBatch = [];

    private void OnReviewSuggestions(object sender, RoutedEventArgs e) => StartReview(all: false);

    private void OnReviewAuto(object sender, RoutedEventArgs e) => StartReview(all: true);

    /// <summary>all = false: Vorschläge (wahrscheinlichste zuerst); true: automatische (unsicherste zuerst).</summary>
    private void StartReview(bool all)
    {
        _reviewAll = all;
        _reviewHandled.Clear();
        DetailPanel.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Visible;
        NextReviewRound();
    }

    private List<(GalleryImage Image, FaceRef Face)> ReviewSource(PersonInfo p) => _reviewAll
        ? p.Faces.Where(f => f.Face.Status == FaceStatus.Auto).OrderBy(f => f.Face.Score).ToList()
        : p.Suggestions.OrderByDescending(f => f.Face.Score).ToList();

    /// <summary>Neue Runde – eingefroren, damit sich nichts verschiebt, während du klickst.</summary>
    private void NextReviewRound()
    {
        if (Current is not { } p)
        {
            CloseReview();
            return;
        }
        _reviewRejected.Clear();
        var source = ReviewSource(p).Where(f => !_reviewHandled.Contains(f.Face.Id)).ToList();
        _reviewBatch = source.Take(ReviewBatch).ToList();
        ReviewTitle.Text = $"Ist das {p.DisplayName}?";
        ReviewFaces.Children.Clear();
        foreach (var (img, face) in _reviewBatch) ReviewFaces.Children.Add(ReviewCell(img, face));
        bool done = _reviewBatch.Count == 0;
        ReviewDone.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        ReviewDone.Text = _reviewHandled.Count == 0
            ? "Nichts zu prüfen."
            : "Alles geprüft.\nDie App lernt aus deinen Antworten und aktualisiert die Vorschläge.";
        ReviewSubmitButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        ReviewSubtitle.Text = done ? "fertig"
            : $"Falsche anklicken · Rechtsklick zeigt das ganze Bild · noch {source.Count:N0}";
        UpdateReviewButton();
    }

    private FrameworkElement ReviewCell(GalleryImage img, FaceRef face)
    {
        var crop = new FaceCrop { Width = 120, Height = 120 };
        crop.Show(img, face);
        var cross = new TextBlock
        {
            Text = "\uE711",   // X
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 44,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var overlay = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0xB0, 0x00, 0x20)),
            Child = cross,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var cell = new Grid { Width = 120, Height = 120, Margin = new Thickness(4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        cell.Children.Add(crop);
        cell.Children.Add(overlay);
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (!_reviewRejected.Remove(face.Id)) _reviewRejected.Add(face.Id);
            overlay.Visibility = _reviewRejected.Contains(face.Id) ? Visibility.Visible : Visibility.Collapsed;
            UpdateReviewButton();
        };
        cell.MouseRightButtonUp += (_, _) =>
        {
            var viewer = new ViewerWindow([img], 0) { Owner = Window.GetWindow(this) };
            viewer.Show();
        };
        return cell;
    }

    private void UpdateReviewButton()
    {
        int reject = _reviewRejected.Count;
        ReviewSubmitButton.Content = $"{_reviewBatch.Count - reject} bestätigen · {reject} ablehnen";
    }

    private async void OnReviewSubmit(object sender, RoutedEventArgs e)
    {
        if (_openPerson is not int pid || _reviewBatch.Count == 0) return;
        var reject = _reviewRejected.ToList();
        var confirm = _reviewBatch.Select(f => f.Face.Id).Where(id => !_reviewRejected.Contains(id)).ToList();
        foreach (var f in _reviewBatch) _reviewHandled.Add(f.Face.Id);
        ReviewSubmitButton.IsEnabled = false;
        var faces = AppServices.Faces;
        try
        {
            await faces.EditAsync(() =>
            {
                faces.Confirm(confirm, pid);
                faces.Reject(reject);
            }, reclassify: true);
        }
        finally
        {
            ReviewSubmitButton.IsEnabled = true;
        }
        NextReviewRound();
    }

    private void OnReviewBack(object sender, RoutedEventArgs e) => CloseReview();

    private void CloseReview()
    {
        ReviewPanel.Visibility = Visibility.Collapsed;
        if (_openPerson is int pid) ShowPerson(pid, keepPosition: true);
        else OverviewPanel.Visibility = Visibility.Visible;
    }

    private void OnToggleSort(object sender, RoutedEventArgs e)
    {
        var lib = AppServices.Library;
        lib.SetOldestFirst(!lib.OldestFirst);
        if (_openPerson is int pid) ShowPerson(pid, keepPosition: false);   // nach dem Umsortieren oben beginnen
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        var name = await Dialogs.AskTextAsync("Umbenennen", "Name der Person (leer = Name entfernen):", p.Name ?? "");
        if (name is null) return;
        var faces = AppServices.Faces;
        await faces.EditAsync(() => faces.Rename(p.Id, name), reclassify: true);
    }

    private async void OnGroup(object sender, RoutedEventArgs e)
    {
        if (Current is not { Name: not null } p) return;
        var group = await Dialogs.PickGroupAsync(Snap.Groups, p.GroupId);
        if (group is not int gid) return;
        var faces = AppServices.Faces;
        await faces.EditAsync(() => faces.SetGroup(p.Id, gid));
    }

    private async void OnMerge(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        var others = Persons.Build(AppServices.Library.Images, Snap).Where(x => x.Id != p.Id).ToList();
        var choice = await Dialogs.PickPersonAsync($"„{p.DisplayName}“ zusammenführen mit …", others, allowNew: false);
        if (choice?.PersonId is not int into) return;
        var faces = AppServices.Faces;
        _openPerson = into;
        await faces.EditAsync(() => faces.Merge(p.Id, into), reclassify: true);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (DetailGrid.Selected.Count == DetailGrid.Images.Count) DetailGrid.ClearSelection();
        else DetailGrid.SelectAll();
    }

    /// <summary>Rechtsklick auf ein Bild der Person.</summary>
    private IReadOnlyList<(string Header, Action Action)> PersonImageActions(GalleryImage img)
    {
        if (_openPerson is not int pid || Current is not { } person) return [];
        var faceIds = Snap.FacesByPath.TryGetValue(img.Path, out var f)
            ? f.Where(x => x.PersonId == pid).Select(x => x.Id).ToList()
            : [];
        if (faceIds.Count == 0) return [];
        var faces = AppServices.Faces;
        return
        [
            ("Als Titelbild verwenden", () => _ = faces.EditAsync(() => faces.SetCover(pid, faceIds[0]), reclassify: true)),
            ($"Nicht {person.DisplayName} (entfernen)", () => _ = faces.EditAsync(() => faces.Reject(faceIds), reclassify: true)),
            ("Andere Person …", async () =>
            {
                var others = Persons.Build(AppServices.Library.Images, Snap).Where(x => x.Id != pid).ToList();
                var choice = await Dialogs.PickPersonAsync("Gesicht zuordnen zu …", others, allowNew: true);
                if (choice is null) return;
                await faces.EditAsync(() =>
                {
                    int target = choice.PersonId ?? faces.CreatePerson(choice.NewName!);
                    faces.Confirm(faceIds, target);
                }, reclassify: true);
            }),
        ];
    }

    private async void OnCover(object sender, RoutedEventArgs e)
    {
        if (_openPerson is not int pid || SelectedFaceIds() is not [var faceId, ..]) return;
        DetailGrid.ClearSelection();
        var faces = AppServices.Faces;
        await faces.EditAsync(() => faces.SetCover(pid, faceId), reclassify: true);
    }

    private async void OnMoveFaces(object sender, RoutedEventArgs e)
    {
        if (_openPerson is not int pid) return;
        var ids = SelectedFaceIds();
        if (ids.Count == 0) return;
        var others = Persons.Build(AppServices.Library.Images, Snap).Where(x => x.Id != pid).ToList();
        var choice = await Dialogs.PickPersonAsync($"{ids.Count} Gesichter zuordnen zu …", others, allowNew: true);
        if (choice is null) return;
        DetailGrid.ClearSelection();
        var faces = AppServices.Faces;
        await faces.EditAsync(() =>
        {
            int target = choice.PersonId ?? faces.CreatePerson(choice.NewName!);
            faces.Confirm(ids, target);
        }, reclassify: true);
    }

    private async void OnRemoveFaces(object sender, RoutedEventArgs e)
    {
        var ids = SelectedFaceIds();
        if (ids.Count == 0) return;
        DetailGrid.ClearSelection();
        var faces = AppServices.Faces;
        await faces.EditAsync(() => faces.Reject(ids), reclassify: true);
    }
}

/// <summary>Eine Zeile Personen-Karten (rundes Gesicht, Name, Anzahl). Kinder werden beim Recycling wiederverwendet.</summary>
public sealed class PersonRowView : Panel
{
    public const double Gap = 12;
    private const double TextHeight = 42;

    private sealed class Card : StackPanel
    {
        public readonly FaceAvatar Avatar = new();
        public readonly TextBlock NameText = new()
        {
            FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0),
        };
        public readonly TextBlock CountText = new() { FontSize = 12, TextAlignment = TextAlignment.Center };

        public Card()
        {
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;
            NameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            CountText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Children.Add(Avatar);
            Children.Add(NameText);
            Children.Add(CountText);
        }
    }

    public PersonRowView()
    {
        Background = Brushes.Transparent;
        DataContextChanged += (_, _) => Sync();
    }

    private PersonCardRow? Row => DataContext as PersonCardRow;

    private void Sync()
    {
        var row = Row;
        int n = row?.Persons.Count ?? 0;
        while (Children.Count < n) Children.Add(new Card());
        while (Children.Count > n) Children.RemoveAt(Children.Count - 1);
        for (int i = 0; i < n; i++)
        {
            var card = (Card)Children[i];
            var p = row!.Persons[i];
            card.Tag = p;
            card.Avatar.Width = card.Avatar.Height = row.CellSize;
            card.Avatar.Person = p;
            card.NameText.Text = p.DisplayName;
            card.CountText.Text = p.Suggestions.Count > 0
                ? $"{p.Images.Count:N0} Bilder · {p.Suggestions.Count} Vorschläge"
                : $"{p.Images.Count:N0} Bilder";
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
            Children[i].Arrange(new Rect(i * (row.CellSize + Gap), Gap / 2, row.CellSize, row.CellSize + TextHeight));
        return finalSize;
    }
}
