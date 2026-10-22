using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// Verification-only observer. It reads events and traces from the isolated app.
// It does not change app settings, providers, pipeline behavior or DTOs.
public static class StartupHook
{
    private static readonly object Gate = new();
    private static string? Path;

    public static void Initialize()
    {
        Path = Environment.GetEnvironmentVariable("TW_OBSERVER_PATH");
        if (Path is null) return;
        Trace.Listeners.Add(new Listener());
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 600; i++)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "typewhisper");
                var program = assembly?.GetType("TypeWhisper.Linux.Program");
                var services = program?.GetProperty("Services", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IServiceProvider;
                var type = assembly?.GetType("TypeWhisper.Linux.Services.DictationOrchestrator");
                if (services is not null && type is not null)
                {
                    var orchestrator = services.GetService(type);
                    if (orchestrator is not null)
                    {
                        type.GetEvent("StatusMessage")!.AddEventHandler(orchestrator,
                            new EventHandler<string>((_, text) => Write("status", text)));
                        type.GetEvent("TranscriptionCompleted")!.AddEventHandler(orchestrator,
                            new EventHandler<string>((_, text) => Write("final_text", text)));
                        type.GetEvent("RecordingStateChanged")!.AddEventHandler(orchestrator,
                            new EventHandler<bool>((_, value) => Write("recording", value.ToString())));
                        Write("observer", "attached");
                        return;
                    }
                }
                await Task.Delay(100);
            }
            Write("observer", "attachment failed");
        });
    }

    private static void Write(string kind, string text)
    {
        lock (Gate)
            File.AppendAllText(Path!, JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
                kind, text,
            }) + "\n");
    }

    private sealed class Listener : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) => StartupHook.Write("trace", message ?? "");
    }
}
