using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.ProcessTestChild;
using TypeWhisper.TranscriptionWorkerProbe;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Starts real workers through the app's own executable, as a package does, with a probe engine
///     that crashes, hangs or fails on request.
/// </summary>
public sealed class TranscriptionWorkerTests : IAsyncLifetime
{
    private static readonly byte[] s_audio = [1, 2, 3, 4];

    // Short to stay under the 108-byte Unix socket path limit.
    private readonly string _root = Path.Join(Path.GetTempPath(), "tww-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _logs = [];
    private readonly ProcessRunner _runner = new();
    private readonly List<IsolatedTranscriptionEngine> _engines = [];
    private WorkerProbePlugin _inner = null!;

    private string PackageDirectory => Path.Join(_root, "package");
    private string PluginDataRoot => Path.Join(_root, "data");
    private string SocketDirectory => Path.Join(_root, "s");
    private string SettingsPath => Path.Join(PluginDataRoot, WorkerProbePlugin.Id, "settings.json");

    private static string AppExecutable => Path.Join(AppContext.BaseDirectory, "typewhisper");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(PackageDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        Directory.CreateDirectory(
            SocketDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
#pragma warning restore CA1416
        var probeAssembly = typeof(WorkerProbePlugin).Assembly.Location;
        File.Copy(probeAssembly, Path.Join(PackageDirectory, Path.GetFileName(probeAssembly)));
        File.WriteAllText(
            Path.Join(PackageDirectory, PluginManifest.FileName),
            JsonSerializer.Serialize(new PluginManifest
            {
                Id = WorkerProbePlugin.Id,
                Name = "Worker probe",
                Version = "1.0.0",
                IsLocal = true,
                AssemblyName = Path.GetFileName(probeAssembly),
                PluginClass = typeof(WorkerProbePlugin).FullName!,
            })
        );
        // The host-side instance only provides metadata and the selection; it never transcribes.
        _inner = new WorkerProbePlugin();
        _inner.SelectModel("small");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            await engine.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private TranscriptionWorkerLaunch Launch(ProcessCommand? executable = null)
    {
        return new TranscriptionWorkerLaunch(
            executable ?? new ProcessCommand(AppExecutable, [TranscriptionWorkerServer.Argument]),
            SocketDirectory,
            PackageDirectory,
            PluginDataRoot,
            Path.Join(_root, "assets"),
            WorkerProbePlugin.Id
        );
    }

    private IsolatedTranscriptionEngine Engine(TranscriptionWorkerLaunch? launch = null)
    {
        var engine = IsolatedTranscriptionEngine.Create(_inner, _runner, launch ?? Launch(), Log);
        _engines.Add(engine);
        return engine;
    }

    private void Log(PluginLogLevel level, string message)
    {
        lock (_logs)
        {
            _logs.Add(message);
        }
    }

    private string[] Logs()
    {
        lock (_logs)
        {
            return [.. _logs];
        }
    }

    private static Dictionary<string, string> Fields(PluginTranscriptionResult result)
    {
        return result.Text.Split(';').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
    }

    private static bool IsRunning(int processId)
    {
        // A zombie still has a /proc entry; only a live process counts.
        var status = Path.Join("/proc", processId.ToString(CultureInfo.InvariantCulture), "stat");
        try
        {
            var stat = File.ReadAllText(status);
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WaitUntilGoneAsync(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (IsRunning(processId))
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Process {processId} is still running.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task TranscribesInAWorkerWithTheHostSelectionAndFullResult()
    {
        var engine = Engine();

        var result = await engine.TranscribeAsync(s_audio, "en", true, "terms", CancellationToken.None);

        var fields = Fields(result);
        Assert.NotEqual(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), fields["pid"]);
        Assert.Equal(engine.WorkerProcessId?.ToString(CultureInfo.InvariantCulture), fields["pid"]);
        Assert.Equal("small", fields["model"]);
        Assert.Equal("small", fields["selected"]);
        Assert.Equal("4", fields["length"]);
        Assert.Equal("True", fields["translate"]);
        Assert.Equal("terms", fields["prompt"]);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds);
        Assert.Equal(0.25f, result.NoSpeechProbability);
        Assert.Equal(new VocabularyTokenTiming("probe", 0.25, 0.5), Assert.Single(result.TokenTimings));
        Assert.Equal(
            new PluginTranscriptionSegment("probe", 0, 1) { NoSpeechProbability = 0.25f },
            Assert.Single(result.Segments)
        );
        // Logs travel separately from answers and may arrive after them.
        await WaitForLogAsync("activated in " + fields["pid"]);
        await WaitForLogAsync("loaded small in " + fields["pid"]);

        var hinted = await engine.TranscribeWithLanguageHintsAsync(s_audio, ["de", "en"], false, null, CancellationToken.None);
        Assert.Equal("hints:de,en", Fields(hinted)["language"]);
        var streamed = await engine.TranscribeStreamingWithLanguageHintsAsync(
            s_audio, ["de", "en"], false, null, _ => true, CancellationToken.None);
        Assert.Equal("hints:de,en", Fields(streamed)["language"]);
        Assert.Equal(fields["pid"], Fields(hinted)["pid"]);
    }

    [Fact]
    public async Task TheWorkerReadsSettingsOnceAndNeverWritesThem()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-setting": "from-host", "secret:token": "sealed" }""");
        var before = await File.ReadAllBytesAsync(SettingsPath);
        var engine = Engine();

        var first = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        var second = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);

        Assert.Equal("from-host", Fields(first)["setting"]);
        // The worker's own write stays in its memory.
        Assert.Equal("written-by-worker", Fields(second)["setting"]);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task EngineErrorsKeepTheirKindAndTheWorker()
    {
        var engine = Engine();
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        var processId = engine.WorkerProcessId;

        var refused = await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.TranscribeAsync(s_audio, "fail", false, null, CancellationToken.None));
        Assert.Equal("The probe refused this audio.", refused.Message);

        _inner.SelectModel("broken");
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        Assert.Equal(processId, engine.WorkerProcessId);
    }

    [Fact]
    public async Task ActivationFailureIsThePluginsErrorNotACrash()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-fail-activation": "refuse" }""");
        var engine = Engine();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadModelAsync("small", CancellationToken.None));

        Assert.Equal("The probe refused to activate.", error.Message);
        Assert.Null(engine.WorkerProcessId);

        // An I/O kind must not be mistaken for the connection failing while the worker starts.
        await File.WriteAllTextAsync(SettingsPath, """{ "probe-fail-activation": "missing" }""");
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LoadModelAsync("small", CancellationToken.None));
        Assert.Equal("The probe runtime is missing.", missing.Message);
    }

    [Fact]
    public async Task ANativeCrashEndsOnlyTheWorkerAndTheNextRequestStartsANewOne()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var first = engine.WorkerProcessId!.Value;

        var crash = await Assert.ThrowsAsync<TranscriptionWorkerCrashedException>(() =>
            engine.TranscribeAsync(s_audio, "crash", false, null, CancellationToken.None));

        Assert.Equal(128 + 11, crash.ExitCode); // SIGSEGV
        Assert.Null(engine.WorkerProcessId);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.NotEqual(first.ToString(CultureInfo.InvariantCulture), Fields(result)["pid"]);
        Assert.Equal("small", Fields(result)["model"]);
    }

    [Fact]
    public async Task AHungCallWithAWorkingHeartbeatHitsTheTotalDeadline()
    {
        var engine = Engine(Launch() with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), RequestTimeout = TimeSpan.FromSeconds(2),
        });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TranscriptionWorkerRequestTimeoutException>(() =>
            engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15));
        await WaitUntilGoneAsync(processId);
        Assert.Single(Logs(), line => line.Contains("exceeded the transcribe deadline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFrozenWorkerIsEndedAfterTheInactivityWindow()
    {
        var engine = Engine(Launch() with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            RequestInactivityTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = Timeout.InfiniteTimeSpan,
        });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;

        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            engine.TranscribeAsync(s_audio, "freeze", false, null, CancellationToken.None));

        await WaitUntilGoneAsync(processId);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task CancellingAHungRequestEndsTheWorkerWithinTheGrace()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "hang", false, null, cancellation.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        await WaitUntilGoneAsync(processId);
    }

    [Fact]
    public async Task ACancellationTheEngineHonoursKeepsTheWorker()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "slow", false, null, cancellation.Token));

        Assert.Equal(processId, engine.WorkerProcessId);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal(processId?.ToString(CultureInfo.InvariantCulture), Fields(result)["pid"]);
    }

    [Fact]
    public async Task UnloadingStopsARunningRequestAndTheWorker()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        var hung = engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None);
        await Task.Delay(300);

        await engine.UnloadModelAsync();

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => hung);
        Assert.Contains("stopped before it finished", stopped.Message, StringComparison.Ordinal);
        await WaitUntilGoneAsync(processId);
        Assert.Null(engine.WorkerProcessId);
        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal("small", Fields(result)["model"]);
    }

    [Fact]
    public async Task UnloadingAlsoStopsRequestsQueuedBehindTheRunningOne()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var running = engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None);
        await Task.Delay(300);
        var queued = engine.TranscribeAsync(s_audio, "hang", false, null, CancellationToken.None);
        var elapsed = Stopwatch.StartNew();

        await engine.UnloadModelAsync();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Contains("stopped before it finished", stopped.Message, StringComparison.Ordinal);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public void LanguageSelectionSupportComesFromTheInProcessEngine()
    {
        var inner = new Moq.Mock<ITranscriptionEngineRole>();
        inner.As<ITranscriptionLanguageSelectionCapabilities>()
            .SetupGet(engine => engine.AutomaticDetectionSupport)
            .Returns(LanguageSelectionSupport.Supported);
        inner.As<ITranscriptionLanguageSelectionCapabilities>()
            .SetupGet(engine => engine.ExplicitSelectionSupport)
            .Returns(LanguageSelectionSupport.Unsupported);

        var wrapped = IsolatedTranscriptionEngine.Create(inner.Object, _runner, Launch(), Log);
        var unknown = Engine();

        Assert.Equal(LanguageSelectionSupport.Supported, wrapped.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Unsupported, wrapped.ExplicitSelectionSupport);
        // The host then drops an explicit language for an auto-detect-only model, as it does in process.
        Assert.True(wrapped.ResolveEffectiveSelection(LanguageSelection.Explicit("de")).IsAutomatic);
        Assert.Equal(LanguageSelectionSupport.Unknown, unknown.ExplicitSelectionSupport);
    }

    [Fact]
    public async Task DeletingAModelOnlyEndsTheWorkerThatLoadedIt()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId;

        await engine.DeleteModelAsync("large", CancellationToken.None);
        Assert.Equal(processId, engine.WorkerProcessId);

        await engine.DeleteModelAsync("small", CancellationToken.None);
        Assert.Null(engine.WorkerProcessId);
    }

    [Fact]
    public async Task AnAccelerationChangeStartsAWorkerWithTheNewPreference()
    {
        var engine = Engine();
        var first = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        var second = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Equal("Auto", first["preference"]);
        Assert.Equal("Cpu", second["preference"]);
        Assert.NotEqual(first["pid"], second["pid"]);
        Assert.False(IsRunning(int.Parse(first["pid"], CultureInfo.InvariantCulture)));
        Assert.Equal($"Probe Cpu in {second["pid"]}", engine.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task AnExplicitComputeBackendReachesEveryNewWorkerUntilThePreferenceChanges()
    {
        var engine = Engine();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        await engine.ConfigureComputeBackendAsync("cuda");
        var configured = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        // The same preference again still supersedes the explicit backend, so the worker is replaced.
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        var superseded = Fields(await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Equal("cuda", configured["backend"]);
        Assert.Equal("", superseded["backend"]);
        Assert.NotEqual(configured["pid"], superseded["pid"]);
    }

    [Fact]
    public async Task AWorkerThatStopsReadingAudioIsEndedAfterTheInactivityWindow()
    {
        var engine = Engine(Launch() with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            RequestInactivityTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = Timeout.InfiniteTimeSpan,
        });
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;
        await _runner.RunOneShotAsync(
            new ProcessCommand("kill", ["-STOP", processId.ToString(CultureInfo.InvariantCulture)]),
            new ProcessOneShotOptions(TimeSpan.FromSeconds(10))
        );

        // Far more than a socket buffer holds, so the upload itself blocks.
        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            engine.TranscribeAsync(new byte[32 << 20], null, false, null, CancellationToken.None));

        await WaitUntilGoneAsync(processId);
        Assert.Contains(Logs(), line => line.Contains("did not accept the transcribe request's audio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndingAWorkerAlsoEndsTheProcessesItStarted()
    {
        var engine = Engine(Launch() with { CancellationGrace = TimeSpan.FromMilliseconds(500) });
        await engine.LoadModelAsync("small", CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, "spawn-and-hang", false, null, cancellation.Token));

        await WaitUntilGoneAsync(await WaitForSpawnedAsync());
    }

    [Fact]
    public async Task DisposingEndsTheWorkerAndRefusesLaterRequests()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var processId = engine.WorkerProcessId!.Value;

        await engine.DisposeAsync();

        await WaitUntilGoneAsync(processId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task AWorkerThatExitsBeforeConnectingIsReportedWithItsExitCode()
    {
        var engine = Engine(Launch(FakeWorker("exit-before-connect")));

        var crash = await Assert.ThrowsAsync<TranscriptionWorkerCrashedException>(() =>
            engine.LoadModelAsync("small", CancellationToken.None));

        Assert.Equal(7, crash.ExitCode);
    }

    [Fact]
    public async Task APeerWithoutTheLaunchTokenIsRejected()
    {
        var engine = Engine(Launch(FakeWorker("wrong-token")));

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.LoadModelAsync("small", CancellationToken.None));
        Assert.Null(engine.WorkerProcessId);
    }

    [Theory]
    [InlineData("oversized-header")]
    [InlineData("garbage-header")]
    [InlineData("payload")]
    public async Task AFrameAWorkerMustNotSendEndsIt(string behavior)
    {
        var engine = Engine(Launch(FakeWorker(behavior)));

        await Assert.ThrowsAsync<TranscriptionWorkerCrashedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Null(engine.WorkerProcessId);
        Assert.Contains(Logs(), line => line.Contains("invalid frame", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnswerForAnotherRequestIsIgnored()
    {
        var engine = Engine(Launch(FakeWorker("stale-then-answer")));

        var result = await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);

        Assert.Equal("fake", result.Text);
    }

    [Fact]
    public async Task AWorkerAndWhatItStartedEndWhenItsHostDisconnectsOrSendsGarbage()
    {
        foreach (var disconnect in new[] { true, false })
        {
            await using var raw = await RawWorker.StartAsync(this, Environment.ProcessId);
            var spawned = await raw.SpawnAndHangAsync();
            if (disconnect)
            {
                raw.Connection.Dispose();
            }
            else
            {
                await raw.Stream.WriteAsync(new byte[] { 255, 255, 255, 127, 0, 0, 0, 0 });
            }

            await raw.Session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilGoneAsync(spawned);
        }
    }

    [Fact]
    public async Task AWorkerAndWhatItStartedEndWhenItsParentExits()
    {
        // The worker's parent is a shell that exits once the worker connected; the worker is then
        // re-parented and must notice on its own while a request hangs and the socket stays open.
        await using var raw = await RawWorker.StartAsync(this, parentProcessId: null);
        var spawned = await raw.SpawnAndHangAsync();

        raw.ReleaseParent();

        await WaitUntilGoneAsync(raw.WorkerProcessId);
        await WaitUntilGoneAsync(spawned);
    }

    [Fact]
    public async Task ACrashedWorkersProcessesEndWithIt()
    {
        var engine = Engine();
        await engine.LoadModelAsync("small", CancellationToken.None);
        var worker = engine.WorkerProcessId!.Value;
        var request = engine.TranscribeAsync(s_audio, "spawn-and-hang", false, null, CancellationToken.None);
        var spawned = await WaitForSpawnedAsync();

        // Killed from outside, the worker leaves no tree for the host to walk.
        Process.GetProcessById(worker).Kill();

        await Assert.ThrowsAsync<TranscriptionWorkerCrashedException>(() => request);
        await WaitUntilGoneAsync(spawned);
    }

    [Fact]
    public async Task AnIdleWorkersProcessesEndWhenItCrashes()
    {
        var engine = Engine();
        await engine.TranscribeAsync(s_audio, "spawn", false, null, CancellationToken.None);
        var spawned = await WaitForSpawnedAsync();

        // Nothing calls the engine afterwards; the exit alone must end what the worker started.
        Process.GetProcessById(engine.WorkerProcessId!.Value).Kill();

        await WaitUntilGoneAsync(spawned);
    }

    private async Task WaitForLogAsync(string expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!Logs().Contains(expected))
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Log line '{expected}' did not arrive.");
            await Task.Delay(50);
        }
    }

    private async Task<int> WaitForSpawnedAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var spawned = Logs().FirstOrDefault(line => line.StartsWith("spawned ", StringComparison.Ordinal));
            if (spawned is not null)
            {
                return int.Parse(spawned["spawned ".Length..], CultureInfo.InvariantCulture);
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "The probe did not start its process.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task AWorkerStartedForAnotherParentExitsAtOnce()
    {
        var result = await _runner.RunOneShotAsync(
            new ProcessCommand(
                AppExecutable,
                [
                    .. Arguments(Path.Join(SocketDirectory, "unused.sock"), parentProcessId: 1),
                ],
                new Dictionary<string, string> { [TranscriptionWorkerServer.TokenEnvironmentVariable] = "token" }
            ),
            new ProcessOneShotOptions(TimeSpan.FromSeconds(30))
        );

        Assert.Equal(TranscriptionWorkerServer.ExitParentGone, result.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--socket /tmp/s")]
    [InlineData("--socket /tmp/s --socket /tmp/t")]
    public async Task InvalidArgumentsExitWithAUsageError(string extra)
    {
        var result = await _runner.RunOneShotAsync(
            new ProcessCommand(
                AppExecutable,
                [TranscriptionWorkerServer.Argument, .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)]
            ),
            new ProcessOneShotOptions(TimeSpan.FromSeconds(30))
        );

        Assert.Equal(TranscriptionWorkerServer.ExitInvalidArguments, result.ExitCode);
        Assert.Contains("invalid transcription worker arguments", result.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsRoundTripAndRejectUnsafeValues()
    {
        var arguments = new TranscriptionWorkerArguments(
            "/run/user/1000/typewhisper/worker.sock",
            42,
            "/opt/plugin",
            "/data",
            "/assets",
            "engine.local",
            TranscriptionAccelerationPreference.Cpu,
            TimeSpan.FromMilliseconds(250)
        );

        Assert.True(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. arguments.ToArguments()],
            out var parsed
        ));
        Assert.Equal(arguments, parsed);
        Assert.False(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. (arguments with { SelectionId = "../x" }).ToArguments()],
            out _
        ));
        Assert.False(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. (arguments with { ParentProcessId = 0 }).ToArguments()],
            out _
        ));
        Assert.False(TranscriptionWorkerArguments.TryParse(
            [TranscriptionWorkerServer.Argument, .. (arguments with { HeartbeatInterval = TimeSpan.Zero }).ToArguments()],
            out _
        ));
        Assert.False(TranscriptionWorkerArguments.TryParse(["--other", .. arguments.ToArguments()], out _));
    }

    [Fact]
    public void RequestBudgetsScaleWithAudioAndKeepLoadingSeparate()
    {
        var launch = Launch();
        var transcribe = new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Transcribe };

        Assert.Equal(TimeSpan.FromSeconds(120), launch.TimeoutFor(transcribe, 0));
        // One minute of 16 kHz PCM16 allows ten minutes more.
        Assert.Equal(TimeSpan.FromSeconds(720), launch.TimeoutFor(transcribe, 60 * 16000 * 2));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            launch.TimeoutFor(new TranscriptionWorkerMessage { Command = TranscriptionWorkerCommands.Load }, 0)
        );
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            (launch with { RequestTimeout = Timeout.InfiniteTimeSpan }).TimeoutFor(transcribe, 0)
        );
        Assert.True(launch.TimeoutFor(transcribe, TranscriptionWorkerProtocol.MaximumAudioBytes) > TimeSpan.FromHours(90));
    }

    [Fact]
    public void StreamingEnginesStayInProcess()
    {
        var streaming = new Moq.Mock<ITranscriptionEngineRole>();
        streaming.SetupGet(engine => engine.SupportsStreaming).Returns(true);

        Assert.Throws<ArgumentException>(() =>
            IsolatedTranscriptionEngine.Create(streaming.Object, _runner, Launch(), Log));
    }

    private static ProcessCommand FakeWorker(string behavior)
    {
        return new ProcessCommand(
            "dotnet",
            [typeof(ProcessTestChildMarker).Assembly.Location, "fake-transcription-worker", behavior]
        );
    }

    private string[] Arguments(string socketPath, int parentProcessId)
    {
        return
        [
            TranscriptionWorkerServer.Argument,
            .. new TranscriptionWorkerArguments(
                socketPath,
                parentProcessId,
                PackageDirectory,
                PluginDataRoot,
                Path.Join(_root, "assets"),
                WorkerProbePlugin.Id,
                TranscriptionAccelerationPreference.Auto
            ).ToArguments(),
        ];
    }

    /// <summary>A real worker with the test as its host, for scenarios the host class prevents.</summary>
    private sealed class RawWorker : IAsyncDisposable
    {
        private readonly string? _releaseFile;

        private RawWorker(IPluginProcessSession session, Socket connection, int workerProcessId, string? releaseFile)
        {
            Session = session;
            Connection = connection;
            Stream = new NetworkStream(connection);
            WorkerProcessId = workerProcessId;
            _releaseFile = releaseFile;
        }

        internal IPluginProcessSession Session { get; }
        internal Socket Connection { get; }
        internal NetworkStream Stream { get; }
        internal int WorkerProcessId { get; }

        /// <summary>Starts the worker directly, or behind a shell that stays until released when no parent is given.</summary>
        internal static async Task<RawWorker> StartAsync(TranscriptionWorkerTests test, int? parentProcessId)
        {
            var socketPath = Path.Join(test.SocketDirectory, $"raw-{Guid.NewGuid():N}.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var environment = new Dictionary<string, string>
            {
                [TranscriptionWorkerServer.TokenEnvironmentVariable] = "token",
            };
            string? releaseFile = null;
            ProcessCommand command;
            if (parentProcessId is { } parent)
            {
                command = new ProcessCommand(AppExecutable, test.Arguments(socketPath, parent), environment);
            }
            else
            {
                releaseFile = Path.Join(test._root, $"release-{Guid.NewGuid():N}");
                // The shell passes its own pid as the worker's parent, then waits for the release file.
                const string script = """
                                      "$0" "$@" --parent-pid $$ &
                                      while [ ! -e "$RELEASE" ]; do sleep 0.05; done
                                      """;
                var arguments = test.Arguments(socketPath, 1).ToList();
                var parentIndex = arguments.IndexOf("--parent-pid");
                arguments.RemoveRange(parentIndex, 2);
                environment["RELEASE"] = releaseFile;
                command = new ProcessCommand("sh", ["-c", script, AppExecutable, .. arguments], environment);
            }

            var session = test._runner.StartSession(
                    command,
                    new ProcessSessionOptions(StandardError: ProcessSessionOutputMode.Discard)
                ).Session
                ?? throw new InvalidOperationException("The worker did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(timeout.Token);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }

            var raw = new RawWorker(session, connection, PeerProcessId(connection), releaseFile);
            try
            {
                TranscriptionWorkerFrame? hello;
                // The probe logs while it activates; those frames may come first.
                do
                {
                    hello = await TranscriptionWorkerProtocol.ReadAsync(raw.Stream, 0, timeout.Token);
                } while (hello?.Message.Type == TranscriptionWorkerMessageTypes.Log);

                Assert.Equal(TranscriptionWorkerMessageTypes.Hello, hello?.Message.Type);
                Assert.Equal("token", hello?.Message.Token);
                Assert.Null(hello?.Message.Error);
                return raw;
            }
            catch
            {
                await raw.DisposeAsync();
                throw;
            }
        }

        /// <summary>Sends a request that starts a process in the worker and hangs; returns that process id.</summary>
        internal async Task<int> SpawnAndHangAsync()
        {
            await TranscriptionWorkerProtocol.WriteAsync(
                Stream,
                new TranscriptionWorkerMessage
                {
                    Type = TranscriptionWorkerMessageTypes.Request,
                    Id = 1,
                    Command = TranscriptionWorkerCommands.Transcribe,
                    Language = "spawn-and-hang",
                },
                new byte[] { 0 },
                CancellationToken.None
            );
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await TranscriptionWorkerProtocol.ReadAsync(Stream, 0, timeout.Token) is { } frame)
            {
                if (frame.Message.LogMessage is { } text && text.StartsWith("spawned ", StringComparison.Ordinal))
                {
                    return int.Parse(text["spawned ".Length..], CultureInfo.InvariantCulture);
                }
            }

            throw new EndOfStreamException("The worker ended before it started a process.");
        }

        internal void ReleaseParent()
        {
            File.WriteAllText(_releaseFile!, "");
        }

        private static int PeerProcessId(Socket socket)
        {
            // Linux: struct ucred { pid_t pid; uid_t uid; gid_t gid; } under SOL_SOCKET/SO_PEERCRED.
            var credentials = new byte[12];
            socket.GetRawSocketOption(1, 17, credentials);
            return BitConverter.ToInt32(credentials, 0);
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            Connection.Dispose();
            await Session.DisposeAsync();
            if (IsRunning(WorkerProcessId))
            {
                Process.GetProcessById(WorkerProcessId).Kill();
            }
        }
    }
}
