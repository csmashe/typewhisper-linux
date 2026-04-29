using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeWhisper.Core;

namespace TypeWhisper.Linux;

public static class Program
{
    private static FileStream? _singleInstanceLock;

    public static IHost Host { get; private set; } = null!;
    public static bool StartMinimized { get; private set; }

    public static int Main(string[] args)
    {
        // Pipe Debug.WriteLine output to stdout so plugin + service logs are
        // visible when the app runs from a terminal.
        Trace.Listeners.Add(new ConsoleTraceListener());

        StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        TypeWhisperEnvironment.EnsureDirectories();

        if (!AcquireSingleInstanceLock())
        {
            Console.Error.WriteLine("TypeWhisper is already running.");
            return 0;
        }

        try
        {
            Host = BuildHost(args);
            Host.Start();

            var exitCode = BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);

            // Graceful shutdown with a hard cap. SharpHook's libuiohook thread
            // waits on X11 events and can block Dispose() indefinitely on a
            // quiet desktop; Host.StopAsync would then hang forever. Cap the
            // wait and fall back to Environment.Exit so the tray icon releases.
            var stopped = Host.StopAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(4));
            _singleInstanceLock?.Dispose();

            if (!stopped)
            {
                Trace.WriteLine("[Program] Host.StopAsync timed out — forcing exit.");
                Environment.Exit(exitCode);
            }
            return exitCode;
        }
        catch
        {
            _singleInstanceLock?.Dispose();
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                // Avalonia's X11 IBus integration can log noisy DBus errors
                // when IBus destroys an input context before Avalonia releases it.
                // TypeWhisper does not depend on IME composition in its own UI.
                EnableIme = false
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static IHost BuildHost(string[] args)
        => Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureServices(ServiceRegistrations.Register)
            .Build();

    private static bool AcquireSingleInstanceLock()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? Path.Combine(Path.GetTempPath(), $"typewhisper-{Environment.UserName}");
        Directory.CreateDirectory(runtimeDir);

        var lockPath = Path.Combine(runtimeDir, "typewhisper.lock");
        try
        {
            _singleInstanceLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
