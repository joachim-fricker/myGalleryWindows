using System.Diagnostics;
using System.Windows.Threading;

namespace MyGallery.Data;

/// <summary>
/// Prüft jede Sekunde, ob die Oberfläche reagiert. Hängt sie (> 500 ms), wird das mit Speicher- und
/// Vorschaubild-Statistik ins Protokoll geschrieben (zur Fehlersuche).
/// </summary>
public static class Watchdog
{
    public static void Start(Dispatcher ui)
    {
        var thread = new Thread(() => Run(ui)) { IsBackground = true, Name = "Watchdog" };
        thread.Start();
    }

    private static void Run(Dispatcher ui)
    {
        int tick = 0;
        while (true)
        {
            Thread.Sleep(1000);
            var sw = Stopwatch.StartNew();
            var op = ui.InvokeAsync(() => { }, DispatcherPriority.Input);
            bool hung = false;
            while (op.Status != DispatcherOperationStatus.Completed)
            {
                Thread.Sleep(20);
                if (!hung && sw.ElapsedMilliseconds > 500)
                {
                    hung = true;
                    Log.Write($"UI reagiert nicht ({Info()})");
                }
                if (sw.ElapsedMilliseconds > 60_000) break;
            }
            if (hung) Log.Write($"UI wieder frei nach {sw.ElapsedMilliseconds} ms ({Info()})");
            else if (++tick % 10 == 0) Log.Write($"ok ({Info()})");
        }
    }

    private static string Info()
    {
        using var p = Process.GetCurrentProcess();
        var (req, loaded) = AppServices.Thumbs.Stats;
        return $"RAM {p.WorkingSet64 / 1024 / 1024} MB, GC-Heap {GC.GetTotalMemory(false) / 1024 / 1024} MB, " +
               $"Gen2 {GC.CollectionCount(2)}, Threads {p.Threads.Count}, Thumbs angefordert {req} / geladen {loaded}";
    }
}
