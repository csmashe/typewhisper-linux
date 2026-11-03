using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;
using Xunit;
using Xunit.Abstractions;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Runs the real whisper.cpp and sherpa-onnx engines in workers. Only runs when
///     <c>TYPEWHISPER_WORKER_LIVE_ROOT</c> names a scratch data root laid out like the app's
///     (<c>PluginData/&lt;id&gt;/Models</c> with the models below), <c>TYPEWHISPER_WORKER_LIVE_PLUGINS</c>
///     the staged Plugins directory and <c>TYPEWHISPER_WORKER_LIVE_RECORDINGS</c> a directory of 16 kHz
///     mono WAV files. <c>TYPEWHISPER_WORKER_LIVE_CUDA=1</c> also runs on the graphics card. Run it
///     with <c>XDG_DATA_HOME</c> inside the scratch root: activation migrates legacy model folders.
/// </summary>
public sealed class TranscriptionWorkerLiveTests(ITestOutputHelper output)
{
    private const int MaximumRecordings = 8;

    private static string Root => Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_ROOT")!;
    private static string Plugins => Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_PLUGINS")!;
    private static string Recordings => Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_RECORDINGS")!;
    private static bool UseCuda => Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_CUDA") == "1";
    private static string AppExecutable => Path.Join(AppContext.BaseDirectory, "typewhisper");

    private readonly List<string> _logs = [];

    [WorkerLiveTheory]
    [InlineData("com.typewhisper.whisper-cpp", "small")]
    [InlineData("com.typewhisper.sherpa-onnx", "parakeet-tdt-0.6b")]
    public async Task AWorkerTranscribesLikeTheEngineInProcessAndSurvivesNativeFaults(string pluginId, string modelId)
    {
        var dataRoot = Path.Join(Root, "PluginData");
        var package = Path.Join(Plugins, pluginId);
        var assets = Path.Join(dataRoot, pluginId);
        var recordings = Directory.GetFiles(Recordings, "*.wav").Order(StringComparer.Ordinal).Take(MaximumRecordings).ToArray();
        Assert.NotEmpty(recordings);
        var audio = recordings.Select(File.ReadAllBytes).ToArray();
        // Long enough that a signal sent shortly after the request lands mid-decode.
        var longAudio = Concatenate(audio, minimumSeconds: 120);

        var loader = new PluginLoader(dataRoot);
        var loaded = loader.LoadPlugin(package) ?? throw new InvalidOperationException(loader.LastLoadFailures[^1].Message);
        var services = new TranscriptionWorkerHostServices(package, dataRoot, assets, Log);
        services.Bind(pluginId);
        await loaded.Instance.ActivateAsync(services);
        var inner = (ITranscriptionEngineRole)loaded.Instance;

        // The reference: the same engine in this process, on the CPU.
        inner.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await inner.LoadModelAsync(modelId, CancellationToken.None);
        var reference = new List<string>();
        foreach (var wav in audio.Append(longAudio))
        {
            reference.Add((await inner.TranscribeAsync(wav, "en", false, null, CancellationToken.None)).Text);
        }

        await inner.UnloadModelAsync();
        inner.SelectModel(modelId);

        var socketDirectory = Path.Join(Root, "s");
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        Directory.CreateDirectory(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var launch = new TranscriptionWorkerLaunch(
            new ProcessCommand(AppExecutable, [TranscriptionWorkerServer.Argument]),
            socketDirectory,
            package,
            dataRoot,
            assets,
            inner.GetTranscriptionSelectionId()
        )
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(500),
            RequestInactivityTimeout = TimeSpan.FromSeconds(5),
            CancellationGrace = TimeSpan.FromSeconds(1),
        };
        var runner = new ProcessRunner();
        await using var engine = IsolatedTranscriptionEngine.Create(inner, runner, launch, Log);
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        var load = Stopwatch.StartNew();
        await engine.LoadModelAsync(modelId, CancellationToken.None);
        output.WriteLine($"{pluginId}: worker {engine.WorkerProcessId} loaded {modelId} in {load.ElapsedMilliseconds} ms");
        for (var index = 0; index < audio.Length; index++)
        {
            var text = (await engine.TranscribeAsync(audio[index], "en", false, null, CancellationToken.None)).Text;
            Assert.Equal(reference[index], text);
        }

        output.WriteLine($"{pluginId}: {audio.Length} recordings match the in-process engine");

        // The worker dies mid-decode, as under the OOM killer: the request is retried in a new worker
        // with the same result. The runtime ignores a SIGSEGV or SIGABRT sent from outside, so real
        // native faults are the probe engine's, in TranscriptionWorkerTests.
        var crashed = await SignalMidDecodeAsync(engine, "KILL", longAudio, CancellationToken.None);
        Assert.Equal(reference[^1], crashed.Text);
        lock (_logs)
        {
            Assert.Contains(_logs, line => line.Contains("1 in a row", StringComparison.Ordinal));
        }

        // A frozen worker is ended after the inactivity window, and the next request works.
        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            SignalMidDecodeAsync(engine, "STOP", longAudio, CancellationToken.None));
        Assert.Equal(reference[0], (await engine.TranscribeAsync(audio[0], "en", false, null, CancellationToken.None)).Text);

        // Cancelling a long native decode returns within the grace period.
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.TranscribeAsync(longAudio, "en", false, null, cancellation.Token));
            output.WriteLine($"{pluginId}: cancellation returned after {elapsed.ElapsedMilliseconds} ms");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        }

        if (!UseCuda)
        {
            return;
        }

        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        await engine.LoadModelAsync(modelId, CancellationToken.None);
        output.WriteLine($"{pluginId}: CUDA status {engine.AccelerationStatus}");
        Assert.Equal(TranscriptionAccelerationBackend.NvidiaCuda, engine.AccelerationStatus.ActiveBackend);
        var differing = 0;
        for (var index = 0; index < audio.Length; index++)
        {
            var text = (await engine.TranscribeAsync(audio[index], "en", false, null, CancellationToken.None)).Text;
            differing += text == reference[index] ? 0 : 1;
        }

        output.WriteLine($"{pluginId}: {differing} of {audio.Length} CUDA results differ from the CPU");

        // Two crashes on the graphics card switch the engine to the CPU, which then answers.
        var gpuWorker = engine.WorkerProcessId!.Value;
        var request = engine.TranscribeAsync(longAudio, "en", false, null, CancellationToken.None);
        await Task.Delay(500);
        Signal(gpuWorker, "KILL");
        await WaitForAsync(() => engine.WorkerProcessId is { } next && next != gpuWorker);
        await Task.Delay(500);
        Signal(engine.WorkerProcessId!.Value, "KILL");
        var fallback = await request;
        Assert.True(engine.UsesCpuFallback);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, engine.AccelerationStatus.ActiveBackend);
        Assert.Equal(reference[^1], fallback.Text);
        output.WriteLine($"{pluginId}: fell back to the CPU: {engine.AccelerationStatus}");
    }

    private static async Task<PluginTranscriptionResult> SignalMidDecodeAsync(
        IsolatedTranscriptionEngine engine,
        string signal,
        byte[] audio,
        CancellationToken ct
    )
    {
        var worker = engine.WorkerProcessId!.Value;
        var request = engine.TranscribeAsync(audio, "en", false, null, ct);
        await Task.Delay(500, ct);
        Assert.False(request.IsCompleted, "The decode finished before the signal; use longer audio.");
        Signal(worker, signal);
        try
        {
            return await request;
        }
        finally
        {
            if (signal == "STOP")
            {
                // Guards against a stopped worker the host failed to end.
                Signal(worker, "KILL");
            }
        }
    }

    private static void Signal(int processId, string signal)
    {
        using var kill = Process.Start("kill", ["-" + signal, processId.ToString(CultureInfo.InvariantCulture)]);
        kill.WaitForExit();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(2), "The condition was not met.");
            await Task.Delay(50);
        }
    }

    // Joins the PCM of 16 kHz mono PCM16 WAV files, repeated until the audio is long enough.
    private static byte[] Concatenate(IReadOnlyList<byte[]> wavs, int minimumSeconds)
    {
        using var pcm = new MemoryStream();
        while (pcm.Length < minimumSeconds * 16000 * 2)
        {
            foreach (var wav in wavs)
            {
                var data = DataChunk(wav);
                pcm.Write(wav, data.Offset, data.Length);
            }
        }

        var result = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        "WAVEfmt "u8.CopyTo(result.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(34), 16);
        "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), (int)pcm.Length);
        pcm.ToArray().CopyTo(result, 44);
        return result;
    }

    private static (int Offset, int Length) DataChunk(byte[] wav)
    {
        for (var offset = 12; offset + 8 <= wav.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            if (wav.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                return (offset + 8, Math.Min(size, wav.Length - offset - 8));
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException("The recording has no data chunk.");
    }

    private void Log(PluginLogLevel level, string message)
    {
        lock (_logs)
        {
            _logs.Add(message);
        }

        output.WriteLine($"[{level}] {message.Split(Environment.NewLine)[0]}");
    }

    private sealed class WorkerLiveTheoryAttribute : TheoryAttribute
    {
        public WorkerLiveTheoryAttribute()
        {
            if (
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_ROOT"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_PLUGINS"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_WORKER_LIVE_RECORDINGS"))
            )
            {
                Skip = "Set TYPEWHISPER_WORKER_LIVE_ROOT, _PLUGINS and _RECORDINGS to run real engines in workers.";
            }
        }
    }
}
