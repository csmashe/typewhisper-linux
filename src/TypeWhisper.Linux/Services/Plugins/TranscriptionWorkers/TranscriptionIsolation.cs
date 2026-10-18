using System.Reflection;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>
///     Decides which local engines run in a worker process and creates their host-side adapters.
///     The worker is this app's own executable in worker mode.
/// </summary>
internal sealed class TranscriptionIsolation
{
    /// <summary>Set to <c>0</c> to keep every engine in the app process, for diagnosis.</summary>
    private const string EnvironmentVariable = "TYPEWHISPER_TRANSCRIPTION_WORKER";

    // Native whisper.cpp and sherpa-onnx inference; a crash there must not end the app.
    private static readonly HashSet<string> s_defaultPluginIds =
    [
        "com.typewhisper.whisper-cpp",
        "com.typewhisper.sherpa-onnx",
    ];

    private readonly ProcessCommand? _executable;
    private readonly IReadOnlySet<string> _pluginIds;
    private readonly Func<string> _socketDirectory;

    internal TranscriptionIsolation(
        ProcessCommand? executable,
        IReadOnlySet<string> pluginIds,
        Func<string> socketDirectory
    )
    {
        _executable = executable;
        _pluginIds = pluginIds;
        _socketDirectory = socketDirectory;
    }

    internal bool IsEnabled => _executable is not null;

    /// <summary>
    ///     The isolation the app uses: Whisper and Sherpa in workers unless switched off. Off when
    ///     another program hosts this assembly, such as a test runner, which has no worker mode.
    /// </summary>
    internal static TranscriptionIsolation CreateDefault()
    {
        var entry = Assembly.GetEntryAssembly();
        var executable = Environment.GetEnvironmentVariable(EnvironmentVariable) == "0" || entry != typeof(Program).Assembly
            ? null
            : ResolveExecutable(Environment.ProcessPath, entry.Location);
        return new TranscriptionIsolation(executable, s_defaultPluginIds, SocketPathResolver.ResolveSocketDirectory);
    }

    /// <summary>
    ///     The command that starts this app in worker mode: the app executable itself, or the
    ///     <c>dotnet</c> host with the app's assembly in a development run. Null when neither is known.
    /// </summary>
    internal static ProcessCommand? ResolveExecutable(string? processPath, string? entryAssembly)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal))
        {
            return new ProcessCommand(processPath, [TranscriptionWorkerServer.Argument]);
        }

        return string.IsNullOrEmpty(entryAssembly)
            ? null
            : new ProcessCommand(processPath, [entryAssembly, TranscriptionWorkerServer.Argument]);
    }

    /// <summary>
    ///     Returns an isolated adapter for <paramref name="engine" />, or null when it stays in process.
    ///     <paramref name="settings" /> gives the plugin's saved settings as text; a worker started with
    ///     others is replaced.
    /// </summary>
    internal IsolatedTranscriptionEngine? TryIsolate(
        ITranscriptionEngineRole engine,
        string pluginId,
        string packageDirectory,
        string pluginDataRoot,
        IPluginHostServices services,
        Func<string>? settings = null
    )
    {
        // Streaming sessions are callback objects that cannot cross the process boundary.
        if (_executable is not { } executable || !_pluginIds.Contains(pluginId) || engine.SupportsStreaming)
        {
            return null;
        }

        var selectionId = engine.GetTranscriptionSelectionId();
        return new IsolatedTranscriptionEngine(
            engine,
            async (acceleration, computeBackend, ct) =>
            {
                // Resolved per start: the runtime directory can vanish with the login session.
                var launch = new TranscriptionWorkerLaunch(
                    executable,
                    _socketDirectory(),
                    Path.GetFullPath(packageDirectory),
                    pluginDataRoot,
                    services.PluginAssetDirectory,
                    selectionId
                );
                return await TranscriptionWorkerProcess
                    .StartAsync(services.Processes, launch, acceleration, computeBackend, services.Log, ct)
                    .ConfigureAwait(false);
            },
            services.Log,
            // The host refreshes what it shows about the engine, such as a switch to the CPU.
            services.NotifyCapabilitiesChanged,
            settings: settings
        );
    }
}
