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
    // Boot-time profiling stopwatch. BootTrace.Stage(name) writes "+Xms: name";
    // lightweight enough to leave on in release builds.
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

        // EnsureDirectories runs before the boot trace exists, so re-report its one privacy-
        // relevant outcome here.
        if (!TypeWhisperEnvironment.AudioDirectoryIsOwnerOnly)
        {
            BootTrace.Stage(
                $"WARNING: '{TypeWhisperEnvironment.AudioPath}' is not owner-only; "
                + "recordings stored there may be readable by other local users"
            );
        }

        // GNOME launches menu apps at nice 6 / ionice idle, which throttles cold start ~60×
        // for a CPU+IO-heavy .NET app. Restore defaults so menu launch matches terminal launch.
        var priorityResult = ProcessPriority.ResetToDefaults(new ProcessRunner());
        BootTrace.Stage($"ProcessPriority reset ({priorityResult})");

        var action = CommandLineParser.Parse(args);
        StartMinimized = action.StartMinimized;

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
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
                return RecordCommand.Run(action.RecordVerb!); // RecordVerb is always non-null here

            case CliActionKind.Status:
                return StatusCommand.Run();

            case CliActionKind.BareToggle:
            case CliActionKind.LaunchGui:
                break; // fall through to single-instance handling + GUI startup
        }

        // Single-instance handling. Bare `typewhisper` sends `toggle` to a running instance
        // and exits. Argument-bearing GUI launches (--minimized etc.) use a side-effect-free
        // probe and bail if an instance is found — they must NOT trigger a toggle just to check.
        // The bind in App startup is the authoritative guard for the probe-then-bind race window.
        BootTrace.Stage($"CommandLineParser.Parse (kind={action.Kind})");
        try
        {
            var socketPath = SocketPathResolver.ResolveControlSocketPath();
            BootTrace.Stage("SocketPathResolver.ResolveControlSocketPath");
            if (action.Kind == CliActionKind.BareToggle)
            {
                if (ControlSocketClient.TrySendToggle(socketPath, out var probeError))
                {
                    // No window will map; clear the launcher's busy cursor.
                    LinuxStartupNotification.NotifyComplete();
                    return 0;
                }

                if (!string.IsNullOrEmpty(probeError))
                {
                    Trace.WriteLine($"[Program] Control socket probe: {probeError}");
                    StartupCancellation.NotifyUnverifiedInstance();
                    return 1;
                }

                BootTrace.Stage("ControlSocketClient.TrySendToggle (no live peer)");
            }
            else if (ControlSocketClient.IsLivePeer(socketPath))
            {
                Console.Error.WriteLine("TypeWhisper is already running.");
                LinuxStartupNotification.NotifyComplete(); // clear launcher's busy cursor
                return 0;
            }
            else if (File.Exists(socketPath))
            {
                StartupCancellation.NotifyUnverifiedInstance();
                return 1;
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
            StartupCancellation.NotifyUnverifiedInstance();
            return 1;
        }

        var restoreResult = SettingsBackupService.ApplyPendingRestoreAtStartup(
            TypeWhisperEnvironment.BasePath
        );
        switch (restoreResult.Status)
        {
            case StartupRestoreStatus.None:
                break;

            case StartupRestoreStatus.Applied:
                Console.WriteLine("Applied the staged settings restore.");
                Trace.WriteLine("[Program] Applied the staged settings restore.");
                BootTrace.Stage("staged settings restore applied");
                break;

            case StartupRestoreStatus.PriorGenerationRestored:
                Console.Error.WriteLine(
                    "The staged settings restore could not be applied. The prior settings generation was restored."
                );
                if (restoreResult.Error is not null)
                {
                    Trace.WriteLine(
                        $"[Program] Settings restore rolled back: {restoreResult.Error}"
                    );
                }

                BootTrace.Stage("staged settings restore rolled back");
                break;

            case StartupRestoreStatus.LockUnavailable:
                Console.Error.WriteLine(
                    "Another TypeWhisper startup is applying a staged settings restore. Startup was canceled."
                );
                Trace.WriteLine($"[Program] Restore lock unavailable: {restoreResult.Error}");
                LinuxStartupNotification.NotifyComplete();
                return 1;

            case StartupRestoreStatus.UnresolvedFailure:
                Console.Error.WriteLine(
                    "TypeWhisper could not safely recover the staged settings restore. Startup was canceled."
                );
                Trace.WriteLine($"[Program] Settings restore recovery failed: {restoreResult.Error}");
                LinuxStartupNotification.NotifyComplete();
                return 1;

            default:
                Console.Error.WriteLine(
                    "TypeWhisper encountered an unknown staged restore state. Startup was canceled."
                );
                LinuxStartupNotification.NotifyComplete();
                return 1;
        }

        Services = BuildServices();
        BootTrace.Stage("BuildServices");

        try
        {
            BootTrace.Stage("StartWithClassicDesktopLifetime begin");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
            when (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
        {
            // Startup raced another instance to the bind; same outcome as a probe finding a live peer.
            Console.Error.WriteLine("TypeWhisper is already running.");
            return 0;
        }
        finally
        {
            // A timed-out HTTP handler may still be using DI-owned services. The process is
            // exiting, so skipping provider disposal here cannot leak anything beyond its life.
            if (!App.SkipProviderDisposal)
            {
                // DisposeAsync: some DI services are IAsyncDisposable-only (e.g. XdgPortalGlobalShortcutsBackend);
                // sync Dispose() throws InvalidOperationException for those.
                Services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    // Public so the Avalonia XAML previewer / design-time tooling can discover and invoke it reflectively.
    // ReSharper disable once MemberCanBePrivate.Global
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(
                new X11PlatformOptions
                {
                    // Set TYPEWHISPER_DISABLE_IME=1 to suppress noisy IBus DBus errors.
                    EnableIme = !IsImeDisabled(),
                    // GLX first: it's the only X11 backend that reliably picks an ARGB framebuffer
                    // for the transparent overlay window. EGL on X11/Mesa typically returns RGB-only
                    // (giving a black box behind the overlay). GLX on Mesa/XWayland and NVIDIA hybrid
                    // throws a per-frame SynchronizationLockException from GlxContext.RestoreContext.Dispose,
                    // but only after rendering — transparency works and the log noise is filtered by
                    // SuppressGlxRenderExceptionLogSink. EGL is the fallback if GLX init fails.
                    RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software],
                }
            )
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        // Wrap the logger sink to suppress two harmless Avalonia log lines:
        // the XSMP SESSION_MANAGER startup warning and the per-frame GLX
        // SynchronizationLockException on Mesa+NVIDIA-hybrid/XWayland.
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
            new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false }
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
