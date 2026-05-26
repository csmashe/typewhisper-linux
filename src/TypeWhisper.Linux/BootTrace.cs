using System.Diagnostics;
using TypeWhisper.Core;

namespace TypeWhisper.Linux;

internal static class BootTrace
{
    // Per-launch file so menu launches (no stdout) still produce a trace.
    // Truncates on each Main() entry — a launch's own startup is what we care
    // about, not a history across launches. Stays null if the file can't be
    // opened, so failures here never block startup.
    private static StreamWriter? s_fileWriter;
    private static readonly object s_lock = new();

    public static void Initialize()
    {
        try
        {
            var path = Path.Combine(TypeWhisperEnvironment.LogsPath, "boot.log");
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            s_fileWriter = new StreamWriter(stream) { AutoFlush = true };
            s_fileWriter.WriteLine($"=== boot trace @ {DateTime.Now:O} ===");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BootTrace] Could not open boot.log: {ex.Message}");
        }
    }

    public static void Stage(string name)
    {
        var line = $"[Boot] +{Program.BootStopwatch.ElapsedMilliseconds,6}ms  {name}";
        Trace.WriteLine(line);
        if (s_fileWriter is not null)
        {
            lock (s_lock)
            {
                s_fileWriter.WriteLine(line);
            }
        }
    }
}
