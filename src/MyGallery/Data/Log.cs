using System.IO;

namespace MyGallery.Data;

/// <summary>Einfaches Fehlerprotokoll: %LOCALAPPDATA%\MyGallery\log.txt (wird bei jedem Start neu begonnen).</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(AppServices.DataFolder, "log.txt");
    private static int _lines;

    static Log()
    {
        try { File.WriteAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Start{Environment.NewLine}"); } catch (Exception) { }
    }

    public static void Write(string message)
    {
        lock (Lock)
        {
            if (++_lines > 2000) return;
            try
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }
    }
}
