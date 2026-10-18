using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>
///     Runs one local transcription engine in a separate process. The host starts its own
///     executable with <see cref="Argument" /> first, so a native crash in the engine ends only
///     this process.
/// </summary>
internal static partial class TranscriptionWorkerServer
{
    /// <summary>The first command-line argument that selects worker mode.</summary>
    internal const string Argument = "--transcription-worker";

    /// <summary>Carries the launch token, which is kept out of the world-readable command line.</summary>
    internal const string TokenEnvironmentVariable = "TYPEWHISPER_TRANSCRIPTION_WORKER_TOKEN";

    internal const int ExitInvalidArguments = 2;
    internal const int ExitParentGone = 3;
    private const int ExitEngineUnavailable = 4;
    private const int ExitConnectionFailed = 5;
    private const int ExitHostDisconnected = 6;

    private const int SigKill = 9;

    private static readonly TimeSpan s_parentPollInterval = TimeSpan.FromMilliseconds(250);
    private static bool s_ownsProcessGroup;

    internal static bool IsWorkerInvocation(IReadOnlyList<string> args)
    {
        return args.Count > 0 && args[0] == Argument;
    }

    /// <summary>Serves requests until the host asks it to stop, disconnects or exits; returns the process exit code.</summary>
    internal static int Run(string[] args)
    {
        if (!TranscriptionWorkerArguments.TryParse(args, out var options))
        {
            Console.Error.WriteLine("typewhisper: invalid transcription worker arguments.");
            return ExitInvalidArguments;
        }

        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        // Not inherited by anything the engine starts.
        Environment.SetEnvironmentVariable(TokenEnvironmentVariable, null);
        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("typewhisper: the transcription worker has no launch token.");
            return ExitInvalidArguments;
        }

        if (!WatchParent(options.ParentProcessId))
        {
            return ExitParentGone;
        }

        // Anything the engine starts joins this group, so ending the group ends it too, even once
        // this process is gone and the host can no longer walk its tree.
        s_ownsProcessGroup = setpgid(0, 0) == 0;

        return RunAsync(options, token).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(TranscriptionWorkerArguments options, string token)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await socket
                .ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), connectTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            await Console.Error.WriteLineAsync($"typewhisper: transcription worker could not connect: {ex.Message}")
                .ConfigureAwait(false);
            return ExitConnectionFailed;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: true);
        var connection = new Connection(stream);
        var services = new TranscriptionWorkerHostServices(
            options.PackageDirectory,
            options.PluginDataRoot,
            options.AssetDirectory,
            (level, message) => connection.Post(new TranscriptionWorkerMessage
            {
                Type = TranscriptionWorkerMessageTypes.Log, LogLevel = level, LogMessage = message,
            })
        );

        ITranscriptionEngineRole engine;
        try
        {
            // The plugin stays loaded until the process ends. Exiting releases native memory
            // more reliably than unloading a context that native code may still reference.
            engine = await LoadEngineAsync(options, services).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await connection.SendAsync(new TranscriptionWorkerMessage
            {
                Type = TranscriptionWorkerMessageTypes.Hello,
                ProtocolVersion = TranscriptionWorkerProtocol.Version,
                ProcessId = Environment.ProcessId,
                Token = token,
                Error = TranscriptionWorkerProtocol.ToError(ex),
            }).ConfigureAwait(false);
            return ExitEngineUnavailable;
        }

        var session = new Session(engine, connection, options.HeartbeatInterval);
        // Announcing the beat lets the host arm its inactivity watchdog only for a worker that beats.
        await connection.SendAsync(new TranscriptionWorkerMessage
        {
            Type = TranscriptionWorkerMessageTypes.Hello,
            ProtocolVersion = TranscriptionWorkerProtocol.Version,
            ProcessId = Environment.ProcessId,
            Token = token,
            State = session.State(),
            HeartbeatInterval = options.HeartbeatInterval,
        }).ConfigureAwait(false);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private static async Task<ITranscriptionEngineRole> LoadEngineAsync(
        TranscriptionWorkerArguments options,
        TranscriptionWorkerHostServices services
    )
    {
        var loader = new PluginLoader(options.PluginDataRoot);
        var loaded = loader.LoadPlugin(options.PackageDirectory)
                     ?? throw new InvalidOperationException(
                         loader.LastLoadFailures is [.., var failure]
                             ? failure.Message
                             : "The plugin package could not be loaded."
                     );
        services.Bind(loaded.Manifest.Id);
        await loaded.Instance.ActivateAsync(services).ConfigureAwait(false);
        var engine = FindEngine(loaded.Instance, options.SelectionId)
                     ?? throw new NotSupportedException(
                         "The plugin does not provide this local transcription engine."
                     );
        if (engine.SupportsStreaming)
        {
            throw new NotSupportedException("Streaming engines cannot run in a transcription worker.");
        }

        if (engine.AccelerationPreference != options.Acceleration)
        {
            engine.SetAccelerationPreference(options.Acceleration);
        }

        // An explicit backend choice is applied after the preference, as it was in the host.
        if (options.ComputeBackend is { } backend)
        {
            await engine.ConfigureComputeBackendAsync(backend).ConfigureAwait(false);
        }

        return engine;
    }

    private static ITranscriptionEngineRole? FindEngine(ITypeWhisperPlugin plugin, string selectionId)
    {
        IEnumerable<ITranscriptionEngineRole> engines = plugin is ITranscriptionEngineRole direct
            ? [direct]
            : [];
        if (plugin is IAdditionalTranscriptionEnginesProvider additional)
        {
            engines = engines.Concat(additional.AdditionalTranscriptionEngines);
        }

        return engines.FirstOrDefault(engine => string.Equals(
            engine.GetTranscriptionSelectionId(),
            selectionId,
            StringComparison.Ordinal
        ));
    }

    // A changed parent id means the host died. The watch has its own thread so a hung engine call
    // can't keep the process alive.
    private static bool WatchParent(int parentProcessId)
    {
        if (getppid() != parentProcessId)
        {
            return false;
        }

        var watcher = new Thread(() =>
        {
            while (getppid() == parentProcessId)
            {
                Thread.Sleep(s_parentPollInterval);
            }

            ExitImmediately(ExitParentGone);
        })
        {
            IsBackground = true, Name = "TranscriptionWorkerParentWatch",
        };
        watcher.Start();
        return true;
    }

    // Skips managed shutdown, which could wait on a thread stuck in native code, and ends the
    // engine's own processes. Never signals a group this process doesn't lead: that is the host's.
    private static void ExitImmediately(int exitCode)
    {
        if (s_ownsProcessGroup)
        {
            _ = kill(0, SigKill);
        }

        _exit(exitCode);
    }

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial int setpgid(int pid, int pgid);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial int kill(int pid, int signal);

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial int getppid();

    // ReSharper disable once InconsistentNaming -- native libc function name.
    [LibraryImport("libc")]
    private static partial void _exit(int status);

    private sealed class Connection(Stream stream)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        internal Stream Stream => stream;

        internal async Task SendAsync(
            TranscriptionWorkerMessage message,
            ReadOnlyMemory<byte> payload = default
        )
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await TranscriptionWorkerProtocol
                    .WriteAsync(stream, message, payload, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        // Logging must not block the engine thread that produced it, nor fail its operation.
        internal void Post(TranscriptionWorkerMessage message)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(message).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
                {
                    // The host is gone or the message is unsendable; the log line is dropped.
                }
            });
        }
    }

    private sealed class Session(
        ITranscriptionEngineRole engine,
        Connection connection,
        TimeSpan? heartbeatInterval
    )
    {
        private readonly Channel<TranscriptionWorkerFrame> _requests =
            Channel.CreateUnbounded<TranscriptionWorkerFrame>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
            );

        private readonly Lock _sync = new();
        private readonly HashSet<long> _canceledBeforeStart = [];
        private long _current;
        private long _lastStarted;
        private CancellationTokenSource? _currentCancellation;
        private string? _loadedModelId;

        internal TranscriptionWorkerState State()
        {
            return new TranscriptionWorkerState(
                engine.SelectedModelId,
                _loadedModelId,
                engine.AccelerationStatus
            );
        }

        internal async Task<int> RunAsync()
        {
            _ = ReadAsync();
            await foreach (var (message, payload) in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (message.Command == TranscriptionWorkerCommands.Shutdown)
                {
                    await connection.SendAsync(Response(message.Id, error: null, result: null))
                        .ConfigureAwait(false);
                    return 0;
                }

                using var cancellation = new CancellationTokenSource();
                lock (_sync)
                {
                    _current = _lastStarted = message.Id;
                    _currentCancellation = cancellation;
                    if (_canceledBeforeStart.Remove(message.Id))
                    {
                        cancellation.Cancel();
                    }
                }

                TranscriptionWorkerResult? result = null;
                TranscriptionWorkerError? error = null;
                using var beating = new CancellationTokenSource();
                var heartbeat = HeartbeatAsync(message.Id, beating.Token);
                try
                {
                    result = await ExecuteAsync(message, payload, cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    error = TranscriptionWorkerProtocol.ToError(ex);
                }
                finally
                {
                    lock (_sync)
                    {
                        _current = 0;
                        _currentCancellation = null;
                    }

                    await beating.CancelAsync().ConfigureAwait(false);
                }

                // The response is the last frame for this id.
                await heartbeat.ConfigureAwait(false);
                try
                {
                    await connection.SendAsync(Response(message.Id, error, result)).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    await connection
                        .SendAsync(Response(message.Id, TranscriptionWorkerProtocol.ToError(ex), null))
                        .ConfigureAwait(false);
                }
            }

            // The host closed the connection without a shutdown request.
            return ExitHostDisconnected;
        }

        private TranscriptionWorkerMessage Response(
            long id,
            TranscriptionWorkerError? error,
            TranscriptionWorkerResult? result
        )
        {
            return new TranscriptionWorkerMessage
            {
                Type = TranscriptionWorkerMessageTypes.Response,
                Id = id,
                Error = error,
                Result = result,
                State = State(),
            };
        }

        // Shows the runtime still schedules work. A stall inside the engine call is invisible here,
        // which is why the host also enforces a total deadline.
        private async Task HeartbeatAsync(long id, CancellationToken ct)
        {
            if (heartbeatInterval is not { } interval)
            {
                return;
            }

            try
            {
                while (true)
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    await connection.SendAsync(new TranscriptionWorkerMessage
                    {
                        Type = TranscriptionWorkerMessageTypes.Heartbeat, Id = id,
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The request finished.
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                // The host is gone; the reader ends the process.
            }
        }

        // Reads on its own so a cancellation reaches a decoding request. Once the host is gone or sent
        // garbage, no answer can be delivered, so the process ends without finishing the decode.
        private async Task ReadAsync()
        {
            var shutdownRequested = false;
            try
            {
                while (
                    await TranscriptionWorkerProtocol
                        .ReadAsync(connection.Stream, TranscriptionWorkerProtocol.MaximumAudioBytes, CancellationToken.None)
                        .ConfigureAwait(false) is { } frame
                )
                {
                    var message = frame.Message;
                    switch (message.Type)
                    {
                        case TranscriptionWorkerMessageTypes.Cancel:
                            lock (_sync)
                            {
                                if (_current == message.Id)
                                {
                                    _currentCancellation?.Cancel();
                                }
                                // Request ids only grow, so a late cancellation of a finished request is ignored.
                                else if (message.Id > _lastStarted)
                                {
                                    _canceledBeforeStart.Add(message.Id);
                                }
                            }

                            break;
                        case TranscriptionWorkerMessageTypes.Request:
                            shutdownRequested |= message.Command == TranscriptionWorkerCommands.Shutdown;
                            _requests.Writer.TryWrite(frame);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                await Console.Error.WriteLineAsync($"typewhisper: transcription worker connection failed: {ex.Message}")
                    .ConfigureAwait(false);
            }

            if (!shutdownRequested)
            {
                ExitImmediately(ExitHostDisconnected);
            }

            _requests.Writer.TryComplete();
        }

        private async Task<TranscriptionWorkerResult?> ExecuteAsync(
            TranscriptionWorkerMessage message,
            byte[] payload,
            CancellationToken ct
        )
        {
            switch (message.Command)
            {
                case TranscriptionWorkerCommands.Load:
                    var modelId = message.ModelId ?? throw new ArgumentException("A model is required.");
                    _loadedModelId = null;
                    await engine.LoadModelAsync(modelId, ct).ConfigureAwait(false);
                    _loadedModelId = modelId;
                    return null;
                case TranscriptionWorkerCommands.Transcribe:
                    await EnsureModelAsync(message.ModelId, ct).ConfigureAwait(false);
                    var result = message.LanguageHints is { } hints
                        ? await engine
                            .TranscribeWithLanguageHintsAsync(payload, hints, message.Translate, message.Prompt, ct)
                            .ConfigureAwait(false)
                        : await engine
                            .TranscribeAsync(payload, message.Language, message.Translate, message.Prompt, ct)
                            .ConfigureAwait(false);
                    return TranscriptionWorkerProtocol.ToResult(result);
                default:
                    throw new NotSupportedException("Unknown transcription worker command.");
            }
        }

        // A restarted worker starts without a model. Each request names the host's selection, so
        // the worker loads and selects exactly that model; engines without one report their own error.
        private async Task EnsureModelAsync(string? modelId, CancellationToken ct)
        {
            if (modelId is null)
            {
                return;
            }

            if (_loadedModelId != modelId)
            {
                _loadedModelId = null;
                await engine.LoadModelAsync(modelId, ct).ConfigureAwait(false);
                _loadedModelId = modelId;
            }

            if (engine.SelectedModelId != modelId)
            {
                engine.SelectModel(modelId);
            }
        }
    }
}

/// <summary>
///     The worker's command line. Paths and ids only: the launch token travels in the environment,
///     which other users cannot read, unlike <c>/proc/&lt;pid&gt;/cmdline</c>.
/// </summary>
internal sealed record TranscriptionWorkerArguments(
    string SocketPath,
    int ParentProcessId,
    string PackageDirectory,
    string PluginDataRoot,
    string AssetDirectory,
    string SelectionId,
    TranscriptionAccelerationPreference Acceleration,
    TimeSpan? HeartbeatInterval = null,
    string? ComputeBackend = null
)
{
    internal IEnumerable<string> ToArguments()
    {
        string[] arguments =
        [
            "--socket", SocketPath,
            "--parent-pid", ParentProcessId.ToString(CultureInfo.InvariantCulture),
            "--package", PackageDirectory,
            "--plugin-data-root", PluginDataRoot,
            "--assets", AssetDirectory,
            "--selection", SelectionId,
            "--acceleration", Acceleration.ToString(),
        ];
        if (HeartbeatInterval is { } interval)
        {
            arguments = [.. arguments, "--heartbeat", interval.ToString("c", CultureInfo.InvariantCulture)];
        }

        return ComputeBackend is null ? arguments : [.. arguments, "--compute-backend", ComputeBackend];
    }

    internal static bool TryParse(IReadOnlyList<string> args, out TranscriptionWorkerArguments options)
    {
        options = null!;
        if (!TranscriptionWorkerServer.IsWorkerInvocation(args) || args.Count % 2 != 1)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (!values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        try
        {
            var parentProcessId = int.Parse(Value("--parent-pid"), NumberStyles.None, CultureInfo.InvariantCulture);
            var acceleration = Enum.Parse<TranscriptionAccelerationPreference>(Value("--acceleration"));
            TimeSpan? heartbeat = values.ContainsKey("--heartbeat")
                ? TimeSpan.ParseExact(Value("--heartbeat"), "c", CultureInfo.InvariantCulture)
                : null;
            var computeBackend = values.ContainsKey("--compute-backend") ? Value("--compute-backend") : null;
            if (
                parentProcessId <= 0
                || !Enum.IsDefined(acceleration)
                || heartbeat is { } beat && beat <= TimeSpan.Zero
                || !PluginSelectionExtensions.IsValidSelectionId(Value("--selection"))
                || computeBackend is not null && !PluginSelectionExtensions.IsValidSelectionId(computeBackend)
            )
            {
                return false;
            }

            options = new TranscriptionWorkerArguments(
                Path.GetFullPath(Value("--socket")),
                parentProcessId,
                Path.GetFullPath(Value("--package")),
                Path.GetFullPath(Value("--plugin-data-root")),
                Path.GetFullPath(Value("--assets")),
                Value("--selection"),
                acceleration,
                heartbeat,
                computeBackend
            );
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }

        string Value(string name)
        {
            return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new FormatException(name);
        }
    }
}
