using Avalonia;
using Avalonia.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net.Sockets;
using TypeWhisper.Core;
using TypeWhisper.Linux.Cli;
using TypeWhisper.Linux.Cli.Commands;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Ipc;

namespace TypeWhisper.Linux;

public static class Program
{
    // Boot-time profiling stopwatch. Single instance, started at the top of
    // Main; BootTrace.Stage(name) writes "+Xms: name" so we can see where
    // startup time goes. Lightweight enough to leave on in release builds.
    public static readonly Stopwatch BootStopwatch = Stopwatch.StartNew();
    public static ServiceProvider Services { get; private set; } = null!;
    public static bool StartMinimized { get; private set; }

    public static int Main(string[] args)
    {
        // Pipe Debug.WriteLine output to stdout so plugin + service logs are
        // visible when the app runs from a terminal.
        Trace.Listeners.Add(new ConsoleTraceListener());
        BootTrace.Stage("Main entered");

        TypeWhisperEnvironment.EnsureDirectories();
        BootTrace.Initialize();
        BootTrace.Stage("EnsureDirectories");

        // GNOME launches menu-clicked apps at nice 6 / ionice idle for shell
        // responsiveness. That throttles cold start ~60× for a CPU+IO-heavy
        // .NET app. Restore defaults so menu launch matches terminal launch.
        var priorityResult = ProcessPriority.ResetToDefaults();
        BootTrace.Stage($"ProcessPriority reset ({priorityResult})");

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
        BootTrace.Stage($"CommandLineParser.Parse (kind={action.Kind})");
        try
        {
            var socketPath = SocketPathResolver.ResolveControlSocketPath();
            BootTrace.Stage("SocketPathResolver.ResolveControlSocketPath");
            if (action.Kind == CliActionKind.BareToggle)
            {
                if (ControlSocketClient.TrySendToggle(socketPath, out var probeError))
                {
                    // We mapped no window — the running instance handles the
                    // toggle — so end the launcher's busy cursor explicitly.
                    LinuxStartupNotification.NotifyComplete();
                    return 0;
                }

                if (!string.IsNullOrEmpty(probeError))
                {
                    Trace.WriteLine($"[Program] Control socket probe: {probeError}");
                }

                BootTrace.Stage("ControlSocketClient.TrySendToggle (no live peer)");
            }
            else if (ControlSocketClient.IsLivePeer(socketPath))
            {
                Console.Error.WriteLine("TypeWhisper is already running.");
                // Same as the toggle path: no window will map, so clear the
                // launcher's busy cursor rather than let it time out.
                LinuxStartupNotification.NotifyComplete();
                return 0;
            }
            else
            {
                BootTrace.Stage("ControlSocketClient.IsLivePeer (none)");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Program] Control socket probe failed: {ex.Message}");
            BootTrace.Stage($"control socket probe threw: {ex.GetType().Name}");
        }

        Services = BuildServices();
        BootTrace.Stage("BuildServices");

        try
        {
            BootTrace.Stage("StartWithClassicDesktopLifetime begin");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
            when (ex is SocketException sx && sx.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // App startup raced another instance to the bind; treat the same
            // as the early probe finding a live peer.
            Console.Error.WriteLine("TypeWhisper is already running.");
            return 0;
        }
        finally
        {
            // DisposeAsync because the DI container holds IAsyncDisposable-only
            // services (e.g. XdgPortalGlobalShortcutsBackend); the sync
            // Dispose() path throws InvalidOperationException for those.
            Services.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                    // Prefer GLX on both native X11 and XWayland: it's the only
                    // X11 backend that reliably picks an ARGB-capable framebuffer
                    // config, which TransparencyLevelHint="Transparent" needs
                    // (used by the dictation overlay window). EGL on X11/Mesa
                    // typically returns an RGB-only visual, so the window paints
                    // opaque black behind the rounded Border and you get a square
                    // black box around the overlay.
                    //
                    // GLX on Mesa/XWayland (and NVIDIA hybrid on native X11)
                    // throws a per-frame SynchronizationLockException from
                    // GlxContext.RestoreContext.Dispose, but the throw happens
                    // after the frame body has rendered — the render loop
                    // continues and transparency works. The log noise is filtered
                    // by SuppressGlxRenderExceptionLogSink.
                    //
                    // EGL is the next fallback if GLX initialization itself
                    // fails; Software is the universal last resort.
                    RenderingMode = new[]
                    {
                        X11RenderingMode.Glx,
                        X11RenderingMode.Egl,
                        X11RenderingMode.Software
                    }
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

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        BootTrace.Stage("ServiceCollection created");
        ServiceRegistrations.Register(services);
        BootTrace.Stage("ServiceRegistrations.Register");
        var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = false,
                ValidateScopes = false
            }
        );
        BootTrace.Stage("ServiceProvider built");
        return provider;
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