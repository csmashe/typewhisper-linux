using TypeWhisper.Plugin.WhisperCpp;
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
}

public class SherpaOnnxPluginTests
{
    [Fact]
    public void SupportedAccelerationBackends_IsCpuAndNvidiaCuda()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();

        Assert.Contains(
            TranscriptionAccelerationBackend.Cpu,
            plugin.SupportedAccelerationBackends);
        Assert.Contains(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public void SetAccelerationPreference_Cpu_TracksPreferenceAndReportsCpu()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            plugin.AccelerationPreference);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public void SetAccelerationPreference_NvidiaCuda_TracksPreferenceAndShowsPending()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
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
            plugin.AccelerationStatus.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetAccelerationPreference_Cpu_WhenRuntimePinnedToCuda_RequiresRestart()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();
        plugin.MarkNativeRuntimeLoadedForTests("cuda");

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(
            TranscriptionAccelerationPreference.Cpu,
            plugin.AccelerationPreference);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        Assert.NotNull(plugin.AccelerationStatus.Detail);
        Assert.Contains(
            "restart",
            plugin.AccelerationStatus.Detail,
            StringComparison.OrdinalIgnoreCase);
    }
}
