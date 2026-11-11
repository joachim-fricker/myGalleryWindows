# myGalleryWindows – myGallery (Windows)

Native Windows-Version der Android-App [myGallery](../myGallery) mit gleichem Funktionsumfang.
Aufbau wie der Activity Viewer (`myactivitiesWeb`): WPF, .NET 10, WPF-UI, SQLite.

## Bauen
1. Visual Studio 2026 Community mit Workload ".NET-Desktopentwicklung"
2. `src/MyGallery/MyGallery.slnx` öffnen, F5

## Veröffentlichen (exe)
Visual Studio: Rechtsklick auf das Projekt → **Veröffentlichen** → Profil **Desktop** → **Veröffentlichen**
(oder `dotnet publish src/MyGallery -p:PublishProfile=Desktop`).
Ergebnis: `C:\Apps\MyGallery\MyGallery.exe` + Ordner `Assets`. Braucht die .NET-10-Desktop-Laufzeit.

## Bildquelle
Alle Bilder in `C:\Data\Images` inkl. Unterordner (entspricht `Data/images/` auf dem Handy),
änderbar unter Einstellungen. Dateien: jpg, jpeg, png, webp, heic/heif (mit Windows-Erweiterung), gif, bmp, tif.

## Stand
| Funktion | Status |
|---|---|
| **Alle**: alle Bilder, neueste zuoberst, Monats-Überschriften | ✅ |
| Datum: EXIF-Aufnahmedatum, sonst Ordnername (2015_Zinnen, 2015-07_Nepal, 20150712_Ausflug), sonst Dateidatum | ✅ |
| Vollbild: Pfeiltasten, Mausrad-/Doppelklick-Zoom, Verschieben, Details (I), Im Explorer zeigen | ✅ |
| **Favoriten**: Herz im Vollbild (Taste F), Herz auf Vorschaubildern, Album „Favoriten“ mit Mehrfachauswahl (Strg+Klick) zum Entfernen | ✅ |
| **Alben**: pro Jahr (aufklappbar) alle Ordner; Jahr aus Ordnername, sonst Jahr der meisten Aufnahmen | ✅ |
| **Länder**: Weltkarte (besuchte rot) + Liste mit Flagge, Anzahl, Zeitraum, Suche; Regeln in `Domain/Countries.cs` | ✅ |
| **Karte**: OpenStreetMap, Bilder als Gruppen mit Vorschaubild + Anzahl, „Bilder im Ausschnitt“ | ✅ |
| **Import vom Handy** (Einstellungen): Favoriten, Personen, Gruppen, Gesichter aus dem Android-Export | ✅ |
| **Personen**: Gruppen („Haupt“ offen, weitere aufklappbar), „Ohne Namen“; Person: umbenennen, Gruppe, zusammenführen, Auswahl (Strg+Klick) → Titelbild / andere Person / entfernen | ✅ |
| **Prüfen** („Ist das …?“, Vorschläge bzw. automatische, 40 pro Runde), **Gruppen bearbeiten**, **Reihenfolge** | ✅ |
| **Gesichtserkennung** für neue Bilder unter Windows: SCRFD (det_500m.onnx) + face_model.onnx (w600k_mbf, wie Android) | ✅ |
| **Personen suchen**: alle ausgewählten Personen auf einem Bild, „Nur diese Personen“, Land, Jahr | ✅ |

## Gesichtsmodelle
In `src/MyGallery/Assets` (oder `%LOCALAPPDATA%\MyGallery`):
- `face_model.onnx` – InsightFace w600k_mbf, dasselbe wie in der Android-App (aus deren `app/src/main/assets`)
- `det_500m.onnx` oder `face_detector.onnx` – InsightFace SCRFD aus `buffalo_s.zip`
  (https://github.com/deepinsight/insightface/releases/download/v0.7/buffalo_s.zip)

## Daten
`%LOCALAPPDATA%\MyGallery`: `gallery.db` (Datei- und EXIF-Cache), `thumbs\` (Vorschaubilder),
`settings.json`, `windows.json` (Fensterpositionen).

## Code-Struktur
```
Data/    AppServices, Settings, GalleryDatabase (SQLite), GalleryLibrary (Ordner-Scan im Hintergrund),
         ExifReader (WIC), ThumbnailCache (Speicher + Festplatte)
Data/    CountryLookup (Ländergrenzen, Assets/countries.geojson), MapIds (Bildnummern für die Karte)
Data/    FaceStore (Port von FaceDb aus Faces.kt, Tabellen face/person/pgroup/…), PhoneImport (ZIP der Android-App)
Data/    FavoritesStore (Tabelle favorite, Schlüssel = relativer Pfad + Dateiname)
Domain/  Persons (wie buildPersons), GalleryImage, ImageDates (Datumslogik wie Grouping.kt), Albums (wie buildAlbums)
Views/   LaenderPage, KartePage, MapHost (WebView2 + Assets/map.html mit Leaflet), AlleBilderPage, AlbenPage, ImageGrid (virtualisiertes Raster mit Auswahl), ThumbImage, ViewerWindow,
         EinstellungenPage
```
