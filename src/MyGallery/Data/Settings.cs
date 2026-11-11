using System.IO;
using System.Text.Json;

namespace MyGallery.Data;

/// <summary>Einstellungen (settings.json im Datenordner).</summary>
public sealed class Settings
{
    public const string DefaultImageFolder = @"C:\Data\Images";

    /// <summary>Wurzelordner der Bilder (entspricht Data/images/ auf dem Handy).</summary>
    public string ImageFolder { get; set; } = DefaultImageFolder;

    /// <summary>Sortierung: älteste Bilder/Jahre zuerst (Standard: neueste zuerst).</summary>
    public bool OldestFirst { get; set; }

    /// <summary>Stand der Länder-Regeln, mit dem die Länder zugeordnet wurden (siehe Countries.RulesVersion).</summary>
    public int CountryRulesVersion { get; set; }

    private string _file = "";

    public static Settings Load(string file)
    {
        Settings s;
        try
        {
            s = File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            s = new();
        }
        s._file = file;
        if (string.IsNullOrWhiteSpace(s.ImageFolder)) s.ImageFolder = DefaultImageFolder;
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
