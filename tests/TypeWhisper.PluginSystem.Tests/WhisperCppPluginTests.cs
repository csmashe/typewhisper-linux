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
