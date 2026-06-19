using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Moq;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class WhisperCppPluginTests
{
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
    public void SetAccelerationPreference_Cpu_WhenRuntimePinnedToCuda_RequiresRestart()
    {
        var plugin = new WhisperCppPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cuda");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        Assert.NotNull(plugin.AccelerationStatus.Detail);
        Assert.Contains(
            "restart",
            plugin.AccelerationStatus.Detail!,
            StringComparison.OrdinalIgnoreCase);
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

        // Replicates Whisper.net's NativeLibraryLoader path arithmetic for linux-x64.
        var loaderSearchDir = Path.Combine(
            Path.GetDirectoryName(installer.LibraryPath)!,
            "runtimes",
            "cuda",
            "linux-x64");

        Assert.Equal(installer.NativeDirectory, loaderSearchDir);
        Assert.False(installer.IsInstalled); // nothing extracted into a fresh temp root
    }

    // The csproj pins Whisper.net / Whisper.net.Runtime, and the on-demand CUDA build
    // is the whisper.net.runtime.cuda.linux nupkg at WhisperCudaRuntimeInstaller.
    // RuntimeVersion. whisper.cpp's native ABI isn't stable across releases, so if
    // these drift the downloaded CUDA runtime fails to load against the managed
    // binding. Fail the build the moment they diverge, as the csproj comment promises.
    [Fact]
    public void WhisperNetPackageVersions_StayInLockStepWithCudaRuntimeVersion()
    {
        var csproj = File.ReadAllText(WhisperCppCsprojPath());

        var managed = Regex.Match(
            csproj,
            """<PackageReference\s+Include="Whisper\.net"\s+Version="([^"]+)"\s*/>""");
        var runtime = Regex.Match(
            csproj,
            """<PackageReference\s+Include="Whisper\.net\.Runtime"\s+Version="([^"]+)"\s*/>""");

        Assert.True(managed.Success, "Could not find the Whisper.net <PackageReference> in the csproj.");
        Assert.True(runtime.Success, "Could not find the Whisper.net.Runtime <PackageReference> in the csproj.");

        Assert.Equal(WhisperCudaRuntimeInstaller.RuntimeVersion, managed.Groups[1].Value);
        Assert.Equal(WhisperCudaRuntimeInstaller.RuntimeVersion, runtime.Groups[1].Value);
    }

    // Resolve the plugin csproj relative to THIS test file so the assertion doesn't
    // depend on the csproj being copied to test output (mirrors LocalizationResourcesTests).
    private static string WhisperCppCsprojPath([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Combine(
                testDir, "..", "..",
                "plugins", "TypeWhisper.Plugin.WhisperCpp",
                "TypeWhisper.Plugin.WhisperCpp.csproj"));
    }
}

public class SherpaOnnxPluginTests
{
    [Fact]
    public void SupportedAccelerationBackends_IsCpuAndNvidiaCuda()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();

        Assert.Contains(TranscriptionAccelerationBackend.Cpu, plugin.SupportedAccelerationBackends);
        Assert.Contains(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public void DefaultAccelerationStatus_ReportsCpu()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();

        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public async Task SetAccelerationPreference_NvidiaCuda_TracksPreferenceAndShowsPending()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();
        var host = CreateHost(out _);
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
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();
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
    public void SetAccelerationPreference_Cpu_WhenRuntimePinnedToCuda_RequiresRestart()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cuda");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        Assert.NotNull(plugin.AccelerationStatus.Detail);
        Assert.Contains(
            "restart",
            plugin.AccelerationStatus.Detail!,
            StringComparison.OrdinalIgnoreCase);
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
                () => TypeWhisper.Plugin.SherpaOnnx.SherpaCudaRuntimeInstaller.VerifySha256(path));
            Assert.Contains("Checksum mismatch", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IPluginHostServices CreateHost(
        out List<(PluginLogLevel level, string message)> logEntries)
    {
        var entries = new List<(PluginLogLevel, string)>();
        logEntries = entries;

        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(Path.GetTempPath());
        host.Setup(h => h.Log(It.IsAny<PluginLogLevel>(), It.IsAny<string>()))
            .Callback<PluginLogLevel, string>((lvl, msg) => entries.Add((lvl, msg)));
        return host.Object;
    }
}
