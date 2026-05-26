using Avalonia;
using Avalonia.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Net.Sockets;
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
                {
                    return 0;
                }

                if (!string.IsNullOrEmpty(probeError))
                {
                    Trace.WriteLine($"[Program] Control socket probe: {probeError}");
                }
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
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
        catch (Exception ex)
            when (ex is SocketException sx && sx.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // App startup raced another instance to the bind; treat the same
            // as the early probe finding a live peer.
            Console.Error.WriteLine("TypeWhisper is already running.");
            return 0;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(
                new X11PlatformOptions
                {
                    // Avalonia's X11 IBus integration can log noisy DBus errors
                    // when IBus destroys an input context before Avalonia releases it.
                    // Set TYPEWHISPER_DISABLE_IME=1 to disable IME composition.
                    EnableIme = !IsImeDisabled(),
                    // Rendering mode order is session-dependent:
                    //   - Native X11: prefer GLX. It's the only X11 backend that
                    //     reliably picks an ARGB-capable framebuffer config, which
                    //     TransparencyLevelHint="Transparent" needs (used by the
                    //     dictation overlay window). EGL on X11/Mesa typically
                    //     returns an RGB-only visual, so the window paints opaque
                    //     black behind the rounded Border and you get a square
                    //     black box around the overlay.
                    //   - XWayland (X11 app on a Wayland session): prefer EGL.
                    //     GlxContext.RestoreContext.Dispose throws
                    //     SynchronizationLockException every frame on Mesa/XWayland
                    //     and breaks the render loop. EGL's opaque-window cost is
                    //     less bad than no rendering at all.
                    // Software is the universal fallback in both lists.
                    RenderingMode = IsNativeX11Session()
                        ? new[]
                        {
                            X11RenderingMode.Glx,
                            X11RenderingMode.Egl,
                            X11RenderingMode.Software
                        }
                        : new[] { X11RenderingMode.Egl, X11RenderingMode.Software }
                }
            )
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        // .LogToTrace() above assigned Logger.Sink synchronously. Wrap it so
        // two harmless Avalonia log lines are dropped:
        //   - the XSMP "SESSION_MANAGER ... not defined" startup warning —
        //     see SuppressXsmpWarningLogSink.
        //   - the per-frame SynchronizationLockException from
        //     GlxContext.RestoreContext.Dispose on GLX/Mesa+NVIDIA-hybrid
        //     and XWayland — the frame still renders, only the log spams.
        //     See SuppressGlxRenderExceptionLogSink.
        if (Logger.Sink is { } sink)
        {
            Logger.Sink = new SuppressGlxRenderExceptionLogSink(
                new SuppressXsmpWarningLogSink(sink)
            );
        }

        return builder;
    }

    private static IHost BuildHost(string[] args)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureServices(ServiceRegistrations.Register)
            .Build();
    }

    // Native X11 = X11 session type AND no Wayland display socket. A Wayland
    // session running this X11 app routes through XWayland, where GLX is broken
    // on Mesa (see RenderingMode comment above).
    private static bool IsNativeX11Session()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrEmpty(waylandDisplay);
    }

    private static bool IsImeDisabled()
    {
        return Environment.GetEnvironmentVariable("TYPEWHISPER_DISABLE_IME") is { } value
               && (
                   value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               );
    }
}