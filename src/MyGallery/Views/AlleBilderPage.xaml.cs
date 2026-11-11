using System.Windows;
using System.Windows.Controls;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Tab „Alle“: alle Bilder, neueste zuoberst, mit Monats-Überschriften.</summary>
public partial class AlleBilderPage : Page
{
    public AlleBilderPage()
    {
        InitializeComponent();
        var lib = AppServices.Library;
        lib.ImagesChanged += ShowImages;
        lib.StatusChanged += ShowStatus;
        Gallery.ImageClicked += OpenViewer;
        Loaded += (_, _) =>
        {
            if (!ReferenceEquals(Gallery.Images, lib.Images)) ShowImages();
            ShowStatus();
        };
    }

    private void ShowImages()
    {
        var lib = AppServices.Library;
        Gallery.SetImages(lib.Images);
        TitleText.Text = $"Alle Bilder ({lib.Images.Count:N0})";
        SortButton.ToolTip = lib.OldestFirst
            ? "Sortierung: älteste zuerst (klicken = neueste zuerst)"
            : "Sortierung: neueste zuerst (klicken = älteste zuerst)";
        UpdateEmpty();
    }

    private void ShowStatus()
    {
        var s = AppServices.Library.Status;
        if (s.Running)
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = s.Total > 0 ? $"{s.Phase}: {s.Done:N0} / {s.Total:N0}" : s.Phase;
            StatusProgress.Visibility = Visibility.Visible;
            StatusProgress.IsIndeterminate = s.Total <= 0;
            StatusProgress.Maximum = Math.Max(1, s.Total);
            StatusProgress.Value = s.Done;
        }
        else if (s.Error is not null)
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = s.Error;
            StatusProgress.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusPanel.Visibility = Visibility.Collapsed;
        }
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        var lib = AppServices.Library;
        bool empty = lib.Images.Count == 0 && !lib.Status.Running;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = $"Keine Bilder gefunden.\nGesucht wird in {lib.Root} (inkl. Unterordner).";
    }

    private void OpenViewer(IReadOnlyList<GalleryImage> images, int index)
    {
        var viewer = new ViewerWindow(images, index) { Owner = Window.GetWindow(this) };
        viewer.Show();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => AppServices.Library.Rescan();

    private void OnToggleSort(object sender, RoutedEventArgs e)
    {
        var lib = AppServices.Library;
        lib.SetOldestFirst(!lib.OldestFirst);
        Gallery.SetImages(lib.Images, keepPosition: false);   // nach dem Umsortieren oben beginnen
    }
}
