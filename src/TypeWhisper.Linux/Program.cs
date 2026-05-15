using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeWhisper.Core;
using TypeWhisper.Linux.Cli;
using TypeWhisper.Linux.Cli.Commands;
using TypeWhisper.Linux.Services.Ipc;

namespace TypeWhisper.Linux;

public static class Program
{
    public static IHost Host { get; private set; } = null!;
    public static bool StartMinimized { get; private set; }

    public static int Main(string[] args)
    {
        // Pipe Debug.WriteLine output to stdout so plugin + service logs are
        // visible when the app runs from a terminal.
        Trace.Listeners.Add(new ConsoleTraceListener());

        TypeWhisperEnvironment.EnsureDirectories();

        var action = CommandLineParser.Parse(args);
        StartMinimized = action.StartMinimized;

        switch (action.Kind)
        {
            case CliActionKind.PrintHelp:
                Console.Write(CommandLineParser.UsageText);
                return 0;

            case CliActionKind.Invalid:
                Console.Error.WriteLine($"typewhisper: {action.ErrorMessage}");
                Console.Error.Write(CommandLineParser.UsageText);
                return 2;

            case CliActionKind.Record:
                // RecordVerb is always non-null on this branch — see parser.
                return RecordCommand.Run(action.RecordVerb!);

            case CliActionKind.Status:
                return StatusCommand.Run();

            case CliActionKind.BareToggle:
            case CliActionKind.LaunchGui:
                // Fall through to single-instance handling + GUI startup.
                break;
        }

        // Single-instance + bare-CLI handling. Bare `typewhisper` is the only
        // launch form that should drive dictation: if an instance is running
        // we send `toggle` and exit. Argument-bearing GUI launches
        // (`--minimized`, etc.) must NOT toggle the existing instance just
        // to discover it exists, so they use a side-effect-free probe and
        // bail with a friendly message. The bind that happens later in App
        // startup remains the authoritative single-instance guard for the
        // probe-then-bind race window.
        try
        {
            var socketPath = SocketPathResolver.ResolveControlSocketPath();
            if (action.Kind == CliActionKind.BareToggle)
            {
                if (ControlSocketClient.TrySendToggle(socketPath, out var probeError))
                    return 0;
                if (!string.IsNullOrEmpty(probeError))
                    Trace.WriteLine($"[Program] Control socket probe: {probeError}");
            }
            else if (ControlSocketClient.IsLivePeer(socketPath))
            {
                Console.Error.WriteLine("TypeWhisper is already running.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Program] Control socket probe failed: {ex.Message}");
        }

        Host = BuildHost(args);
        Host.Start();

        try
        {
            var exitCode = BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);

            // Graceful shutdown with a hard cap. SharpHook's libuiohook thread
            // waits on X11 events and can block Dispose() indefinitely on a
            // quiet desktop; Host.StopAsync would then hang forever. Cap the
            // wait and fall back to Environment.Exit so the tray icon releases.
            var stopped = Host.StopAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(4));

            if (!stopped)
            {
                Trace.WriteLine("[Program] Host.StopAsync timed out — forcing exit.");
                Environment.Exit(exitCode);
            }
            return exitCode;
        }
        catch (Exception ex) when (ex is SocketException sx && sx.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // App startup raced another instance to the bind; treat the same
            // as the early probe finding a live peer.
            Console.Error.WriteLine("TypeWhisper is already running.");
            return 0;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                // Avalonia's X11 IBus integration can log noisy DBus errors
                // when IBus destroys an input context before Avalonia releases it.
                // Set TYPEWHISPER_DISABLE_IME=1 to disable IME composition.
                EnableIme = !IsImeDisabled()
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        // .LogToTrace() above assigned Logger.Sink synchronously. Wrap it so
        // the harmless XSMP "SESSION_MANAGER ... not defined" warning the X11
        // backend emits on every Wayland startup is dropped — see
        // SuppressXsmpWarningLogSink for why it's safe to ignore.
        if (Logger.Sink is { } sink)
            Logger.Sink = new SuppressXsmpWarningLogSink(sink);

        return builder;
    }

    private static IHost BuildHost(string[] args)
        => Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureServices(ServiceRegistrations.Register)
            .Build();

    private static bool IsImeDisabled() =>
        Environment.GetEnvironmentVariable("TYPEWHISPER_DISABLE_IME") is { } value &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
