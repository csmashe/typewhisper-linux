using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>The worker process ended or stopped answering; nothing it was doing completed.</summary>
internal class TranscriptionWorkerCrashedException(
    string message,
    int? exitCode,
    string diagnostics,
    Exception? inner = null
) : Exception(message, inner)
{
    /// <summary>The worker's exit code, when it could be observed.</summary>
    internal int? ExitCode { get; } = exitCode;

    /// <summary>The last lines the worker and its native libraries wrote to stdout and stderr.</summary>
    internal string Diagnostics { get; } = diagnostics;
}

/// <summary>
///     The worker stayed silent for a whole inactivity window during a request and was ended by the
///     host. It counts as a crash; the message says that nothing actually crashed.
/// </summary>
internal sealed class TranscriptionWorkerUnresponsiveException(string message, string diagnostics)
    : TranscriptionWorkerCrashedException(message, null, diagnostics);

/// <summary>A worker exceeded its total request budget, even if its heartbeat still ran.</summary>
internal sealed class TranscriptionWorkerRequestTimeoutException(string message) : TimeoutException(message);

/// <summary>One running worker. Requests are answered in order; the owner sends one at a time.</summary>
internal interface ITranscriptionWorkerConnection : IAsyncDisposable
{
    TranscriptionAccelerationPreference Acceleration { get; }
    int ProcessId { get; }
    bool IsAlive { get; }
    TranscriptionWorkerState? State { get; }

    /// <summary>
    ///     Returns the worker's answer, including engine errors. Throws
    ///     <see cref="TranscriptionWorkerCrashedException" /> if the worker ends first.
    /// </summary>
    Task<TranscriptionWorkerMessage> SendAsync(
        TranscriptionWorkerMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    );
}

/// <summary>Everything needed to start a worker for one engine; the acceleration is chosen per start.</summary>
internal sealed record TranscriptionWorkerLaunch(
    ProcessCommand Executable,
    string SocketDirectory,
    string PackageDirectory,
    string PluginDataRoot,
    string AssetDirectory,
    string SelectionId
)
{
    // Silence, not work, is measured: a slow decode or model load keeps beating. 90 s is 18 missed
    // beats, enough to ride out a long GC pause without ending a worker that is only busy.
    private static readonly TimeSpan s_defaultHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_defaultRequestInactivityTimeout = TimeSpan.FromSeconds(90);

    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the worker reports that a request is still running.</summary>
    internal TimeSpan HeartbeatInterval { get; init; } = s_defaultHeartbeatInterval;

    /// <summary>
    ///     How long a worker may stay silent during a request before it is ended;
    ///     <see cref="Timeout.InfiniteTimeSpan" /> disables the watchdog.
    /// </summary>
    internal TimeSpan RequestInactivityTimeout { get; init; } = s_defaultRequestInactivityTimeout;

    /// <summary>
    ///     Overrides the total request deadline. Null allows two minutes plus ten times the audio
    ///     duration for transcription, and ten minutes for loading. Heartbeats do not extend it.
    /// </summary>
    internal TimeSpan? RequestTimeout { get; init; }

    internal bool WatchesInactivity => RequestInactivityTimeout != Timeout.InfiniteTimeSpan;

    internal TimeSpan TimeoutFor(TranscriptionWorkerMessage request, int payloadBytes)
    {
        if (RequestTimeout is { } configured)
        {
            return configured != Timeout.InfiniteTimeSpan && configured <= TimeSpan.Zero
                ? throw new InvalidOperationException("The request timeout must be positive or infinite.")
                : configured;
        }

        return request.Command switch
        {
            TranscriptionWorkerCommands.Load => TimeSpan.FromMinutes(10),
            // The WAV is normally 16 kHz mono PCM16; counting its header as audio only adds a little.
            TranscriptionWorkerCommands.Transcribe => TimeSpan.FromSeconds(120 + 10d * payloadBytes / (16000 * 2d)),
            _ => TimeSpan.FromMinutes(1),
        };
    }
}

/// <summary>
///     The host side of one worker. The worker is a child started through the process runner and
///     connects back over a Unix socket in a private directory; the host accepts exactly one peer,
///     checks that its process id is the child's, then requires the launch token in its hello.
/// </summary>
internal sealed partial class TranscriptionWorkerProcess : ITranscriptionWorkerConnection
{
    private const int DiagnosticLines = 60;
    private const int DiagnosticLineCharacters = 1000;
    private const int SigKill = 9;

    // sun_path holds 108 bytes including the terminating NUL.
    private const int MaximumSocketPathBytes = 107;

    private readonly IPluginProcessSession _session;
    private readonly NetworkStream _stream;
    private readonly TranscriptionWorkerLaunch _launch;
    private readonly Action<PluginLogLevel, string> _log;
    private readonly ConcurrentQueue<string> _output;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _reader = Task.CompletedTask;
    private Task _outputPump = Task.CompletedTask;
    private long _nextId;
    private int _disposed;
    private bool _heartbeats;

    // Set when the host ends the worker, which the process exit only confirms later.
    private volatile bool _terminating;

    private TranscriptionWorkerProcess(
        IPluginProcessSession session,
        NetworkStream stream,
        ConcurrentQueue<string> output,
        TranscriptionWorkerLaunch launch,
        TranscriptionAccelerationPreference acceleration,
        Action<PluginLogLevel, string> log
    )
    {
        _session = session;
        _stream = stream;
        _output = output;
        _launch = launch;
        _log = log;
        Acceleration = acceleration;
        ProcessId = session.ProcessId;
    }

    public TranscriptionAccelerationPreference Acceleration { get; }
    public int ProcessId { get; }
    public bool IsAlive => !_terminating && !_ended.Task.IsCompleted && _session.IsRunning;
    public TranscriptionWorkerState? State { get; private set; }

    internal static async Task<TranscriptionWorkerProcess> StartAsync(
        IPluginProcessSupervisor processes,
        TranscriptionWorkerLaunch launch,
        TranscriptionAccelerationPreference acceleration,
        string? computeBackend,
        Action<PluginLogLevel, string> log,
        CancellationToken ct
    )
    {
        // One late beat must not end a worker; the window has to hold several.
        if (launch.WatchesInactivity && launch.RequestInactivityTimeout < launch.HeartbeatInterval * 2)
        {
            throw new ArgumentException(
                "The inactivity window must be at least twice the heartbeat interval.",
                nameof(launch)
            );
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var socketName = $"w{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.sock";
        var socketPath = Path.Join(launch.SocketDirectory, socketName);
        // A deep fallback directory can exceed the Unix socket path limit; a fresh owner-only
        // directory under the temp root then holds the socket instead.
        DirectoryInfo? shortDirectory = null;
        if (Encoding.UTF8.GetByteCount(socketPath) > MaximumSocketPathBytes)
        {
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            shortDirectory = Directory.CreateTempSubdirectory("tw-");
#pragma warning restore CA1416
            socketPath = Path.Join(shortDirectory.FullName, socketName);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        IPluginProcessSession? session = null;
        Socket? accepted = null;
        var output = new ConcurrentQueue<string>();
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);

            var arguments = new TranscriptionWorkerArguments(
                socketPath,
                Environment.ProcessId,
                launch.PackageDirectory,
                launch.PluginDataRoot,
                launch.AssetDirectory,
                launch.SelectionId,
                acceleration,
                launch.WatchesInactivity ? launch.HeartbeatInterval : null,
                computeBackend
            );
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in launch.Executable.Environment ?? new Dictionary<string, string>())
            {
                environment[name] = value;
            }

            environment[TranscriptionWorkerServer.TokenEnvironmentVariable] = token;
            var started = processes.StartSession(
                launch.Executable with
                {
                    Arguments = [.. launch.Executable.Arguments, .. arguments.ToArguments()],
                    Environment = environment,
                },
                new ProcessSessionOptions(
                    StandardOutput: ProcessSessionOutputMode.Lines,
                    StandardError: ProcessSessionOutputMode.Lines
                )
            );
            session = started.Session
                      ?? throw new InvalidOperationException(
                          "The local transcription engine could not be started: " + started.StartError
                      );
            var outputPump = CaptureOutputAsync(session, output);
            _ = EndOrphansAfterExitAsync(session);

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(launch.StartupTimeout);
            try
            {
                // A worker that exits before connecting never completes the accept.
                var accepting = listener.AcceptAsync(startup.Token).AsTask();
                if (await Task.WhenAny(accepting, session.Completion).ConfigureAwait(false) != accepting)
                {
                    throw await CrashAsync(session, output, "The local transcription engine ended while starting.")
                        .ConfigureAwait(false);
                }

                accepted = await accepting.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                session.Terminate();
                throw StartupTimeout(output);
            }

            // Only the child may speak for it, even within the private socket directory.
            if (!UnixPeerCredentials.IsProcessOfEffectiveUser(accepted, session.ProcessId))
            {
                throw new InvalidDataException("An unexpected process connected to the transcription worker socket.");
            }

            var worker = new TranscriptionWorkerProcess(
                session,
                new NetworkStream(accepted, ownsSocket: true),
                output,
                launch,
                acceleration,
                log
            ) { _outputPump = outputPump };
            accepted = null;
            session = null;
            try
            {
                await worker.ReceiveHelloAsync(token, ct).ConfigureAwait(false);
                return worker;
            }
            catch
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            listener.Dispose();
            TryDelete(socketPath);
            if (shortDirectory is not null)
            {
                TryDeleteDirectory(shortDirectory.FullName);
            }
            accepted?.Dispose();
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveHelloAsync(string token, CancellationToken ct)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(_launch.StartupTimeout);
        TranscriptionWorkerMessage hello;
        try
        {
            TranscriptionWorkerFrame frame;
            // Plugins may log while they activate, before the worker reports that it is ready.
            while (
                (frame = await TranscriptionWorkerProtocol.ReadAsync(_stream, 0, startup.Token).ConfigureAwait(false)
                         ?? throw await CrashAsync("The local transcription engine ended while starting.").ConfigureAwait(false))
                .Message.Type == TranscriptionWorkerMessageTypes.Log
            )
            {
                ForwardLog(frame.Message);
            }

            hello = frame.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill();
            throw StartupTimeout(_output);
        }
        catch (Exception ex) when (ex is IOException && !ct.IsCancellationRequested)
        {
            throw await CrashAsync("The local transcription engine ended while starting.", ex).ConfigureAwait(false);
        }

        if (
            hello.Type != TranscriptionWorkerMessageTypes.Hello
            || hello.Token is null
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hello.Token),
                Encoding.ASCII.GetBytes(token)
            )
        )
        {
            throw new InvalidDataException("The transcription worker did not identify itself.");
        }

        if (hello.ProtocolVersion != TranscriptionWorkerProtocol.Version)
        {
            throw new InvalidDataException("The transcription worker does not match this TypeWhisper version.");
        }

        // Activation errors are the plugin's own and repeat on every start; they are not crashes,
        // so they are decoded outside the transport handling above.
        if (hello.Error is { } error)
        {
            throw TranscriptionWorkerProtocol.ToException(error);
        }

        State = hello.State;
        // A worker that announces no beat is never ended for silence.
        _heartbeats = hello.HeartbeatInterval is not null;

        _reader = Task.Run(ReadAsync, CancellationToken.None);
    }

    public async Task<TranscriptionWorkerMessage> SendAsync(
        TranscriptionWorkerMessage request,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (payload.Length > TranscriptionWorkerProtocol.MaximumAudioBytes)
        {
            throw new ArgumentException("The audio is too large for the local transcription engine.", nameof(payload));
        }

        var timeout = _launch.TimeoutFor(request, payload.Length);
        using var deadline = new CancellationTokenSource(timeout);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = requestCancellation.Token;
        var id = Interlocked.Increment(ref _nextId);
        var pending = new PendingRequest();
        _pending[id] = pending;
        try
        {
            if (_ended.Task.IsCompleted)
            {
                throw await CrashAsync("The local transcription engine stopped unexpectedly.").ConfigureAwait(false);
            }

            // A worker always reads, even while it decodes, so audio that stops flowing for a whole
            // inactivity window means a frozen worker, just like silence after the upload.
            using var upload = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (_heartbeats && _launch.WatchesInactivity)
            {
                upload.CancelAfter(_launch.RequestInactivityTimeout);
            }

            try
            {
                await WriteAsync(
                    request with { Type = TranscriptionWorkerMessageTypes.Request, Id = id },
                    payload,
                    upload.Token
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A partially written frame cannot be reused. Ending the worker also releases a
                // blocked socket write, so a frozen reader cannot hold the engine's gate forever.
                _pending.TryRemove(id, out _);
                Kill();
                if (token.IsCancellationRequested)
                {
                    throw;
                }

                _log(
                    PluginLogLevel.Warning,
                    $"The transcription worker did not accept the {request.Command} request's audio for "
                    + $"{_launch.RequestInactivityTimeout.TotalSeconds:0} s and was ended."
                );
                throw new TranscriptionWorkerUnresponsiveException(
                    "The local transcription engine stopped answering and was ended.",
                    Diagnostics
                );
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw await CrashAsync("The local transcription engine stopped unexpectedly.", ex).ConfigureAwait(false);
            }

            var response = await WaitAsync(pending, id, request.Command, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (response.State is { } state)
            {
                State = state;
            }

            return response;
        }
        catch (TranscriptionWorkerCrashedException ex) when (ct.IsCancellationRequested)
        {
            // The caller gave up first; whatever the worker did meanwhile is not the answer.
            throw new OperationCanceledException("The transcription request was canceled.", ex, ct);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or TranscriptionWorkerCrashedException
            && deadline.IsCancellationRequested
            && !ct.IsCancellationRequested
        )
        {
            _pending.TryRemove(id, out _);
            Kill();
            _log(
                PluginLogLevel.Warning,
                $"The transcription worker exceeded the {request.Command} deadline of {timeout.TotalSeconds:0} s and was ended."
            );
            throw new TranscriptionWorkerRequestTimeoutException(
                "The local transcription engine took too long and was stopped. Try again or choose another model."
            );
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // Native decoding may not stop at once. After a cancellation the worker gets a short grace
    // period to answer; one that does not is ended, so cancelling never waits for a long decode.
    private async Task<TranscriptionWorkerMessage> WaitAsync(
        PendingRequest pending,
        long id,
        string? command,
        CancellationToken ct
    )
    {
        var answer = pending.Answer.Task;
        try
        {
            return await WatchAsync(pending, id, command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !answer.IsCompleted)
        {
            using var cancelWrite = new CancellationTokenSource(_launch.CancellationGrace);
            try
            {
                await WriteAsync(
                    new TranscriptionWorkerMessage { Type = TranscriptionWorkerMessageTypes.Cancel, Id = id },
                    default,
                    cancelWrite.Token
                ).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                Kill();
            }

            try
            {
                await answer.WaitAsync(_launch.CancellationGrace, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or TranscriptionWorkerCrashedException)
            {
                _log(PluginLogLevel.Info, "The transcription worker did not stop after cancellation and was ended.");
                Kill();
            }

            throw new OperationCanceledException(ct);
        }
    }

    // Heartbeats tell a frozen process from slow inference. The request token also carries the
    // total deadline, because native inference can hang while the heartbeat keeps running.
    private async Task<TranscriptionWorkerMessage> WatchAsync(
        PendingRequest pending,
        long id,
        string? command,
        CancellationToken ct
    )
    {
        var answer = pending.Answer.Task;
        if (!_heartbeats || !_launch.WatchesInactivity)
        {
            return await answer.WaitAsync(ct).ConfigureAwait(false);
        }

        var window = _launch.RequestInactivityTimeout;
        for (var remaining = window - pending.Silence; remaining > TimeSpan.Zero; remaining = window - pending.Silence)
        {
            try
            {
                return await answer.WaitAsync(remaining, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A beat may have arrived meanwhile; the loop measures the silence again.
            }
        }

        // An answer that lands on the window's last tick is still an answer.
        if (answer.IsCompleted)
        {
            return await answer.ConfigureAwait(false);
        }

        _log(
            PluginLogLevel.Warning,
            $"The transcription worker did not answer the {command} request for {window.TotalSeconds:0} s and was ended."
        );
        // The reader must not report the end as a crash of this request; the exit was the host's doing.
        _pending.TryRemove(id, out _);
        Kill();
        throw new TranscriptionWorkerUnresponsiveException(
            "The local transcription engine stopped answering and was ended.",
            Diagnostics
        );
    }

    private async Task WriteAsync(
        TranscriptionWorkerMessage message,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default
    )
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TranscriptionWorkerProtocol.WriteAsync(_stream, message, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    // A frame the worker should never send (a payload, an unreadable header) means the protocol is
    // broken; the worker is ended rather than trusted with the next request.
    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (
                await TranscriptionWorkerProtocol.ReadAsync(_stream, 0, CancellationToken.None).ConfigureAwait(false)
                    is { } frame
            )
            {
                var message = frame.Message;
                switch (message.Type)
                {
                    case TranscriptionWorkerMessageTypes.Log:
                        ForwardLog(message);
                        break;
                    // An id with no pending request is a late answer to a request that was given up.
                    case TranscriptionWorkerMessageTypes.Response
                        when _pending.TryGetValue(message.Id, out var pending):
                        pending.Answer.TrySetResult(message);
                        break;
                    case TranscriptionWorkerMessageTypes.Heartbeat
                        when _pending.TryGetValue(message.Id, out var pending):
                        pending.Touch();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            failure = ex;
            if (ex is InvalidDataException)
            {
                _log(PluginLogLevel.Warning, $"The transcription worker sent an invalid frame and was ended: {ex.Message}");
                Kill();
            }
        }

        _ended.TrySetResult();
        if (_pending.IsEmpty)
        {
            return;
        }

        var crash = await CrashAsync("The local transcription engine stopped unexpectedly.", failure)
            .ConfigureAwait(false);
        foreach (var pending in _pending.Values)
        {
            pending.Answer.TrySetException(crash);
        }
    }

    private void ForwardLog(TranscriptionWorkerMessage message)
    {
        if (message.LogMessage is { } text)
        {
            _log(message.LogLevel ?? PluginLogLevel.Info, text);
        }
    }

    private string Diagnostics => string.Join(Environment.NewLine, _output);

    // A worker that hangs while starting counts as a hang, which is not retried.
    private static TranscriptionWorkerUnresponsiveException StartupTimeout(ConcurrentQueue<string> output)
    {
        return new TranscriptionWorkerUnresponsiveException(
            "The local transcription engine did not start in time and was ended.",
            string.Join(Environment.NewLine, output)
        );
    }

    private Task<TranscriptionWorkerCrashedException> CrashAsync(string message, Exception? inner = null)
    {
        return CrashAsync(_session, _output, message, inner);
    }

    private static async Task<TranscriptionWorkerCrashedException> CrashAsync(
        IPluginProcessSession session,
        ConcurrentQueue<string> output,
        string message,
        Exception? inner = null
    )
    {
        int? exitCode = null;
        try
        {
            exitCode = (await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)).ExitCode;
        }
        catch (TimeoutException)
        {
            // Still running: the connection failed first. The caller ends it.
        }

        var code = exitCode is { } value ? $" (exit code {value})" : "";
        return new TranscriptionWorkerCrashedException(
            message + code,
            exitCode,
            string.Join(Environment.NewLine, output),
            inner
        );
    }

    // Keeps the last lines for crash reports; the rest is drained so the child never blocks on a full pipe.
    private static async Task CaptureOutputAsync(IPluginProcessSession session, ConcurrentQueue<string> output)
    {
        try
        {
            await foreach (var line in session.ReadOutputAsync().ConfigureAwait(false))
            {
                output.Enqueue(
                    line.Text.Length > DiagnosticLineCharacters ? line.Text[..DiagnosticLineCharacters] : line.Text
                );
                while (output.Count > DiagnosticLines)
                {
                    output.TryDequeue(out _);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The session ended while its output was read.
        }
    }

    private void Kill()
    {
        _terminating = true;
        _session.Terminate();
    }

    // The worker leads its own process group, so what the engine started can be ended once the worker
    // is gone. The group id can't be reused while any member lives, so a group that still has
    // members right after the exit is this worker's.
    private static async Task EndOrphansAfterExitAsync(IPluginProcessSession session)
    {
        var processGroup = session.ProcessId;
        try
        {
            await session.Completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Exited all the same; only its output drain failed.
        }

        if (ProcessGroupHasMembers(processGroup))
        {
            _ = kill(-processGroup, SigKill);
        }
    }

    private static bool ProcessGroupHasMembers(int processGroup)
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out _))
            {
                continue;
            }

            try
            {
                // Fields after the parenthesized command name: state, ppid, pgrp.
                var stat = File.ReadAllText(Path.Join(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', 4);
                if (int.TryParse(fields[2], out var group) && group == processGroup)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process ended while the list was read.
            }
        }

        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty private directory left in the temp root is harmless.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory is private; a leftover socket file accepts nobody once the listener is closed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (IsAlive && _reader != Task.CompletedTask)
        {
            // A graceful stop lets the engine exit on its own. It is short: native teardown is not awaited.
            try
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await WriteAsync(
                    new TranscriptionWorkerMessage
                    {
                        Type = TranscriptionWorkerMessageTypes.Request,
                        Id = Interlocked.Increment(ref _nextId),
                        Command = TranscriptionWorkerCommands.Shutdown,
                    },
                    default,
                    shutdown.Token
                ).ConfigureAwait(false);
                await _session.Completion.WaitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Ended below.
            }
        }

        // Reaps the child (and anything it started) whether or not it stopped on its own.
        await _session.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
            await _outputPump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Teardown must not replace the caller's outcome.
        }

        _write.Dispose();
    }

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial int kill(int pid, int signal);

    // The answer and the time of the last frame the worker sent for this request.
    private sealed class PendingRequest
    {
        private long _lastFrame = Environment.TickCount64;

        internal TaskCompletionSource<TranscriptionWorkerMessage> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TimeSpan Silence =>
            TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastFrame));

        internal void Touch()
        {
            Volatile.Write(ref _lastFrame, Environment.TickCount64);
        }
    }
}
