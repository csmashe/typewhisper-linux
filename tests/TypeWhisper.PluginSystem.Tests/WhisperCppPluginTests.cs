extern alias SherpaOnnx;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Moq;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
// The WhisperCpp plugin's (global) copy of the shared provisioner; the WhisperCpp ref is
// not aliased, so this is the unambiguous CudaRuntimeProvisioner/CudaRuntimeProfile.
using TypeWhisper.Plugins.Shared.Cuda;
// SherpaOnnx's own types live behind the extern alias (see the test csproj).
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
// SherpaOnnx's separate copy of the shared provisioner (for the sherpa injection fakes).
using SherpaCuda = SherpaOnnx::TypeWhisper.Plugins.Shared.Cuda;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    private const float TranscriptionNoSpeechThreshold = 0.8f;

    [Fact]
    public async Task AccumulateSegmentsAsync_SpeechThenTrailingSilence_UsesMinimumProbability()
    {
        var result = await WhisperCppPlugin.AccumulateSegmentsAsync(
            AsAsyncEnumerable(
                Segment("  Hello world.  ", 0.05f, language: "en", endSeconds: 1.25),
                Segment(" Thank you. ", 0.95f, language: "en", endSeconds: 1.5)
            ),
            TranscriptionNoSpeechThreshold
        );

        Assert.Equal("Hello world.", result.Text);
        Assert.Equal(0.05f, result.NoSpeechProbability);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds);
    }

    [Fact]
    public async Task AccumulateSegmentsAsync_AllSegmentsSilent_ReturnsEmptyTextAndMinimumProbability()
    {
        var result = await WhisperCppPlugin.AccumulateSegmentsAsync(
            AsAsyncEnumerable(Segment("Noise", 0.9f), Segment("Thank you.", 0.95f)),
            TranscriptionNoSpeechThreshold
        );

        Assert.Empty(result.Text);
        Assert.Equal(0.9f, result.NoSpeechProbability);
    }

    [Fact]
    public async Task AccumulateSegmentsAsync_NoSegments_ReturnsEmptyTextAndNullProbability()
    {
        var result = await WhisperCppPlugin.AccumulateSegmentsAsync(
            AsAsyncEnumerable(),
            TranscriptionNoSpeechThreshold
        );

        Assert.Empty(result.Text);
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public async Task AccumulateSegmentsAsync_MultipleSpeechSegments_JoinsTextAndUsesMinimumProbability()
    {
        var result = await WhisperCppPlugin.AccumulateSegmentsAsync(
            AsAsyncEnumerable(Segment(" First ", 0.1f), Segment(" second. ", 0.3f)),
            TranscriptionNoSpeechThreshold
        );

        Assert.Equal("First second.", result.Text);
        Assert.Equal(0.1f, result.NoSpeechProbability);
    }

    [Fact]
    public async Task AccumulateSegmentsAsync_SilenceThenSpeech_UsesMinimumProbabilityRegardlessOfOrder()
    {
        var result = await WhisperCppPlugin.AccumulateSegmentsAsync(
            AsAsyncEnumerable(Segment("Thank you.", 0.95f), Segment(" Speech ", 0.05f)),
            TranscriptionNoSpeechThreshold
        );

        Assert.Equal("Speech", result.Text);
        Assert.Equal(0.05f, result.NoSpeechProbability);
    }

    [Fact]
    public void SupportedAccelerationBackends_IsCpuAndNvidiaCuda()
    {
        var plugin = new WhisperCppPlugin();

        Assert.Contains(TranscriptionAccelerationBackend.Cpu, plugin.SupportedAccelerationBackends);
        Assert.Contains(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public void DefaultAccelerationStatus_ReportsCpu()
    {
        var plugin = new WhisperCppPlugin();

        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public void SetAccelerationPreference_Cpu_TracksPreference()
    {
        var plugin = new WhisperCppPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
    }

    [Fact]
    public void SetAccelerationPreference_NvidiaCuda_TracksPreferenceAndShowsPending()
    {
        var plugin = new WhisperCppPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
    }

    [Fact]
    public void SetAccelerationPreference_NvidiaCuda_WhenRuntimePinnedToCpu_RequiresRestart()
    {
        var plugin = new WhisperCppPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cpu");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        // The native runtime is pinned to CPU; the request is surfaced as
        // restart-required rather than silently dropped.
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        Assert.NotNull(plugin.AccelerationStatus.Detail);
        Assert.Contains(
            "restart",
            plugin.AccelerationStatus.Detail!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetAccelerationPreference_Cpu_WhenRuntimePinnedToCuda_DoesNotRequireRestart()
    {
        var plugin = new WhisperCppPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cuda");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
        // A [Cuda]-pinned native runtime can run CPU compute by rebuilding the factory
        // with UseGpu=false — no restart, just a reload. The status reflects the pending
        // switch to CPU rather than reporting a (false) restart requirement.
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public void SetAccelerationPreference_WhenCudaPinnedRunningCpu_TogglesWithoutRestart()
    {
        var plugin = new WhisperCppPlugin();
        // Models a first-load GPU-context failure: the native runtime pinned [Cuda] but
        // the factory fell back to CPU compute. A later CUDA preference must NOT report
        // restart-required (the [Cuda] .so set is already loaded; GPU is a reload away).
        plugin.MarkNativeRuntimeLoadedForTests("cuda", effectiveCompute: "cpu");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);

        // And switching back to CPU stays on CPU, still without a restart.
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    // The on-demand CUDA runtime is downloaded into NativeDirectory, but Whisper.net
    // is pointed at it via RuntimeOptions.LibraryPath. Its loader takes
    // Path.GetDirectoryName(LibraryPath) and appends runtimes/cuda/<platform>-<arch>.
    // This contract is what makes the downloaded runtime resolvable; assert the two
    // properties stay in lockstep so a layout change can't silently break GPU loads.
    [Fact]
    public void WhisperCudaRuntimeInstaller_LibraryPath_ResolvesToNativeDirectory()
    {
        using var http = new HttpClient();
        var root = Path.Join(Path.GetTempPath(), "tw-whisper-cuda-" + Guid.NewGuid().ToString("N"));
        var installer = new WhisperCudaRuntimeInstaller(root, http);

        // Mirrors the directory Whisper.net's NativeLibraryLoader searches for
        // linux-x64: LibraryPath's parent + runtimes/cuda/linux-x64.
        var loaderSearchDir = Path.Join(
            Path.GetDirectoryName(installer.LibraryPath)!,
            "runtimes",
            "cuda",
            "linux-x64");

        Assert.Equal(installer.NativeDirectory, loaderSearchDir);
        Assert.False(installer.IsInstalled); // nothing extracted into a fresh temp root
    }

    // The csproj pins Whisper.net / Whisper.net.Runtime, and the on-demand CUDA build
    // is the whisper.net.runtime.cuda.linux nu pkg at WhisperCudaRuntimeInstaller.
    // RuntimeVersion. whisper.cpp's native ABI isn't stable across releases, so if
    // these drift the downloaded CUDA runtime fails to load against the managed
    // binding. Fail the build the moment they diverge, as the csproj comment promises.
    [Fact]
    public void WhisperNetPackageVersions_StayInLockStepWithCudaRuntimeVersion()
    {
        var csproj = File.ReadAllText(WhisperCppCsprojPath());

        var managed = WhisperNetVersionRegex().Match(csproj);
        var runtime = WhisperNetRuntimeVersionRegex().Match(csproj);

        Assert.True(managed.Success, "Could not find the Whisper.net <PackageReference> in the csproj.");
        Assert.True(runtime.Success, "Could not find the Whisper.net.Runtime <PackageReference> in the csproj.");

        Assert.Equal(WhisperCudaRuntimeInstaller.RuntimeVersion, managed.Groups[1].Value);
        Assert.Equal(WhisperCudaRuntimeInstaller.RuntimeVersion, runtime.Groups[1].Value);
    }

    [GeneratedRegex("""<PackageReference\s+Include="Whisper\.net"\s+Version="([^"]+)"\s*/>""")]
    private static partial Regex WhisperNetVersionRegex();

    [GeneratedRegex("""<PackageReference\s+Include="Whisper\.net\.Runtime"\s+Version="([^"]+)"\s*/>""")]
    private static partial Regex WhisperNetRuntimeVersionRegex();

    // CI-portable state-machine test: a CUDA load whose backend is switched to CPU mid-
    // provision must abort rather than pin the process to a backend the user no longer
    // wants. The injected provisioner blocks inside EnsureReadyAsync so the test can flip
    // the backend before the post-provision re-check runs. No native load is reached.
    [Fact]
    public async Task LoadModelAsync_BackendSwitchedDuringProvision_AbortsLoad()
    {
        // EnsureCudaRuntimeReadyAsync's Linux-x64 platform gate throws before the fake
        // provisioner runs on unsupported hosts, so `Started` would never complete and the
        // await below would hang. The CI/dev target is Linux x64; skip elsewhere.
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var temp = new TempAssetDir();
        var host = CreateHostMock(temp.Path);
        var provisioner = new BlockingProvisioner();
        var installer = new NoopWhisperInstaller(temp.Path);

        var plugin = new WhisperCppPlugin();
        plugin.SetCudaDependenciesForTests(provisioner, installer);
        await plugin.ActivateAsync(host.Object);
        WriteDummyModel(temp.Path, "ggml-tiny.bin");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        var loadTask = plugin.LoadModelAsync("tiny", CancellationToken.None);

        await provisioner.Started;
        // User switches to CPU while the (blocked) CUDA provision is in flight.
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        provisioner.Release();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loadTask);
        Assert.Contains("Compute backend changed", ex.Message);
    }

    // Once Whisper.net's one-shot native loader has failed (poisoned static), a subsequent
    // load must fail fast with the restart-required message and NOT re-enter FromPath.
    [Fact]
    public async Task LoadModelAsync_AfterNativeRuntimeLoadFailed_FailsFastWithRestartMessage()
    {
        using var temp = new TempAssetDir();
        var host = CreateHostMock(temp.Path);

        var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(host.Object);
        WriteDummyModel(temp.Path, "ggml-tiny.bin");
        plugin.MarkNativeRuntimeLoadFailedForTests();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => plugin.LoadModelAsync("tiny", CancellationToken.None));
        Assert.Contains("restart", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Mock<IPluginHostServices> CreateHostMock(string assetDir)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(assetDir);
        host.Setup(h => h.PluginAssetDirectory).Returns(assetDir);
        return host;
    }

    private static void WriteDummyModel(string assetDir, string fileName)
    {
        var modelsDir = Path.Join(assetDir, "Models");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Join(modelsDir, fileName), "dummy");
    }

    private static WhisperCppTranscriptionSegment Segment(
        string text,
        float noSpeechProbability,
        string? language = null,
        double endSeconds = 0
    ) => new(text, language, TimeSpan.FromSeconds(endSeconds), noSpeechProbability);

    private static async IAsyncEnumerable<WhisperCppTranscriptionSegment> AsAsyncEnumerable(
        params WhisperCppTranscriptionSegment[] segments
    )
    {
        foreach (var segment in segments)
        {
            await Task.Yield();
            yield return segment;
        }
    }

    // A provisioner that blocks inside EnsureReadyAsync until released, signaling when it
    // has started so a test can deterministically interleave a backend switch.
    private sealed class BlockingProvisioner : CudaRuntimeProvisioner
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingProvisioner()
            : base(Path.GetTempPath(), new HttpClient()) { }

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public override async Task EnsureReadyAsync(
            CudaRuntimeProfile profile,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
    }

    private sealed class NoopWhisperInstaller : WhisperCudaRuntimeInstaller
    {
        public NoopWhisperInstaller(string root)
            : base(root, new HttpClient()) { }

        public override Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct)
        {
            progress?.Report(1.0);
            return Task.CompletedTask;
        }
    }

    private sealed class TempAssetDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "tw-whisper-asset-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    // Resolve the plugin csproj relative to THIS test file so the assertion doesn't
    // depend on the csproj being copied to test output (mirrors LocalizationResourcesTests).
    private static string WhisperCppCsprojPath([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Join(
                testDir, "..", "..",
                "plugins", "TypeWhisper.Plugin.WhisperCpp",
                "TypeWhisper.Plugin.WhisperCpp.csproj"));
    }
}

public partial class SherpaOnnxPluginTests
{
    [Fact]
    public void SupportedAccelerationBackends_IsCpuAndNvidiaCuda()
    {
        var plugin = new SherpaOnnxPlugin();

        Assert.Contains(TranscriptionAccelerationBackend.Cpu, plugin.SupportedAccelerationBackends);
        Assert.Contains(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public void DefaultAccelerationStatus_ReportsCpu()
    {
        var plugin = new SherpaOnnxPlugin();

        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public async Task SetAccelerationPreference_NvidiaCuda_TracksPreferenceAndShowsPending()
    {
        var plugin = new SherpaOnnxPlugin();
        var host = CreateHost();
        await plugin.ActivateAsync(host);

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        // Pending until the next model load actually provisions + pins the runtime.
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
    }

    [Fact]
    public void SetAccelerationPreference_NvidiaCuda_WhenRuntimePinnedToCpu_RequiresRestart()
    {
        var plugin = new SherpaOnnxPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cpu");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        Assert.NotNull(plugin.AccelerationStatus.Detail);
        Assert.Contains(
            "restart",
            plugin.AccelerationStatus.Detail!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetAccelerationPreference_Cpu_WhenRuntimePinnedToCuda_DoesNotRequireRestart()
    {
        var plugin = new SherpaOnnxPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cuda");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
        // A CUDA-wired ORT runtime can build a CPU recognizer with no restart — just a
        // reload. The status reflects the switch to CPU rather than a (false) restart flag.
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public void SetAccelerationPreference_WhenCudaWiredRunningCpu_TogglesWithoutRestart()
    {
        var plugin = new SherpaOnnxPlugin();
        // Models a first-load CUDA-recognizer failure: the CUDA ORT runtime is wired into
        // the process, but the recognizer fell back to CPU. A later CUDA preference must
        // NOT report restart-required (a CUDA recognizer is just a reload away).
        plugin.MarkNativeRuntimeLoadedForTests("cuda", effectiveProvider: "cpu");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);

        // Switching back to CPU stays on CPU, still without a restart.
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    // The GPU runtime tarball is downloaded then dlopen'd, so its integrity is pinned
    // by SHA-256 and verified before extraction. Lock in the fail-closed contract: a
    // download whose bytes don't match the pinned digest must throw (so it never
    // reaches extraction), rather than silently caching unverified native code.
    [Fact]
    public void SherpaCudaRuntimeInstaller_VerifySha256_RejectsArtifactNotMatchingPinnedDigest()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "tw-sherpa-cuda-bad-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "not the real sherpa-onnx GPU tarball");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SherpaCudaRuntimeInstaller.VerifySha256(path));
            Assert.Contains("Checksum mismatch", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // libonnxruntime_providers_cuda.so MUST be extracted (so ORT can discover it next
    // to libonnxruntime.so and load it host-first at session creation) but MUST NEVER
    // be preloaded directly: its ELF constructors dereference a ProviderHost that
    // libonnxruntime.so installs only when IT loads the provider through its own
    // provider-bridge. dlopen'ing it ourselves runs those constructors with a null
    // host → uncatchable segfault. Lock the invariant so a future "preload everything"
    // refactor can't silently reintroduce it (see SherpaOnnxNativeRuntime.PreloadOrder).
    [Fact]
    public void SherpaCudaProvider_IsExtractedButNeverPreloaded()
    {
        Assert.Contains(
            "libonnxruntime_providers_cuda.so",
            SherpaCudaRuntimeInstaller.CoreRuntimeFiles);
        Assert.DoesNotContain(
            "libonnxruntime_providers_cuda.so",
            SherpaOnnxNativeRuntime.PreloadOrder);
    }

    // The csproj pins org.k2fsa.sherpa.onnx, and the on-demand GPU build is the
    // v{RuntimeVersion} release tarball at SherpaCudaRuntimeInstaller. sherpa-onnx's
    // C API isn't ABI-stable across releases, so if the managed package and the GPU
    // runtime version drift the downloaded native libs fail to load against the
    // binding. The version is embedded separately in RuntimeVersion, AssetFileName,
    // and DownloadUrl — assert all of them stay in lockstep with the csproj so a
    // partial bump fails the build (mirrors WhisperNetPackageVersions above).
    [Fact]
    public void SherpaOnnxPackageVersion_StaysInLockStepWithCudaRuntimeVersion()
    {
        var csproj = File.ReadAllText(SherpaOnnxCsprojPath());

        var managed = SherpaOnnxVersionRegex().Match(csproj);

        Assert.True(
            managed.Success,
            "Could not find the org.k2fsa.sherpa.onnx <PackageReference> in the csproj.");

        var version = managed.Groups[1].Value;
        // RuntimeVersion is the tag form ("v1.12.23"); the csproj pins the bare version.
        Assert.Equal(
            version,
            SherpaCudaRuntimeInstaller.RuntimeVersion.TrimStart('v'));
        Assert.Contains(
            version,
            SherpaCudaRuntimeInstaller.AssetFileName);
        Assert.Contains(
            version,
            SherpaCudaRuntimeInstaller.DownloadUrl);
    }

    [GeneratedRegex("""<PackageReference\s+Include="org\.k2fsa\.sherpa\.onnx"\s+Version="([^"]+)"\s*/>""")]
    private static partial Regex SherpaOnnxVersionRegex();

    // Resolve the plugin csproj relative to THIS test file (mirrors WhisperCppCsprojPath).
    private static string SherpaOnnxCsprojPath([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Join(
                testDir, "..", "..",
                "plugins", "TypeWhisper.Plugin.SherpaOnnx",
                "TypeWhisper.Plugin.SherpaOnnx.csproj"));
    }

    private static IPluginHostServices CreateHost()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(Path.GetTempPath());
        return host.Object;
    }

    // CI-portable state-machine test mirroring the whisper one: a CUDA load whose backend
    // is switched to CPU mid-provision must abort. The injected provisioner blocks inside
    // EnsureReadyAsync; once the backend is switched to CPU the wiring guard skips
    // ConfigureCudaRuntime entirely (the installer's RuntimeDirectory also points at a
    // non-existent temp dir as a further safeguard against any dlopen). The abort fires on
    // the post-provision re-check, before any recognizer.
    [Fact]
    public async Task LoadModelAsync_BackendSwitchedDuringProvision_AbortsLoad()
    {
        // EnsureCudaRuntimeReadyAsync's Linux-x64 platform gate throws before the fake
        // provisioner runs on unsupported hosts, so `Started` would never complete and the
        // await below would hang. The CI/dev target is Linux x64; skip elsewhere.
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var temp = new TempAssetDir();
        var host = CreateHostMock(temp.Path);
        var provisioner = new SherpaBlockingProvisioner();
        var installer = new NoopSherpaInstaller(temp.Path);

        var plugin = new SherpaOnnxPlugin();
        plugin.SetCudaDependenciesForTests(provisioner, installer);
        await plugin.ActivateAsync(host.Object);
        WriteParakeetModelFiles(temp.Path);

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        var loadTask = plugin.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None);

        await provisioner.Started;
        // User switches to CPU while the (blocked) CUDA provision is in flight.
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        provisioner.Release();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loadTask);
        Assert.Contains("Compute backend changed", ex.Message);
    }

    [Fact]
    public async Task LoadModelAsync_NativeParseFailure_DeletesArtifactsAndMarksModelFetchable()
    {
        using var temp = new TempAssetDir();
        var host = CreateHostMock(temp.Path);
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(host.Object);
        WriteParakeetModelFiles(temp.Path);
        plugin.SetParakeetRecognizerFactoryForTests(
            (_, _) => throw new InvalidOperationException(
                "Failed to load model because protobuf parsing failed."
            )
        );

        Assert.True(plugin.IsModelDownloaded("parakeet-tdt-0.6b"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => plugin.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None)
        );

        Assert.Contains("protobuf parsing failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(plugin.IsModelDownloaded("parakeet-tdt-0.6b"));
        Assert.Empty(
            Directory.GetFiles(Path.Join(temp.Path, "Models", "parakeet-tdt-0.6b"))
        );
    }

    [Fact]
    public async Task LoadModelAsync_EnvironmentFailure_PreservesDownloadedArtifacts()
    {
        using var temp = new TempAssetDir();
        var host = CreateHostMock(temp.Path);
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(host.Object);
        WriteParakeetModelFiles(temp.Path);
        plugin.SetParakeetRecognizerFactoryForTests(
            (_, _) => throw new InvalidOperationException(
                "CUDA execution provider failed to initialize because libcudnn was unavailable."
            )
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => plugin.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None)
        );

        Assert.Contains("CUDA execution provider", ex.Message);
        Assert.True(plugin.IsModelDownloaded("parakeet-tdt-0.6b"));
        Assert.Equal(
            4,
            Directory.GetFiles(Path.Join(temp.Path, "Models", "parakeet-tdt-0.6b")).Length
        );
    }

    [Fact]
    public void ArtifactPreflight_CanaryTokensWithoutBlank_AcceptedButStillStructurallyChecked()
    {
        using var temp = new TempAssetDir();
        using var plugin = new SherpaOnnxPlugin();
        var dir = WriteCanaryModelFiles(temp.Path);

        // Canary (attention encoder-decoder) has no blank token; preflight must accept
        // it, or a blank requirement would fail every Canary download and delete caches.
        plugin.RunArtifactPreflightForTests("canary-180m-flash", dir);

        // The blank exemption must not switch off the remaining token checks.
        File.WriteAllText(Path.Join(dir, "tokens.txt"), "<unk> 0\n<pad> not-an-id\n");
        Assert.Throws<InvalidDataException>(
            () => plugin.RunArtifactPreflightForTests("canary-180m-flash", dir)
        );
    }

    private static Mock<IPluginHostServices> CreateHostMock(string assetDir)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(assetDir);
        host.Setup(h => h.PluginAssetDirectory).Returns(assetDir);
        return host;
    }

    private static string WriteCanaryModelFiles(string assetDir)
    {
        var dir = Path.Join(assetDir, "Models", "canary-180m-flash");
        Directory.CreateDirectory(dir);
        foreach (var fileName in new[] { "encoder.int8.onnx", "decoder.int8.onnx" })
            File.WriteAllBytes(Path.Join(dir, fileName), [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00]);

        // Real Canary vocab uses <unk>/<pad>/<|...|> tokens and no transducer blank symbol.
        File.WriteAllText(Path.Join(dir, "tokens.txt"), "<unk> 0\nfoo 1\n");
        return dir;
    }

    private static void WriteParakeetModelFiles(string assetDir)
    {
        var dir = Path.Join(assetDir, "Models", "parakeet-tdt-0.6b");
        Directory.CreateDirectory(dir);
        foreach (var fileName in new[]
                 {
                     "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx",
                 })
        {
            // Minimal ONNX protobuf framing (ir_version=9, non-empty graph) for the
            // structural preflight; the injected recognizer factory means these bytes
            // never reach the native loader.
            File.WriteAllBytes(
                Path.Join(dir, fileName),
                [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00]
            );
        }

        File.WriteAllText(Path.Join(dir, "tokens.txt"), "<blk> 0\n");
    }

    private sealed class SherpaBlockingProvisioner : SherpaCuda.CudaRuntimeProvisioner
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SherpaBlockingProvisioner()
            : base(Path.GetTempPath(), new HttpClient()) { }

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public override async Task EnsureReadyAsync(
            SherpaCuda.CudaRuntimeProfile profile,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
    }

    private sealed class NoopSherpaInstaller : SherpaCudaRuntimeInstaller
    {
        public NoopSherpaInstaller(string root)
            : base(root, new HttpClient()) { }

        public override Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct)
        {
            progress?.Report(1.0);
            return Task.CompletedTask;
        }
    }

    private sealed class TempAssetDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "tw-sherpa-asset-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
