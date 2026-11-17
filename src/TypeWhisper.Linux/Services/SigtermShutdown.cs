using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Turns the first SIGTERM (logout, <c>kill</c>) into the app's normal quit. By default it
///     skips App's teardown, leaving media a dictation paused paused and ducked streams quiet.
///     A second SIGTERM, or a quit still running after the fallback delay, ends the process.
/// </summary>
internal sealed class SigtermShutdown : IDisposable
{
    private static readonly TimeSpan s_defaultFallbackDelay = TimeSpan.FromSeconds(15);

    private readonly Action _requestShutdown;
    private readonly Action _forceExit;
    private readonly TimeSpan _fallbackDelay;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable _registration;
    private int _requested;
    private ITimer? _fallback;

    internal SigtermShutdown(
        Action requestShutdown,
        Action forceExit,
        TimeSpan fallbackDelay,
        TimeProvider timeProvider,
        Func<Func<bool>, IDisposable> registerSigtermHandler
    )
    {
        _requestShutdown = requestShutdown;
        _forceExit = forceExit;
        _fallbackDelay = fallbackDelay;
        _timeProvider = timeProvider;
        _registration = registerSigtermHandler(HandleSigterm);
    }

    /// <summary>
    ///     Handles SIGTERM until the process exits. <paramref name="requestShutdown" /> starts the
    ///     normal quit; <paramref name="forceExit" /> must end the process.
    /// </summary>
    public static SigtermShutdown Register(Action requestShutdown, Action forceExit) =>
        new(
            requestShutdown,
            forceExit,
            s_defaultFallbackDelay,
            TimeProvider.System,
            handler => PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context => context.Cancel = handler()
            )
        );

    /// <summary>Returns true to cancel the signal's default termination.</summary>
    private bool HandleSigterm()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 1)
        {
            return false;
        }

        // Armed before the request so a request that throws or never returns is still bounded.
        _fallback = _timeProvider.CreateTimer(
            _ => _forceExit(),
            null,
            _fallbackDelay,
            Timeout.InfiniteTimeSpan
        );

        try
        {
            _requestShutdown();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[App] SIGTERM shutdown request failed: {ex.Message}");
            _forceExit();
        }

        return true;
    }

    public void Dispose()
    {
        _registration.Dispose();
        _fallback?.Dispose();
    }
}
