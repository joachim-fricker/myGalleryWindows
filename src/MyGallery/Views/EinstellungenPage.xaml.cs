using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MyGallery.Data;

namespace MyGallery.Views;

public partial class EinstellungenPage : Page
{
    public EinstellungenPage()
    {
        InitializeComponent();
        DataText.Text = $"Datenordner: {AppServices.DataFolder}";
        DbText.Text = $"Datenbank: {AppServices.Db.FilePath}";
        Loaded += async (_, _) =>
        {
            Update();
            var size = await Task.Run(() => AppServices.Thumbs.DiskSize());
            ThumbText.Text = $"Cache auf der Festplatte: {size / 1024.0 / 1024.0:N0} MB ({AppServices.Thumbs.Folder}). " +
                             "Wird bei Bedarf automatisch neu erstellt.";
        };
        AppServices.Library.ImagesChanged += Update;
    }

    private bool _updating;

    private void Update()
    {
        _updating = true;
        OldestFirstSwitch.IsChecked = AppServices.Library.OldestFirst;
        _updating = false;
        FolderBox.Text = AppServices.Library.Root;
        CountText.Text = $"{AppServices.Library.Images.Count:N0} Bilder";
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Bildordner wählen",
            InitialDirectory = AppServices.Library.Root,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AppServices.Library.SetRoot(dialog.FolderName);
        Update();
    }

    private void OnRescan(object sender, RoutedEventArgs e) => AppServices.Library.Rescan();

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Export der Android-App wählen",
            Filter = "myGallery-Export (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (AppServices.Faces.FaceCount > 0 &&
            !await Dialogs.ConfirmAsync("Importieren",
                "Unter Windows sind bereits Personen vorhanden. Sie werden durch die Daten vom Handy ersetzt. Weiter?",
                "Ersetzen"))
            return;

        ImportButton.IsEnabled = false;
        ImportProgress.Visibility = Visibility.Visible;
        var progress = new Progress<string>(t => ImportText.Text = t);
        try
        {
            var images = AppServices.Library.Images;
            var r = await Task.Run(() => PhoneImport.Run(dialog.FileName, images, progress));
            ImportText.Text =
                $"Fertig: {r.ImagesMatched:N0} von {r.Images:N0} Bildern vom Handy hier gefunden · {r.Favorites:N0} Favoriten · " +
                $"{r.Persons:N0} Personen in {r.Groups} Gruppen · {r.Faces:N0} Gesichter" +
                (r.FacesSkipped > 0 ? $" ({r.FacesSkipped:N0} auf Bildern, die es hier nicht gibt, ausgelassen)" : "") +
                $" · Modell {r.Model}";
        }
        catch (Exception ex)
        {
            ImportText.Text = $"Import fehlgeschlagen: {ex.Message}";
            Log.Write($"Import: {ex}");
        }
        finally
        {
            ImportProgress.Visibility = Visibility.Collapsed;
            ImportButton.IsEnabled = true;
        }
    }

    private void OnSortChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        AppServices.Library.SetOldestFirst(OldestFirstSwitch.IsChecked == true);
    }

    private void OnClearThumbs(object sender, RoutedEventArgs e)
    {
        AppServices.Thumbs.Clear();
        ThumbText.Text = "Vorschaubilder gelöscht – sie werden beim Anzeigen neu erstellt.";
    }
}
