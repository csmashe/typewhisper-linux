extern alias SherpaOnnx;

using System.Runtime.InteropServices;
using Moq;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using SherpaCuda = SherpaOnnx::TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxCancellationTests
{
    // Regression: before the fix, cancellation entered the general CUDA fallback
    // catch, changed the backend to CPU, and continued toward recognizer creation.
    [Fact]
    public async Task LoadModelAsync_ProvisioningCancellation_PreservesCudaState()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var temp = new TempAssetDirectory();
        using var plugin = new SherpaOnnxPlugin();
        var provisioner = new CancelableProvisioner();
        plugin.SetCudaDependenciesForTests(provisioner, new NoopInstaller(temp.Path));
        plugin.SetHostForTests(CreateHost(temp.Path).Object);
        plugin.SetParakeetRecognizerFactoryForTests(
            (_, _) => throw new InvalidOperationException(
                "Recognizer creation must not run after cancellation."
            )
        );
        WriteParakeetModelFiles(temp.Path);
        plugin.SelectModel("parakeet-tdt-0.6b");
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var preferenceBefore = plugin.AccelerationPreference;
        var statusBefore = plugin.AccelerationStatus;
        using var cts = new CancellationTokenSource();
        var loadTask = plugin.LoadModelAsync("parakeet-tdt-0.6b", cts.Token);

        await provisioner.Started;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loadTask);
        Assert.Equal("cuda", plugin.ComputeBackendForTests);
        Assert.Equal(preferenceBefore, plugin.AccelerationPreference);
        Assert.Equal(statusBefore, plugin.AccelerationStatus);
        Assert.Equal("parakeet-tdt-0.6b", plugin.SelectedModelId);
    }

    // New-contract test: the coordinator did not exist before this fix. It pins the
    // checkpoint immediately after each synchronous native-call delegate returns.
    [Fact]
    public void Decode_CancellationAfterFirstChunk_PreventsLaterDelegateCalls()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
        {
            calls++;
            // ReSharper disable once AccessToDisposedClosure -- lambda runs synchronously inside coordinator.Decode below, before the `using var cts` is disposed at scope end.
            cts.Cancel();
            return "first chunk";
        });
        var audio = new float[SherpaDecodeCoordinator.MaximumChunkSampleCount + 1];

        Assert.ThrowsAny<OperationCanceledException>(
            () => coordinator.Decode(audio, parseCanaryPayload: false, cts.Token)
        );
        Assert.Equal(1, calls);
    }

    // New-contract test: cancellation must unwind the plugin's production _sync
    // transaction so unload/configuration work cannot remain blocked.
    [Fact]
    public async Task DecodeTransaction_Cancellation_ReleasesSync()
    {
        using var plugin = new SherpaOnnxPlugin();
        using var cts = new CancellationTokenSource();
        var audio = new float[SherpaDecodeCoordinator.MaximumChunkSampleCount + 1];

        // ReSharper disable once MethodSupportsCancellation -- the delegate must run and cancel from within; passing cts.Token to Task.Run would cancel scheduling instead.
        // ReSharper disable AccessToDisposedClosure -- the task is awaited via Assert.ThrowsAnyAsync below, so the closure completes before the `using var plugin`/`using var cts` are disposed at scope end.
        var canceledDecode = Task.Run(
            () =>
                plugin.RunDecodeTransactionForTests(
                    audio,
                    parseCanaryPayload: false,
                    _ =>
                    {
                        cts.Cancel();
                        return "first chunk";
                    },
                    cts.Token
                )
        );
        // ReSharper restore AccessToDisposedClosure
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledDecode);

        // ReSharper disable once MethodSupportsCancellation -- no ambient token belongs here; this decode uses CancellationToken.None to prove the lock was released, so a token would only cancel scheduling.
        // ReSharper disable AccessToDisposedClosure -- the task is awaited via WaitAsync below, so the closure completes before the `using var plugin` is disposed at scope end.
        var nextDecode = Task.Run(
            () =>
                plugin.RunDecodeTransactionForTests(
                    [],
                    parseCanaryPayload: false,
                    _ => "lock released",
                    CancellationToken.None
                )
        );
        // ReSharper restore AccessToDisposedClosure
        // ReSharper disable once MethodSupportsCancellation -- deliberately a time-bounded hang guard; a token isn't wanted here.
        var result = await nextDecode.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("lock released", result.Text);
    }

    // New-contract test: chunking bounds every synchronous delegate invocation to
    // the named 15-second maximum.
    [Fact]
    public void Decode_LongAudio_EveryChunkRespectsNamedMaximum()
    {
        var chunkLengths = new List<int>();
        var coordinator = new SherpaDecodeCoordinator(chunk =>
        {
            chunkLengths.Add(chunk.Length);
            return string.Empty;
        });
        var audio = new float[SherpaDecodeCoordinator.MaximumChunkSampleCount * 3 + 123];

        _ = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal(15, SherpaDecodeCoordinator.MaximumChunkDurationSeconds);
        Assert.True(chunkLengths.Count > 1);
        Assert.All(
            chunkLengths,
            length => Assert.InRange(
                length,
                1,
                SherpaDecodeCoordinator.MaximumChunkSampleCount
            )
        );
    }

    // New-contract test: crafted Canary payloads prove that every chunk is parsed
    // before longest-token overlap stitching, preserving one copy of boundary text.
    [Fact]
    public void Decode_CanaryChunkOverlap_StitchesWithoutLossOrDuplication()
    {
        var payloads = new Queue<string>(
            [
                """{"text":"the quick brown fox","lang":"en"}""",
                """{"text":"brown fox jumps high","lang":"en"}""",
            ]
        );
        var coordinator = new SherpaDecodeCoordinator(_ => payloads.Dequeue());
        var audio = new float[SherpaDecodeCoordinator.MaximumChunkSampleCount + 1];

        var result = coordinator.Decode(
            audio,
            parseCanaryPayload: true,
            CancellationToken.None
        );

        Assert.Equal("the quick brown fox jumps high", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Empty(payloads);
    }

    // A non-string "text"/"lang" must fall back to the raw payload rather than
    // throwing InvalidOperationException out of JsonElement.GetString().
    [Theory]
    [InlineData("""{"text":42,"lang":"en"}""", """{"text":42,"lang":"en"}""", "en")]
    [InlineData("""{"text":true,"lang":"en"}""", """{"text":true,"lang":"en"}""", "en")]
    [InlineData("""{"text":"hello","lang":42}""", "hello", null)]
    [InlineData("""{"text":"hello","lang":false}""", "hello", null)]
    public void Decode_CanaryPayloadWithNonStringFields_FallsBackWithoutThrowing(
        string payload,
        string expectedText,
        string? expectedLanguage
    )
    {
        var coordinator = new SherpaDecodeCoordinator(_ => payload);

        var result = coordinator.Decode(
            new float[16],
            parseCanaryPayload: true,
            CancellationToken.None
        );

        Assert.Equal(expectedText, result.Text);
        Assert.Equal(expectedLanguage, result.DetectedLanguage);
    }

    private static Mock<IPluginHostServices> CreateHost(string assetDirectory)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(assetDirectory);
        host.Setup(h => h.PluginAssetDirectory).Returns(assetDirectory);
        return host;
    }

    private static void WriteParakeetModelFiles(string assetDirectory)
    {
        var directory = Path.Join(assetDirectory, "Models", "parakeet-tdt-0.6b");
        Directory.CreateDirectory(directory);
        foreach (
            var fileName in new[]
            {
                "encoder.int8.onnx",
                "decoder.int8.onnx",
                "joiner.int8.onnx",
            }
        )
            File.WriteAllBytes(
                Path.Join(directory, fileName),
                [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00]
            );

        File.WriteAllText(Path.Join(directory, "tokens.txt"), "<blk> 0\n");
    }

    private sealed class CancelableProvisioner : SherpaCuda.CudaRuntimeProvisioner
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancelableProvisioner()
            : base(Path.GetTempPath(), new HttpClient()) { }

        internal Task Started => _started.Task;

        public override async Task EnsureReadyAsync(
            SherpaCuda.CudaRuntimeProfile profile,
            IProgress<double>? progress,
            CancellationToken ct
        )
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class NoopInstaller : SherpaCudaRuntimeInstaller
    {
        internal NoopInstaller(string root)
            : base(root, new HttpClient()) { }

        public override Task EnsureInstalledAsync(
            IProgress<double>? progress,
            CancellationToken ct
        ) => Task.CompletedTask;
    }

    private sealed class TempAssetDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "tw-sherpa-cancel-" + Guid.NewGuid().ToString("N")
        );

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
