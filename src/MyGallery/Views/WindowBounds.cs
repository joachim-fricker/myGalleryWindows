using System.IO;
using System.Text.Json;
using System.Windows;
using MyGallery.Data;

namespace MyGallery.Views;

/// <summary>
/// Grösse und Position von Fenstern: beim ersten Mal relativ zum Bildschirm,
/// danach so, wie der Benutzer sie zuletzt hatte (gespeichert in windows.json).
/// </summary>
public static class WindowBounds
{
    private sealed record Bounds(double Left, double Top, double Width, double Height, bool Maximized);

    private static readonly string FilePath = Path.Combine(AppServices.DataFolder, "windows.json");
    private static readonly Dictionary<string, Bounds> Saved = Load();

    public static void Apply(Window window, string key, double widthFraction, double heightFraction, double widthAdjust = 0)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        if (Saved.TryGetValue(key, out var b) && IsOnScreen(b))
        {
            window.Left = b.Left;
            window.Top = b.Top;
            window.Width = b.Width;
            window.Height = b.Height;
            if (b.Maximized) window.WindowState = WindowState.Maximized;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            window.Width = area.Width * widthFraction + widthAdjust;
            window.Height = area.Height * heightFraction;
            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
        }

        window.Closing += (_, _) =>
        {
            var r = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
            Saved[key] = new Bounds(r.Left, r.Top, r.Width, r.Height, window.WindowState == WindowState.Maximized);
            Save();
        };
    }

    /// <summary>Gespeicherte Position noch sichtbar (z. B. zweiter Monitor abgesteckt)?</summary>
    private static bool IsOnScreen(Bounds b) =>
        b.Width >= 300 && b.Height >= 200 &&
        b.Left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        b.Top + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight &&
        b.Left + b.Width - 100 > SystemParameters.VirtualScreenLeft &&
        b.Top >= SystemParameters.VirtualScreenTop - 10;

    private static Dictionary<string, Bounds> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Bounds>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
